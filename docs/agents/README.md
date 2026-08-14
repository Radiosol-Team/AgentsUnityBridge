# AgentsUnityBridge guide for coding agents

This is the operational guide for agents working with a Unity repository that integrates AgentsUnityBridge. In project instructions and conversation, the terms **bridge**, **Unity bridge**, **UnityBridge**, **AgentsBridge**, **AgentsUnityBridge**, and **Codex Bridge** normally mean this system unless the surrounding context clearly names another bridge.

Use the bridge whenever an already-open Unity editor can answer a question or validate a change more accurately than a second Unity process, generated project files, or static inspection alone.

## What the bridge is

AgentsUnityBridge has two cooperating parts:

1. The standalone **AgentsBridge daemon** owns the stable loopback HTTP API at `http://127.0.0.1:9876/` (equivalent to `http://localhost:9876/`). It remains reachable while Unity reloads assemblies, freezes, exits, or crashes.
2. An **editor-only Unity connector** opens an outbound WebSocket to the daemon at `/v1/unity/connect`. It runs commands that require Unity APIs and reconnects after assembly reloads.

The daemon is the endpoint agents call. Do not try to call the Unity connector directly. Only loopback connections are accepted; this is a local development tool, not a remote service or a Player runtime feature.

The standalone desktop app starts or monitors the daemon, shows Unity and API activity, discovers Unity Hub projects, can activate a project, and can install signed release updates. None of that changes the HTTP workflow described here.

## When agents should use it

Prefer the bridge for Unity-project work when it can help with any of the following:

- determine whether Unity is open, connected, compiling, importing, testing, playing, blocked, or showing a modal dialog;
- refresh changed assets and request script recompilation;
- read current Unity Console errors and warnings;
- list, filter, and run EditMode or PlayMode tests in the open editor;
- validate C# changes with Unity's real import and assembly state;
- validate asset, prefab, scene, package, asmdef, or editor-pipeline changes;
- compare a new test result with recent local runs before deciding a failure was caused by the current task;
- avoid launching another Unity editor for a project that is already open.

Do not treat `dotnet build`, generated `.csproj` files, IDE analysis, or a standalone compiler as authoritative for Unity assemblies. They can be useful secondary checks, but Unity Console/import state and Unity Test Framework results are the source of truth for an integrated Unity project.

The bridge is not required for a pure prose-only change that cannot affect Unity behavior. For code or asset work, use it whenever it is available and relevant.

## Start every session with health

Call:

```text
GET http://localhost:9876/health
```

`/ping` is an alias. The response intentionally separates daemon health from Unity health.

Interpret the important fields as follows:

| Observation | Meaning | Agent action |
|---|---|---|
| Request cannot connect | The daemon is not running or port `9876` is unavailable. | Start/open AgentsBridge, or ask the user to do so. Do not silently skip intended Unity validation. |
| `daemonResponsive: true`, `unityConnected: false`, `editorState: offline` | The daemon works and Unity is not running. | Open/activate the correct Unity project if allowed, otherwise ask the user. |
| `unityConnected: false`, `editorState: loading` | A Unity process exists, but it is still starting or its connector is not enabled/connected. | Wait and retry; if persistent, enable/activate the bridge for that project. |
| `unityConnected: true`, `mainThreadResponsive: true` | The editor connector and Unity main thread are usable. | Continue with status, refresh, errors, or tests. |
| `unityConnected: true`, `mainThreadResponsive: false` | The connector exists but Unity's main thread is not pumping. A modal, import, freeze, debugger pause, or focus-dependent UI may be blocking it. | Ask the user to focus Unity and inspect/close the modal before sending more main-thread commands. |
| `crashReport` is present | The daemon detected a likely recent Unity crash. | Report it, inspect the details, and reopen Unity if appropriate. Do not dismiss it without a reason. |

`/health` is daemon-owned and stays useful across Unity domain reloads. Main-thread endpoints can temporarily disconnect or time out while scripts compile. This is expected; retry health until `unityConnected` and `mainThreadResponsive` recover.

