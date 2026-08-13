using System.Diagnostics;

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
    long DurationMilliseconds);

public sealed class ApiCallLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiCallLog callLog)
    {
        if (context.Request.Path.Equals("/api-calls", StringComparison.OrdinalIgnoreCase))
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
                stopwatch.ElapsedMilliseconds));
        }
    }
}
