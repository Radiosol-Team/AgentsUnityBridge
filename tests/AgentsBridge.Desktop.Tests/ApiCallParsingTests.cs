using System.Text.Json;
using Xunit;

namespace AgentsBridge.Desktop.Tests;

public sealed class ApiCallParsingTests
{
    [Fact]
    public void GroupApiCalls_StacksOnlyConsecutiveMatchingCalls()
    {
        DateTimeOffset first = new(2026, 8, 13, 16, 0, 0, TimeSpan.Zero);
        IReadOnlyList<ApiCallEntry> calls =
        [
            new(first.AddSeconds(2), "GET", "/health", 200, 4, "curl/8.0"),
            new(first.AddSeconds(1), "GET", "/health", 200, 3, "curl/8.0"),
            new(first, "GET", "/status", 200, 8, "curl/8.0")
        ];

        IReadOnlyList<MainWindow.ApiCallGroup> groups = MainWindow.GroupApiCalls(calls);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(first.AddSeconds(1), groups[0].Oldest.TimestampUtc);
        Assert.Equal(first.AddSeconds(2), groups[0].Latest.TimestampUtc);
        Assert.Equal("/status", groups[1].Latest.Path);
    }

    [Fact]
    public void ParseApiCalls_ReadsDaemonHistoryInOrder()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "ok": true,
              "calls": [
                {
                  "timestampUtc": "2026-08-13T16:00:01Z",
                  "method": "GET",
                  "path": "/errors?limit=50",
                  "statusCode": 200,
                  "durationMilliseconds": 42,
                  "caller": "curl/8.0"
                },
                {
                  "timestampUtc": "2026-08-13T16:00:00Z",
                  "method": "POST",
                  "path": "/unity/activate-bridge",
                  "statusCode": 202,
                  "durationMilliseconds": 8
                }
              ]
            }
            """);

        IReadOnlyList<ApiCallEntry> calls = BridgeStatusClient.ParseApiCalls(document.RootElement);

        Assert.Equal(2, calls.Count);
        Assert.Equal("/errors?limit=50", calls[0].Path);
        Assert.Equal("curl/8.0", calls[0].Caller);
        Assert.Equal(42, calls[0].DurationMilliseconds);
        Assert.Equal(202, calls[1].StatusCode);
    }

    [Fact]
    public void ParseApiCalls_UsesLegacyLabelWhenCallerIsMissing()
    {
        using JsonDocument document = JsonDocument.Parse("""
            { "calls": [{ "timestampUtc": "2026-08-13T16:00:00Z", "method": "GET", "path": "/health", "statusCode": 200, "durationMilliseconds": 1 }] }
            """);

        ApiCallEntry call = Assert.Single(BridgeStatusClient.ParseApiCalls(document.RootElement));

        Assert.Equal("legacy daemon", call.Caller);
    }
}