## The standard Unity validation loop

After changing C# or anything that affects script compilation:

1. Call `GET /recompile`.
2. Expect `202` and a brief connector disconnect during Unity's domain reload.
3. Poll `GET /health` until `unityConnected: true` again.
4. Call `GET /wait-ready?timeout=300`.
5. Call `GET /errors?limit=50` and evaluate errors plus relevant warnings.
6. Inspect recent comparable runs with `GET /test-runs?limit=5`.
7. Run the narrowest relevant tests through `GET /run-tests?...`.
8. Expand to a broader assembly or mode only when the task risk warrants it.

After changing only non-script assets, call `/refresh-assets`, then `/wait-ready`, `/errors`, and relevant tests. `/recompile` already forces an asset refresh before requesting compilation, so a separate refresh is usually unnecessary for C# changes.

Do not call errors or tests immediately after `/recompile`; wait for the connector to return and the editor to become ready.

## Public HTTP endpoints

All endpoints are on `http://localhost:9876`. Most are `GET` because they preserve the original agent-facing bridge contract, including operations that cause Unity actions. Unity activation and crash dismissal are `POST`.

### Discovery and diagnostics

| Endpoint | Purpose and useful details |
|---|---|
| `GET /` or `GET /help` | Lists the supported agent-facing endpoints. Useful when a project connector may be newer than this guide. |
| `GET /health` or `/ping` | Daemon, Unity connection, main-thread heartbeat, process, modal, and crash diagnostics. Safe first probe. |
| `GET /status` | Unity compile/import/play/test state, dirty scenes, and connector details. Requires Unity. |
| `GET /api-calls?limit=100` | Latest completed daemon calls, newest first, including method, path, status, duration, and caller label where available. Limit is clamped to daemon capacity (currently 250). Its own polling and routine dashboard health polling are excluded. |
| `GET /unity/processes` | Unity processes visible to the daemon. Works without a connector. |
| `GET /unity/projects` | Unity Hub project discovery, including paths and matching editor information. Works without a connector. |

### Import, compile, and console

| Endpoint | Purpose and response behavior |
|---|---|
| `GET /refresh-assets` | Runs `AssetDatabase.Refresh()`. Use for non-script asset changes. |
| `GET /recompile` | Forces an asset refresh and requests script compilation. Returns `202`; follow the standard reconnect/readiness loop. |
| `GET /force-recompile` | Alias of `/recompile`. |
| `GET /wait-ready?timeout=300` | Waits until Unity is not compiling, importing/updating, changing PlayMode, or running bridge-started tests. Timeout is clamped to 1–3600 seconds. `200` means ready; `202` means the wait timed out and includes the current state. |
| `GET /errors?limit=50` | Reads recent Unity Console entries and returns total error/warning/log counts. `limit` controls returned entries, not the totals, and is clamped to 0–500. `hasErrors` is also true while compiling. |

### Tests

| Endpoint | Purpose and filters |
|---|---|
| `GET /list-tests?mode=editmode` | Lists tests without running them. `mode` accepts `editmode`, `playmode`, or `all`; default is EditMode. Default timeout is 120 seconds. |
| `GET /run-tests?mode=editmode&timeout=900` | Runs tests through Unity Test Framework. Mode accepts `editmode`, `playmode`, or `all`; default is EditMode. Default timeout is 900 seconds. |
| `GET /test-runs?limit=5` | Returns recent completed bridge test runs from the project's local `Library`. Limit is 1–50. |
| `GET /latest-test-run` | Alias of `/test-runs`; use `limit=1` when only the latest run is wanted. |

Test selection query parameters are:

- `assembly=EditTests` — assembly names;
- `name=Namespace.Fixture.Test` — exact test names understood by Unity Test Framework;
- `group=LocalizedOutput` — Unity group-name filters, useful for fixture or pattern-oriented selection;
- `category=Smoke` — NUnit/Unity categories;
- `mode=editmode|playmode|all`;
- `timeout=1..3600`;
- `sceneChanges=cancel|save|discard|prompt`.

