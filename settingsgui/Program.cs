// Settings window for the Mouse Without Borders tweaks in %LOCALAPPDATA%\mwb8k\settings.ini.
// Changes apply when you press Save; MWB re-reads the file within a second.
// The test section shows what MWB itself measured at the screen edge (shared memory "Local\MwbEdgeStats",
// written by EdgeStats in the patched MWB), and can turn on MWB's test mode so edge hits don't switch PCs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2
        }
        catch (EntryPointNotFoundException)
        {
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SettingsForm());
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

internal sealed class SettingsForm : Form
{
    private const int ContentWidth = 470;
    private const int Drag = 1;
    private const int Push = 2;

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
    private static readonly string PowerToysDir = Path.Combine(LocalAppData, "PowerToys");
    private static readonly string MwbSettingsFile = Path.Combine(LocalAppData, "Microsoft", "PowerToys", "MouseWithoutBorders", "settings.json");

    private static readonly Color BackColorDark = Color.FromArgb(32, 32, 32);
    private static readonly Color FieldColor = Color.FromArgb(45, 45, 45);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 240);
    private static readonly Color DimColor = Color.FromArgb(165, 165, 165);
    private static readonly Color FaintColor = Color.FromArgb(110, 110, 110);
    private static readonly Color AccentColor = Color.FromArgb(76, 194, 255);
    private static readonly Color GoodColor = Color.FromArgb(108, 203, 95);
    private static readonly Color BadColor = Color.FromArgb(255, 153, 102);

    private readonly NumericUpDown flickSpeed;
    private readonly NumericUpDown pushSpeed;
    private readonly NumericUpDown restMs;
    private readonly NumericUpDown moveHz;
    private readonly CheckBox testMode;
    private readonly Label easyMouseHint;
    private readonly Label[] rowSpeed = new Label[3];
    private readonly Label[] rowNeeded = new Label[3];
    private readonly Label[] rowResult = new Label[3];
    private readonly Label lastHit;
    private readonly Label status;
    private readonly Button save;

    private MemoryMappedFile map;
    private MemoryMappedViewAccessor view;
    private int lastSequence = -1;
    private int lastSequenceChangeTick = Environment.TickCount;
    private bool loading;
    private bool dirty;

    // What MWB is using: the values in the settings file, not the ones being edited.
    private int savedFlick;
    private int savedPush;

    internal SettingsForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Mouse Without Borders tweaks";
        Font = new Font("Segoe UI", 9.75F);
        BackColor = BackColorDark;
        ForeColor = TextColor;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 16);

        string mwbExe = Path.Combine(PowerToysDir, "PowerToys.MouseWithoutBorders.exe");
        if (File.Exists(mwbExe))
        {
            Icon = Icon.ExtractAssociatedIcon(mwbExe);
        }

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Location = new Point(Padding.Left, Padding.Top) };
        Controls.Add(layout);

        layout.Controls.Add(Heading("Switching PCs at a screen edge"));
        flickSpeed = AddSetting(layout, "Drag into the edge", "How fast you have to drag the cursor into an edge to switch, including carrying on into it without stopping. 0 = switch as soon as the cursor touches the edge.", 10000, 100, "px/s");
        pushSpeed = AddSetting(layout, "Push from the border", "How hard you have to push once the cursor has rested on the edge (see below).", 10000, 100, "px/s");
        restMs = AddSetting(layout, "Rest on the border first", "How long the cursor has to sit on the edge, without pushing, before a push counts as a push from the border. Longer means a drag that stalls at the edge still needs the drag speed.", 2000, 50, "ms");

        layout.Controls.Add(Heading("Test it"));
        testMode = new CheckBox { Text = "Test mode: measure edge hits without switching PCs (only while this window is focused)", AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Checked = false, Margin = new Padding(0, 2, 0, 2) };
        testMode.CheckedChanged += (s, e) => WriteHeartbeat();
        layout.Controls.Add(testMode);
        easyMouseHint = Note(string.Empty, 8);
        layout.Controls.Add(easyMouseHint);

        var results = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, MinimumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 0, 0, 4) };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        results.Controls.Add(ColumnLabel("Last hit"), 0, 0);
        results.Controls.Add(ColumnLabel("Measured"), 1, 0);
        results.Controls.Add(ColumnLabel("Needed"), 2, 0);
        results.Controls.Add(ColumnLabel(string.Empty), 3, 0);
        AddResultRow(results, Drag, "Drag into the edge");
        AddResultRow(results, Push, "Push from the border");
        layout.Controls.Add(results);
        lastHit = Note(string.Empty, 18);
        layout.Controls.Add(lastHit);

        layout.Controls.Add(Heading("8k polling"));
        moveHz = AddSetting(layout, "Mouse updates sent to the other PC", "Cap on cursor updates per second sent to the other PC, so 4k/8k polling doesn't lag it. 0 = unlimited.", 8000, 50, "per second", slider: false);

        var link = new LinkLabel { Text = "Open Mouse Without Borders settings in PowerToys", AutoSize = true, LinkColor = AccentColor, ActiveLinkColor = AccentColor, VisitedLinkColor = AccentColor, Margin = new Padding(0, 0, 0, 14) };
        link.LinkClicked += (s, e) => OpenPowerToysSettings();
        layout.Controls.Add(link);

        var buttons = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, MinimumSize = new Size(ContentWidth, 0), Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var defaults = MakeButton("Defaults");
        defaults.Click += (s, e) => { LoadValues(new Dictionary<string, int>()); SetDirty(true); };
        save = MakeButton("Save");
        save.Click += (s, e) => Save();
        var close = MakeButton("Close");
        close.Click += (s, e) => Close();
        buttons.Controls.Add(defaults, 0, 0);
        buttons.Controls.Add(save, 2, 0);
        buttons.Controls.Add(close, 3, 0);
        layout.Controls.Add(buttons);

        status = Note(string.Empty, 0);
        status.Margin = new Padding(0, 8, 0, 0);
        layout.Controls.Add(status);
        CancelButton = close;
        AcceptButton = save;

        LoadValues(ReadFile());
        savedFlick = (int)flickSpeed.Value;
        savedPush = (int)pushSpeed.Value;
        SetDirty(false);
        status.Text = "Settings file: " + SettingsFile;
        foreach (var n in new[] { flickSpeed, pushSpeed, restMs, moveHz })
        {
            n.ValueChanged += (s, e) =>
            {
                if (!loading)
                {
                    SetDirty(true);
                }
            };
        }

        DeleteOldCopies();

        // PowerToys' own MWB settings are only read on open and when this window gets focus, so its saves aren't blocked.
        Activated += (s, e) => UpdateEasyMouseHint();
        UpdateEasyMouseHint();

        var poll = new Timer { Interval = 50 };
        poll.Tick += (s, e) => Poll();
        poll.Start();
        Poll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // dark title bar
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (dirty)
        {
            var answer = MessageBox.Show(this, "Save your changes?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
        }

        view?.Write(8, 0); // test mode off
        base.OnFormClosing(e);
    }

    private void SetDirty(bool value)
    {
        dirty = value;
        save.BackColor = dirty ? AccentColor : FieldColor;
        save.ForeColor = dirty ? Color.Black : TextColor;

        if (dirty)
        {
            status.Text = "Not saved yet. Press Save to use these values.";
        }
    }

    // Updating renames the running exe out of the way; clean those up once it's no longer running.
    private static void DeleteOldCopies()
    {
        try
        {
            foreach (string old in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "MwbSettings.old*.exe"))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, int> ReadFile()
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(SettingsFile))
            {
                foreach (string raw in File.ReadAllLines(SettingsFile))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');

                    if (!line.StartsWith("#", StringComparison.Ordinal) && eq > 0 && int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    {
                        values[line.Substring(0, eq).Trim()] = v;
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return values;
    }

    private void LoadValues(Dictionary<string, int> values)
    {
        loading = true;
        SetValue(flickSpeed, values, "EdgeFlickSpeed", 2500);
        SetValue(pushSpeed, values, "EdgePushSpeed", 1200);
        SetValue(restMs, values, "EdgePushRestMs", 100);
        SetValue(moveHz, values, "MaxMouseMoveHz", 500);
        loading = false;
    }

    private static void SetValue(NumericUpDown box, Dictionary<string, int> values, string key, int fallback)
    {
        int v = values.TryGetValue(key, out int found) ? found : fallback;
        box.Value = Math.Max(box.Minimum, Math.Min(box.Maximum, v));
    }

    private bool Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Mouse Without Borders tweaks. Edit here or with MwbSettings.exe; changes apply within a second, no restart needed.");
        sb.AppendLine("# Speeds are in pixels per second. Higher = harder to switch PCs, lower = easier.");
        sb.AppendLine("# The Easy Mouse setting still decides when switching works at all (Enabled, or only while Ctrl/Shift is held).");
        sb.AppendLine();
        sb.AppendLine("# Dragging the cursor into a screen edge (and carrying on into it without stopping).");
        sb.AppendLine("# 0 = switch as soon as the cursor touches the edge.");
        sb.AppendLine("EdgeFlickSpeed=" + (int)flickSpeed.Value);
        sb.AppendLine();
        sb.AppendLine("# Pushing into the edge after the cursor has rested on the border (EdgePushRestMs).");
        sb.AppendLine("EdgePushSpeed=" + (int)pushSpeed.Value);
        sb.AppendLine();
        sb.AppendLine("# How long (ms) the cursor has to rest on the border, without pushing, before EdgePushSpeed applies.");
        sb.AppendLine("EdgePushRestMs=" + (int)restMs.Value);
        sb.AppendLine();
        sb.AppendLine("# Max mouse-move updates per second sent to the other PC (fixes 4k/8k polling lag). 0 = unlimited.");
        sb.AppendLine("MaxMouseMoveHz=" + (int)moveHz.Value);

        try
        {
            // Write a temp file and swap it in, so MWB never reads a half-written file.
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            string temp = SettingsFile + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));

            if (File.Exists(SettingsFile))
            {
                File.Replace(temp, SettingsFile, null);
            }
            else
            {
                File.Move(temp, SettingsFile);
            }
            savedFlick = (int)flickSpeed.Value;
            savedPush = (int)pushSpeed.Value;
            SetDirty(false);
            status.Text = "Saved. Mouse Without Borders uses it within a second.";
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            status.Text = "Couldn't save: " + ex.Message;
            return false;
        }
    }

    private void Poll()
    {
        int now = Environment.TickCount;

        if (view == null)
        {
            try
            {
                map = MemoryMappedFile.CreateOrOpen("Local\\MwbEdgeStats", 64);
                view = map.CreateViewAccessor(0, 64);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastHit.Text = "Can't reach Mouse Without Borders: " + ex.Message;
                return;
            }
        }

        WriteHeartbeat();

        int sequence = view.ReadInt32(0);
        if (sequence != lastSequence)
        {
            lastSequence = sequence;
            lastSequenceChangeTick = now;
        }

        int lastKind = view.ReadInt32(4);

        for (int kind = Drag; kind <= Push; kind++)
        {
            int offset = 16 + ((kind - 1) * 16);
            int peak = view.ReadInt32(offset);

            // Judge the hit against the saved settings; MWB's own "needed" is from whenever the hit happened.
            int needed = kind == Drag ? savedFlick : Math.Max(1, savedPush);
            bool passes = peak >= needed;
            int hitTick = view.ReadInt32(offset + 12);
            bool seen = hitTick != 0 && sequence != 0;
            bool recent = seen && unchecked(now - hitTick) < 5000;

            rowSpeed[kind].Text = seen ? peak.ToString("N0", CultureInfo.CurrentCulture) + " px/s" : "–";
            rowNeeded[kind].Text = seen ? needed.ToString("N0", CultureInfo.CurrentCulture) + " px/s" : "–";
            rowResult[kind].Text = !seen ? string.Empty : passes ? (testMode.Checked && ActiveForm == this ? "would switch" : "switched") : "too soft";
            rowResult[kind].ForeColor = passes ? GoodColor : BadColor;
            Color text = recent ? TextColor : FaintColor;
            rowSpeed[kind].ForeColor = text;
            rowNeeded[kind].ForeColor = text;
            if (!recent)
            {
                rowResult[kind].ForeColor = FaintColor;
            }
        }

        if (sequence == 0)
        {
            lastHit.Text = "Nothing measured yet. Drag into or push against a screen edge that leads to another PC.";
        }
        else
        {
            string kindName = lastKind == Push ? "a push from the border" : "a drag into the edge";
            lastHit.Text = unchecked(now - lastSequenceChangeTick) < 1000
                ? "MWB is measuring " + kindName + " right now."
                : "Last edge hit counted as " + kindName + ". Measured is the peak of that hit.";
        }
    }

    private void WriteHeartbeat()
    {
        if (view == null)
        {
            return;
        }

        // Test mode stops PCs switching, so it only holds while this window is the one you're using.
        int beat = Environment.TickCount;
        view.Write(8, testMode.Checked && ActiveForm == this ? (beat == 0 ? 1 : beat) : 0);
    }

    private void UpdateEasyMouseHint()
    {
        int mode = -1;

        try
        {
            if (File.Exists(MwbSettingsFile))
            {
                string json;
                using (var stream = new FileStream(MwbSettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                var m = Regex.Match(json,"\"EasyMouse\"\\s*:\\s*\\{\\s*\"value\"\\s*:\\s*(\\d+)");
                if (m.Success)
                {
                    mode = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
        }
        catch (IOException)
        {
        }

        easyMouseHint.Text = mode switch
        {
            0 => "Easy Mouse is off in PowerToys, so screen edges never switch PCs (and nothing gets measured).",
            2 => "Easy Mouse is set to Ctrl: hold Ctrl while you drag into or push against an edge.",
            3 => "Easy Mouse is set to Shift: hold Shift while you drag into or push against an edge.",
            _ => "Drag into or push against a screen edge that leads to another PC.",
        };
    }

    private void AddResultRow(TableLayoutPanel table, int kind, string name)
    {
        int row = table.RowCount = table.RowCount + 1;
        table.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 3, 0, 3) }, 0, row);
        rowSpeed[kind] = new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        rowNeeded[kind] = new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        rowResult[kind] = new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 3), Font = new Font(Font, FontStyle.Bold) };
        table.Controls.Add(rowSpeed[kind], 1, row);
        table.Controls.Add(rowNeeded[kind], 2, row);
        table.Controls.Add(rowResult[kind], 3, row);
    }

    private static Label ColumnLabel(string text) => new Label { Text = text, AutoSize = true, ForeColor = DimColor, Margin = new Padding(0, 0, 0, 2) };

    private NumericUpDown AddSetting(FlowLayoutPanel host, string title, string description, int max, int step, string unit, bool slider = true)
    {
        var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, MinimumSize = new Size(ContentWidth, 0), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) }, 0, 0);

        var valueRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
        var box = new NumericUpDown { Minimum = 0, Maximum = slider ? max * 2 : max, Increment = step, Width = 76, TextAlign = HorizontalAlignment.Right, BackColor = FieldColor, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
        valueRow.Controls.Add(box);
        valueRow.Controls.Add(new Label { Text = unit, AutoSize = true, ForeColor = DimColor, Margin = new Padding(6, 5, 0, 0) });
        header.Controls.Add(valueRow, 1, 0);
        host.Controls.Add(header);
        host.Controls.Add(Note(description, slider ? 0 : 16));

        if (slider)
        {
            var bar = new TrackBar { Minimum = 0, Maximum = max, TickFrequency = max / 10, SmallChange = step, LargeChange = step * 10, Width = ContentWidth, BackColor = BackColorDark, Margin = new Padding(0, 6, 0, 10) };
            bool syncing = false;
            bar.ValueChanged += (s, e) =>
            {
                if (syncing)
                {
                    return;
                }

                syncing = true;
                box.Value = (int)Math.Round(bar.Value / (double)step) * step;
                syncing = false;
            };
            box.ValueChanged += (s, e) =>
            {
                if (syncing)
                {
                    return;
                }

                syncing = true;
                bar.Value = (int)Math.Min(bar.Maximum, box.Value);
                syncing = false;
            };
            host.Controls.Add(bar);
        }

        return box;
    }

    private Label Heading(string text) => new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };

    private static Label Note(string text, int bottom) => new Label { Text = text, AutoSize = true, ForeColor = DimColor, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 2, 0, bottom) };

    private static Button MakeButton(string text)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 32), FlatStyle = FlatStyle.Flat, BackColor = FieldColor, ForeColor = TextColor, Margin = new Padding(8, 0, 0, 0), UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
        return b;
    }

    private static void OpenPowerToysSettings()
    {
        string exe = Path.Combine(PowerToysDir, "PowerToys.exe");

        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe, "--open-settings=MouseWithoutBorders") { UseShellExecute = true });
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
