using System.Diagnostics;
using FanDeck.Cooling;
using FanDeck.Hardware;
using Microsoft.Win32;

namespace FanDeck.UI;

/// <summary>
/// Owns the hardware, the fan loop, the tray icon and the window. Lives until Exit.
/// Fan control can be switched off at runtime (game-safe mode), so the hardware and loop come and go.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    // FACEIT's app, its anti-cheat client and its service.
    static readonly string[] FaceitProcesses = ["FACEIT", "faceitclient", "faceitservice"];

    readonly AppConfig _cfg = AppConfig.Load();
    readonly AuraUsb? _aura;
    readonly NotifyIcon _tray = new();
    readonly MainForm _form;
    readonly EventWaitHandle _showSignal;
    readonly RegisteredWaitHandle _showWait;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly Dictionary<Profile, ToolStripMenuItem> _profileItems = [];
    readonly ToolStripMenuItem _autostartItem;
    readonly ToolStripMenuItem _giveBackItem;
    readonly ToolStripMenuItem _gameSafeItem;
    readonly ToolStripMenuItem _faceitItem;
    FanHardware? _hw;
    FanLoop? _loop;
    Icon? _icon;
    IntPtr _iconHandle;
    string? _iconKey;
    int _ticks;
    bool _configDirty;
    bool _hiddenTipShown;
    bool _shutDown;
    bool _switching;
    bool? _failedSwitchTarget;

    public bool FanServiceRunning { get; private set; }
    public bool LightingServiceRunning { get; private set; }
    public bool FaceitRunning { get; private set; }

    /// <summary>FanDeck has stepped aside: fans are on the BIOS curve and the PawnIO driver is unloaded.</summary>
    public bool GameSafe { get; private set; }

    /// <summary>Why a switch into or out of game-safe mode failed, if it did.</summary>
    public string? GameSafeError { get; private set; }

    public FanLoop? Loop => _loop;
    public AppConfig Config => _cfg;

    public TrayApp(bool startInTray)
    {
        _aura = AuraUsb.TryOpen(out string? auraError);
        Program.EmergencyRelease = () =>
        {
            var hw = _hw;
            if (hw is null)
                return;
            try { lock (hw) hw.ReleaseAll(); }
            catch { /* already going down */ }
        };
        RefreshServices();
        FaceitRunning = DetectFaceit();

        // Start in the state the settings ask for. Both paths run before the window exists, so they are synchronous.
        if (WantGameSafe())
        {
            GameSafe = true;
            TrySwitchDriver(load: false);
        }
        else
        {
            if (PawnIoDriver.IsDisabled)
                TrySwitchDriver(load: true); // left unloaded by an earlier game-safe session
            StartFanControl(new FanHardware());
        }

        _form = new MainForm(_aura, auraError, this);
        _ = _form.Handle; // so BeginInvoke works before the window is first shown

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open FanDeck", null, (_, _) => ShowWindow())
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var profile in Profiles.All)
        {
            var item = new ToolStripMenuItem(profile.ToString(), null, (_, _) => SetProfile(profile));
            _profileItems[profile] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        _gameSafeItem = new ToolStripMenuItem("Game-safe mode (unload driver)", null, (_, _) => SetGameSafe(!_cfg.GameSafe));
        _faceitItem = new ToolStripMenuItem("Game-safe automatically while FACEIT runs", null, (_, _) =>
        {
            _cfg.AutoGameSafeForFaceit = !_cfg.AutoGameSafeForFaceit;
            _cfg.Save();
            UpdateGameSafe();
        });
        menu.Items.Add(_gameSafeItem);
        menu.Items.Add(_faceitItem);
        menu.Items.Add(new ToolStripSeparator());
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());
        _giveBackItem = new ToolStripMenuItem("Give back to Armoury Crate", null, async (_, _) => await GiveBackAsync());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(_giveBackItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
        menu.Opening += (_, _) =>
        {
            _autostartItem.Checked = Autostart.IsEnabled();
            _giveBackItem.Visible = _cfg.ServiceBackups.Count > 0;
            _gameSafeItem.Checked = _cfg.GameSafe;
            _faceitItem.Checked = _cfg.AutoGameSafeForFaceit;
        };

        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };
        UpdateTray(null);
        _tray.Visible = true;

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal, (_, _) => _form.BeginInvoke(new Action(ShowWindow)), null, Timeout.Infinite, false);

        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += (_, _) => Shutdown();

        if (!startInTray)
            ShowWindow();
    }

    public void SetProfile(Profile profile)
    {
        _cfg.Profile = profile;
        _cfg.Save();
        _loop?.Poke();
        _form.ShowSnapshot(_loop?.Latest);
        UpdateTray(_loop?.Latest);
    }

    /// <summary>Turns game-safe mode on or off by hand (it can also come on by itself while FACEIT runs).</summary>
    public void SetGameSafe(bool on)
    {
        _cfg.GameSafe = on;
        _cfg.Save();
        _failedSwitchTarget = null;
        UpdateGameSafe();
    }

    /// <summary>Slider moves mark the config dirty; it is saved on the next tick rather than on every pixel.</summary>
    public void MarkDirty() => _configDirty = true;

    public async Task TakeControlAsync()
    {
        try
        {
            await Task.Run(() => AsusServices.TakeOver(_cfg));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            MessageBox.Show(_form, "Could not stop Armoury Crate's services:\n\n" + ex.Message, "FanDeck",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshServices();
        _loop?.CheckServicesNow();
        _form.ShowSnapshot(_loop?.Latest);
    }

    public async Task GiveBackAsync()
    {
        try
        {
            await Task.Run(() => AsusServices.GiveBack(_cfg));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            MessageBox.Show(_form, "Could not restore Armoury Crate's services:\n\n" + ex.Message, "FanDeck",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshServices();
        _loop?.CheckServicesNow();
        _form.ShowSnapshot(_loop?.Latest);
    }

    public void NotifyHidden()
    {
        if (_hiddenTipShown)
            return;
        _hiddenTipShown = true;
        _tray.ShowBalloonTip(3000, "FanDeck is still running",
            "It keeps controlling your fans from the tray. Right-click the icon to exit.", ToolTipIcon.Info);
    }

    bool WantGameSafe() => _cfg.GameSafe || (_cfg.AutoGameSafeForFaceit && FaceitRunning);

    /// <summary>Moves into or out of game-safe mode when the wanted state changed. Runs the slow driver work off the UI thread.</summary>
    async void UpdateGameSafe()
    {
        bool want = WantGameSafe();
        if (_switching || _shutDown || want == GameSafe || want == _failedSwitchTarget)
            return;

        _switching = true;
        GameSafeError = null;
        try
        {
            if (want)
            {
                StopFanControl(); // fans back to the BIOS before the driver goes
                GameSafe = true;
                await Task.Run(() => PawnIoDriver.Unload());
                if (!_cfg.GameSafe)
                    _tray.ShowBalloonTip(4000, "FACEIT detected",
                        "FanDeck stepped aside: its driver is unloaded and the BIOS runs your fans until FACEIT closes.",
                        ToolTipIcon.Info);
            }
            else
            {
                await Task.Run(() => PawnIoDriver.Load());
                var hw = await Task.Run(() => new FanHardware());
                if (_shutDown)
                {
                    hw.Dispose();
                    return;
                }
                StartFanControl(hw);
                GameSafe = false;
            }
            _failedSwitchTarget = null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            GameSafeError = ex.Message;
            _failedSwitchTarget = want; // don't retry every second; try again when the wanted state changes
        }
        finally
        {
            _switching = false;
        }
        _form.ShowSnapshot(_loop?.Latest);
        UpdateTray(_loop?.Latest);
    }

    /// <summary>Used at startup, where blocking for a second is fine.</summary>
    void TrySwitchDriver(bool load)
    {
        try
        {
            if (load)
                PawnIoDriver.Load();
            else
                PawnIoDriver.Unload();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            GameSafeError = ex.Message;
        }
    }

    void StartFanControl(FanHardware hw)
    {
        _hw = hw;
        _loop = new FanLoop(hw, _cfg);
    }

    /// <summary>Hands every fan back to the BIOS and lets go of the driver.</summary>
    void StopFanControl()
    {
        _loop?.Dispose();
        _hw?.Dispose();
        _loop = null;
        _hw = null;
    }

    void OnTick()
    {
        if (++_ticks % 5 == 0)
            RefreshServices();
        if (_ticks % 2 == 0)
        {
            FaceitRunning = DetectFaceit();
            UpdateGameSafe();
        }

        var snapshot = _loop?.Latest;
        UpdateTray(snapshot);
        if (_form.Visible)
            _form.ShowSnapshot(snapshot);
        if (_configDirty)
        {
            _configDirty = false;
            _cfg.Save();
        }
    }

    void RefreshServices()
    {
        FanServiceRunning = AsusServices.IsRunning(AsusServices.FanService);
        LightingServiceRunning = AsusServices.IsRunning(AsusServices.LightingService);
    }

    /// <summary>Looks only at process names from the system list; never opens a handle to any game or anti-cheat.</summary>
    static bool DetectFaceit()
    {
        foreach (string name in FaceitProcesses)
        {
            var found = Process.GetProcessesByName(name);
            bool any = found.Length > 0;
            foreach (var p in found)
                p.Dispose();
            if (any)
                return true;
        }
        return false;
    }

    void ShowWindow()
    {
        _form.ShowSnapshot(_loop?.Latest);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    void UpdateTray(LoopSnapshot? snapshot)
    {
        var profile = _cfg.Profile;
        foreach (var (p, item) in _profileItems)
            item.Checked = p == profile;

        string key = GameSafe ? "game-safe" : profile.ToString();
        if (_iconKey != key)
        {
            var color = GameSafe ? Icons.GameSafe : Icons.ColorFor(profile);
            SetIcon(Icons.Fan(color, out IntPtr handle), handle);
            _iconKey = key;
        }

        string cpu = snapshot?.CpuTemp is float c ? $" · CPU {c:0}°C" : "";
        string mode = GameSafe ? "Game-safe (BIOS runs the fans)"
            : FanServiceRunning ? "Armoury Crate in control"
            : profile.ToString();
        _tray.Text = $"FanDeck · {mode}{cpu}";
    }

    void SetIcon(Icon icon, IntPtr handle)
    {
        _tray.Icon = icon;
        _form.Icon = icon;
        _icon?.Dispose();
        if (_iconHandle != IntPtr.Zero)
            Icons.Destroy(_iconHandle);
        _icon = icon;
        _iconHandle = handle;
    }

    void ToggleAutostart()
    {
        try
        {
            if (Autostart.IsEnabled())
                Autostart.Disable();
            else
                Autostart.Enable();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            MessageBox.Show("Could not change the startup task:\n\n" + ex.Message, "FanDeck",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // The chip can come back from sleep on its BIOS settings; re-apply straight away.
        if (e.Mode == PowerModes.Resume)
            _loop?.Poke();
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Shutdown();

    void Exit()
    {
        Shutdown();
        _form.AllowClose();
        _form.Close();
        ExitThread();
    }

    /// <summary>Hands every fan back to the BIOS and releases the hardware. Safe to call more than once.</summary>
    void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;

        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _showWait.Unregister(null);
        StopFanControl();
        _aura?.Dispose();
        _tray.Visible = false;
        _cfg.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _tray.Dispose();
            _timer.Dispose();
            _showSignal.Dispose();
        }
        base.Dispose(disposing);
    }
}