Each filter may contain comma-separated values. Combine filters only when that matches the intended Unity Test Framework selection. When unsure of a name, call `/list-tests` first.

Examples:

```text
GET /list-tests?mode=editmode
GET /run-tests?mode=editmode&assembly=EditTests&timeout=900
GET /run-tests?mode=editmode&assembly=EditTests&group=LocalizedOutput
GET /run-tests?mode=editmode&name=Namespace.Fixture.Test
GET /run-tests?mode=playmode&category=Smoke&timeout=900
GET /test-runs?limit=5
```

If a run exceeds the HTTP wait, `/run-tests` returns `202`, `status: running`, and a job ID; the Unity run continues. Poll `/status` and `/test-runs` instead of immediately starting a duplicate. Only one bridge test run is allowed at a time.

Completed results include pass/fail/skip/inconclusive/assert counts, failed test names, and failure messages, stacks, and output. The connector stores at most 50 runs in:

```text
Library/CodexBridge/latest-test-run.json
Library/CodexBridge/test-runs.jsonl
```

These files are local, ignored operational evidence. They are not shared CI evidence and must not be committed as generated assets.

## Dirty-scene safety is mandatory

Unity Test Framework can open a Save/Don't Save/Cancel dialog before both EditMode and PlayMode runs. That modal blocks automation, so the connector checks dirty scenes before starting.

The default is `sceneChanges=cancel`. When any loaded scene is dirty, `/run-tests` returns HTTP `409` with `error: unsaved_scenes`, the exact dirty scenes, available choices, and no test run is started.

Policies:

- `cancel` (default): preserve all scene changes and do not run tests.
- `save`: save every path-backed dirty scene, then run. This writes project files. An untitled dirty scene cannot be saved automatically and returns `untitled_scene`.
- `discard`: reload path-backed scenes from disk and remove dirty untitled scenes, then run. This destroys in-memory scene edits.
- `prompt`: allow Unity's native modal. Use only while the user is present and able to interact with Unity.

Never choose `save` or `discard` casually. Follow the host project's agent instructions and the available approval mechanism. Present the listed dirty scene paths so the user understands the scope when approval is required. A successful test run is not worth silently destroying work.

## Activating Unity when disconnected

When the daemon is healthy but Unity is offline or loading, inspect:

```text
GET /unity/processes
GET /unity/projects
```

If project activation is authorized, call:

```text
POST /unity/activate-bridge?projectPath=<URL-encoded-absolute-project-path>
```

`projectName=<name>` is also supported when unambiguous. The daemon can infer a project from a running Unity window or the sole existing Hub project, but an explicit absolute path is safer when multiple projects exist.

Activation starts or focuses the project using its matching Unity Hub installation and requests bridge enablement through the Unity connector's command-line entry point. A `202 activation_requested` response means activation began, not that Unity is ready. Poll `/health`. `200 already_connected` means no action was needed. `409 project_not_inferred` means choose an explicit project; do not guess among multiple repositories.

The Unity connector is opt-in per OS user and project. A project may expose an onboarding window or menu command to enable it. Consult the Unity repository's own `AGENTS.md` for exact UI labels and permission rules.

## Failure and recovery playbook

### Daemon unreachable

- Confirm `http://localhost:9876/health` rather than assuming Unity itself is the problem.
- Start the installed AgentsBridge desktop/daemon if this is within task authority; otherwise ask the user.
- Do not launch a second Unity editor as a silent substitute.

### Daemon works, Unity disconnected

- Read `/health`, `/unity/processes`, and `/unity/projects`.
- Unity may simply be reloading after `/recompile`; wait before intervening.
- If Unity is absent, activate/open the correct project when authorized.
- If Unity is running but remains disconnected, its project connector may be disabled; follow that repository's onboarding/menu instructions.

### Main thread unresponsive

