using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentsBridge.Desktop;

internal sealed class ReleaseUpdater : IDisposable
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Radiosol-Team/AgentsUnityBridge/releases/latest";
    private const string InstallerAssetName = "AgentsBridge-win-x64-setup.exe";

    private readonly HttpClient _client;
    private readonly Version _currentVersion;

    internal ReleaseUpdater(HttpClient? client = null, Version? currentVersion = null)
    {
        _client = client ?? new HttpClient();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentsBridge-Updater/1.0");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _currentVersion = currentVersion ??
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
    }

    internal async Task<AvailableRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using HttpResponseMessage response = await _client.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        AvailableRelease? release = ParseRelease(stream);
        return release is not null && release.Version > _currentVersion ? release : null;
    }

    internal async Task InstallAsync(AvailableRelease release, CancellationToken cancellationToken)
    {
        string updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "AgentsBridge",
            "updates",
            release.Version.ToString());
        Directory.CreateDirectory(updateDirectory);

        string installerPath = Path.Combine(updateDirectory, InstallerAssetName);
        string checksumPath = installerPath + ".sha256";
        await DownloadAsync(release.InstallerUrl, installerPath, cancellationToken);
        await DownloadAsync(release.ChecksumUrl, checksumPath, cancellationToken);

        string[] checksumParts = (await File.ReadAllTextAsync(checksumPath, cancellationToken))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (checksumParts.Length == 0 || checksumParts[0].Length != 64)
        {
            throw new InvalidDataException("The release contains an invalid SHA-256 checksum.");
        }

        string expectedChecksum = checksumParts[0];
        await VerifyChecksumAsync(installerPath, expectedChecksum, cancellationToken);

        _ = Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS " +
                        "/FORCECLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
        }) ?? throw new InvalidOperationException("Windows did not start the AgentsBridge installer.");
    }

    internal static AvailableRelease? ParseRelease(Stream json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string? tagName = ReadString(root, "tag_name");
        if (!TryParseVersion(tagName, out Version? version))
        {
            return null;
        }

        string? installerUrl = null;
        string? checksumUrl = null;
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = ReadString(asset, "name");
            string? url = ReadString(asset, "browser_download_url");
            if (string.Equals(name, InstallerAssetName, StringComparison.OrdinalIgnoreCase))
            {
                installerUrl = url;
            }
            else if (string.Equals(name, InstallerAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
        }

        return !TryCreateSecureUri(installerUrl, out Uri? installerUri) ||
               !TryCreateSecureUri(checksumUrl, out Uri? checksumUri)
            ? null
            : new AvailableRelease(
                version!,
                tagName!,
                ReadString(root, "name") ?? tagName!,
                ReadString(root, "body"),
                installerUri!,
                checksumUri!);
    }

    internal static bool TryParseVersion(string? tag, out Version? version)
    {
        string candidate = tag?.Trim().TrimStart('v', 'V') ?? string.Empty;
        int suffix = candidate.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            candidate = candidate[..suffix];
        }

        if (!Version.TryParse(candidate, out version))
        {
            return false;
        }

        version = new Version(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision));
        return true;
    }

    internal static async Task VerifyChecksumAsync(
        string path,
        string expectedChecksum,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] checksum = await SHA256.HashDataAsync(stream, cancellationToken);
        string actualChecksum = Convert.ToHexString(checksum);
        if (!string.Equals(actualChecksum, expectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded installer failed its SHA-256 integrity check.");
        }
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryCreateSecureUri(string? value, out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
               uri.Scheme == Uri.UriSchemeHttps;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

internal sealed record AvailableRelease(
    Version Version,
    string TagName,
    string DisplayName,
    string? ReleaseNotes,
    Uri InstallerUrl,
    Uri ChecksumUrl);
