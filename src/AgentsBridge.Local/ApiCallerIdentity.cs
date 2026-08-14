namespace AgentsBridge.Local;

/// <summary>
/// Identifies first-party clients without relying on a user-agent string.
/// </summary>
public static class ApiCallerIdentity
{
    public const string HeaderName = "X-AgentsBridge-Caller";
    public const string DesktopDashboard = "desktop-dashboard";
}
