using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CanAIRy;

internal sealed record UpdateRelease(Version Version, string Tag, string AssetName, Uri AssetUrl, Uri ChecksumsUrl, long Size);
internal sealed record StagedUpdate(UpdateRelease Release, string PackagePath, string Sha256);

internal sealed class GitHubUpdateService(HttpClient client)
{
    public const string ReleasesPage = "https://github.com/bglidwell/CanAIRy/releases/latest";
    private const string DownloadPrefix = "https://github.com/bglidwell/CanAIRy/releases/download/";
    private const long MaximumPackageSize = 512L * 1024 * 1024;

    public static string? InstalledKind(string directory)
    {
        var marker = Path.Combine(directory, "install-kind.txt");
        if (!File.Exists(marker)) return null;
        var kind = File.ReadAllText(marker).Trim();
        return kind is "msi" or "exe" ? kind : null;
    }

    public async Task<UpdateRelease?> CheckAsync(Version current, string kind, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/bglidwell/CanAIRy/releases/latest");
        request.Headers.UserAgent.ParseAdd("CanAIRy/" + current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(token), current, kind);
    }

    internal static UpdateRelease? ParseRelease(string json, Version current, string kind)
    {
        if (kind is not ("msi" or "exe")) throw new ArgumentException("Unsupported installer type.", nameof(kind));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version)) return null;
        var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        if (version <= installed) return null;
        var assetName = $"CanAIRy-{version}-win-x64" + (kind == "msi" ? ".msi" : "-setup.exe");
        Uri? asset = null, checksums = null;
        long size = 0;
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            if (name != assetName && name != "SHA256SUMS.txt") continue;
            var expected = DownloadPrefix + tag + "/" + name;
            if (item.GetProperty("browser_download_url").GetString() != expected)
                throw new InvalidDataException("Update asset is outside this repository's release.");
            if (name == assetName)
            {
                asset = new Uri(expected);
                size = item.GetProperty("size").GetInt64();
            }
            else checksums = new Uri(expected);
        }
        if (asset is null || checksums is null || size <= 0 || size > MaximumPackageSize)
            throw new InvalidDataException("The release is missing a complete installer and checksum manifest.");
        return new UpdateRelease(version, tag, assetName, asset, checksums, size);
    }

    public async Task<StagedUpdate> DownloadAsync(UpdateRelease release, string updatesDirectory, CancellationToken token)
    {
        var manifest = await client.GetStringAsync(release.ChecksumsUrl, token);
        var checksum = ReadChecksum(manifest, release.AssetName);
        var directory = Path.Combine(updatesDirectory, release.Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var package = Path.Combine(directory, release.AssetName);
        var temporary = package + ".download";
        try
        {
            using var response = await client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    length += count;
                    if (length > release.Size) throw new InvalidDataException("Installer exceeds its published size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (length != release.Size) throw new InvalidDataException("Installer download is incomplete.");
            }
            await using (var input = File.OpenRead(temporary))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installer checksum does not match the GitHub release.");
            }
            File.Move(temporary, package);
            return new StagedUpdate(release, package, checksum);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    internal static string ReadChecksum(string manifest, string assetName)
    {
        var matches = manifest.Split('\n').Select(line => Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$"))
            .Where(match => match.Success && match.Groups[2].Value == assetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Installer checksum is missing or ambiguous.");
        return matches[0].Groups[1].Value;
    }

    public static void StartInstaller(StagedUpdate update, string executable, string kind)
    {
        if (kind is not ("msi" or "exe")) throw new InvalidOperationException("Portable copies must be updated manually.");
        var directory = Path.GetDirectoryName(update.PackagePath)!;
        var script = Path.Combine(directory, "apply-update.ps1");
        using (var source = typeof(GitHubUpdateService).Assembly.GetManifestResourceStream("CanAIRy.UpdateInstaller.ps1")
            ?? throw new InvalidOperationException("Update helper is missing."))
        using (var target = File.Create(script)) source.CopyTo(target);
        var requestPath = Path.Combine(directory, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId, Package = update.PackagePath, update.Sha256,
            Executable = executable, Kind = kind
        }));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RequestPath", requestPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the update installer.");
    }
}
