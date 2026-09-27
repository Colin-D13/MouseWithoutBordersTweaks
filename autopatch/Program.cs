// Starts at sign-in and stays running. Whenever a PowerToys update (at sign-in or mid-session) replaces
// the patched Mouse Without Borders DLL, re-applies the fix via apply.ps1 and shows a notification.
//   MwbAutoPatch.exe              run the watcher
//   MwbAutoPatch.exe --install    run at sign-in (HKCU Run key) and start now
//   MwbAutoPatch.exe --uninstall  stop running at sign-in and stop the running watcher
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MwbAutoPatch";
    private const string PatchMarker = "MouseMoveCoalescer";
    private const string ExitEventName = @"Local\MwbAutoPatch.Exit";
    private const string DllName = "PowerToys.MouseWithoutBorders.dll";

    // Fallback in case a file change notification is missed.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    // An update is considered finished once the DLL has been left alone this long and no installer runs.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(60);

    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string PowerToysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerToys");
    private static readonly string MwbDll = Path.Combine(PowerToysDir, DllName);
    private static readonly string LogFile = Path.Combine(Root, "autopatch.log");
    private static readonly object LogLock = new object();

    private static readonly AutoResetEvent DllChanged = new AutoResetEvent(true); // Set: check once at startup.
    private static long lastDllEventTicks = DateTime.MinValue.Ticks;
    private static string failedDllKey;

    [STAThread]
    private static int Main(string[] args)
    {
        string arg = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

        if (arg == "--install")
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            }

            Process.Start(Application.ExecutablePath);
            return 0;
        }

        if (arg == "--uninstall")
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                key.DeleteValue(RunValue, false);
            }

            if (EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle running))
            {
                running.Set();
                running.Dispose();
            }

            return 0;
        }

        using (var mutex = new Mutex(true, @"Local\MwbAutoPatch", out bool firstInstance))
        using (var exit = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName))
        {
            if (!firstInstance)
            {
                return 0;
            }

            FileSystemWatcher watcher = null;
            var handles = new WaitHandle[] { exit, DllChanged };

            try
            {
                while (true)
                {
                    watcher = watcher ?? TryWatch();

                    if (WaitHandle.WaitAny(handles, PollInterval) == 0)
                    {
                        return 0;
                    }

                    try
                    {
                        CheckAndPatch(exit);
                    }
                    catch (Exception e)
                    {
                        Log("Unexpected error: " + e);
                    }

                    if (watcher != null && !watcher.EnableRaisingEvents)
                    {
                        watcher.Dispose();
                        watcher = null;
                    }
                }
            }
            finally
            {
                watcher?.Dispose();
            }
        }
    }

    private static FileSystemWatcher TryWatch()
    {
        if (!Directory.Exists(PowerToysDir))
        {
            return null;
        }

        var watcher = new FileSystemWatcher(PowerToysDir, DllName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (s, e) => OnDllEvent();
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += (s, e) => OnDllEvent();
        watcher.Error += (s, e) => { ((FileSystemWatcher)s).EnableRaisingEvents = false; OnDllEvent(); };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static void OnDllEvent()
    {
        Interlocked.Exchange(ref lastDllEventTicks, DateTime.UtcNow.Ticks);
        DllChanged.Set();
    }

    private static void CheckAndPatch(WaitHandle exit)
    {
        if (!NeedsPatch())
        {
            return;
        }

        // Let a running update finish before touching anything.
        while (!IsSettled())
        {
            if (exit.WaitOne(TimeSpan.FromSeconds(5)))
            {
                return;
            }
        }

        if (!NeedsPatch())
        {
            return;
        }

        // Let PowerToys finish starting (after sign-in or the installer relaunching it), since apply.ps1 restarts it.
        for (int i = 0; i < 90 && Process.GetProcessesByName("PowerToys").Length == 0; i++)
        {
            if (exit.WaitOne(TimeSpan.FromSeconds(2)))
            {
                return;
            }
        }

        if (exit.WaitOne(TimeSpan.FromSeconds(15)) || !NeedsPatch())
        {
            return;
        }

        string key = DllKey();
        string version = FileVersionInfo.GetVersionInfo(MwbDll).FileVersion;
        Log($"PowerToys {version}: Mouse Without Borders is unpatched, running apply.ps1.");
        int exitCode = RunApply();
        bool ok = exitCode == 0 && !NeedsPatch();
        Log(ok ? "Patched." : $"Failed (exit code {exitCode}).");

        if (ok)
        {
            failedDllKey = null;
            Notify("Mouse Without Borders fix re-applied", $"PowerToys {version} was updated, so the fix was applied again.", ToolTipIcon.Info);
        }
        else
        {
            // Don't retry (and re-notify) for this same DLL until it changes or the next sign-in.
            failedDllKey = key;
            Notify("Mouse Without Borders fix failed", $"Couldn't patch PowerToys {version}. Details: {LogFile}", ToolTipIcon.Warning);
        }
    }

    private static bool NeedsPatch()
    {
        return !File.Exists(Path.Combine(Root, "disabled")) && File.Exists(MwbDll) && !IsPatched() && DllKey() != failedDllKey;
    }

    private static bool IsPatched()
    {
        try
        {
            // The patched DLL's metadata contains the coalescer's type name; the stock DLL doesn't.
            return Encoding.ASCII.GetString(File.ReadAllBytes(MwbDll)).Contains(PatchMarker);
        }
        catch (IOException)
        {
            return true; // Missing or locked (mid-update?): look again on the next change.
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string DllKey()
    {
        try
        {
            var info = new FileInfo(MwbDll);
            return $"{info.Length}|{info.CreationTimeUtc.Ticks}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsSettled()
    {
        DateTime lastChange = new DateTime(Interlocked.Read(ref lastDllEventTicks), DateTimeKind.Utc);

        try
        {
            // Installers keep the build's write time but create the file anew, so look at both.
            DateTime created = File.GetCreationTimeUtc(MwbDll);
            DateTime written = File.GetLastWriteTimeUtc(MwbDll);
            lastChange = new[] { lastChange, created, written }.Max();
        }
        catch (IOException)
        {
        }

        bool installerRunning = Process.GetProcesses().Any(p => p.ProcessName.StartsWith("PowerToys", StringComparison.OrdinalIgnoreCase)
            && p.ProcessName.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0);

        return !installerRunning && DateTime.UtcNow - lastChange >= SettleTime;
    }

    private static int RunApply()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(Root, "apply.ps1")}\"",
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using (Process p = new Process { StartInfo = psi })
        {
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Log("  " + e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log("  " + e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds))
            {
                Log("apply.ps1 timed out after 15 minutes.");
                p.Kill();
                return -1;
            }

            p.WaitForExit();
            return p.ExitCode;
        }
    }

    private static void Log(string line)
    {
        lock (LogLock)
        {
            try
            {
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1024 * 1024)
                {
                    File.Delete(LogFile);
                }

                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Notify(string title, string text, ToolTipIcon icon)
    {
        using (var tray = new NotifyIcon())
        using (var timer = new System.Windows.Forms.Timer { Interval = 10000 })
        {
            string exe = Path.Combine(PowerToysDir, "PowerToys.exe");
            tray.Icon = File.Exists(exe) ? Icon.ExtractAssociatedIcon(exe) : SystemIcons.Information;
            tray.Text = "MWB fix";
            tray.Visible = true;
            tray.ShowBalloonTip(10000, title, text, icon);
            timer.Tick += (s, e) => Application.ExitThread();
            timer.Start();
            Application.Run();
            tray.Visible = false;
        }
    }
}
