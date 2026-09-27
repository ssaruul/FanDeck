using FanDeck.Cooling;
using FanDeck.Hardware;

namespace FanDeck.UI;

/// <summary>The small window: temperatures, profile buttons, one row per fan, and lighting.</summary>
sealed class MainForm : Form
{
    static readonly (string Label, AuraEffect Effect)[] Effects =
    [
        ("Static", AuraEffect.Static),
        ("Breathing", AuraEffect.Breathing),
        ("Strobing", AuraEffect.Strobing),
        ("Color cycle", AuraEffect.ColorCycle),
        ("Rainbow", AuraEffect.Rainbow),
        ("Off", AuraEffect.Off),
    ];

    sealed record FanRow(Label Name, Label Rpm, Label Percent, TrackBar Slider);

    readonly AppConfig _cfg;
    readonly AuraUsb? _aura;
    readonly string? _auraError;
    readonly TrayApp _app;

    readonly TableLayoutPanel _banner;
    readonly Label _temps;
    readonly Label _profileHint;
    readonly CurveGraph _graph;
    readonly Label _graphHint;
    readonly LinkLabel _resetCurve;
    readonly Dictionary<Profile, RadioButton> _profileButtons = [];
    readonly TableLayoutPanel _fanTable;
    readonly Label _fanNote;
    readonly ComboBox _effect;
    readonly ComboBox _target;
    readonly Button _colorButton;
    readonly Button _apply;
    readonly Label _rgbStatus;
    readonly LinkLabel _giveBack;
    readonly LinkLabel _gameSafeLink;
    readonly Dictionary<string, FanRow> _rows = [];

    LoopSnapshot? _last;
    string _fanLayout = "";
    Profile? _shownProfile;
    Color _color;
    bool _syncing;
    bool _allowClose;

    public MainForm(AuraUsb? aura, string? auraError, TrayApp app)
    {
        var cfg = app.Config;
        _cfg = cfg;
        _aura = aura;
        _auraError = auraError;
        _app = app;
        _color = ParseColor(cfg.Lighting.Color);

        // Sizes are scaled by hand with Px(), since the fan rows are built after WinForms' own auto-scaling runs.
        AutoScaleMode = AutoScaleMode.None;
        Text = "FanDeck";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Pad(16),
        };
        Controls.Add(root);

