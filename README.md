# AgentsBridge

AgentsBridge keeps an agent-facing API available while Unity reloads, freezes, or exits. A standalone daemon owns the stable HTTP endpoint; a small Unity editor connector executes the commands that require Unity APIs.

## Architecture

- `AgentsBridge.Daemon` listens on `http://127.0.0.1:9876` and owns the public API.
- `AgentsBridge.Desktop` displays daemon and Unity connection health.
- `AgentsBridge.Contracts` contains the wire protocol shared by the standalone processes.
- The Unity connector opens an outbound WebSocket to `/v1/unity/connect` and reconnects after assembly reloads.

Only loopback connections are accepted. The first protocol version intentionally keeps the existing endpoint names and query strings so current agent instructions remain useful.

## Development

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/AgentsBridge.Daemon
dotnet run --project src/AgentsBridge.Desktop
```

The daemon must be running before Unity can connect. While the daemon is healthy but Unity is offline, `/health` remains available and Unity-dependent endpoints return HTTP 503 with a structured `unity_disconnected` response.
