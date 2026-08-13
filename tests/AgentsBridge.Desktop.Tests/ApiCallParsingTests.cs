using System.Text.Json;
using Xunit;

namespace AgentsBridge.Desktop.Tests;

public sealed class ApiCallParsingTests
{
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
                  "durationMilliseconds": 42
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
        Assert.Equal(42, calls[0].DurationMilliseconds);
        Assert.Equal(202, calls[1].StatusCode);
    }
}
