using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace CanAIRy;

public partial class MainWindow
{
    private readonly HttpClient _updateClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private bool _updateBusy;
    private bool _applyingUpdate;
    private bool _manualInstall;
    private UpdateRelease? _availableUpdate;
    private StagedUpdate? _stagedUpdate;
    private string? _installedKind;

    private async Task RunUpdateLoopAsync()
    {
        try
        {
            await CheckUpdatesAsync();
            var lastCheck = DateTimeOffset.UtcNow;
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await ApplyPendingUpdateAsync();
                if (DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(6)) continue;
                lastCheck = DateTimeOffset.UtcNow;
                await CheckUpdatesAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("Automatic updates stopped: " + ex.Message); }
    }

    private async Task CheckUpdatesAsync()
    {
        if (_updateBusy || _applyingUpdate) return;
        _updateBusy = true;
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            UpdateStatusText.Text = "Checking GitHub for updates…";
            _installedKind = GitHubUpdateService.InstalledKind(AppContext.BaseDirectory);
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 1, 0);
            _availableUpdate = await new GitHubUpdateService(_updateClient).CheckAsync(version, _installedKind ?? "msi", _lifetime.Token);
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = "You're up to date.";
                CheckUpdatesButton.Content = "Check for updates";
                return;
            }
            UpdateStatusText.Text = $"Version {_availableUpdate.Version} is available.";
            CheckUpdatesButton.Content = _installedKind is null ? "Download update" : "Install update";
            if (_installedKind is null)
            {
                UpdateStatusText.Text += " Portable copies update manually.";
                return;
            }
            if (_settings.LastUpdateAttemptVersion == _availableUpdate.Version.ToString())
            {
                UpdateStatusText.Text += " The previous attempt did not complete; select Install update to retry.";
                return;
            }
            if (_settings.AutomaticUpdates) await DownloadUpdateAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update check failed. Try again later.";
            AppendLog("Update check: " + ex.Message);
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = !_applyingUpdate;
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_availableUpdate is null || _installedKind is null) return;
        if (_stagedUpdate?.Release.Version == _availableUpdate.Version)
        {
            await ApplyPendingUpdateAsync();
            return;
        }
        UpdateStatusText.Text = $"Downloading version {_availableUpdate.Version}…";
        _stagedUpdate = await new GitHubUpdateService(_updateClient).DownloadAsync(
            _availableUpdate, Path.Combine(ProductInfo.AppDataFolder, "Updates"), _lifetime.Token);
        UpdateStatusText.Text = "Update ready. It will install when casting stops.";
        await ApplyPendingUpdateAsync();
    }

    private async Task ApplyPendingUpdateAsync()
    {
        if (_stagedUpdate is null || _installedKind is null || _applyingUpdate ||
            (!_settings.AutomaticUpdates && !_manualInstall) || _backend.IsRunning || _activeOptions is not null) return;
        _lifetime.Token.ThrowIfCancellationRequested();
        _applyingUpdate = true;
        UpdateButtons();
        try
        {
            _settings.LastUpdateAttemptVersion = _stagedUpdate.Release.Version.ToString();
            SaveSettings();
            GitHubUpdateService.StartInstaller(_stagedUpdate,
                Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unknown."), _installedKind);
            await ExitAsync();
        }
        catch (Exception ex)
        {
            _applyingUpdate = false;
            _stagedUpdate = null;
            _manualInstall = false;
            UpdateStatusText.Text = "Update could not start. Select Install update to retry.";
            AppendLog("Update installation: " + ex.Message);
            UpdateButtons();
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) { await CheckUpdatesAsync(); return; }
        if (_installedKind is null)
        {
            Process.Start(new ProcessStartInfo(GitHubUpdateService.ReleasesPage) { UseShellExecute = true });
            return;
        }
        if (_updateBusy || _applyingUpdate) return;
        _updateBusy = true;
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            _manualInstall = true;
            if (_stagedUpdate is null) await DownloadUpdateAsync();
            else await ApplyPendingUpdateAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update failed. You can retry.";
            AppendLog("Update download: " + ex.Message);
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = !_applyingUpdate;
        }
    }

    private async void AutomaticUpdates_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_settings.AutomaticUpdates) await CheckUpdatesAsync();
    }
}
