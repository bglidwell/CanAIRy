using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CanAIRy;

public partial class SourcePickerWindow : Window
{
    private IReadOnlyList<CaptureSource> _sources;
    private readonly CancellationTokenSource _lifetime = new();

    public ObservableCollection<SourceTile> ScreenTiles { get; } = [];
    public ObservableCollection<SourceTile> WindowTiles { get; } = [];
    public CaptureSource? SelectedSource { get; private set; }

    public SourcePickerWindow(IReadOnlyList<CaptureSource> sources, CaptureSource? selectedSource)
    {
        InitializeComponent();
        ThemeService.Attach(this);
        DataContext = this;
        _sources = sources;
        SelectedSource = selectedSource;
        PopulateTiles();
        Loaded += async (_, _) => await LoadVisibleThumbnailsAsync();
        Closing += (_, _) => _lifetime.Cancel();
    }

    private void PopulateTiles()
    {
        ScreenTiles.Clear();
        foreach (var source in _sources.Where(source => source.IsDesktop))
            ScreenTiles.Add(CreateTile(source));
        WindowTiles.Clear();
        foreach (var source in _sources.Where(source => !source.IsDesktop))
            WindowTiles.Add(CreateTile(source));
    }

    private SourceTile CreateTile(CaptureSource source) => new(source)
    {
        IsSelected = SameSource(source, SelectedSource)
    };

    private async Task LoadVisibleThumbnailsAsync()
    {
        var tiles = ScreenTiles.Concat(WindowTiles).Where(tile => tile.Thumbnail is null).ToArray();
        using var concurrency = new SemaphoreSlim(3);
        await Task.WhenAll(tiles.Select(async tile =>
        {
            await concurrency.WaitAsync(_lifetime.Token);
            try
            {
                var thumbnail = await Task.Run(
                    () => WindowCatalog.CaptureThumbnail(tile.Source, 480, 270),
                    _lifetime.Token);
                if (!_lifetime.IsCancellationRequested) tile.Thumbnail = thumbnail;
            }
            catch (OperationCanceledException) { }
            finally { concurrency.Release(); }
        }));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _sources = WindowCatalog.GetSources();
        PopulateTiles();
        await LoadVisibleThumbnailsAsync();
    }

    private void SourceTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SourceTile tile) return;
        SelectedSource = tile.Source;
        DialogResult = true;
    }

    private static bool SameSource(CaptureSource left, CaptureSource? right) =>
        right is not null && (left.IsDesktop && right.IsDesktop || left.WindowHandle == right.WindowHandle);
}

public sealed class SourceTile : INotifyPropertyChanged
{
    private System.Windows.Media.Imaging.BitmapSource? _thumbnail;
    private bool _isSelected;

    public SourceTile(CaptureSource source) => Source = source;
    public CaptureSource Source { get; }

    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
