using System.Text.Json;

namespace AgentsBridge.Daemon;

internal static class BridgeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    internal static IResult Json(int statusCode, object value) =>
        Results.Json(value, Options, statusCode: statusCode);

    internal static IResult Raw(int statusCode, string body) =>
        Results.Text(body, "application/json; charset=utf-8", statusCode: statusCode);
}
