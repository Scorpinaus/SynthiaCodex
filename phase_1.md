# Phase 1 Notes

## 2026-07-07 Initial Protocol Read

- Decision: implement Phase 1 against the documented app-server JSONL protocol: `initialize`, `initialized`, `thread/start`, `turn/start`, streamed notifications, and `turn/interrupt`.
- Rationale: the current Codex manual describes app-server as JSON-RPC-like messages over stdio with `"jsonrpc":"2.0"` omitted, and the implementation plan matches that lifecycle.
- Learning: `turn/start` input is an array of typed content objects, with text prompts shaped like `{ "type": "text", "text": "..." }`.
- Learning: the stable docs name sandbox overrides as `sandbox`; the plan requires `workspace-write`, so tests assert `params.sandbox = "workspace-write"`.
- Error: running the packaged `codex` command from this sandboxed session returned `Access is denied`, even with escalation. I used the official Codex manual as the protocol source instead of a generated local schema.
- Next step: add failing tests for the app-server handshake, thread/turn request shape, notification mapping, and cancellation before adding production code.

## 2026-07-07 Test-First Implementation

- Decision: keep the app-server client testable through `IAppServerTransport`, with the real process transport implemented separately.
- Rationale: Phase 1 needs confidence in JSONL request/response behavior without requiring a live authenticated Codex process during unit tests.
- Learning: request IDs start at `0`; the tests assert `initialize = 0`, `thread/start = 1`, and `turn/start = 2` so accidental ordering changes are visible.
- Learning: app-server notifications are best treated as raw events plus mapped timeline items. This preserves debugging detail while giving the UI readable state.
- Error: the first green compile surfaced a fake-transport disposal bug. The client owns and disposes its transport, so the fake transport had to become idempotent.
- Implementation: added app-server models, sandbox protocol values, `CodexThreadService`, `CodexAppServerClient`, `CodexAppServerProcessTransport`, and `CodexProcessService`.
- Implementation: wired the WPF shell to start app-server, initialize, start a single thread, start turns with selected project `cwd`, stream timeline/raw events, show final response, cancel active turns, and stop the process on app exit.
- Implementation: added cleanup around failed app-server initialization so a failed handshake disposes the client and transport immediately.
- Verification: `dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 7 tests.
- Verification: `dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: `dotnet test NativeCodexAssistant.sln` completed successfully, though the real assertions are in the console test runner above.
- Verification: `scripts\publish-portable.cmd` produced `portable\NativeCodexAssistant\NativeCodexAssistant.App.exe`.
- Learning: `%LOCALAPPDATA%\Microsoft\WindowsApps` has no `codex*` execution alias in this environment; `Get-Command codex -All` resolves only to the packaged `C:\Program Files\WindowsApps\OpenAI.Codex_...\app\resources` entries.
- Remaining caveat: a real app-server smoke test could not be run from this session because the installed packaged `codex.exe` reports `Access is denied` when invoked by the shell.

## 2026-07-07 Discovery And Live Smoke Follow-Up

- Decision: treat a Codex executable as usable only when it can run `--version`; keep searching after broken PATH candidates.
- Rationale: WindowsApps packaged entries can exist on PATH but fail with `Access is denied`; stopping at that path prevents the app from finding the working user-local Codex app bin.
- Learning: this machine has a usable Codex binary at `C:\Users\Admin\AppData\Local\OpenAI\Codex\bin\codex.exe`, while PATH also includes an inaccessible WindowsApps resources folder.
- Implementation: `CodexDiscoveryService` now skips unusable candidates and checks `%LOCALAPPDATA%\OpenAI\Codex\bin`.
- Error: a live app-server run exposed overlapping stdin writes during `initialize`/`initialized`; the client now serializes JSONL writes with an async gate.
- Learning: generated schemas for local `codex-cli 0.130.0-alpha.5` show `thread/start` accepts `sandbox`, while `turn/start` expects `sandboxPolicy` as an object such as `{ "type": "workspaceWrite" }`.
- Implementation: `turn/start` now sends schema-correct `sandboxPolicy`; `CodexThreadService` maps v2 notification shapes including `turn.status`, `error.message`, `error.additionalDetails`, and `agentMessage`.
- Verification: `cmd.exe /c dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 13 tests.
- Verification: `cmd.exe /c dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: `cmd.exe /c dotnet test NativeCodexAssistant.sln` completed successfully.
- Verification: `cmd.exe /c scripts\publish-portable.cmd` produced the portable app folder.
- Verification: `cmd.exe /c "set NCA_RUN_LIVE_CODEX_SMOKE=1&& dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj"` passed, including live app-server initialize and real thread creation.
- Remaining caveat: a real model turn reaches `turn/started`, but the local Codex runtime returns upstream `401 Unauthorized` / stream-disconnected errors before a successful assistant response. The app now maps those error notifications and failed `turn/completed` payloads clearly.

## 2026-07-07 Prompt Preservation And Auth Failure Hardening

