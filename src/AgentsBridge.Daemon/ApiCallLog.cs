using System.Diagnostics;
using AgentsBridge.Local;

namespace AgentsBridge.Daemon;

public sealed class ApiCallLog
{
    public const int DefaultCapacity = 250;

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<ApiCallLogEntry> _entries = new();

    public ApiCallLog()
        : this(DefaultCapacity)
    {
    }

    public ApiCallLog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public void Add(ApiCallLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<ApiCallLogEntry> ReadLatest(int limit)
    {
        int safeLimit = Math.Clamp(limit, 1, _capacity);
        lock (_gate)
        {
            return _entries.Reverse().Take(safeLimit).ToArray();
        }
    }
}

public sealed record ApiCallLogEntry(
    DateTimeOffset TimestampUtc,
    string Method,
    string Path,
    int StatusCode,
    long DurationMilliseconds,
    string Caller);

public sealed class ApiCallLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiCallLog callLog)
    {
        if (ShouldExcludeFromHistory(context))
        {
            await next(context);
            return;
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int? failedStatusCode = null;
        try
        {
            await next(context);
        }
        catch
        {
            failedStatusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            callLog.Add(new ApiCallLogEntry(
                startedAt,
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                failedStatusCode ?? context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                DescribeCaller(context)));
        }
    }

    private static bool ShouldExcludeFromHistory(HttpContext context)
    {
        if (context.Request.Path.Equals("/api-calls", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string caller = DescribeCaller(context);
        return string.Equals(caller, ApiCallerIdentity.DesktopDashboard, StringComparison.Ordinal) ||
               string.Equals(caller, "AgentsBridge.Daemon", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(caller, "AgentsBridge.Desktop", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeCaller(HttpContext context)
    {
        string declaredCaller = context.Request.Headers[ApiCallerIdentity.HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(declaredCaller))
        {
            return declaredCaller;
        }

        string? processName = LoopbackCallerResolver.TryResolve(
            context.Connection.LocalPort,
            context.Connection.RemotePort);
        if (!string.IsNullOrWhiteSpace(processName))
        {
            return processName;
        }

        string userAgent = context.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            return userAgent.Length <= 64 ? userAgent : userAgent[..61] + "...";
        }

        return "loopback client";
    }
}
