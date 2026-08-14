using System.Net;
using System.Text.Json;
using AgentsBridge.Local;

namespace AgentsBridge.Desktop;

internal sealed class BridgeStatusClient : IDisposable
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:9876"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public BridgeStatusClient()
    {
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            ApiCallerIdentity.HeaderName,
            ApiCallerIdentity.DesktopDashboard);
    }

    internal async Task<BridgeDashboard> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonElement health = await GetJsonAsync("/health", TimeSpan.FromSeconds(3), cancellationToken);
            bool unityConnected = ReadBoolean(health, "unityConnected");
            JsonElement unity = ReadElement(health, "unity");
            UnityCrashReportInfo? crashReport = ReadCrashReport(health);

            if (!unityConnected)
            {
                return BridgeDashboard.DaemonOnly(
                    ReadString(unity, "projectPath"),
                    crashReport,
                    crashReport is not null
                        ? "Unity appears to have crashed recently."
                        : "Waiting for a Unity editor connection.");
            }

            Task<JsonElement> statusTask = GetJsonAsync("/status", TimeSpan.FromSeconds(12), cancellationToken);
            Task<JsonElement> errorsTask = GetJsonAsync("/errors?limit=1", TimeSpan.FromSeconds(22), cancellationToken);
            Task<JsonElement> runsTask = GetJsonAsync("/test-runs?limit=1", TimeSpan.FromSeconds(12), cancellationToken);
            await Task.WhenAll(statusTask, errorsTask, runsTask);

            JsonElement status = await statusTask;
            JsonElement errors = await errorsTask;
            JsonElement runs = await runsTask;

            bool isCompiling = ReadBoolean(status, "isCompiling");
            bool isUpdating = ReadBoolean(status, "isUpdating");
            bool isPlaying = ReadBoolean(status, "isPlayingOrWillChangePlaymode");
            bool testRunActive = ReadBoolean(status, "testRunActive");
            bool mainThreadResponsive = ReadBoolean(health, "mainThreadResponsive");
            bool possibleModal = ReadBoolean(health, "possibleModalDialog");

            return new BridgeDashboard(
                true,
                true,
                mainThreadResponsive,
                possibleModal,
                ReadString(unity, "projectPath"),
                ReadString(unity, "unityVersion"),
                ReadString(unity, "connectedAtUtc"),
                EditorState(isCompiling, isUpdating, isPlaying, testRunActive, mainThreadResponsive),
                isCompiling,
                isUpdating,
                isPlaying,
                testRunActive,
                ReadDirtyScenes(status),
                ReadInt32(errors, "errorCount"),
                ReadInt32(errors, "warningCount"),
                ReadLatestRun(runs),
                crashReport,
                possibleModal
                    ? "Unity's main thread appears blocked. Focus the editor and check for a popup."
                    : "Unity is connected and responding.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BridgeDashboard.Offline("The AgentsBridge daemon is not reachable on port 9876.");
        }
    }

    internal async Task<TestRunResult> RunEditModeTestsAsync(
        string sceneChanges,
        CancellationToken cancellationToken)
    {
        string path = "/run-tests?mode=editmode&timeout=900&sceneChanges=" + Uri.EscapeDataString(sceneChanges);
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(path, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (response.StatusCode == HttpStatusCode.Conflict &&
                string.Equals(ReadString(root, "error"), "unsaved_scenes", StringComparison.Ordinal))
            {
                return TestRunResult.AwaitingScenes(ReadDirtyScenes(root));
            }

            if (!response.IsSuccessStatusCode)
            {
                return TestRunResult.Failed(
                    ReadString(root, "message") ?? $"Unity returned HTTP {(int)response.StatusCode}.");
            }

            TestRunSummary summary = ParseRun(root);
            return TestRunResult.Completed(summary);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return TestRunResult.Failed(exception.Message);
        }
    }

    internal async Task<IReadOnlyList<ApiCallEntry>> ReadApiCallsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement root = await GetJsonAsync(
                "/api-calls?limit=" + Math.Clamp(limit, 1, 250),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            return ParseApiCalls(root);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<ApiCallEntry> ParseApiCalls(JsonElement root)
    {
        if (!root.TryGetProperty("calls", out JsonElement calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return calls.EnumerateArray()
            .Select(call => new ApiCallEntry(
                ReadDateTimeOffset(call, "timestampUtc"),
                ReadString(call, "method") ?? "?",
                ReadString(call, "path") ?? "/",
                ReadInt32(call, "statusCode"),
                ReadInt64(call, "durationMilliseconds"),
                ReadString(call, "caller") ?? "legacy daemon"))
            .ToArray();
    }

    internal async Task<bool> DiscardCrashAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.PostAsync(
                "/unity/crash/discard",
                content: null,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private async Task<JsonElement> GetJsonAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        using HttpResponseMessage response = await _client.GetAsync(path, linkedSource.Token);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(linkedSource.Token);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string EditorState(
        bool isCompiling,
        bool isUpdating,
        bool isPlaying,
        bool testRunActive,
        bool mainThreadResponsive)
    {
        if (!mainThreadResponsive)
        {
            return "Blocked or showing a popup";
        }

        if (testRunActive)
        {
            return "Running tests";
        }

        if (isCompiling)
        {
            return "Compiling scripts";
        }

        if (isUpdating)
        {
            return "Importing assets";
        }

        if (isPlaying)
        {
            return "Play Mode";
        }

        return "Ready";
    }

    private static IReadOnlyList<DirtySceneInfo> ReadDirtyScenes(JsonElement root)
    {
        if (!root.TryGetProperty("dirtyScenes", out JsonElement scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return scenes.EnumerateArray()
            .Select(scene => new DirtySceneInfo(
                ReadString(scene, "name") ?? "Untitled",
                ReadString(scene, "path")))
            .ToArray();
    }

    private static TestRunSummary? ReadLatestRun(JsonElement root)
    {
        if (!root.TryGetProperty("runs", out JsonElement runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement.ArrayEnumerator enumerator = runs.EnumerateArray();
        return enumerator.MoveNext() ? ParseRun(enumerator.Current) : null;
    }

    private static UnityCrashReportInfo? ReadCrashReport(JsonElement root)
    {
        JsonElement crash = ReadElement(root, "crashReport");
        string? logPath = ReadString(crash, "logPath");
        string? detectedAtUtc = ReadString(crash, "detectedAtUtc");
        string? logLastWriteTimeUtc = ReadString(crash, "logLastWriteTimeUtc");
        string? summary = ReadString(crash, "summary");

        return string.IsNullOrWhiteSpace(logPath) ||
               string.IsNullOrWhiteSpace(detectedAtUtc) ||
               string.IsNullOrWhiteSpace(logLastWriteTimeUtc)
            ? null
            : new UnityCrashReportInfo(logPath, detectedAtUtc, logLastWriteTimeUtc, summary ?? "Unity crash log was found.");
    }

    private static TestRunSummary ParseRun(JsonElement root)
    {
        IReadOnlyList<string> failures = root.TryGetProperty("failedTestNames", out JsonElement names) &&
                                             names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .Take(8)
                .ToArray()
            : [];

        return new TestRunSummary(
            ReadBoolean(root, "ok"),
            ReadString(root, "mode") ?? "editmode",
            ReadString(root, "startedAtUtc"),
            ReadString(root, "finishedAtUtc"),
            ReadInt32(root, "passCount"),
            ReadInt32(root, "failCount"),
            ReadInt32(root, "skipCount"),
            failures);
    }

    private static JsonElement ReadElement(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement value)
            ? value
            : default;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement value) &&
               value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement value) &&
               value.TryGetInt64(out long result)
            ? result
            : 0;
    }

    private static DateTimeOffset ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        return DateTimeOffset.TryParse(ReadString(element, propertyName), out DateTimeOffset result)
            ? result
            : DateTimeOffset.MinValue;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

internal sealed record BridgeDashboard(
    bool DaemonConnected,
    bool UnityConnected,
    bool MainThreadResponsive,
    bool PossibleModalDialog,
    string? ProjectPath,
    string? UnityVersion,
    string? ConnectedAtUtc,
    string EditorState,
    bool IsCompiling,
    bool IsUpdating,
    bool IsPlaying,
    bool TestRunActive,
    IReadOnlyList<DirtySceneInfo> DirtyScenes,
    int ErrorCount,
    int WarningCount,
    TestRunSummary? LatestRun,
    UnityCrashReportInfo? CrashReport,
    string Summary)
{
    internal static BridgeDashboard Offline(string summary) =>
        new(false, false, false, false, null, null, null, "Offline", false, false, false, false, [], 0, 0, null, null, summary);

    internal static BridgeDashboard DaemonOnly(string? projectPath, UnityCrashReportInfo? crashReport, string summary) =>
        new(true, false, false, false, projectPath, null, null, "Unity offline", false, false, false, false, [], 0, 0, null, crashReport, summary);

    internal BridgeDashboard WithDisconnectedEditorState(string editorState, string summary) =>
        this with { EditorState = editorState, Summary = summary };

    internal BridgeDashboard WithCrashReport(UnityCrashReportInfo crashReport) =>
        this with { CrashReport = crashReport, Summary = "Unity appears to have crashed recently." };

    internal BridgeDashboard WithoutCrashReport() =>
        this with
        {
            CrashReport = null,
            EditorState = DaemonConnected ? "Unity offline" : "Offline",
            Summary = DaemonConnected
                ? "Waiting for a Unity editor connection."
                : "The AgentsBridge daemon is not reachable on port 9876."
        };
}

internal sealed record DirtySceneInfo(string Name, string? Path);

internal sealed record ApiCallEntry(
    DateTimeOffset TimestampUtc,
    string Method,
    string Path,
    int StatusCode,
    long DurationMilliseconds,
    string Caller);

internal sealed record UnityCrashReportInfo(
    string LogPath,
    string DetectedAtUtc,
    string LogLastWriteTimeUtc,
    string Summary);

internal sealed record TestRunSummary(
    bool Passed,
    string Mode,
    string? StartedAtUtc,
    string? FinishedAtUtc,
    int PassCount,
    int FailCount,
    int SkipCount,
    IReadOnlyList<string> FailedTestNames);

internal enum TestRunResultKind
{
    Completed,
    AwaitingSceneDecision,
    Failed
}

internal sealed record TestRunResult(
    TestRunResultKind Kind,
    TestRunSummary? Summary,
    IReadOnlyList<DirtySceneInfo> DirtyScenes,
    string? Error)
{
    internal static TestRunResult Completed(TestRunSummary summary) =>
        new(TestRunResultKind.Completed, summary, [], null);

    internal static TestRunResult AwaitingScenes(IReadOnlyList<DirtySceneInfo> scenes) =>
        new(TestRunResultKind.AwaitingSceneDecision, null, scenes, null);

    internal static TestRunResult Failed(string error) =>
        new(TestRunResultKind.Failed, null, [], error);
}
