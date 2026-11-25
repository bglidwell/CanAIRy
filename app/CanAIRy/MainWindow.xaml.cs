using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace CanAIRy;

public partial class MainWindow : Window
{
    private readonly BackendService _backend = new();
    private readonly ObservableCollection<Receiver> _receivers = [];
    private readonly ObservableCollection<CaptureSource> _sources = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _appIcon;
    private bool _allowExit;
    private bool _stopRequested;
    private StreamOptions? _activeOptions;
    private bool _refreshingDevices;
    private bool _devicesVerified;
    private bool _applyingPreferences;
    private CancellationTokenSource? _qualityLifetime;
    private int _previewGeneration;

    public MainWindow()
    {
        InitializeComponent();
        ThemeService.Attach(this);
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Version 1.0" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        ReceiverBox.ItemsSource = _receivers;
        SourceBox.ItemsSource = _sources;

        StartInTrayCheck.IsChecked = _settings.StartInTray;
        AutomaticUpdatesCheck.IsChecked = _settings.AutomaticUpdates;
        LaunchAtSignInCheck.IsChecked = SettingsStore.LaunchAtSignIn;
        FpsBox.SelectedIndex = _settings.FramesPerSecond == 60 ? 1 : 0;
        BitrateBox.SelectedIndex = _settings.BitrateKbps switch { 4500 => 1, 8000 => 2, _ => 0 };
        CursorCheck.IsChecked = _settings.ShowCursor;
        PresentationModeCheck.IsChecked = _settings.PresentationMode;

        _appIcon = LoadAppIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = ProductInfo.DisplayName,
            Icon = _appIcon,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowSettings);
        _trayIcon.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
        _backend.LogReceived += line => Dispatcher.Invoke(() => AppendLog(line));
        _backend.StatusChanged += status => Dispatcher.Invoke(() => SetStatus(status));
        _backend.PinRequested += RequestPinAsync;
        _backend.SenderExited += exit => Dispatcher.InvokeAsync(() => SenderExitedAsync(exit));
        Loaded += async (_, _) =>
        {
            RefreshSources();

            await RefreshDevicesAsync();
            if (_settings.StartInTray && Environment.GetCommandLineArgs().Contains("--tray")) Hide();
            _ = RunUpdateLoopAsync();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Hide();
        };
        Closing += (_, e) =>
        {
            if (_allowExit) return;
            e.Cancel = true;
            Hide();
        };
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon)
                return icon;
        }
        catch { }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshingDevices || _activeOptions is not null || _backend.IsRunning) return;
        _refreshingDevices = true;
        _devicesVerified = false;
        try
        {
            SetStatus("Discovering...");
            var selectedId = _settings.SelectedDeviceId;
            var found = await _backend.DiscoverAsync(_lifetime.Token);
            _receivers.Clear();
            foreach (var receiver in found) _receivers.Add(receiver);
            ReceiverBox.SelectedItem = string.IsNullOrWhiteSpace(selectedId) ? null :
                _receivers.FirstOrDefault(item => item.DeviceId == selectedId);
            _devicesVerified = true;
            ApplySharingPreferences();
            SetStatus(found.Count == 0 ? "No Apple TVs found" : "Ready");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("Discovery failed");
            AppendLog(ex.Message);
        }
        finally { _refreshingDevices = false; }
        UpdateButtons();
    }

    private void RefreshSources()
    {
        var previous = SourceBox.SelectedItem as CaptureSource;
        _sources.Clear();
        foreach (var source in WindowCatalog.GetSources().Where(s => CastingPolicy.CanChooseSource(PresentationModeCheck.IsChecked == true, s.IsDesktop))) _sources.Add(source);
        SourceBox.SelectedItem = previous is null ? null : _sources.FirstOrDefault(item =>
            item.WindowHandle == previous.WindowHandle && item.WindowTitle == previous.WindowTitle);
        if (SourceBox.SelectedItem is null) ClearPreview();
    }

    private void ClearPreview()
    {
        Interlocked.Increment(ref _previewGeneration);
        PreviewTitle.Text = "Choose a source";
        SelectedSourceSubtitle.Text = "Select what to share";
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }
    private async Task UpdatePreviewAsync(CaptureSource source)
    {
        var generation = Interlocked.Increment(ref _previewGeneration);
        PreviewTitle.Text = source.DisplayTitle;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        try
        {
            var thumbnail = await Task.Run(() => WindowCatalog.CaptureThumbnail(source, 320, 180), _lifetime.Token);
            if (generation != _previewGeneration || SourceBox.SelectedItem != source) return;
            PreviewImage.Source = thumbnail;
            PreviewPlaceholder.Visibility = thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
    }

    private StreamOptions SelectedOptions(bool forcePair)
    {
        var receiver = ReceiverBox.SelectedItem as Receiver ?? throw new InvalidOperationException("Select an Apple TV");
        var source = SourceBox.SelectedItem as CaptureSource ?? throw new InvalidOperationException("Select what to share");
        if (!_devicesVerified || _refreshingDevices || !_receivers.Contains(receiver))
            throw new InvalidOperationException("Refresh and select an available Apple TV first.");
        if (!CastingPolicy.CanChooseSource(PresentationModeCheck.IsChecked == true, source.IsDesktop))
            throw new InvalidOperationException("Presentation mode only permits an individual window.");
        if (!source.IsDesktop)
        {
            var windows = WindowCatalog.GetSources().Select(s => (s.WindowTitle, s.WindowHandle, s.AppName));
            if (!CastingPolicy.MatchesWindow(source.WindowTitle, source.WindowHandle, source.AppName, windows))
                throw new InvalidOperationException("This window has changed or its title is ambiguous. Choose a uniquely titled window before casting.");
        }
        var fps = int.Parse(((ComboBoxItem)FpsBox.SelectedItem).Tag.ToString()!);
        var bitrate = int.Parse(((ComboBoxItem)BitrateBox.SelectedItem).Tag.ToString()!);
        return new StreamOptions(receiver, source, fps, bitrate, CursorCheck.IsChecked == true, forcePair);
    }

    private async Task StartAsync(bool forcePair)
    {
        try
        {
            LogBox.Clear();
            await StartSessionAsync(SelectedOptions(forcePair));
            UpdateButtons();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, ProductInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Ready");
        }
    }

    private async Task StartSessionAsync(StreamOptions options)
    {
        if (_applyingUpdate) throw new InvalidOperationException("CanAIRy is installing an update.");
        if (_activeOptions is not null || _backend.IsRunning) throw new InvalidOperationException("A cast is already active.");
        _stopRequested = false;
        _activeOptions = options with { ForcePair = false };
        UpdateButtons();
        try
        {
            SaveSettings();
            await _backend.StartAsync(options, _lifetime.Token);
        }
        catch { _activeOptions = null; UpdateButtons(); throw; }
        UpdateButtons();
    }

    private Task SenderExitedAsync(int exitCode)
    {
        if (_allowExit) return Task.CompletedTask;
        var interrupted = CastingPolicy.WasInterrupted(_stopRequested, _activeOptions is not null);
        _activeOptions = null;
        StopQualityMonitor();
        if (interrupted)
        {
            // Capture never resumes automatically, even for a previously paired device.
            SourceBox.SelectedItem = null;
            _devicesVerified = false;
            SetStatus("Connection lost - select again");
            AppendLog("Cast ended. Refresh the receiver and choose a source to reconnect.");
            _trayIcon.ShowBalloonTip(5000, ProductInfo.DisplayName, "Cast ended. Open CanAIRy to reconnect.", Forms.ToolTipIcon.Warning);
        }
        else SetStatus("Ready");
        UpdateButtons();
        return Task.CompletedTask;
    }

    private void StopQualityMonitor()
    {
        _qualityLifetime?.Cancel();
        _qualityLifetime = null;
        NetworkQualityText.Text = "Network quality: not casting";
    }

    private async Task MonitorQualityAsync(StreamOptions options)
    {
        if (_qualityLifetime is not null) return;
        if (!IPAddress.TryParse(options.Receiver.Ip, out var address))
        {
            NetworkQualityText.Text = "Network quality: unavailable";
            return;
        }
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _qualityLifetime = lifetime;
        using var ping = new Ping();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                string status;
                try
                {
                    var reply = await ping.SendPingAsync(address, 1000);
                    status = reply.Status == IPStatus.Success
                        ? CastingPolicy.FormatRoundTrip(reply.RoundtripTime)
                        : CastingPolicy.FormatRoundTrip(null);
                }
                catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Security.SecurityException) { status = "Receiver RTT: unavailable"; }
                if (lifetime.IsCancellationRequested || _qualityLifetime != lifetime) return;
                NetworkQualityText.Text = status;
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_qualityLifetime == lifetime) _qualityLifetime = null;
            lifetime.Dispose();
        }
    }
    private Task<string?> RequestPinAsync()
    {
        return Dispatcher.InvokeAsync(() =>
        {
            var window = new PinWindow { Owner = this };
            return window.ShowDialog() == true ? window.Pin : null;
        }).Task;
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        if (status == "Mirroring" && _activeOptions is { } casting && !_stopRequested)
            _ = MonitorQualityAsync(casting);
        if (status is "Connection lost" or "Disconnected") StopQualityMonitor();
        _trayIcon.Text = status == "Mirroring" ? "CanAIRy: casting" : ProductInfo.DisplayName;
        var brushKey = status switch
        {
            "Mirroring" => "SuccessBrush",
            "Connection lost" or "Connection lost - select again" or "Disconnected" or "Discovery failed" => "DangerBrush",
            "Ready" => "AccentBrush",
            _ => "WarningBrush"
        };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        UpdateButtons();
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void UpdateButtons()
    {
        if (!IsLoaded) return;
        var active = _backend.IsRunning || _activeOptions is not null;
        StartButton.IsEnabled = CastingPolicy.CanStart(_devicesVerified, _refreshingDevices, active, _applyingUpdate, ReceiverBox.SelectedItem is not null, SourceBox.SelectedItem is not null);
        PairButton.IsEnabled = StartButton.IsEnabled;
        StopButton.IsEnabled = _backend.IsRunning;
        ReceiverBox.IsEnabled = !active && !_refreshingDevices;
        SourceBox.IsEnabled = !active;
        FpsBox.IsEnabled = !active;
        BitrateBox.IsEnabled = !active;
        CursorCheck.IsEnabled = !active;
        PresentationModeCheck.IsEnabled = !active;
    }

    private void ApplySharingPreferences()
    {
        if (ReceiverBox.SelectedItem is not Receiver receiver || string.IsNullOrWhiteSpace(receiver.DeviceId) ||
            !_settings.ReceiverPreferences.TryGetValue(receiver.DeviceId, out var preferences)) return;
        _applyingPreferences = true;
        try
        {
            FpsBox.SelectedIndex = preferences.FramesPerSecond == 60 ? 1 : 0;
            BitrateBox.SelectedIndex = preferences.BitrateKbps switch { 4500 => 1, 8000 => 2, _ => 0 };
            CursorCheck.IsChecked = preferences.ShowCursor;
        }
        finally { _applyingPreferences = false; }
    }

    private void SaveSettings()
    {
        if (_refreshingDevices || _applyingPreferences || !IsLoaded) return;
        _settings.FramesPerSecond = FpsBox.SelectedIndex == 1 ? 60 : 30;
        _settings.BitrateKbps = BitrateBox.SelectedIndex switch { 1 => 4500, 2 => 8000, _ => 0 };
        _settings.ShowCursor = CursorCheck.IsChecked == true;
        _settings.PresentationMode = PresentationModeCheck.IsChecked == true;
        if (ReceiverBox.SelectedItem is Receiver receiver && !string.IsNullOrWhiteSpace(receiver.DeviceId))
        {
            _settings.SelectedDeviceId = receiver.DeviceId;
            _settings.ReceiverPreferences[receiver.DeviceId] = new(
                _settings.FramesPerSecond, _settings.BitrateKbps, _settings.ShowCursor);
        }
        _settings.KnownReceivers = _receivers.ToList();
        _settings.StartInTray = StartInTrayCheck.IsChecked == true;
        _settings.AutomaticUpdates = AutomaticUpdatesCheck.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private void SharingPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    private void PresentationMode_Click(object sender, RoutedEventArgs e)
    {
        if (_activeOptions is not null || _backend.IsRunning) return;
        SourceBox.SelectedItem = null;
        RefreshSources();
        SaveSettings();
        UpdateButtons();
    }
    private void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void BuildTrayMenu()
    {
        var menu = _trayIcon.ContextMenuStrip!;
        menu.Items.Clear();

        var settings = new Forms.ToolStripMenuItem("Open settings") { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        settings.Click += (_, _) => Dispatcher.Invoke(ShowSettings);
        menu.Items.Add(settings);

        var devices = new Forms.ToolStripMenuItem("Apple TV") { Enabled = _activeOptions is null && !_backend.IsRunning && _devicesVerified && !_refreshingDevices };
        foreach (var receiver in _receivers)
        {
            var item = new Forms.ToolStripMenuItem(receiver.Name)
            {
                Checked = receiver.DeviceId == _settings.SelectedDeviceId
            };
            item.Click += (_, _) => Dispatcher.Invoke(() =>
            {
                ReceiverBox.SelectedItem = receiver;
                SaveSettings();
            });
            devices.DropDownItems.Add(item);
        }
        if (devices.DropDownItems.Count == 0) devices.DropDownItems.Add("No Apple TVs found").Enabled = false;
        menu.Items.Add(devices);

        var share = new Forms.ToolStripMenuItem("Start mirroring");
        if (PresentationModeCheck.IsChecked != true) AddTraySource(share, new CaptureSource { Name = "Entire desktop" });
        var windows = WindowCatalog.GetSources().Where(source => !source.IsDesktop).Take(25).ToArray();
        if (windows.Length > 0)
        {
            share.DropDownItems.Add(new Forms.ToolStripSeparator());
            foreach (var source in windows) AddTraySource(share, source);
        }
        share.Enabled = !_applyingUpdate && !_backend.IsRunning && _activeOptions is null && _devicesVerified && !_refreshingDevices && ReceiverBox.SelectedItem is Receiver;
        menu.Items.Add(share);

        var stop = new Forms.ToolStripMenuItem("Stop mirroring") { Enabled = _backend.IsRunning };
        stop.Click += async (_, _) => await Dispatcher.InvokeAsync(StopSessionAsync);
        menu.Items.Add(stop);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var exit = new Forms.ToolStripMenuItem("Exit");
        exit.Click += async (_, _) => await Dispatcher.InvokeAsync(ExitAsync);
        menu.Items.Add(exit);
    }

    private void AddTraySource(Forms.ToolStripMenuItem parent, CaptureSource source)
    {
        var label = source.IsDesktop ? source.Name : source.Name.Split("  ·  ")[0];
        if (label.Length > 70) label = label[..67] + "…";
        var item = new Forms.ToolStripMenuItem(label);
        item.Click += async (_, _) => await Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_activeOptions is not null || _backend.IsRunning) return;
                RefreshSources();
                SourceBox.SelectedItem = _sources.FirstOrDefault(candidate =>
                    candidate.WindowHandle == source.WindowHandle && candidate.WindowTitle == source.WindowTitle);
                await StartSessionAsync(SelectedOptions(false));
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, ProductInfo.DisplayName); }
        });
        parent.DropDownItems.Add(item);
    }

    private async Task StopSessionAsync()
    {
        _stopRequested = true;
        StopQualityMonitor();
        await _backend.StopAsync();
        UpdateButtons();
    }

    private async Task ExitAsync()
    {
        _allowExit = true;
        _lifetime.Cancel();
        _updateClient.Dispose();
        await StopSessionAsync();
        await _backend.DisposeAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private void RefreshSources_Click(object sender, RoutedEventArgs e) { if (_activeOptions is null && !_backend.IsRunning) RefreshSources(); UpdateButtons(); }
    private void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeOptions is not null || _backend.IsRunning) return;
            var available = WindowCatalog.GetSources().Where(s => CastingPolicy.CanChooseSource(PresentationModeCheck.IsChecked == true, s.IsDesktop)).ToArray();
            var picker = new SourcePickerWindow(available, SourceBox.SelectedItem as CaptureSource, PresentationModeCheck.IsChecked == true) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedSource is not { } selected) return;

            _sources.Clear();
            foreach (var source in available) _sources.Add(source);
            if (!_sources.Any(source => source.IsDesktop == selected.IsDesktop && source.WindowHandle == selected.WindowHandle))
                _sources.Add(selected);
            SourceBox.SelectedItem = _sources.First(source => source.IsDesktop == selected.IsDesktop && source.WindowHandle == selected.WindowHandle);
            UpdateButtons();
        }
        catch (Exception ex)
        {
            AppendLog("Source picker failed: " + ex);
            System.Windows.MessageBox.Show(this, ex.Message, ProductInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await StartAsync(false);
    private async void Pair_Click(object sender, RoutedEventArgs e) => await StartAsync(true);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();
    private void Preference_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.LaunchAtSignIn = LaunchAtSignInCheck.IsChecked == true;
        SaveSettings();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == ReceiverBox)
        {
            if (ReceiverBox.SelectedItem is Receiver receiver)
            {
                ReceiverDetail.Text = $"{receiver.Ip}:{receiver.Port}  ·  {receiver.DeviceId}";
                ApplySharingPreferences();
            }
            else ReceiverDetail.Text = "Select an available receiver";
        }
        if (sender == SourceBox)
        {
            if (SourceBox.SelectedItem is CaptureSource source)
            {
                SelectedSourceSubtitle.Text = source.Subtitle;
                _ = UpdatePreviewAsync(source);
            }
            else ClearPreview();
        }
        SaveSettings();
        UpdateButtons();
    }
}