using System.Text.Json.Serialization;

namespace AgentsBridge.Contracts;

/// <summary>
/// Constants shared by both ends of the local bridge protocol.
/// </summary>
public static class BridgeProtocol
{
    public const int CurrentVersion = 1;
    public const string UnitySocketPath = "/v1/unity/connect";
}

/// <summary>
/// First message sent by a Unity editor after opening the WebSocket.
/// </summary>
public sealed record UnityHello(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("unityVersion")] string UnityVersion,
    [property: JsonPropertyName("pluginVersion")] string PluginVersion);

/// <summary>
/// A daemon request forwarded to the active Unity editor.
/// </summary>
public sealed record BridgeCommand(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("pathAndQuery")] string PathAndQuery);

/// <summary>
/// Unity's complete HTTP-compatible response to a forwarded command.
/// </summary>
public sealed record BridgeCommandResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("statusCode")] int StatusCode,
    [property: JsonPropertyName("body")] string Body);

public sealed record UnityConnectionSnapshot(
    bool Connected,
    string? ProjectId,
    string? ProjectPath,
    string? UnityVersion,
    string? PluginVersion,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastMessageAtUtc);