        // Armoury Crate warning.
        _banner = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            BackColor = Color.FromArgb(255, 243, 205),
            Padding = Pad(10, 8, 10, 8),
            Margin = Pad(0, 0, 0, 12),
            Dock = DockStyle.Fill,
        };
        _banner.Controls.Add(new Label
        {
            Text = "Armoury Crate is also controlling your fans and RGB, and it will override FanDeck.",
            AutoSize = true,
            MaximumSize = new Size(Px(360), Px(0)),
            Anchor = AnchorStyles.Left,
        });
        var takeControl = new Button { Text = "Take control", AutoSize = true, Anchor = AnchorStyles.Right };
        takeControl.Click += async (_, _) =>
        {
            takeControl.Enabled = false;
            takeControl.Text = "Stopping…";
            await _app.TakeControlAsync();
            takeControl.Text = "Take control";
            takeControl.Enabled = true;
        };
        _banner.Controls.Add(takeControl);
        root.Controls.Add(_banner);

        _temps = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 16f),
            Margin = Pad(0, 0, 0, 10),
            Text = "Reading sensors…",
        };
        root.Controls.Add(_temps);

        // Profile buttons.
        var profiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pad(0) };
        // Wide enough for the longest label in bold (the checked state), so nothing shifts when switching.
        using var bold = new Font(Font, FontStyle.Bold);
        int buttonWidth = Profiles.All.Max(p => TextRenderer.MeasureText(p.ToString(), bold).Width) + Px(24);
        foreach (var profile in Profiles.All)
        {
            var button = ProfileButton(profile, buttonWidth);
            _profileButtons[profile] = button;
            profiles.Controls.Add(button);
        }
        root.Controls.Add(profiles);
        _profileHint = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = Pad(0, 6, 0, 6) };
        root.Controls.Add(_profileHint);

        // Fan curve graph for the active profile.
        _graph = new CurveGraph { Size = new Size(Px(580), Px(210)), Margin = Pad(0) };
        _graph.CurveEdited += points =>
        {
            _cfg.SetCurve(_cfg.Profile, points);
            _cfg.Save();
            _app.Loop?.Poke();
            ShowCurve();
        };
        root.Controls.Add(_graph);
        var graphFooter = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pad(0, 2, 0, 12) };
        _graphHint = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = Pad(0, 0, 12, 0) };
        _resetCurve = new LinkLabel { Text = "Reset curve", AutoSize = true, Margin = Pad(0) };
        _resetCurve.LinkClicked += (_, _) =>
        {
            _cfg.SetCurve(_cfg.Profile, null);
            _cfg.Save();
            _app.Loop?.Poke();
            ShowCurve();
        };
        graphFooter.Controls.Add(_graphHint);
        graphFooter.Controls.Add(_resetCurve);
        root.Controls.Add(graphFooter);

        // Fans.
        var fansBox = Section("Fans");
        _fanTable = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Margin = Pad(0) };
        _fanNote = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(Px(480), Px(0)), Margin = Pad(0, 6, 0, 0) };
        var rescan = new LinkLabel { Text = "Rescan fan headers", AutoSize = true, Margin = Pad(0, 6, 0, 0) };
        rescan.LinkClicked += (_, _) =>
        {
            _app.Loop?.Rescan();
            _fanLayout = "";
        };
        _gameSafeLink = new LinkLabel { Text = "Turn off game-safe mode", AutoSize = true, Margin = Pad(0, 6, 0, 0) };
        _gameSafeLink.LinkClicked += (_, _) => _app.SetGameSafe(false);
        fansBox.Controls.Add(_fanTable);
        fansBox.Controls.Add(_fanNote);
        fansBox.Controls.Add(_gameSafeLink);
        fansBox.Controls.Add(rescan);
        root.Controls.Add(fansBox.Parent);

        // Lighting.
        var lightBox = Section("Lighting");
        var lightRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pad(0) };
        _effect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(120) };
        foreach (var (label, _) in Effects)
            _effect.Items.Add(label);
        _effect.SelectedIndex = Math.Max(0, Array.FindIndex(Effects, e => e.Effect == cfg.Lighting.Effect));
        _effect.SelectedIndexChanged += (_, _) => _colorButton!.Enabled = AuraUsb.UsesColor(SelectedEffect);

        _colorButton = new Button { Width = Px(44), Height = Px(28), FlatStyle = FlatStyle.Flat, BackColor = _color, Cursor = Cursors.Hand };
        _colorButton.FlatAppearance.BorderColor = Color.Gray;
        _colorButton.Click += (_, _) => PickColor();
        _colorButton.Enabled = AuraUsb.UsesColor(SelectedEffect);

        _target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(190) };
        _target.Items.Add("All lighting");
        foreach (var zone in aura?.Zones ?? [])
            _target.Items.Add(zone.Name);
        _target.SelectedIndex = Math.Clamp(cfg.Lighting.Target + 1, 0, _target.Items.Count - 1);

        _apply = new Button { Text = "Apply", AutoSize = true };
        _apply.Click += (_, _) => ApplyLighting();

        lightRow.Controls.Add(_effect);
        lightRow.Controls.Add(_colorButton);
        lightRow.Controls.Add(new Label { Text = "on", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Pad(6, 7, 3, 0) });
        lightRow.Controls.Add(_target);
        lightRow.Controls.Add(_apply);
        _rgbStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(Px(480), Px(0)), Margin = Pad(0, 6, 0, 0) };
        lightBox.Controls.Add(lightRow);
        lightBox.Controls.Add(_rgbStatus);
        root.Controls.Add(lightBox.Parent);

        _giveBack = new LinkLabel { Text = "Give fan and RGB control back to Armoury Crate", AutoSize = true, Margin = Pad(0, 4, 0, 0) };
        _giveBack.LinkClicked += async (_, _) => await _app.GiveBackAsync();
        root.Controls.Add(_giveBack);
    }

    AuraEffect SelectedEffect => Effects[Math.Max(0, _effect.SelectedIndex)].Effect;

    public void AllowClose() => _allowClose = true;

    /// <summary>Refreshes everything from the latest fan-loop reading. Called once a second while visible.</summary>
    public void ShowSnapshot(LoopSnapshot? s)
    {
        _last = s;
        var profile = _cfg.Profile;
        bool watching = _app.FanServiceRunning;
        bool manual = profile == Profile.Manual;

        _temps.Text = _app.GameSafe ? "Game-safe mode"
            : s is null ? "Reading sensors…"
            : $"CPU {Temp(s.CpuTemp)}      GPU {Temp(s.GpuTemp)}";

        _syncing = true;
        foreach (var (p, button) in _profileButtons)
            button.Checked = p == profile;
        _profileHint.Text = Profiles.Describe(profile);
        ShowCurve();
        _graph.SetLive((s?.Fans ?? [])
            .Where(f => f.Controlled && f.SourceTemp is not null)
            .Select(f => new CurveGraph.LiveDot(f.Name, f.SourceTemp!.Value, f.Percent))
            .ToList());

        var fans = s?.Fans ?? [];
        string layout = string.Join("|", fans.Select(f => $"{f.Id}={f.Name}"));
        if (layout != _fanLayout)
        {
            BuildFanRows(fans);
            _fanLayout = layout;
            _shownProfile = null;
        }

        bool profileChanged = _shownProfile != profile;
        _shownProfile = profile;
        foreach (var f in fans)
        {
            if (!_rows.TryGetValue(f.Id, out var row))
                continue;
            row.Rpm.Text = f.Rpm is float rpm ? $"{rpm:N0} RPM" : "–";
            row.Percent.Text = f.Controlled ? $"{f.Percent:0}%" : "BIOS";
            row.Name.ForeColor = f.Controlled ? SystemColors.ControlText : Color.Gray;
            row.Slider.Enabled = manual && !watching && f.Controlled;
            if (!manual || !f.Controlled)
                row.Slider.Value = Percent(f.Percent);
            else if (profileChanged)
                row.Slider.Value = Percent(_cfg.Fan(f.Id).ManualPercent);
        }
        _syncing = false;

        _fanNote.Text = FanNote(s, fans.Count, manual);
        _gameSafeLink.Visible = _app.GameSafe && _cfg.GameSafe;

        bool acRunning = _app.FanServiceRunning || _app.LightingServiceRunning;
        _banner.Visible = acRunning;
        _giveBack.Visible = !acRunning && _cfg.ServiceBackups.Count > 0;

        _apply.Enabled = _aura is not null && !_app.LightingServiceRunning;
        _rgbStatus.Text = _aura is null
            ? $"Lighting unavailable: {_auraError}"
            : _app.LightingServiceRunning
                ? "Armoury Crate's lighting service is running. Take control first."
                : $"Aura controller {_aura.Firmware}: {_aura.ArgbHeaders} ARGB headers. " +
                  "Effects are saved on the board and keep running after FanDeck closes.";
    }

    string FanNote(LoopSnapshot? s, int fanCount, bool manual)
    {
        string? error = _app.GameSafeError is { } e ? $" (Couldn't finish switching: {e})" : null;
        if (_app.GameSafe)
            return (_cfg.GameSafe
                       ? "Game-safe mode is on. FanDeck's driver is unloaded and the BIOS runs your fans, so no anti-cheat has anything to object to. Lighting still works."
                       : "FACEIT is running, so FanDeck stepped aside: its driver is unloaded and the BIOS runs your fans until FACEIT closes.")
                   + error;
        if (error is not null)
            return error.Trim(' ', '(', ')');
        if (s?.Notice is { } notice)
            return notice;
        if (PawnIoDriver.IsInstalled && !PawnIoDriver.IsFaceitCompatible)
            return $"Your PawnIO driver ({PawnIoDriver.Version}) is older than 2.1, and FACEIT blocks that version. Update it from pawnio.eu.";
        if (fanCount == 0)
            return "No spinning fans found on the motherboard headers. Plug fans in, then press Rescan.";
        return manual
            ? "Drag the sliders. Fans stay at or above 20–25%, and every fan runs at full speed if the CPU reaches 95 °C."
            : "Right-click a fan's name to rename it, choose which temperature it follows, or hand it to FanDeck if it shows BIOS.";
    }

    /// <summary>Puts the active profile's curve on the graph: editable for Silent/Balanced/Performance, a flat line for Max.</summary>
    void ShowCurve()
    {
        var profile = _cfg.Profile;
        bool editable = Profiles.HasCurve(profile);
        IReadOnlyList<CurvePoint>? curve = editable ? _cfg.CurveFor(profile)
            : profile == Profile.Max ? [new(Profiles.MinTemp, 100), new(Profiles.MaxTemp, 100)]
            : null;
        _graph.SetCurve(curve, editable, Icons.ColorFor(profile));
        _graphHint.Text = editable
            ? "Drag a point to change the curve. Double-click to add one, right-click to remove."
            : profile == Profile.Max
                ? "Max ignores temperature."
                : "Manual ignores temperature. Set each fan with its slider below.";
        _resetCurve.Visible = editable && _cfg.HasCustomCurve(profile);
    }

    void BuildFanRows(IReadOnlyList<FanStatus> fans)
    {
        _fanTable.SuspendLayout();
        _fanTable.Controls.Clear();
        _rows.Clear();
        _fanTable.RowCount = Math.Max(1, fans.Count);

        for (int i = 0; i < fans.Count; i++)
        {
            string id = fans[i].Id;
            float floor = fans[i].Floor;

            var name = new Label
            {
                Text = fans[i].Name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Font = new Font(Font, FontStyle.Bold),
                MinimumSize = new Size(Px(130), Px(0)),
                Cursor = Cursors.Hand,
            };
            name.ContextMenuStrip = FanMenu(id);
            var rpm = new Label { AutoSize = true, MinimumSize = new Size(Px(96), 0), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Left };
            var pct = new Label { AutoSize = true, MinimumSize = new Size(Px(56), 0), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Left };
            var slider = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None,
                AutoSize = false,
                Size = new Size(Px(210), Px(30)),
                SmallChange = 1,
                LargeChange = 10,
                Anchor = AnchorStyles.Left,
                BackColor = BackColor,
            };
            slider.ValueChanged += (_, _) =>
            {
                if (_syncing || _cfg.Profile != Profile.Manual)
                    return;
                if (slider.Value < floor)
                {
                    slider.Value = (int)floor; // raises ValueChanged again with an allowed value
                    return;
                }
                _cfg.Fan(id).ManualPercent = slider.Value;
                _app.MarkDirty();
                _app.Loop?.Poke();
            };

            _fanTable.Controls.Add(name, 0, i);
            _fanTable.Controls.Add(rpm, 1, i);
            _fanTable.Controls.Add(pct, 2, i);
            _fanTable.Controls.Add(slider, 3, i);
            _rows[id] = new FanRow(name, rpm, pct, slider);
        }
        _fanTable.ResumeLayout();
    }

    ContextMenuStrip FanMenu(string id)
    {
        var menu = new ContextMenuStrip();
        var rename = new ToolStripMenuItem("Rename…", null, (_, _) => Rename(id));
        var control = new ToolStripMenuItem("Let FanDeck control this fan", null, (_, _) => ToggleControlled(id));
        var cpuOnly = new ToolStripMenuItem("Follow CPU temperature only", null, (_, _) => SetFollowGpu(id, false));
        var both = new ToolStripMenuItem("Follow CPU or GPU, whichever is hotter", null, (_, _) => SetFollowGpu(id, true));
        menu.Items.AddRange([rename, control, new ToolStripSeparator(), cpuOnly, both]);
        menu.Opening += (_, _) =>
        {
            var fan = _last?.Fans.FirstOrDefault(f => f.Id == id);
            control.Checked = fan?.Controlled ?? false;
            cpuOnly.Checked = fan is { FollowsGpu: false };
            both.Checked = fan is { FollowsGpu: true };
        };
        return menu;
    }

    void ToggleControlled(string id)
    {
        bool controlled = _last?.Fans.FirstOrDefault(f => f.Id == id)?.Controlled ?? false;
        if (!controlled && MessageBox.Show(this,
                "FanDeck will set this header's speed from the active profile.\n\n" +
                "Don't do this for a pump or anything that isn't a case or CPU fan.",
                "Control this fan?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _cfg.Fan(id).Controlled = !controlled;
        _cfg.Save();
        _app.Loop?.Poke();
    }

    void SetFollowGpu(string id, bool followGpu)
    {
        _cfg.Fan(id).FollowGpu = followGpu;
        _cfg.Save();
        _app.Loop?.Poke();
    }

    void Rename(string id)
    {
        string current = _last?.Fans.FirstOrDefault(f => f.Id == id)?.Name ?? "";
        string? name = Prompt("Rename fan", "Name for this fan header (leave empty for the default):", current);
        if (name is null)
            return;
        _cfg.Fan(id).Name = name.Length == 0 ? null : name;
        _cfg.Save();
        _app.Loop?.Poke();
    }

    RadioButton ProfileButton(Profile profile, int width)
    {
        var button = new RadioButton
        {
            Text = profile.ToString(),
            Appearance = Appearance.Button,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Size = new Size(width, Px(36)),
            FlatStyle = FlatStyle.Flat,
            Margin = Pad(0, 0, 6, 0),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(205, 205, 205);
        button.FlatAppearance.CheckedBackColor = Icons.ColorFor(profile);
        button.CheckedChanged += (_, _) =>
        {
            button.ForeColor = button.Checked ? Color.White : SystemColors.ControlText;
            button.Font = new Font(Font, button.Checked ? FontStyle.Bold : FontStyle.Regular);
            if (button.Checked && !_syncing)
                _app.SetProfile(profile);
        };
        return button;
    }

    void PickColor()
    {
        using var dialog = new ColorDialog { Color = _color, FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _color = dialog.Color;
        _colorButton.BackColor = _color;
    }

    void ApplyLighting()
    {
        if (_aura is null)
            return;
        var effect = SelectedEffect;
        int target = _target.SelectedIndex - 1; // row 0 is "All lighting"
        try
        {
            _aura.Apply(effect, _color, target);
            _cfg.Lighting.Effect = effect;
            _cfg.Lighting.Color = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";
            _cfg.Lighting.Target = target;
            _cfg.Save();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            MessageBox.Show(this, "Could not set the lighting:\n\n" + ex.Message, "FanDeck",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Whoever launched us may have asked for a minimized or hidden start. Undo that once the
        // startup show has finished; this window is only ever opened to be looked at.
        BeginInvoke(() =>
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The close button hides to the tray; fans keep being controlled. Exit is in the tray menu.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyHidden();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>A titled group whose content stacks top to bottom. Returns the inner panel; add the parent to the form.</summary>
    FlowLayoutPanel Section(string title)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Pad(10, 6, 10, 10),
            Margin = Pad(0, 0, 0, 12),
            Dock = DockStyle.Fill,
        };
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        box.Controls.Add(inner);
        return inner;
    }

    string? Prompt(string title, string text, string value)
    {
        using var dialog = new Form
        {
            Text = title,
            Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Pad(12),
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var box = new TextBox { Text = value, Width = Px(280) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = Px(280) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(new Label { Text = text, AutoSize = true });
        layout.Controls.Add(box);
        layout.Controls.Add(buttons);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    int Px(int logical) => LogicalToDeviceUnits(logical);

    Padding Pad(int all) => new(Px(all));

    Padding Pad(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));

    static int Percent(float value) => (int)Math.Clamp(MathF.Round(value), 0, 100);

    static string Temp(float? celsius) => celsius is float c ? $"{c:0} °C" : "n/a";

    static Color ParseColor(string html)
    {
        try { return ColorTranslator.FromHtml(html); }
        catch { return Color.Red; }
    }
}
