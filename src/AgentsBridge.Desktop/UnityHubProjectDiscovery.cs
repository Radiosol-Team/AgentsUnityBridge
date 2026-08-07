using System.Text.Json;

namespace AgentsBridge.Desktop;

/// <summary>
/// Reads Unity Hub's local project index without requiring the Hub process to be running.
/// The parser tolerates unknown fields so Hub can evolve its schema independently.
/// </summary>
internal sealed class UnityHubProjectDiscovery
{
    internal IReadOnlyList<UnityProjectInfo> Discover()
    {
        string? indexPath = CandidateIndexPaths().FirstOrDefault(File.Exists);
        return indexPath is null ? [] : Parse(File.ReadAllText(indexPath));
    }

    internal static IReadOnlyList<UnityProjectInfo> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<UnityProjectInfo> projects = [];
        foreach (JsonProperty property in data.EnumerateObject())
        {
            JsonElement value = property.Value;
            string path = ReadString(value, "path") ?? property.Name;
            string name = ReadString(value, "title") ?? System.IO.Path.GetFileName(path);
            string? version = ReadString(value, "version") ?? ReadProjectVersion(path);
            DateTimeOffset? lastModified = ReadUnixMilliseconds(value, "lastModified");

            projects.Add(new UnityProjectInfo(
                name,
                path,
                version,
                lastModified,
                Directory.Exists(path)));
        }

        return projects
            .OrderByDescending(project => project.LastModified)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> CandidateIndexPaths()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            yield return System.IO.Path.Combine(applicationData, "UnityHub", "projects-v1.json");
            yield return System.IO.Path.Combine(applicationData, "UnityHub", "projects-v2.json");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && OperatingSystem.IsMacOS())
        {
            yield return System.IO.Path.Combine(
                userProfile,
                "Library",
                "Application Support",
                "UnityHub",
                "projects-v1.json");
        }
    }

    private static string? ReadProjectVersion(string projectPath)
    {
        string versionFile = System.IO.Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile))
        {
            return null;
        }

        const string prefix = "m_EditorVersion:";
        string? versionLine = File.ReadLines(versionFile)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        return versionLine?[prefix.Length..].Trim();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
    }
}
