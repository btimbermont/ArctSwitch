using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ArctSwitch;

sealed class TrayApp : ApplicationContext
{
    readonly Config _config;
    readonly HeadsetMonitor _monitor = new();
    readonly NotifyIcon _tray;
    readonly Control _ui = new();
    static readonly Color OnColor = Color.FromArgb(255, 82, 0);
    readonly Icon _iconOn = MakeHeadsetIcon(OnColor);
    readonly Icon _iconOff = MakeSpeakerIcon(Color.FromArgb(140, 140, 140));
    Icon? _batteryIcon;      // headset + battery bar, redrawn only when the level changes
    int? _batteryIconLevel;
    readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    readonly ToolStripMenuItem _autoItem = new("Switch automatically") { CheckOnClick = true };
    readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };
    SettingsForm? _settings;
    bool _configured;
    string _lastTooltip = "";

    public TrayApp()
    {
        _ = _ui.Handle; // create the handle so BeginInvoke marshals to this (UI) thread

        _configured = Config.Exists;
        _config = _configured ? Config.Load() : Config.CreateDefault();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Use headset devices now", null, (_, _) => Apply(_config.HeadsetOn, "Headset devices"));
        menu.Items.Add("Use headset-off devices now", null, (_, _) => Apply(_config.HeadsetOff, "Headset-off devices"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add("Open log folder", null, (_, _) => Process.Start("explorer.exe", Log.Dir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) =>
        {
            _autoItem.Checked = _config.AutoSwitch;
            _startupItem.Checked = Autostart.Enabled;
        };
        _autoItem.CheckedChanged += (_, _) =>
        {
            if (_config.AutoSwitch == _autoItem.Checked) return;
            _config.AutoSwitch = _autoItem.Checked;
            if (_configured) _config.Save();
        };
        _startupItem.CheckedChanged += (_, _) => Autostart.Enabled = _startupItem.Checked;

        _tray = new NotifyIcon { Icon = _iconOff, Text = "ArctSwitch", ContextMenuStrip = menu, Visible = true };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

        _monitor.StateChanged += s => _ui.BeginInvoke(() => OnStateChanged(s));
        _monitor.Polled += () => _ui.BeginInvoke(UpdateStatus);
        _monitor.PollIntervalMs = _config.PollIntervalMs;
        _monitor.Start();
        Log.Write("ArctSwitch started");

        if (!_configured) _ui.BeginInvoke(ShowSettings);
    }

    void OnStateChanged(HeadsetState state)
    {
        UpdateStatus();
        if (!_configured || !_config.AutoSwitch) return;
        ApplyForState(state);
    }

    void ApplyForState(HeadsetState state)
    {
        if (state == HeadsetState.On) Apply(_config.HeadsetOn, "Headset on");
        else if (state == HeadsetState.Off) Apply(_config.HeadsetOff, "Headset off");
    }

    List<string> Apply(DeviceProfile profile, string title)
    {
        List<string> failed;
        try { failed = AudioSwitcher.Apply(profile); }
        catch (Exception ex) { failed = new List<string> { ex.Message }; }

        Log.Write($"Applied '{title}'" + (failed.Count > 0 ? $" with errors: {string.Join("; ", failed)}" : ""));
        if (failed.Count > 0)
            _tray.ShowBalloonTip(4000, $"{title}: some devices not set", string.Join("\n", failed), ToolTipIcon.Warning);
        else if (_config.ShowNotifications)
            _tray.ShowBalloonTip(2000, title, Describe(profile), ToolTipIcon.None);
        return failed;
    }

    static string Describe(DeviceProfile p)
    {
        var parts = new[] { p.Playback?.Name, p.Communications?.Name, p.Recording?.Name }
            .Where(n => !string.IsNullOrEmpty(n)).Distinct();
        return string.Join("\n", parts);
    }

    void UpdateStatus()
    {
        string text = !_monitor.DongleFound
            ? "Dongle not found"
            : _monitor.State switch
            {
                HeadsetState.On => "Headset on" + (_monitor.Battery is int b ? $" · battery {b}%" : ""),
                HeadsetState.Off => "Headset off",
                _ => "Detecting…",
            };
        if (text == _lastTooltip) return;
        _lastTooltip = text;
        _statusItem.Text = text;
        _tray.Text = "ArctSwitch – " + text;
        UpdateIcon();
    }

    void UpdateIcon()
    {
        if (_monitor.State != HeadsetState.On) { _tray.Icon = _iconOff; return; }
        if (_monitor.Battery is not int level) { _tray.Icon = _iconOn; return; }

        if (_batteryIcon == null || _batteryIconLevel != level)
        {
            var previous = _batteryIcon;
            _batteryIcon = MakeHeadsetBatteryIcon(OnColor, level);
            _batteryIconLevel = level;
            _tray.Icon = _batteryIcon;
            previous?.Dispose(); // only after the tray has switched to the new icon
        }
        else
        {
            _tray.Icon = _batteryIcon;
        }
    }

    void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsForm(_config, _monitor, p => Apply(p, "Applied"), _iconOn);
        _settings.FormClosed += (_, _) =>
        {
            if (_settings.DialogResult == DialogResult.OK)
            {
                _configured = true;
                if (_config.AutoSwitch) ApplyForState(_monitor.State);
            }
            _settings.Dispose();
            _settings = null;
        };
        _settings.Show();
        _settings.Activate();
    }

    protected override void ExitThreadCore()
    {
        _monitor.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _settings?.Close();
        _batteryIcon?.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
        Log.Write("ArctSwitch exited");
        base.ExitThreadCore();
    }

    // Icons are drawn in code so the app needs no embedded image resources.

    static Icon MakeHeadsetIcon(Color color) => MakeIcon(g => DrawHeadset(g, color));

    /// <summary>Headset shrunk to the left, with a vertical battery bar on the right that fills from the bottom.</summary>
    static Icon MakeHeadsetBatteryIcon(Color color, int level) => MakeIcon(g =>
    {
        g.TranslateTransform(0, 3);
        g.ScaleTransform(0.8f, 0.8f);
        DrawHeadset(g, color);
        g.ResetTransform();

        const float x = 25.5f, top = 4f, width = 6f, height = 24f;
        using var track = new SolidBrush(Color.FromArgb(110, 128, 128, 128));
        g.FillRectangle(track, x, top, width, height);

        level = Math.Clamp(level, 0, 100);
        var fill = level > 50 ? Color.FromArgb(76, 175, 80)
                 : level > 20 ? Color.FromArgb(255, 193, 7)
                 : Color.FromArgb(229, 57, 53);
        float filled = Math.Max(level > 0 ? 2f : 0f, height * level / 100f);
        using var brush = new SolidBrush(fill);
        g.FillRectangle(brush, x, top + height - filled, width, filled);
    });

    static void DrawHeadset(Graphics g, Color color)
    {
        using var pen = new Pen(color, 3.5f);
        g.DrawArc(pen, 5, 4, 22, 24, 180, 180);
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, 3, 15, 7, 13);
        g.FillRectangle(brush, 22, 15, 7, 13);
    }

    static Icon MakeSpeakerIcon(Color color) => MakeIcon(g =>
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, 2, 11, 6, 10);
        g.FillPolygon(brush, new Point[] { new(7, 11), new(15, 4), new(15, 28), new(7, 21) });
        using var pen = new Pen(color, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, 12, 10, 10, 12, -50, 100);
        g.DrawArc(pen, 11, 4, 18, 24, -50, 100);
    });

    static Icon MakeIcon(Action<Graphics> draw)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            draw(g);
        }
        // Icon.FromHandle does not take ownership of the HICON, so clone it into an Icon that does
        // and free the original; disposing the returned Icon then releases everything.
        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(hicon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
