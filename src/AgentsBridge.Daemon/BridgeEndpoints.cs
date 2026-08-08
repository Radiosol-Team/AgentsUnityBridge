using AgentsBridge.Contracts;
using AgentsBridge.Local;

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
        app.MapGet("/unity/processes", UnityProcesses);
        app.MapGet("/unity/projects", UnityProjects);
        app.MapPost("/unity/activate-bridge", ActivateUnityBridge);
        app.MapPost("/unity/crash/discard", DiscardUnityCrash);

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
                "GET /unity/processes",
                "GET /unity/projects",
                "POST /unity/activate-bridge?projectPath=<path>",
                "POST /unity/crash/discard",
                "GET /ping or /health"
            }
        });
    }

    private static async Task<IResult> HealthAsync(
        HttpContext context,
        UnityConnectionManager connections,
        UnityProcessMonitor processMonitor,
        UnityCrashDetector crashDetector,
        CancellationToken cancellationToken)
    {
        UnityConnectionSnapshot snapshot = connections.GetSnapshot();
        UnityProcessSnapshot processSnapshot = processMonitor.Read();
        UnityCrashReport? crashReport = crashDetector.Read(processSnapshot, DateTimeOffset.UtcNow);
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
                editorState = processSnapshot.IsRunning ? "loading" : "offline",
                hint = processSnapshot.IsRunning
                    ? "Unity is running; it may still be loading or the bridge is not active yet."
                    : crashReport is not null
                        ? "Unity appears to have crashed recently."
                        : "AgentsBridge is running, but no Unity editor is connected.",
                unityProcess = processSnapshot,
                crashReport,
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
                editorState = "blocked_or_bridge_disconnected",
                hint = unityHealth.Error,
                unityProcess = processSnapshot,
                crashReport,
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
            editorState = ReadBoolean(editorHealth, "mainThreadResponsive") ? "connected" : "blocked_or_showing_popup",
            hint = ReadString(editorHealth, "hint"),
            unityProcess = processSnapshot,
            crashReport,
            unityHealth = editorHealth,
            unity = connections.GetSnapshot()
        });
    }

    private static IResult UnityProcesses(UnityProcessMonitor processMonitor)
    {
        UnityProcessSnapshot snapshot = processMonitor.Read();
        return BridgeJson.Json(StatusCodes.Status200OK, new
        {
            ok = true,
            unityRunning = snapshot.IsRunning,
            snapshot.Summary,
            processes = snapshot.Processes
        });
    }

    private static IResult UnityProjects(UnityHubProjectDiscovery projectDiscovery)
    {
        try
        {
            return BridgeJson.Json(StatusCodes.Status200OK, new
            {
                ok = true,
                projects = projectDiscovery.Discover()
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return BridgeJson.Json(StatusCodes.Status500InternalServerError, new
            {
                ok = false,
                error = "unity_hub_projects_unavailable",
                message = exception.Message
            });
        }
    }

    private static IResult DiscardUnityCrash(UnityCrashDetector crashDetector)
    {
        crashDetector.Discard();
        return BridgeJson.Json(StatusCodes.Status200OK, new
        {
            ok = true,
            message = "Unity crash state discarded."
        });
    }

    private static IResult ActivateUnityBridge(
        HttpRequest request,
        UnityConnectionManager connections,
        UnityProcessMonitor processMonitor,
        UnityHubProjectDiscovery projectDiscovery,
        UnityEditorLauncher launcher)
    {
        UnityConnectionSnapshot unity = connections.GetSnapshot();
        if (unity.Connected)
        {
            return BridgeJson.Json(StatusCodes.Status200OK, new
            {
                ok = true,
                status = "already_connected",
                message = "Unity is already connected to AgentsBridge.",
                unity
            });
        }

        UnityProcessSnapshot processes = processMonitor.Read();
        IReadOnlyList<UnityProjectInfo> projects = projectDiscovery.Discover();
        UnityProjectInfo? project = FindActivationProject(request.Query["projectPath"], request.Query["projectName"], projects, processes);
        if (project is null)
        {
            return BridgeJson.Json(StatusCodes.Status409Conflict, new
            {
                ok = false,
                error = "project_not_inferred",
                message = "Unity is running or expected, but AgentsBridge could not infer which Unity Hub project to activate.",
                unityRunning = processes.IsRunning,
                processes = processes.Processes,
                projects
            });
        }

        LaunchResult result = launcher.Launch(project, forceBridgeConnect: true);
        return BridgeJson.Json(result.Success ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict, new
        {
            ok = result.Success,
            status = result.Success ? "activation_requested" : "activation_failed",
            result.Message,
            project
        });
    }

    private static UnityProjectInfo? FindActivationProject(
        string? requestedPath,
        string? requestedName,
        IReadOnlyList<UnityProjectInfo> projects,
        UnityProcessSnapshot processes)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return projects.FirstOrDefault(project =>
                       string.Equals(project.Path, requestedPath, StringComparison.OrdinalIgnoreCase))
                   ?? UnityHubProjectDiscovery.FromProjectPath(requestedPath);
        }

        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return projects.FirstOrDefault(project =>
                string.Equals(project.Name, requestedName, StringComparison.OrdinalIgnoreCase));
        }

        UnityProjectInfo? titledProject = projects.FirstOrDefault(project =>
            project.Exists && processes.LooksLikeProject(project.Name));
        if (titledProject is not null)
        {
            return titledProject;
        }

        UnityProjectInfo[] existingProjects = projects.Where(project => project.Exists).Take(2).ToArray();
        return existingProjects.Length == 1 ? existingProjects[0] : null;
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
