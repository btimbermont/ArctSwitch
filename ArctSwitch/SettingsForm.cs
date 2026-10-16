using NAudio.CoreAudioApi;

namespace ArctSwitch;

sealed class SettingsForm : Form
{
    sealed class Item
    {
        public DeviceRef? Ref { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    sealed class ProfileEditor
    {
        public required ComboBox Playback, Communications, Recording;
    }

    readonly Config _config;
    readonly HeadsetMonitor _monitor;
    readonly Func<DeviceProfile, List<string>> _apply;
    readonly ProfileEditor _on, _off;
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
    readonly CheckBox _autoSwitch = new() { Text = "Switch automatically when the headset turns on or off", AutoSize = true };
    readonly CheckBox _notify = new() { Text = "Show a notification when switching", AutoSize = true };
    readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    readonly NumericUpDown _pollInterval = new()
    {
        Minimum = HeadsetMonitor.MinPollIntervalMs,
        Maximum = 60000,
        Increment = 100,
        Width = 80,
        Margin = new Padding(3, 0, 3, 0),
    };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public SettingsForm(Config config, HeadsetMonitor monitor, Func<DeviceProfile, List<string>> apply, Icon icon)
    {
        _config = config;
        _monitor = monitor;
        _apply = apply;

        Text = "ArctSwitch settings";
        Icon = icon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(_status);

        var onBox = BuildProfileBox("When the headset is ON", out _on);
        var offBox = BuildProfileBox("When the headset is OFF", out _off);
        root.Controls.Add(onBox);
        root.Controls.Add(offBox);

        var options = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
        var pollRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        pollRow.Controls.Add(new Label { Text = "Check headset status every", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 0) });
        pollRow.Controls.Add(_pollInterval);
        pollRow.Controls.Add(new Label { Text = $"ms (minimum {HeadsetMonitor.MinPollIntervalMs})", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 3, 0) });
        options.Controls.AddRange(new Control[] { _autoSwitch, _notify, _autostart, pollRow });
        root.Controls.Add(options);

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var refresh = new Button { Text = "Refresh devices", AutoSize = true };
        refresh.Click += (_, _) => { var on = Read(_on); var off = Read(_off); Fill(_on, on); Fill(_off, off); };
        save.Click += (_, _) => SaveAndClose();

        var buttons = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(refresh, 0, 0);
        buttons.Controls.Add(save, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        Fill(_on, config.HeadsetOn);
        Fill(_off, config.HeadsetOff);
        _autoSwitch.Checked = config.AutoSwitch;
        _notify.Checked = config.ShowNotifications;
        _autostart.Checked = Autostart.Enabled;
        _pollInterval.Value = Math.Clamp(config.PollIntervalMs, (int)_pollInterval.Minimum, (int)_pollInterval.Maximum);

        UpdateStatus();
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
    }

    GroupBox BuildProfileBox(string title, out ProfileEditor editor)
    {
        var box = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(3, 3, 3, 8) };
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        ComboBox Row(string label)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, Margin = new Padding(3) };
            grid.Controls.Add(l);
            grid.Controls.Add(c);
            return c;
        }

        editor = new ProfileEditor
        {
            Playback = Row("Sound output (games, music):"),
            Communications = Row("Communications output (chat):"),
            Recording = Row("Microphone:"),
        };

        var ed = editor;
        var test = new Button { Text = "Apply now", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 6, 3, 0) };
        test.Click += (_, _) =>
        {
            var failed = _apply(Read(ed));
            if (failed.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, failed), "Some devices could not be set", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        grid.Controls.Add(new Label());
        grid.Controls.Add(test);

        box.Controls.Add(grid);
        return box;
    }

    static void Fill(ProfileEditor ed, DeviceProfile p)
    {
        var render = AudioSwitcher.List(DataFlow.Render);
        var capture = AudioSwitcher.List(DataFlow.Capture);
        Fill(ed.Playback, render, p.Playback);
        Fill(ed.Communications, render, p.Communications);
        Fill(ed.Recording, capture, p.Recording);
    }

    static void Fill(ComboBox combo, List<AudioEndpoint> devices, DeviceRef? selected)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.Add(new Item { Text = "(leave unchanged)" });
        foreach (var d in devices) combo.Items.Add(new Item { Ref = DeviceRef.From(d), Text = d.Name });

        int index = 0;
        if (selected != null && !string.IsNullOrEmpty(selected.Id))
        {
            index = devices.FindIndex(d => d.Id == selected.Id) + 1;
            if (index == 0)
            {
                // Remember a configured device even while it is unplugged/disabled.
                combo.Items.Add(new Item { Ref = selected, Text = $"{selected.Name} (not connected)" });
                index = combo.Items.Count - 1;
            }
        }
        combo.SelectedIndex = index;
        combo.EndUpdate();
    }

    static DeviceProfile Read(ProfileEditor ed) => new()
    {
        Playback = (ed.Playback.SelectedItem as Item)?.Ref,
        Communications = (ed.Communications.SelectedItem as Item)?.Ref,
        Recording = (ed.Recording.SelectedItem as Item)?.Ref,
    };

    void UpdateStatus()
    {
        _status.Text = !_monitor.DongleFound
            ? "Status: Arctis wireless dongle not found"
            : _monitor.State switch
            {
                HeadsetState.On => $"Status: headset ON" + (_monitor.Battery is int b ? $" (battery {b}%)" : ""),
                HeadsetState.Off => "Status: headset OFF",
                _ => "Status: detecting…",
            };
    }

    void SaveAndClose()
    {
        _config.HeadsetOn = Read(_on);
        _config.HeadsetOff = Read(_off);
        _config.AutoSwitch = _autoSwitch.Checked;
        _config.ShowNotifications = _notify.Checked;
        _config.PollIntervalMs = (int)_pollInterval.Value;
        _monitor.PollIntervalMs = _config.PollIntervalMs;
        try
        {
            _config.Save();
            Autostart.Enabled = _autostart.Checked;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
