using AgentsBridge.Daemon;
using AgentsBridge.Local;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentsBridge.Daemon.Tests;

public sealed class ApiCallLogTests
{
    [Fact]
    public void ReadLatest_ReturnsNewestFirstAndEnforcesCapacity()
    {
        ApiCallLog log = new(capacity: 2);
        log.Add(Entry("/first"));
        log.Add(Entry("/second"));
        log.Add(Entry("/third"));

        IReadOnlyList<ApiCallLogEntry> calls = log.ReadLatest(10);

        Assert.Collection(
            calls,
            call => Assert.Equal("/third", call.Path),
            call => Assert.Equal("/second", call.Path));
    }

    [Fact]
    public async Task Middleware_RecordsMethodPathQueryStatusAndDuration()
    {
        ApiCallLog log = new();
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = "/unity/activate-bridge";
        context.Request.QueryString = new QueryString("?projectName=radiosol");
        ApiCallLoggingMiddleware middleware = new(async requestContext =>
        {
            await Task.Delay(2, TestContext.Current.CancellationToken);
            requestContext.Response.StatusCode = StatusCodes.Status202Accepted;
        });

        await middleware.InvokeAsync(context, log);

        ApiCallLogEntry call = Assert.Single(log.ReadLatest(10));
        Assert.Equal("POST", call.Method);
        Assert.Equal("/unity/activate-bridge?projectName=radiosol", call.Path);
        Assert.Equal(StatusCodes.Status202Accepted, call.StatusCode);
        Assert.Equal("loopback client", call.Caller);
        Assert.True(call.DurationMilliseconds >= 0);
    }

    [Fact]
    public async Task Middleware_DoesNotRecordApiCallsEndpoint()
    {
        ApiCallLog log = new();
        DefaultHttpContext context = new();
        context.Request.Path = "/api-calls";
        ApiCallLoggingMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, log);

        Assert.Empty(log.ReadLatest(10));
    }

    [Fact]
    public async Task Middleware_DoesNotRecordDashboardHealthChecks()
    {
        ApiCallLog log = new();
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/health";
        context.Request.Headers[ApiCallerIdentity.HeaderName] = ApiCallerIdentity.DesktopDashboard;
        ApiCallLoggingMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, log);

        Assert.Empty(log.ReadLatest(10));
    }

    [Fact]
    public void Add_FromConcurrentRequests_RemainsBoundedAndConsistent()
    {
        const int capacity = 100;
        ApiCallLog log = new(capacity);

        Parallel.For(0, 1_000, index => log.Add(Entry("/call/" + index)));

        IReadOnlyList<ApiCallLogEntry> calls = log.ReadLatest(1_000);
        Assert.Equal(capacity, calls.Count);
        Assert.Equal(capacity, calls.Select(call => call.Path).Distinct().Count());
    }

    private static ApiCallLogEntry Entry(string path) =>
        new(DateTimeOffset.UtcNow, "GET", path, StatusCodes.Status200OK, 1, "test");
}
