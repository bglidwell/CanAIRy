using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CanAIRy;

internal sealed class BackendService : IAsyncDisposable
{
    private Process? _sender;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _stopRequested;

    public event Action<string>? LogReceived;
    public event Action<string>? StatusChanged;
    public event Func<Task<string?>>? PinRequested;
    public event Action<int>? SenderExited;

    public bool IsRunning => _sender is { HasExited: false };

    private static string BinaryPath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required backend file is missing: {name}", path);
        return path;
    }

    public static string CredentialPath
    {
        get
        {
            return ProductInfo.DataFile("credentials.json");
        }
    }

    public async Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(BinaryPath("airplay-discover.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-timeout");
        start.ArgumentList.Add("3s");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start discovery");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Apple TV discovery failed" : error.Trim());
        return JsonSerializer.Deserialize<List<Receiver>>(output,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? [];
    }

    public async Task StartAsync(StreamOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) throw new InvalidOperationException("A mirror session is already running");
            Interlocked.Exchange(ref _stopRequested, 0);

            var start = new ProcessStartInfo(BinaryPath("airplay-sender.exe"))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            Add(start, "-target", options.Receiver.Ip);
            Add(start, "-port", options.Receiver.Port.ToString(CultureInfo.InvariantCulture));
            Add(start, "-creds", CredentialPath);
            Add(start, "-fps", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture));
            Add(start, "-video-codec", "h264");
            Add(start, "-hwaccel", "none");
            start.ArgumentList.Add("-no-audio");
            start.ArgumentList.Add("-control-stdin");
            if (!options.ShowCursor) start.ArgumentList.Add("-no-cursor");
            if (options.BitrateKbps > 0) Add(start, "-bitrate", options.BitrateKbps.ToString(CultureInfo.InvariantCulture));
            if (!options.Source.IsDesktop) Add(start, "-window-title", options.Source.WindowTitle!);
            if (options.ForcePair) start.ArgumentList.Add("-pair");

            _sender = Process.Start(start) ?? throw new InvalidOperationException("Could not start mirroring backend");
            StatusChanged?.Invoke(options.ForcePair ? "Waiting for Apple TV PIN…" : "Connecting…");
            _ = PumpAsync(_sender.StandardOutput, cancellationToken);
            _ = PumpAsync(_sender.StandardError, cancellationToken);
            _ = WatchExitAsync(_sender, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Add(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private async Task PumpAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        var pending = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            pending.Append(buffer, 0, count);
            var text = pending.ToString();

            if (text.Contains("Enter the PIN shown on the receiver:", StringComparison.OrdinalIgnoreCase))
            {
                pending.Clear();
                await RequestAndSubmitPinAsync();
                continue;
            }

            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) continue;
            var complete = text[..(lastNewline + 1)];
            pending.Clear();
            pending.Append(text[(lastNewline + 1)..]);
            foreach (var raw in complete.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                HandleLine(raw.TrimEnd('\r'));
        }
        if (pending.Length > 0) HandleLine(pending.ToString());
    }

    private async Task RequestAndSubmitPinAsync()
    {
        var callback = PinRequested;
        var pin = callback is null ? null : await callback.Invoke();
        if (string.IsNullOrWhiteSpace(pin))
        {
            await StopAsync();
            return;
        }
        if (_sender is { HasExited: false })
        {
            await _sender.StandardInput.WriteLineAsync(pin.Trim());
            await _sender.StandardInput.FlushAsync();
            StatusChanged?.Invoke("Pairing…");
        }
    }

    private void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        LogReceived?.Invoke(line);
        if (line.Contains("connected to:", StringComparison.OrdinalIgnoreCase)) StatusChanged?.Invoke("Authenticating…");
        else if (line.Contains("pairing complete", StringComparison.OrdinalIgnoreCase)) StatusChanged?.Invoke("Preparing secure stream…");
        else if (line.Contains("screen capture started", StringComparison.OrdinalIgnoreCase)) StatusChanged?.Invoke("Starting video…");
        else if (line.Contains("mirror session ready", StringComparison.OrdinalIgnoreCase)) StatusChanged?.Invoke("Mirroring");
        else if (line.Contains("streaming error", StringComparison.OrdinalIgnoreCase)) StatusChanged?.Invoke("Connection lost");
    }

    private async Task WatchExitAsync(Process process, CancellationToken cancellationToken)
    {
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        var exit = process.ExitCode;
        if (ReferenceEquals(_sender, process)) _sender = null;
        StatusChanged?.Invoke(Interlocked.CompareExchange(ref _stopRequested, 0, 0) == 1 ? "Ready" : "Disconnected");
        SenderExited?.Invoke(exit);
        process.Dispose();
    }

    public async Task StopAsync()
    {
        var process = _sender;
        if (process is null || process.HasExited) return;
        Interlocked.Exchange(ref _stopRequested, 1);
        StatusChanged?.Invoke("Stopping…");
        try
        {
            await process.StandardInput.WriteLineAsync("stop");
            await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
