using AgentsBridge.Contracts;

namespace AgentsBridge.Daemon;

public static class BridgeEndpoints
{
    private static readonly HashSet<string> ForwardedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/status",
        "/wait-ready",
        "/errors",
        "/run-tests",
        "/test-runs",
        "/latest-test-run",
        "/list-tests",
        "/refresh-assets",
        "/recompile",
        "/force-recompile"
    };

    public static void MapBridgeEndpoints(this WebApplication app)
    {
        app.MapGet("/", Help);
        app.MapGet("/help", Help);
        app.MapGet("/ping", HealthAsync);
        app.MapGet("/health", HealthAsync);

        app.Map(BridgeProtocol.UnitySocketPath, AcceptUnityAsync);

        foreach (string path in ForwardedPaths)
        {
            app.MapGet(path, ForwardAsync);
        }
    }

    private static IResult Help()
    {
        return BridgeJson.Json(StatusCodes.Status200OK, new
        {
            ok = true,
            owner = "AgentsBridge.Daemon",
            protocolVersion = BridgeProtocol.CurrentVersion,
            endpoints = new[]
            {
                "GET /status",
                "GET /wait-ready?timeout=300",
                "GET /errors?limit=50",
                "GET /run-tests?mode=editmode|playmode|all&assembly=EditTests&name=Fixture.Test&group=Regex&category=Category&sceneChanges=cancel|save|discard|prompt&timeout=900",
                "GET /test-runs?limit=10",
                "GET /list-tests?mode=editmode|playmode|all&timeout=120",
                "GET /refresh-assets",
                "GET /recompile",
                "GET /ping or /health"
            }
        });
    }

    private static async Task<IResult> HealthAsync(
        HttpContext context,
        UnityConnectionManager connections,
        CancellationToken cancellationToken)
    {
        UnityConnectionSnapshot snapshot = connections.GetSnapshot();
        if (!snapshot.Connected)
        {
            return BridgeJson.Json(StatusCodes.Status200OK, new
            {
                ok = true,
                message = "pong",
                daemonResponsive = true,
                listenerResponsive = true,
                unityConnected = false,
                mainThreadResponsive = false,
                possibleModalDialog = false,
                hint = "AgentsBridge is running, but no Unity editor is connected.",
                unity = snapshot
            });
        }

        BridgeReply unityHealth = await connections.SendAsync(
            context.Request.Path + context.Request.QueryString,
            TimeSpan.FromSeconds(3),
            cancellationToken);

        if (!unityHealth.Succeeded)
        {
            return BridgeJson.Json(StatusCodes.Status200OK, new
            {
                ok = true,
                message = "pong",
                daemonResponsive = true,
                listenerResponsive = true,
                unityConnected = connections.GetSnapshot().Connected,
                mainThreadResponsive = false,
                possibleModalDialog = false,
                hint = unityHealth.Error,
                unity = connections.GetSnapshot()
            });
        }

        System.Text.Json.JsonElement editorHealth =
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(unityHealth.Body!);

        // Keep Unity's established health fields at the top level for existing clients.
        return BridgeJson.Json(StatusCodes.Status200OK, new
        {
            ok = true,
            message = "pong",
            daemonResponsive = true,
            listenerResponsive = true,
            unityConnected = true,
            mainThreadResponsive = ReadBoolean(editorHealth, "mainThreadResponsive"),
            mainThreadHeartbeatAgeSeconds = ReadDouble(editorHealth, "mainThreadHeartbeatAgeSeconds"),
            possibleModalDialog = ReadBoolean(editorHealth, "possibleModalDialog"),
            hint = ReadString(editorHealth, "hint"),
            unityHealth = editorHealth,
            unity = connections.GetSnapshot()
        });
    }

    private static async Task<IResult> ForwardAsync(
        HttpContext context,
        UnityConnectionManager connections,
        CancellationToken cancellationToken)
    {
        int timeoutSeconds = QueryTimeout(context.Request, defaultSeconds: 30);
        BridgeReply reply = await connections.SendAsync(
            context.Request.Path + context.Request.QueryString,
            TimeSpan.FromSeconds(timeoutSeconds + 5),
            cancellationToken);

        if (!reply.Succeeded)
        {
            return BridgeJson.Json(reply.StatusCode, new
            {
                ok = false,
                error = reply.ErrorCode,
                message = reply.Error,
                daemonResponsive = true,
                unity = connections.GetSnapshot()
            });
        }

        return BridgeJson.Raw(reply.StatusCode, reply.Body!);
    }

    private static async Task AcceptUnityAsync(
        HttpContext context,
        UnityConnectionManager connections,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = "websocket_required"
            }, cancellationToken);
            return;
        }

        using System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        await connections.RunSessionAsync(socket, cancellationToken);
    }

    private static int QueryTimeout(HttpRequest request, int defaultSeconds)
    {
        return int.TryParse(request.Query["timeout"], out int timeout)
            ? Math.Clamp(timeout, 1, 3600)
            : defaultSeconds;
    }

    private static bool ReadBoolean(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out System.Text.Json.JsonElement value) &&
               value.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static double? ReadDouble(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out System.Text.Json.JsonElement value) &&
               value.TryGetDouble(out double result)
            ? result
            : null;
    }

    private static string? ReadString(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out System.Text.Json.JsonElement value) &&
               value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
