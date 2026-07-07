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
