using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAIRy;
using Xunit;

public sealed class GitHubUpdateServiceTests
{
    private static string ReleaseJson(string tag = "v0.1.25", bool draft = false, bool prerelease = false,
        string origin = "https://github.com/bglidwell/CanAIRy", long size = 7)
    {
        var version = tag.TrimStart('v');
        var names = new[] { $"CanAIRy-{version}-win-x64.msi", $"CanAIRy-{version}-win-x64-setup.exe", "SHA256SUMS.txt" };
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease,
            assets = names.Select(name => new { name, size, browser_download_url = $"{origin}/releases/download/{tag}/{name}" })
        });
    }

    [Theory]
    [InlineData("msi", ".msi")]
    [InlineData("exe", "-setup.exe")]
    public void SelectsTheMatchingInstaller(string kind, string suffix)
    {
        var update = GitHubUpdateService.ParseRelease(ReleaseJson(), new Version(0, 1, 9, 0), kind);
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 1, 25), update.Version);
        Assert.EndsWith(suffix, update.AssetName);
    }

    [Theory]
    [InlineData("v0.1.25", true, false)]
    [InlineData("v0.1.25", false, true)]
    [InlineData("v0.1.25-beta.1", false, false)]
    [InlineData("v0.1.9", false, false)]
    [InlineData("v0.1.8", false, false)]
    public void IgnoresNonStableAndNonNewerReleases(string tag, bool draft, bool prerelease)
    {
        Assert.Null(GitHubUpdateService.ParseRelease(ReleaseJson(tag, draft, prerelease), new Version(0, 1, 9, 0), "msi"));
    }

    [Fact]
    public void RejectsAssetsFromAnotherRepository()
    {
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ParseRelease(
            ReleaseJson(origin: "https://github.com/someone/other"), new Version(0, 1, 0), "msi"));
    }

    [Fact]
    public void RejectsIncompleteRelease()
    {
        using var json = JsonDocument.Parse(ReleaseJson());
        var release = JsonSerializer.Serialize(new { tag_name = "v0.1.25", draft = false, prerelease = false,
            assets = json.RootElement.GetProperty("assets").EnumerateArray().Take(2).ToArray() });
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ParseRelease(release, new Version(0, 1, 0), "msi"));
    }

    [Fact]
    public void RequiresAnExactUnambiguousChecksumEntry()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, GitHubUpdateService.ReadChecksum($"{hash}  app.msi\r\n", "app.msi"));
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ReadChecksum($"{hash}  other-app.msi", "app.msi"));
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ReadChecksum($"{hash}  app.msi\n{hash}  app.msi", "app.msi"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DownloadsOnlyACompleteVerifiedPackage(bool badHash, bool truncated)
    {
        var payload = Encoding.UTF8.GetBytes("package");
        var update = GitHubUpdateService.ParseRelease(ReleaseJson(size: truncated ? 8 : 7), new Version(0, 1, 0), "msi")!;
        var hash = badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(payload));
        using var client = new HttpClient(new Handler(request => request.RequestUri == update.ChecksumsUrl
            ? new StringContent($"{hash}  {update.AssetName}") : new ByteArrayContent(payload)));
        var service = new GitHubUpdateService(client);
        var directory = Path.Combine(Path.GetTempPath(), "CanAIRy-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (badHash || truncated)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(update, directory, TestContext.Current.CancellationToken));
                Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
            }
            else
            {
                var staged = await service.DownloadAsync(update, directory, TestContext.Current.CancellationToken);
                Assert.Equal(payload, await File.ReadAllBytesAsync(staged.PackagePath, TestContext.Current.CancellationToken));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChecksGitHubWithoutAnAccountOrToken()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://api.github.com/repos/bglidwell/CanAIRy/releases/latest", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            return new StringContent(ReleaseJson());
        }));
        Assert.NotNull(await new GitHubUpdateService(client).CheckAsync(new Version(0, 1, 0), "msi", TestContext.Current.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(request) });
    }
}
