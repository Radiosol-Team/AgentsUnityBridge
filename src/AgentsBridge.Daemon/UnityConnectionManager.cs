using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentsBridge.Contracts;

namespace AgentsBridge.Daemon;

/// <summary>
/// Owns the single active Unity connection and correlates forwarded requests with responses.
/// The class intentionally keeps connection state independent from any desktop window.
/// </summary>
public sealed class UnityConnectionManager(ILogger<UnityConnectionManager> logger)
{
    private readonly object _sessionGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new();

    private WebSocket? _socket;
    private UnityHello? _hello;
    private DateTimeOffset? _connectedAtUtc;
    private DateTimeOffset? _lastMessageAtUtc;
    private long _generation;

    public UnityConnectionSnapshot GetSnapshot()
    {
        lock (_sessionGate)
        {
            bool connected = _socket?.State == WebSocketState.Open && _hello is not null;
            return new UnityConnectionSnapshot(
                connected,
                _hello?.ProjectId,
                _hello?.ProjectPath,
                _hello?.UnityVersion,
                _hello?.PluginVersion,
                _connectedAtUtc,
                _lastMessageAtUtc);
        }
    }

    public async Task RunSessionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        UnityHello hello;
        try
        {
            string firstMessage = await ReceiveTextAsync(socket, cancellationToken);
            hello = JsonSerializer.Deserialize<UnityHello>(firstMessage, BridgeJson.Options)
                ?? throw new BridgeProtocolException("Unity sent an empty hello message.");
            ValidateHello(hello);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Rejected a Unity bridge connection during handshake.");
            await CloseSafelyAsync(socket, WebSocketCloseStatus.PolicyViolation, exception.Message);
            return;
        }

        long generation;
        long previousGeneration;
        WebSocket? previous;
        lock (_sessionGate)
        {
            previous = _socket;
            previousGeneration = _generation;
            _socket = socket;
            _hello = hello;
            _connectedAtUtc = DateTimeOffset.UtcNow;
            _lastMessageAtUtc = _connectedAtUtc;
            generation = ++_generation;
        }

        logger.LogInformation(
            "Unity connected for {ProjectPath} using protocol {ProtocolVersion}.",
            hello.ProjectPath,
            hello.ProtocolVersion);

        if (previous is not null && !ReferenceEquals(previous, socket))
        {
            FailPending(previousGeneration, "Unity reconnected before responding.");
            await CloseSafelyAsync(previous, WebSocketCloseStatus.NormalClosure, "Replaced by a newer Unity connection.");
        }

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string payload = await ReceiveTextAsync(socket, cancellationToken);
                lock (_sessionGate)
                {
                    _lastMessageAtUtc = DateTimeOffset.UtcNow;
                }

                BridgeCommandResponse? response = JsonSerializer.Deserialize<BridgeCommandResponse>(payload, BridgeJson.Options);
                if (response?.Type != "response")
                {
                    logger.LogWarning("Ignored an unsupported Unity message: {Payload}", payload);
                    continue;
                }

                if (_pending.TryGetValue(response.Id, out PendingCommand? pending) &&
                    pending.Generation == generation &&
                    _pending.TryRemove(response.Id, out _))
                {
                    pending.Completion.TrySetResult(response);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or BridgeProtocolException)
        {
            logger.LogInformation(exception, "Unity bridge connection closed.");
        }
        finally
        {
            DisconnectIfCurrent(socket, generation);
        }
    }

    public async Task<BridgeReply> SendAsync(
        string pathAndQuery,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        WebSocket? socket;
        long generation;
        string id = Guid.NewGuid().ToString("N");
        TaskCompletionSource<BridgeCommandResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sessionGate)
        {
            socket = _socket?.State == WebSocketState.Open && _hello is not null ? _socket : null;
            generation = _generation;
            if (socket is not null && !_pending.TryAdd(id, new PendingCommand(generation, completion)))
            {
                throw new InvalidOperationException("Could not register a bridge command.");
            }
        }

        if (socket is null)
        {
            return BridgeReply.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "unity_disconnected",
                "AgentsBridge is running, but no Unity editor is connected.");
        }

        try
        {
            BridgeCommand command = new("command", id, pathAndQuery);
            await SendJsonAsync(socket, command, cancellationToken);

            using CancellationTokenSource timeoutSource = new(timeout);
            using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            BridgeCommandResponse response = await completion.Task.WaitAsync(linkedSource.Token);
            return BridgeReply.Success(response.StatusCode, response.Body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BridgeReply.Failure(
                StatusCodes.Status504GatewayTimeout,
                "unity_timeout",
                $"Unity did not answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (WebSocketException exception)
        {
            return BridgeReply.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "unity_disconnected",
                exception.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendJsonAsync<T>(WebSocket socket, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, BridgeJson.Options);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void DisconnectIfCurrent(WebSocket socket, long generation)
    {
        bool disconnected = false;
        lock (_sessionGate)
        {
            if (ReferenceEquals(_socket, socket) && _generation == generation)
            {
                _socket = null;
                disconnected = true;
            }
        }

        if (!disconnected)
        {
            return;
        }

        FailPending(generation, "Unity disconnected before responding.");
        logger.LogInformation("Unity disconnected; the daemon remains available.");
    }

    private void FailPending(long generation, string message)
    {
        foreach ((string id, PendingCommand pending) in _pending)
        {
            if (pending.Generation == generation && _pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetException(new WebSocketException(message));
            }
        }
    }

    private static void ValidateHello(UnityHello hello)
    {
        if (hello.Type != "hello")
        {
            throw new BridgeProtocolException("The first Unity message must be a hello message.");
        }

        if (hello.ProtocolVersion != BridgeProtocol.CurrentVersion)
        {
            throw new BridgeProtocolException(
                $"Unsupported protocol version {hello.ProtocolVersion}; expected {BridgeProtocol.CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(hello.ProjectId) || string.IsNullOrWhiteSpace(hello.ProjectPath))
        {
            throw new BridgeProtocolException("Unity hello must identify its project.");
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream stream = new();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The peer closed the bridge connection.");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new BridgeProtocolException("Only UTF-8 text messages are supported.");
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 4 * 1024 * 1024)
            {
                throw new BridgeProtocolException("Bridge message exceeded the 4 MiB limit.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }

    private static async Task CloseSafelyAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // The peer may already be gone; shutdown remains best effort.
        }
    }

    private sealed record PendingCommand(
        long Generation,
        TaskCompletionSource<BridgeCommandResponse> Completion);
}

public sealed record BridgeReply(bool Succeeded, int StatusCode, string? Body, string? ErrorCode, string? Error)
{
    public static BridgeReply Success(int statusCode, string body) =>
        new(true, statusCode, body, null, null);

    public static BridgeReply Failure(int statusCode, string errorCode, string error) =>
        new(false, statusCode, null, errorCode, error);
}

internal sealed class BridgeProtocolException(string message) : Exception(message);
