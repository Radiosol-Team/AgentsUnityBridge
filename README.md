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

The desktop app can start the packaged daemon, shows live Unity compilation/import/test and dirty-scene state, and lists projects from Unity Hub. When no editor is connected, a project can be opened with its matching Hub-installed Unity version.

## Releases

Every push to `main` runs formatting, build, and test validation, creates self-contained packages for Windows, Linux, and Intel/Apple Silicon macOS, and publishes them in a generated GitHub release. The workflow requires the repository's Actions setting to allow `GITHUB_TOKEN` write access to repository contents.
