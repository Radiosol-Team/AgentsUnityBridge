using System.Text;
using Xunit;

namespace AgentsBridge.Desktop.Tests;

public sealed class ReleaseUpdaterTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, 0)]
    [InlineData("2.0", 2, 0, 0, 0)]
    [InlineData("v0.2.14.1", 0, 2, 14, 1)]
    [InlineData("v3.4.5-beta+build", 3, 4, 5, 0)]
    public void TryParseVersion_AcceptsReleaseTags(
        string tag,
        int major,
        int minor,
        int build,
        int revision)
    {
        bool parsed = ReleaseUpdater.TryParseVersion(tag, out Version? version);

        Assert.True(parsed);
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Fact]
    public void ParseRelease_ReturnsInstallerAndChecksumAssets()
    {
        const string json = """
            {
              "tag_name": "v0.3.7",
              "name": "AgentsBridge v0.3.7",
              "body": "Safer updates",
              "assets": [
                {
                  "name": "AgentsBridge-win-x64-setup.exe",
                  "browser_download_url": "https://example.test/setup.exe"
                },
                {
                  "name": "AgentsBridge-win-x64-setup.exe.sha256",
                  "browser_download_url": "https://example.test/setup.exe.sha256"
                }
              ]
            }
            """;
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

        AvailableRelease? release = ReleaseUpdater.ParseRelease(stream);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 3, 7, 0), release.Version);
        Assert.Equal(new Uri("https://example.test/setup.exe"), release.InstallerUrl);
        Assert.Equal("Safer updates", release.ReleaseNotes);
    }

    [Fact]
    public async Task VerifyChecksumAsync_WhenChecksumDoesNotMatch_RejectsFile()
    {
        string path = Path.GetTempFileName();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(path, "installer bytes", cancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReleaseUpdater.VerifyChecksumAsync(path, new string('0', 64), cancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
