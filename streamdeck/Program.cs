// Stream Deck plugin with two keys for PowerToys Mouse Without Borders:
//   Game Lock: toggles "Disable Easy Mouse when the foreground window is fullscreen", so MWB doesn't switch PCs while a
//              fullscreen app (a game) is focused.
//   PC Switch: turns switching PCs at screen edges (Easy Mouse) off entirely, and back to the mode it was in before.
// Key state 0 = on, 1 = off. The keys follow the settings when they're changed in PowerToys too.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    private const string GameLockAction = "com.colin.mwbtoggle.gamelock";
    private const string SwitchingAction = "com.colin.mwbtoggle.switching";
    private const int StateOn = 0;
    private const int StateOff = 1;

    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys", "MouseWithoutBorders", "settings.json");
    private static readonly Regex FullscreenSetting = new Regex("(\"DisableEasyMouseWhenForegroundWindowIsFullscreen\"\\s*:\\s*\\{\\s*\"value\"\\s*:\\s*)(true|false)");
    private static readonly Regex EasyMouseSetting = new Regex("(\"EasyMouse\"\\s*:\\s*\\{\\s*\"value\"\\s*:\\s*)(\\d+)");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly Dictionary<string, string> Contexts = new Dictionary<string, string>(); // context -> action
    private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);

    private static ClientWebSocket socket;
    private static Timer refreshTimer;

    // Easy Mouse mode to go back to when PC Switch is turned on again (1 = Enabled, 2 = Ctrl, 3 = Shift).
    private static int restoreMode = 1;

    private static void Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            options[args[i].TrimStart('-')] = args[i + 1];
        }

        Run(int.Parse(options["port"]), options["pluginUUID"], options["registerEvent"]).GetAwaiter().GetResult();
    }

    private static async Task Run(int port, string pluginUuid, string registerEvent)
    {
        socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None);
        await Send(new Dictionary<string, object> { ["event"] = registerEvent, ["uuid"] = pluginUuid });

        // PowerToys (or its settings window) may change the settings too; show that on the keys.
        refreshTimer = new Timer(_ => ShowAll().GetAwaiter().GetResult(), null, Timeout.Infinite, Timeout.Infinite);
        FileSystemWatcher watcher = null;
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(SettingsFile), Path.GetFileName(SettingsFile)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (s, e) => refreshTimer.Change(250, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => refreshTimer.Change(250, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
        }
        catch (ArgumentException)
        {
            // PowerToys' settings folder doesn't exist yet.
        }

        var buffer = new byte[16384];
        var message = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);

            try
            {
                await Handle(Json.Deserialize<Dictionary<string, object>>(text));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
            }
        }

        watcher?.Dispose();
    }

    private static async Task Handle(Dictionary<string, object> e)
    {
        string name = e.TryGetValue("event", out object ev) ? ev as string : null;
        string context = e.TryGetValue("context", out object ctx) ? ctx as string : null;
        string action = e.TryGetValue("action", out object act) ? act as string : null;
        var payload = e.TryGetValue("payload", out object p) ? p as Dictionary<string, object> : null;
        var settings = payload != null && payload.TryGetValue("settings", out object s) ? s as Dictionary<string, object> : null;

        if (settings != null && settings.TryGetValue("restoreMode", out object saved) && saved is int mode && mode >= 1 && mode <= 3)
        {
            restoreMode = mode;
        }

        switch (name)
        {
            case "willAppear":
                lock (Contexts)
                {
                    Contexts[context] = action;
                }

                bool? on = IsOn(action, ReadText());
                if (on.HasValue)
                {
                    await SetState(context, on.Value);
                }

                break;

            case "willDisappear":
                lock (Contexts)
                {
                    Contexts.Remove(context);
                }

                break;

            case "keyDown":
                bool done = action == SwitchingAction ? await ToggleSwitching(context) : ToggleGameLock();
                if (!done)
                {
                    await Send(new Dictionary<string, object> { ["event"] = "showAlert", ["context"] = context });
                    break;
                }

                await ShowAll();
                break;
        }
    }

    // Game Lock is on when the fullscreen block is on; PC Switch is on when Easy Mouse isn't disabled.
    private static bool? IsOn(string action, string text)
    {
        if (text == null)
        {
            return null;
        }

        if (action == SwitchingAction)
        {
            Match mode = EasyMouseSetting.Match(text);
            return mode.Success ? mode.Groups[2].Value != "0" : (bool?)null;
        }

        Match block = FullscreenSetting.Match(text);
        return block.Success ? block.Groups[2].Value == "true" : (bool?)null;
    }

    private static bool ToggleGameLock() =>
        Edit(text =>
        {
            Match m = FullscreenSetting.Match(text);
            return m.Success ? FullscreenSetting.Replace(text, m.Groups[1].Value + (m.Groups[2].Value == "true" ? "false" : "true"), 1) : null;
        });

    private static async Task<bool> ToggleSwitching(string context)
    {
        int previous = -1;
        bool done = Edit(text =>
        {
            Match m = EasyMouseSetting.Match(text);
            if (!m.Success)
            {
                return null;
            }

            previous = int.Parse(m.Groups[2].Value);
            int next = previous != 0 ? 0 : restoreMode;
            return EasyMouseSetting.Replace(text, m.Groups[1].Value + next, 1);
        });

        // Remember the mode being turned off, with the key, so turning it back on restores it.
        if (done && previous > 0)
        {
            restoreMode = previous;
            await Send(new Dictionary<string, object> { ["event"] = "setSettings", ["context"] = context, ["payload"] = new Dictionary<string, object> { ["restoreMode"] = previous } });
        }

        return done;
    }

    private static async Task ShowAll()
    {
        string text = ReadText();
        KeyValuePair<string, string>[] all;
        lock (Contexts)
        {
            all = Contexts.ToArray();
        }

        foreach (var key in all)
        {
            bool? on = IsOn(key.Value, text);
            if (on.HasValue)
            {
                await SetState(key.Key, on.Value);
            }
        }
    }

    private static Task SetState(string context, bool on) =>
        Send(new Dictionary<string, object> { ["event"] = "setState", ["context"] = context, ["payload"] = new Dictionary<string, object> { ["state"] = on ? StateOn : StateOff } });

    private static async Task Send(object value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(value));
        await SendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            SendLock.Release();
        }
    }

    // Applies edit (null = can't) to PowerToys' MWB settings file, keeping its encoding.
    private static bool Edit(Func<string, string> edit)
    {
        // PowerToys writes this file too; retry if it's busy for a moment.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(SettingsFile);
                bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                string updated = edit(new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0)));
                if (updated == null)
                {
                    return false;
                }

                File.WriteAllText(SettingsFile, updated, new UTF8Encoding(bom));
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static string ReadText()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using (var stream = new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        return null;
    }
}
