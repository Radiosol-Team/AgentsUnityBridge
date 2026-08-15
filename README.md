# Unity Agents Bridge

Unity Agents Bridge keeps an agent-facing API available while Unity reloads, freezes, or exits. A standalone daemon owns the stable HTTP endpoint; a small Unity editor connector executes the commands that require Unity APIs.

## For coding agents

Read [the complete agent guide](docs/agents/README.md) before using the bridge or changing a Unity project that integrates it. The guide defines the normal edit/compile/error/test loop, every public endpoint, dirty-scene safety, recovery procedures, and practical usage patterns.

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

When Unity is running but the editor bridge has not connected yet, the desktop app shows Unity as loading instead of offline. The same local view is available from the daemon:

```text
GET  /unity/processes
GET  /unity/projects
POST /unity/activate-bridge?projectPath=<path>
GET  /api-calls?limit=100
```

`/health` also includes `editorState` and `unityProcess`, so agents can distinguish "Unity is not running" from "Unity is loading or the bridge is not active." Bridge activation starts or focuses the selected Unity Hub project and asks the Unity-side connector to enable itself through its command-line entry point.

The daemon keeps the latest 250 completed API calls in memory. The desktop dashboard displays the newest calls live beside Unity diagnostics as compact terminal-style rows: timestamp, method, path, HTTP status, duration, and a brief caller label. Consecutive matching calls are stacked with a count and time range. On Windows, the daemon resolves a loopback caller to its process name when the request has no explicit caller identity, and labels shell children of Codex as `PowerShell (Codex)`. Reading `/api-calls` and routine dashboard `GET /health` checks are intentionally excluded from the history, so UI polling does not obscure agent activity.

## Releases

Every push to `main` runs formatting, build, and test validation, creates self-contained packages for Windows, Linux, and Intel/Apple Silicon macOS, and publishes them in a generated GitHub release. The workflow requires the repository's Actions setting to allow `GITHUB_TOKEN` write access to repository contents.

Windows users can download `AgentsBridge-win-x64-setup.exe` from the latest release. The per-user installer does not require administrator access, adds Unity Agents Bridge to the Start menu, offers a desktop shortcut, registers a standard Windows uninstaller, and launches the daemon when installation finishes.

The Windows desktop app checks GitHub's latest release when it starts. If a newer installer is available, it shows the release notes and asks once whether to install it. On approval, Unity Agents Bridge downloads the installer and its published SHA-256 checksum, verifies the download, and runs the update silently; there are no further installer prompts.
