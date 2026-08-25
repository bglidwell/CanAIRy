using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private int _reconnectAttempt;

    public MainWindow()
    {
        InitializeComponent();
        ReceiverBox.ItemsSource = _receivers;
        SourceBox.ItemsSource = _sources;
        foreach (var receiver in _settings.KnownReceivers) _receivers.Add(receiver);
        StartInTrayCheck.IsChecked = _settings.StartInTray;
        LaunchAtSignInCheck.IsChecked = SettingsStore.LaunchAtSignIn;
        FpsBox.SelectedIndex = _settings.FramesPerSecond == 60 ? 1 : 0;
        BitrateBox.SelectedIndex = _settings.BitrateKbps switch { 4500 => 1, 8000 => 2, _ => 0 };
        CursorCheck.IsChecked = _settings.ShowCursor;

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
            ReceiverBox.SelectedItem = _receivers.FirstOrDefault(item => item.DeviceId == _settings.SelectedDeviceId);
            await RefreshDevicesAsync();
            if (_settings.StartInTray && Environment.GetCommandLineArgs().Contains("--tray")) Hide();
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
        try
        {
            SetStatus("Discovering…");
            var selectedId = (ReceiverBox.SelectedItem as Receiver)?.DeviceId;
            var found = await _backend.DiscoverAsync(_lifetime.Token);
            _receivers.Clear();
            foreach (var receiver in found) _receivers.Add(receiver);
            ReceiverBox.SelectedItem = _receivers.FirstOrDefault(item => item.DeviceId == selectedId)
                                       ?? _receivers.FirstOrDefault(item => item.Name.Contains("Great Room", StringComparison.OrdinalIgnoreCase))
                                       ?? _receivers.FirstOrDefault();
            SetStatus(found.Count == 0 ? "No Apple TVs found" : "Ready");
            SaveSettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("Discovery failed");
            AppendLog(ex.Message);
        }
        UpdateButtons();
    }

    private void RefreshSources()
    {
        var selectedTitle = (SourceBox.SelectedItem as CaptureSource)?.WindowTitle;
        _sources.Clear();
        foreach (var source in WindowCatalog.GetSources()) _sources.Add(source);
        SourceBox.SelectedItem = _sources.FirstOrDefault(item => item.WindowTitle == selectedTitle) ?? _sources.FirstOrDefault();
    }

    private StreamOptions SelectedOptions(bool forcePair)
    {
        var receiver = ReceiverBox.SelectedItem as Receiver ?? throw new InvalidOperationException("Select an Apple TV");
        var source = SourceBox.SelectedItem as CaptureSource ?? throw new InvalidOperationException("Select what to share");
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
        _stopRequested = false;
        _activeOptions = options with { ForcePair = false };
        await _backend.StartAsync(options, _lifetime.Token);
        UpdateButtons();
    }

    private async Task SenderExitedAsync(int exitCode)
    {
        UpdateButtons();
        if (_stopRequested || _activeOptions is null || exitCode == 0) return;
        if (_reconnectAttempt >= 4)
        {
            SetStatus("Connection lost");
            AppendLog("Automatic reconnect stopped after four attempts.");
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Pow(2, _reconnectAttempt));
        _reconnectAttempt++;
        SetStatus($"Reconnecting in {delay.TotalSeconds:0}s…");
        try
        {
            await Task.Delay(delay, _lifetime.Token);
            await _backend.StartAsync(_activeOptions, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendLog("Reconnect failed: " + ex.Message);
            await SenderExitedAsync(1);
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
        StatusDot.Fill = status switch
        {
            "Mirroring" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 183, 106)),
            "Connection lost" or "Disconnected" or "Discovery failed" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 68, 56)),
            "Ready" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 107, 254)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 144, 9))
        };
        UpdateButtons();
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void UpdateButtons()
    {
        var running = _backend.IsRunning;
        StartButton.IsEnabled = !running && ReceiverBox.SelectedItem is not null && SourceBox.SelectedItem is not null;
        PairButton.IsEnabled = StartButton.IsEnabled;
        StopButton.IsEnabled = running;
        ReceiverBox.IsEnabled = !running;
        SourceBox.IsEnabled = !running;
    }

    private void SaveSettings()
    {
        _settings.SelectedDeviceId = (ReceiverBox.SelectedItem as Receiver)?.DeviceId;
        _settings.KnownReceivers = _receivers.ToList();
        _settings.FramesPerSecond = FpsBox.SelectedIndex == 1 ? 60 : 30;
        _settings.BitrateKbps = BitrateBox.SelectedIndex switch { 1 => 4500, 2 => 8000, _ => 0 };
        _settings.ShowCursor = CursorCheck.IsChecked == true;
        _settings.StartInTray = StartInTrayCheck.IsChecked == true;
        SettingsStore.Save(_settings);
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

        var devices = new Forms.ToolStripMenuItem("Apple TV");
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
        AddTraySource(share, new CaptureSource { Name = "Entire desktop" });
        var windows = WindowCatalog.GetSources().Where(source => !source.IsDesktop).Take(25).ToArray();
        if (windows.Length > 0)
        {
            share.DropDownItems.Add(new Forms.ToolStripSeparator());
            foreach (var source in windows) AddTraySource(share, source);
        }
        share.Enabled = !_backend.IsRunning && ReceiverBox.SelectedItem is Receiver;
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
                SourceBox.SelectedItem = _sources.FirstOrDefault(candidate => candidate.WindowTitle == source.WindowTitle) ?? source;
                await StartSessionAsync(SelectedOptions(false));
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, ProductInfo.DisplayName); }
        });
        parent.DropDownItems.Add(item);
    }

    private async Task StopSessionAsync()
    {
        _stopRequested = true;
        _activeOptions = null;
        _reconnectAttempt = 0;
        await _backend.StopAsync();
        UpdateButtons();
    }

    private async Task ExitAsync()
    {
        _allowExit = true;
        _lifetime.Cancel();
        await StopSessionAsync();
        await _backend.DisposeAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private void RefreshSources_Click(object sender, RoutedEventArgs e) { RefreshSources(); UpdateButtons(); }
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
        if (ReceiverBox.SelectedItem is Receiver receiver)
            ReceiverDetail.Text = $"{receiver.Ip}:{receiver.Port}  ·  {receiver.DeviceId}";
        if (IsLoaded) SaveSettings();
        UpdateButtons();
    }
}