- Stop sending main-thread operations.
- Ask the user to focus Unity and check for modal dialogs, Save prompts, package dialogs, crash dialogs, debugger pauses, or other blocking UI.
- Retry `/health` after the obstruction is cleared.

### Unity crashed

- Preserve and report the `crashReport` details from `/health`.
- Reopen the correct project if authorized, then repeat health/readiness/error checks.
- `POST /unity/crash/discard` only dismisses the daemon's recorded crash state; it does not repair or restart Unity. Use it only after the report has been handled.

### Command timeout or disconnect

- A domain reload can drop the connector after a command was accepted.
- Check health and current status before retrying a mutating command.
- For tests, check `/status` and `/test-runs` so you do not start a duplicate run.
- Use `/api-calls` to distinguish a request that never reached the daemon from one that completed with an HTTP error.

### Console or tests already failing

- Compare `/test-runs?limit=5` with the new run before changing unrelated behavior.
- Separate pre-existing debt from regressions caused by the current task.
- Do not silently broaden a feature task into unrelated failure repair; report existing failures clearly.

## Practical tips and tricks

- Use the narrowest endpoint that answers the question. `/health` is cheaper and more resilient than a main-thread command.
- Treat HTTP status as part of the result: `200` completed, `202` accepted/running/timed out while work may continue, `409` needs a safe decision or disambiguation, `503` means Unity is disconnected, and `500` is an operation failure.
- Read the JSON body even for non-`200` responses; it includes stable error codes and recovery hints.
- URL-encode project paths and filter values. Quote URLs containing `&` in shells.
- Prefer targeted tests during iteration, then the relevant assembly/suite before handoff.
- Use `/list-tests` instead of guessing full test names.
- Use `/status` before tests to catch dirty scenes and an already-active test run early.
- `wait-ready` readiness does not prove the Console is clean; always call `/errors` separately.
- A clean Console does not prove behavior; run relevant tests or an appropriate manual smoke.
- After asset-only changes, `/refresh-assets` is enough. After C# or asmdef changes, use `/recompile`.
- The daemon survives assembly reloads specifically so the agent can observe reconnection; temporary `unityConnected: false` after recompilation is normal.
- The desktop API-call history is diagnostic, not a test oracle. Use endpoint results and saved test-run records for validation evidence.
- Record exactly which endpoints/tests were actually run and their results. Do not describe a source file's existence as a successful test run.

## Shell examples

Any HTTP client is acceptable. On Windows PowerShell, `curl.exe` avoids ambiguity with older PowerShell aliases:

```powershell
curl.exe http://localhost:9876/health
curl.exe "http://localhost:9876/wait-ready?timeout=300"
curl.exe "http://localhost:9876/errors?limit=50"
curl.exe "http://localhost:9876/run-tests?mode=editmode&assembly=EditTests&timeout=900"
curl.exe -X POST "http://localhost:9876/unity/activate-bridge?projectPath=C%3A%5CProjects%5CMyUnityProject"
```

On macOS/Linux, ordinary `curl` uses the same URLs.

## For bridge maintainers

The daemon's public route list is implemented in `src/AgentsBridge.Daemon/BridgeEndpoints.cs`; protocol messages live in `src/AgentsBridge.Contracts`; the desktop client consumes the same local API. A Unity repository owns its connector implementation and project-specific opt-in instructions.

When changing a public endpoint, query parameter, response meaning, connection lifecycle, or safety policy:

1. keep the daemon and connector protocol compatible;
2. update `/help` and tests;
3. update this guide;
4. update the integrating Unity repository's `AGENTS.md` and embedded guide;
5. preserve loopback-only access and the daemon's usefulness while Unity is unavailable.

For development of this repository:

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/AgentsBridge.Daemon
dotnet run --project src/AgentsBridge.Desktop
```

Release builds package the daemon and desktop app for supported platforms. Windows installs are per-user and the desktop app can verify and apply published installer updates. Those distribution details do not alter the local API contract agents should use.