- Decision: preserve the prompt text while a turn is running and after failed/cancelled turns; clear it only after a completed turn.
- Rationale: a failed Codex turn, especially an auth/network failure outside the app, should leave the user's task text available for retry.
- Implementation: `MainViewModel` now blocks duplicate submissions while a turn is active, keeps the prompt during `turn/start`, and surfaces auth failures as "Codex authentication failed. Sign in and retry."
- Implementation: `CodexThreadService` tracks `RequiresAuthentication` and `LastErrorDetail` from error notifications and failed `turn/completed` payloads.
- Error: the parser originally tried to read object-valued `codexErrorInfo` as a string; that exception stopped the app-server read loop before `turn/completed` could update the UI.
- Fix: JSON leaf readers now return `null` for non-string/non-int object leaves instead of throwing.
- Verification: `cmd.exe /c dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 14 tests.
- Verification: `cmd.exe /c dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: `cmd.exe /c dotnet test NativeCodexAssistant.sln` completed successfully.
- Verification: `cmd.exe /c scripts\publish-portable.cmd` produced the portable app folder.
- Verification: `cmd.exe /c "set NCA_RUN_LIVE_CODEX_SMOKE=1&& dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj"` passed all 14 tests, including live app-server initialization.

## 2026-07-07 Cancel Request Thread ID Fix

- Error: clicking Cancel after a turn produced `App-server error -32600: Invalid request: missing field `threadId``.
- Decision: send both `threadId` and `turnId` in `turn/interrupt`, and disable the Cancel command unless a turn is actively running with both IDs available.
- Rationale: local Codex app-server requires the thread context for interrupts; sending only the turn ID can also accidentally target a stale completed turn.
- Implementation: `CodexAppServerClient.CancelTurnAsync` now requires `threadId` and `turnId`; `MainViewModel` passes the active thread and turn and gates `CancelTurnCommand`.
- Verification: added client and view-model regression coverage; `cmd.exe /c dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 15 tests.
- Verification: `cmd.exe /c dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: after closing the running app, `cmd.exe /c scripts\publish-portable.cmd` refreshed `portable\NativeCodexAssistant` with the fixed executable.

## 2026-07-07 Per-Project Thread Persistence

- Error: reopening the app lost the visible thread surface and the app started from an in-memory thread state only.
- Learning: local `codex-cli 0.130.0-alpha.5` exposes `thread/resume`; generated schema says resume can load by `threadId`, and recommends using `threadId` whenever possible.
- Decision: persist one thread snapshot per project in settings and resume that real Codex thread before starting the next turn.
- Rationale: this keeps Phase 1 single-threaded while matching the expected "reopen and continue" behavior without faking context.
- Implementation: `AppSettings` now stores `ProjectThreadState`; `CodexThreadService` can restore a visible snapshot; `CodexAppServerClient` sends `thread/resume`; `MainViewModel` restores the selected project's snapshot and resumes before `turn/start`.
- Caveat: this is still a lightweight single-thread store, not the full Phase 3 multi-thread list/fork/archive UI.
- Verification: `cmd.exe /c dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 17 tests.
- Verification: `cmd.exe /c dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: `cmd.exe /c "set NCA_RUN_LIVE_CODEX_SMOKE=1&& dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj"` passed all 17 tests, including live app-server initialization.
- Verification: `cmd.exe /c scripts\publish-portable.cmd` refreshed `portable\NativeCodexAssistant` with the thread persistence build.

## 2026-07-07 Per-Turn Model And Reasoning Controls

- Error: the UI could not choose a model or reasoning effort per turn.
- Learning: local `TurnStartParams` uses `model` for model override and `effort` for reasoning effort; supported efforts are `none`, `minimal`, `low`, `medium`, `high`, and `xhigh`.
- Learning: local app-server exposes `model/list`, so the app can load model IDs dynamically instead of hardcoding a stale model list.
- Decision: add an editable model override selector, a reasoning dropdown, and a Load button that populates models from `model/list`.
- Rationale: leaving the model field blank keeps Codex config/default behavior, while explicit values are sent on the next `turn/start`.
- Implementation: `CodexTurnStartRequest` now includes optional model and reasoning effort; `CodexAppServerClient` sends `params.model` and `params.effort`; `MainViewModel` persists last-used overrides in settings.
- Verification: `cmd.exe /c dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 21 tests.
- Verification: `cmd.exe /c dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: `cmd.exe /c "set NCA_RUN_LIVE_CODEX_SMOKE=1&& dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj"` passed all 21 tests, including live app-server initialization.
- Verification: `cmd.exe /c scripts\publish-portable.cmd` refreshed `portable\NativeCodexAssistant` with the model/reasoning controls.

## 2026-07-07 Graceful Application Close

- Decision: route both the title-bar close button and a new in-app Exit button through `MainViewModel.ShutdownAsync`.
- Rationale: closing should persist project/thread state, interrupt an active turn when possible, dispose the app-server client, and stop the stdio app-server transport before WPF exits.
- Implementation: `MainWindow` now cancels the first close event, awaits shutdown, then closes for real; `MainViewModel` exposes `ExitApplicationCommand`, `CloseRequested`, and `ShutdownAsync`.
- Implementation: shutdown sends `turn/interrupt` with `threadId` and `turnId` when a turn is active, using a short timeout so close does not hang forever.
- Verification: added tests for close request and active-turn shutdown; `cmd.exe /c dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj` passed 23 tests.
- Verification: `cmd.exe /c dotnet build NativeCodexAssistant.sln` passed with 0 warnings and 0 errors.
- Verification: `cmd.exe /c "set NCA_RUN_LIVE_CODEX_SMOKE=1&& dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj"` passed all 23 tests, including live app-server initialization.
- Verification: `cmd.exe /c scripts\publish-portable.cmd` refreshed `portable\NativeCodexAssistant` with the graceful close path.
