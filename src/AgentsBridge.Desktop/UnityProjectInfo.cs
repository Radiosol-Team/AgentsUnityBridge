namespace AgentsBridge.Desktop;

internal sealed record UnityProjectInfo(
    string Name,
    string Path,
    string? UnityVersion,
    DateTimeOffset? LastModified,
    bool Exists)
{
    public override string ToString() => $"{Name} ({UnityVersion ?? "unknown Unity version"})";
}
