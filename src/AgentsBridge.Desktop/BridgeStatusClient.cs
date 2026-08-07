using System.Net.Http.Json;
using System.Text.Json;

namespace AgentsBridge.Desktop;

internal sealed class BridgeStatusClient : IDisposable
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:9876"),
        Timeout = TimeSpan.FromSeconds(3)
    };

    public async Task<BridgeHealth> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync("/health", cancellationToken);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

            bool unityConnected = ReadBoolean(body, "unityConnected");
            JsonElement unity = body.TryGetProperty("unity", out JsonElement unityElement)
                ? unityElement
                : default;

            return new BridgeHealth(
                true,
                unityConnected,
                ReadString(unity, "projectPath"),
                ReadString(unity, "unityVersion"),
                ReadString(unity, "connectedAtUtc"),
                unityConnected ? "Unity is connected." : "Waiting for a Unity editor connection.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new BridgeHealth(
                false,
                false,
                null,
                null,
                null,
                "The AgentsBridge daemon is not reachable on port 9876.");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.True;
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

internal sealed record BridgeHealth(
    bool DaemonConnected,
    bool UnityConnected,
    string? ProjectPath,
    string? UnityVersion,
    string? ConnectedAtUtc,
    string Summary);
