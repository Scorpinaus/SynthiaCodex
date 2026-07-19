# Server-Request Approvals and Execution Policy Implementation Plan

- **Status:** Implemented 19 July 2026
- **Date:** 19 July 2026
- **Target:** Native Codex Assistant / SynthiaCode
- **Scope:** Bidirectional app-server request handling, interactive approvals, and configurable sandbox/approval policies

## 1. Recommendation

Implement this work as a safety-critical vertical slice, not as a dialog added directly to `CodexAppServerClient`.

The first releasable slice should:

1. Correctly distinguish client responses, server requests, and notifications on the JSON-RPC transport.
2. Support command-execution, file-change, and permission approval requests end to end.
3. Queue approval prompts across parallel threads and resolve each request exactly once.
4. Add configurable sandbox and approval-policy overrides with safe defaults and managed-requirement awareness.
5. Fail closed for unsupported server-request methods so the app-server never waits indefinitely.

The initial user-facing defaults should be:

- Sandbox: `workspace-write`.
- Approval policy: `on-request`.
- Approval reviewer: `user`.
- Network inside the workspace-write sandbox: disabled unless separately approved by Codex.

These defaults preserve SynthiaCode's current write boundary while adding the interactive approval path that the app currently lacks. Users can explicitly choose **Use Codex configuration** for either policy; SynthiaCode should not edit `config.toml` as part of this work.

## 2. Research findings

### 2.1 Protocol contract

Codex app-server uses bidirectional JSON-RPC without a `jsonrpc` field on the wire:

- Client request: `{ "method": "...", "id": ..., "params": ... }`.
- Client response: `{ "id": ..., "result": ... }` or `{ "id": ..., "error": ... }`.
- Server request: `{ "method": "...", "id": ..., "params": ... }`.
- Notification: `{ "method": "...", "params": ... }`.

The request ID type is a union of integer and string. The client must preserve the incoming representation when responding.

Current approval methods in the checked-in schema are:

| Method | Required response |
| --- | --- |
| `item/commandExecution/requestApproval` | Command approval decision |
| `item/fileChange/requestApproval` | File-change approval decision |
| `item/permissions/requestApproval` | Granted subset plus turn/session scope |

Other server-request methods already present in the schema are:

- `item/tool/requestUserInput`;
- `mcpServer/elicitation/request`;
- `item/tool/call`;
- `account/chatgptAuthTokens/refresh`;
- deprecated `applyPatchApproval` and `execCommandApproval`.

The transport must recognize all server requests even when the first UI release implements only the three approval families. Unknown or deferred methods need a deterministic JSON-RPC error response rather than being mistaken for client responses or left unresolved.

### 2.2 Approval lifecycle

For command and file-change requests, app-server emits the item start, sends the approval request, receives the client decision, emits `serverRequest/resolved`, and eventually emits `item/completed`.

`serverRequest/resolved` can arrive because another client answered, the turn completed, the turn was interrupted, or the pending request was otherwise cleared. The UI must dismiss the prompt without sending a second response.

Command decisions can include:

- `accept`;
- `acceptForSession`;
- `decline`;
- `cancel`;
- an exec-policy amendment, when one is proposed;
- a network-policy amendment, when one is proposed.

File-change decisions are `accept`, `acceptForSession`, `decline`, or `cancel`.

Permission requests differ from binary approvals. The response must contain only a subset of the requested filesystem/network permissions and may use `scope: "turn"` or `scope: "session"`. An empty grant is the fail-closed decline response.

### 2.3 Policy contract

Sandbox and approval policy are separate controls:

- Sandbox mode defines the technical boundary.
- Approval policy defines when Codex pauses and asks to cross or amend that boundary.

Supported sandbox modes are:

- `read-only`;
- `workspace-write`;
- `danger-full-access`.

Supported current approval-policy choices are:

- `untrusted`;
- `on-request`;
- `never`;
- granular policy objects.

`on-failure` remains accepted by some schemas for compatibility but is deprecated. It must be parsed if returned by an older configuration, but it should not appear as a selectable new value.

The v2 thread and turn methods support policy overrides:

- `thread/start`: `sandbox`, `approvalPolicy`, `approvalsReviewer`;
- `thread/resume`: the same policy fields;
- `thread/fork`: the same policy fields;
- `turn/start`: `sandboxPolicy`, `approvalPolicy`, `approvalsReviewer`.

`config/read` returns the effective Codex configuration for a working directory. `configRequirements/read` returns organization-enforced allowed approval policies and sandbox modes. The application must treat managed requirements as constraints, not preferences.

### 2.4 Current SynthiaCode gap

`CodexAppServerClient.ProcessLine` currently sends every message containing `id` to `CompletePendingRequest`. A server request also contains `id`, so it is silently ignored when its ID does not exist in the outgoing pending-request dictionary.

Additional current limitations:

- only integer response IDs are parsed;
- there is no server-request event or response API;
- `workspace-write` is hardcoded in resume, fork, and turn-start paths;
- no approval policy is sent;
- settings do not persist execution-policy selections;
- the current `IUserInteractionService` is synchronous and suitable only for simple destructive confirmations;
- there is no queue or lifecycle model for simultaneous approvals from parallel threads.

## 3. Scope

### 3.1 Included in the first release

- Generic server-request transport classification and response writing.
- Typed parsing for command, file-change, and permission approvals.
- Global approval queue that supports requests from multiple running threads.
- Window-level accessible approval UI.
- Exact-once response semantics.
- Handling of `serverRequest/resolved`, turn completion/interruption, reconnect, and shutdown.
- App-level sandbox and approval-policy preferences.
- **Use Codex configuration** options.
- Effective-config and managed-requirement reads.
- Serialization of selected policies on thread start/resume/fork and turn start.
- Safe compatibility behavior for older app-server versions.
- Protocol, view-model, persistence, lifecycle, accessibility, and live-smoke tests.

### 3.2 Deferred but enabled by the transport design

- Full `item/tool/requestUserInput` question UI.
- MCP elicitation forms and URL-flow completion.
- Dynamic client tool calls.
- External ChatGPT-token refresh requests.
- Granular approval-policy editor.
- Persistent exec-policy and network-policy management UI.
- Config-file editing through `config/value/write` or `config/batchWrite`.
- Custom writable-root editing.
- Windows sandbox installation/setup UI.

### 3.3 Explicit non-goals

- Do not auto-approve requests in SynthiaCode.
- Do not infer approval from the currently selected thread or from a previous modal.
- Do not persist app-server session approval caches locally.
- Do not log full commands, user-entered secrets, or MCP form contents.
- Do not silently fall back to broader filesystem or network access.
- Do not enable `experimentalApi` solely for this feature.
- Do not implement deprecated legacy approval methods unless a live compatibility test proves that SynthiaCode's v2 flows still receive them.

## 4. Proposed architecture

```text
app-server transport read loop
  -> classify JSON-RPC envelope
     -> client response -> existing outgoing request completion
     -> notification -> existing notification batching/routing
     -> server request -> typed server-request event
          -> AppServerSessionCoordinator
          -> ApprovalQueueViewModel
          -> window-level ApprovalPromptView
          -> user decision
          -> coordinator/client response writer
          -> { id, result } on the original connection

serverRequest/resolved / turn completion / interrupt / disconnect
  -> invalidate queued or visible request
  -> dismiss UI without a second response
```

### 4.1 Ownership boundaries

| Layer | Responsibility |
| --- | --- |
| Core | Request IDs, typed approval payloads, decisions, policy values, effective-policy models |
| Infrastructure | JSON classification, tolerant parsing, exact wire serialization, response correlation |
| App coordinator | Connection-scoped routing, response API, reconnect/disposal behavior |
| Approval view model | Queue, selection, decision validation, exact-once UI state |
| WPF view | Rendering, keyboard navigation, focus containment, warning presentation |
| Main view model | Project/thread labels, policy preference persistence, high-level status |

The app-server read loop must never block waiting for WPF. It raises an event and continues reading. The UI later sends the response through the same connection's serialized write gate.

## 5. Core domain model

Add focused files under `NativeCodexAssistant.Core/Codex/AppServer` rather than continuing to grow `CodexAppServerModels.cs`.

### 5.1 Request envelope

Proposed types:

```csharp
public readonly record struct CodexRequestId(string JsonValue, CodexRequestIdKind Kind);

public sealed record CodexServerRequest(
    CodexRequestId RequestId,
    string Method,
    JsonObject Params,
    CodexServerRequestPayload Payload);
```

`CodexRequestId` should preserve either a string or integer value without converting numeric and string IDs into the same key. Prefer typed integer/string fields over storing a mutable `JsonNode` directly.

### 5.2 Typed approval requests

Use a closed request hierarchy for the first supported families:

- `CodexCommandApprovalRequest`;
- `CodexFileChangeApprovalRequest`;
- `CodexPermissionApprovalRequest`;
- `CodexUnsupportedServerRequest`.

All supported request records include:

- request ID;
- method;
- thread ID;
- optional turn ID;
- item ID;
- arrival time and optional server `startedAtMs`;
- raw params retained only in memory for forward-compatible diagnostics.

Command-specific fields include command, working directory, reason, parsed command actions, network approval context, proposed exec-policy amendment, proposed network-policy amendments, optional available decisions, and optional approval callback ID.

File-specific fields include reason and optional grant root. The view model should correlate with the existing `fileChange` item when available to show the proposed changed paths; it must still work when the item notification arrives late or was filtered.

Permission-specific fields include the request working directory, reason, requested network state, and requested filesystem entries. Filesystem entries must retain path/glob/special-path shape and access level.

### 5.3 Decision types

Do not pass UI strings to Infrastructure. Add typed decisions:

- `CodexCommandApprovalDecision`;
- `CodexFileChangeApprovalDecision`;
- `CodexPermissionGrantDecision`.

The command decision type must represent simple decisions and structured exec/network amendments without lossy string conversion.

### 5.4 Execution-policy types

Add:

```csharp
public enum CodexSandboxMode { ReadOnly, WorkspaceWrite, DangerFullAccess }
public enum CodexApprovalPolicy { Untrusted, OnRequest, Never }
public enum CodexApprovalsReviewer { User, AutoReview }
```

Use nullable values in `CodexExecutionPolicyOverride` to mean **Use Codex configuration**. Parse deprecated `on-failure` and legacy `guardian_subagent` into display-only compatibility values, but do not emit them from new UI choices.

Keep the existing `CodexSandbox` enum temporarily as a compatibility alias or migrate it mechanically in one isolated change. Do not maintain two independent wire mappings long-term.

## 6. Transport and protocol implementation

### 6.1 Classify messages correctly

Change `ProcessLine` to apply this order:

1. `method != null && id != null` -> server request.
2. `method == null && id != null` -> response to an outgoing client request.
3. `method != null && id == null` -> notification.
4. neither -> protocol warning; ignore without crashing the connection.

This classification must occur before attempting to parse an integer ID.

### 6.2 Expose server requests

Add `ServerRequestReceived` to `CodexAppServerClient` and `IAppServerSessionCoordinator`. Server requests must bypass the high-frequency notification batcher.

Event delivery requirements:

- preserve wire arrival order;
- do not run UI code on the read-loop thread;
- catch subscriber exceptions at the app boundary so one UI bug does not terminate transport reading;
- log only request method and non-sensitive correlation identifiers.

### 6.3 Write responses

Add a single response method on the client:

```csharp
Task RespondToServerRequestAsync(
    CodexRequestId requestId,
    CodexServerRequestResult result,
    CancellationToken cancellationToken = default);
```

It must:

- serialize `{ "id": originalId, "result": ... }`;
- use the existing `writeGate`;
- reject a second response for the same incoming request ID;
- reject a response after the owning connection is disposed;
- remove pending-incoming state only after the write succeeds;
- retain enough state to retry once when a write is cancelled before any bytes are handed to the transport;
- never retry across a replacement app-server connection.

Add a separate internal helper for JSON-RPC error responses. Unsupported server requests should receive error `-32601` with a short method-not-supported message. Malformed supported requests should receive `-32602` and remain fail-closed.

### 6.4 Parse defensively

The checked-in schemas and current documentation differ in a few optional fields, such as `availableDecisions` and experimental additional permissions. Parsers should:

- require only the schema-required correlation fields;
- accept absent optional fields;
- ignore unknown fields;
- preserve unknown optional data in the in-memory raw payload;
- reject wrong required-field types with a protocol error response rather than disconnecting the entire session.

### 6.5 Incoming-request registry

Maintain a connection-local registry keyed by typed request ID. Registry states:

- `Pending`;
- `Responding`;
- `Responded`;
- `ResolvedByServer`;
- `AbandonedOnDisconnect`.

Use compare-and-set transitions under a lock. This registry is the source of truth for exact-once behavior; button disabling in WPF is only presentation defense.

### 6.6 Configuration endpoints

Add typed client methods:

- `ReadConfigAsync(cwd, includeLayers: false)`;
- `ReadConfigRequirementsAsync()`.

Only parse fields needed by this slice:

- effective `sandbox_mode`;
- effective `sandbox_workspace_write.network_access`;
- effective `approval_policy`;
- effective `approvals_reviewer`;
- origins for those keys;
- allowed sandbox modes;
- allowed approval policies.

Keep the raw configuration response available for diagnostics, but do not expose or persist unrelated config data.

## 7. Approval queue and lifecycle

Create `ApprovalQueueViewModel`, owned by `MainViewModel` but independent of `TaskViewModel`.

### 7.1 Queue behavior

- Maintain one global queue because approvals can arrive from non-selected parallel threads.
- Show one active prompt at a time.
- Order by arrival; use server timestamp only for display.
- Add a pending-approval badge to the corresponding thread navigation item.
- Include project and thread labels in the prompt.
- Selecting the prompt's thread should be optional; the user must be able to decide without changing the active workspace.
- Never move or reorder the project/thread hierarchy merely because an approval arrived.

### 7.2 Resolution behavior

When the user chooses a decision:

1. Atomically mark the request as responding.
2. Disable all decision controls.
3. Send the response through the coordinator.
4. On successful write, remove the prompt and activate the next request.
5. On write failure, show an inline error and offer retry only if the same connection is still active.

When `serverRequest/resolved` arrives first:

1. Mark the request resolved by server.
2. Close the prompt and remove its badge.
3. Do not send a response.
4. Show a brief non-error status such as “Approval no longer needed.”

### 7.3 Turn and connection lifecycle

- `turn/completed` or `turn/interrupt` notification: remove any unresolved prompts for that turn after `serverRequest/resolved`, with a short grace period for notification reordering.
- User cancels a turn while its prompt is open: send `cancel` when supported, then issue `turn/interrupt`; accept either `serverRequest/resolved` or the response write as the first terminal event.
- Connection failure: immediately abandon all prompts owned by the failed connection and label them expired; never answer them on the replacement connection.
- Reconnect: start with an empty incoming-request registry. Resuming a thread may produce new requests with new IDs.
- App shutdown: stop accepting new UI decisions, best-effort cancel supported pending requests with a short timeout, interrupt turns, and then dispose the transport. If the connection is already failed, abandon without retry.

## 8. Approval user experience

### 8.1 Window-level prompt

Add `Views/ApprovalPromptView.xaml` as a modal overlay in `MainWindow.xaml`, above the shell and below no other interactive surface.

The prompt should include:

- approval type and risk-specific icon/color;
- project and thread names;
- agent reason, if present;
- command or requested operation;
- working directory;
- network host/protocol when `networkApprovalContext` is present;
- proposed changed paths or grant root for file requests;
- requested filesystem/network permissions for permission requests;
- queue position when more than one approval is waiting.

Do not show a meaningless shell command as the main description for managed-network prompts. The official protocol explicitly treats `networkApprovalContext` as the user-facing source of truth.

### 8.2 Commands

Command and file-change prompts expose only decisions supported by both the request's `availableDecisions` and the local client:

- **Allow once** -> `accept`;
- **Allow for this session** -> `acceptForSession`;
- **Decline** -> `decline`;
- **Decline and stop task** -> `cancel`.

When a proposed exec-policy amendment is present, show a separate explicit action such as **Allow matching commands** with the exact proposed prefix/rule. Do not combine it with **Allow for this session**.

When a proposed network-policy amendment is present, show its host and allow/deny effect explicitly. Persistent network-policy changes are advanced and should remain hidden in the first release unless the generated schema and live smoke test confirm the exact wire contract.

### 8.3 Permissions

Permission prompts render each requested permission with an unchecked/checked control:

- requested filesystem entries grouped by read/write;
- requested network enablement;
- scope choice: this turn or this session.

Default the selected grant to the requested set only after the user opens the details; defaulting to an empty grant is safer but makes accidental decline too easy. Recommended compromise: show the requested set selected, require an explicit **Grant selected** action, and always provide **Decline all**. The serializer must intersect selected permissions with the original request before sending.

### 8.4 Accessibility and focus

- Move keyboard focus into the prompt when it opens.
- Trap focus while the overlay is active.
- Restore focus to the prior control when the queue empties.
- `Escape` maps to **Decline**, never **Allow**.
- `Enter` must not approve unless focus is already on an approval button.
- Provide automation names for the operation, risk, command, path, and each decision.
- Announce newly queued approvals through a live region.
- Support 200% text scale, high contrast, light/dark/System themes, and the 800-pixel minimum window width.
- Long commands and paths use selectable, wrapping or horizontally scrollable monospace text without expanding the window.

## 9. Configurable execution policies

### 9.1 Settings UI

Add an **Execution permissions** card to `DetailsView` with:

1. Sandbox selector:
   - Workspace write (recommended/default);
   - Read only;
   - Full access;
   - Use Codex configuration.
2. Approval selector:
   - Ask when requested (recommended/default);
   - Ask for untrusted commands;
   - Never ask;
   - Use Codex configuration.
3. Read-only effective-policy summary for the selected project.
4. Managed-by-organization indicators and disabled disallowed choices.
5. Warning text explaining that sandbox and approval policy are independent.

Do not expose deprecated `on-failure`. Display inherited granular policies as “Granular policy from Codex configuration” without attempting to edit them.

### 9.2 Safety confirmations

- Selecting `danger-full-access` requires a blocking warning confirmation.
- Selecting `never` requires a separate warning confirmation.
- Selecting both requires confirmation that explicitly states Codex can execute model-generated commands without sandbox restriction or interactive prompts.
- If confirmation is declined, restore the previous selection and do not persist.
- Show a persistent danger banner while either risky setting is active.

### 9.3 Persistence

Add nullable settings fields:

- `SandboxModeOverride`;
- `ApprovalPolicyOverride`;
- optionally `ApprovalsReviewerOverride` when auto-review is included in the release.

Migration behavior:

- missing `SandboxModeOverride` in an existing settings file -> initialize to `workspace-write` to preserve current behavior;
- missing `ApprovalPolicyOverride` -> initialize to `on-request` to add the new safe interactive behavior;
- explicit JSON `null` -> Use Codex configuration.

Because absent and explicit `null` have different migration meanings, use either a settings schema version or a separate `ExecutionPolicyInitialized` flag. Do not rely on the JSON serializer to distinguish them implicitly.

Update `AppSettingsSnapshot` and literal legacy-settings tests. Do not persist effective Codex config, requirements, request queues, session grants, or approval decisions.

### 9.4 Precedence and validation

Use this precedence:

1. Organization requirements constrain every value.
2. Explicit SynthiaCode override, if allowed.
3. Effective Codex project/user configuration.
4. App-server default.

If a persisted override is disallowed by current requirements:

- do not silently select a broader value;
- switch the runtime choice to **Use Codex configuration**;
- keep the disallowed stored value only long enough to show a migration warning, then clear it on confirmed save;
- display the effective managed value;
- let app-server remain the final enforcement authority.

If config endpoints are unavailable on an older app-server:

- keep `workspace-write` plus `on-request` as the local safe defaults;
- label effective/managed status as unavailable;
- rely on protocol rejection as the final guard;
- never retry a rejected turn by removing the selected safety fields automatically.

### 9.5 Applying policies

Extend the Core request models and serializers so the same resolved policy is used for:

- new thread start;
- thread resume;
- thread fork;
- turn start;
- automatic replacement thread after resume failure.

For thread methods, serialize kebab-case sandbox modes. For `turn/start`, serialize the structured camel-case `sandboxPolicy` shape. Approval-policy serialization must support omit/inherit and current string values.

Do not send policy changes through `turn/steer`; policy selection is disabled while a turn is active and applies to the next turn.

## 10. File-by-file change map

### Core

- `Core/Codex/AppServer/CodexServerRequestModels.cs` — request ID, envelopes, typed request payloads, resolution state.
- `Core/Codex/AppServer/CodexApprovalModels.cs` — approval request details and decision records.
- `Core/Codex/AppServer/CodexExecutionPolicyModels.cs` — sandbox, approval policy, reviewer, effective config, requirements.
- `Core/Codex/AppServer/CodexAppServerModels.cs` — extend thread/turn request records to accept policy overrides; migrate or delegate existing sandbox mapping.
- `Core/Settings/AppSettings.cs` — persisted override/migration fields.
- `Core/Settings/AppSettingsSnapshot.cs` — clone new settings fields.

### Infrastructure

- `Infrastructure/Codex/CodexAppServerClient.cs` — classification, incoming registry, parsing, response serialization, config reads, policy serialization.
- Consider `Infrastructure/Codex/CodexServerRequestParser.cs` and `CodexServerRequestSerializer.cs` if the client would otherwise exceed a maintainable size.
- No changes to the stdio transport contract should be necessary.

### Application services

- `App/Services/IAppServerSessionCoordinator.cs` — server-request event, response method, config/requirements reads.
- `App/Services/AppServerSessionCoordinator.cs` — attach/detach server-request handlers with each client, tag requests with connection generation, and reject responses for stale generations.
- Do not extend `IUserInteractionService` with asynchronous approval dialogs; the queue/view-model flow has a longer lifecycle than that service's synchronous confirmations.

### View models

- `App/ViewModels/ApprovalQueueViewModel.cs` — queue, active prompt, decisions, invalidation, badges.
- `App/ViewModels/ApprovalPromptViewModel.cs` — presentation projection for one request.
- `App/ViewModels/ExecutionPolicyViewModel.cs` — choices, effective config, requirements, warnings, persistence callback.
- `App/ViewModels/MainViewModel.cs` — composition, thread/project lookup, connection routing, shutdown ordering.
- `App/ViewModels/ProjectNavigationItemViewModel.cs` — pending-approval count/status projection if required by the current grouping design.

### Views and themes

- `App/Views/ApprovalPromptView.xaml` and code-behind only for focus management.
- `App/MainWindow.xaml` — overlay host.
- `App/Views/DetailsView.xaml` — execution-permissions settings card.
- theme dictionaries — warning/danger surfaces only if existing semantic brushes are insufficient.

### Tests

- `NativeCodexAssistant.Tests/ApprovalProtocolTests.cs`.
- `NativeCodexAssistant.Tests/ApprovalQueueTests.cs`.
- `NativeCodexAssistant.Tests/ExecutionPolicyTests.cs`.
- `NativeCodexAssistant.Tests/ApprovalPresentationTests.cs` or additions to responsive layout tests.
- `Program.cs` — register the new console-runner tests.

### Documentation after implementation

- Update `docs/current-architecture.md` with bidirectional request flow and approval ownership.
- Update `implementation_plan.md` to mark the approval/policy slice delivered without marking unrelated Phase 6 work complete.
- Add a short user-facing section to `README.md` describing sandbox/approval choices.

## 11. Implementation sequence

### Step 1 — Lock protocol behavior with failing tests

Add fake-transport tests proving:

- a message with both `method` and `id` is raised as a server request;
- integer and string request IDs round-trip unchanged;
- a server request no longer enters outgoing-response correlation;
- command/file/permission payloads parse required and optional fields;
- malformed payloads produce `-32602`;
- unknown methods produce `-32601`;
- exactly one response can be written per request ID.

Exit criterion: tests fail only because the transport feature is not implemented.

### Step 2 — Implement generic bidirectional transport

Add classification, typed request IDs, server-request event, incoming registry, result/error response writers, and disconnect cleanup.

Exit criterion: all Step 1 tests pass and existing initialize/thread/model/account tests remain unchanged.

### Step 3 — Add policy models and wire serialization

Add current sandbox/approval types and extend start/resume/fork/turn requests. Add exact JSON assertions for every mode and inherit behavior.

Exit criterion: no hardcoded `CodexSandbox.WorkspaceWrite` remains in `MainViewModel`; one policy resolver supplies every lifecycle path.

### Step 4 — Read effective config and requirements

Implement minimal `config/read` and `configRequirements/read` clients, tolerant parsing, unsupported-method fallback, and requirement validation.

Exit criterion: selections can be validated without editing Codex configuration, and a disallowed choice is never sent.

### Step 5 — Implement queue and lifecycle

Add coordinator routing, connection-generation protection, global queue, exact-once decision commands, resolved-notification handling, badges, reconnect cleanup, and shutdown behavior.

Exit criterion: parallel fake threads can each request approval, be answered in order, and be independently invalidated.

### Step 6 — Add WPF approval prompt

Implement the overlay, command/file/permission layouts, focus behavior, keyboard rules, and responsive/theme tests.

Exit criterion: all supported requests can be understood and answered without opening raw diagnostics.

### Step 7 — Add policy settings UI and persistence

Add selectors, effective/managed summaries, dangerous-setting confirmations, migration handling, settings snapshots, and legacy JSON tests.

Exit criterion: restart preserves explicit overrides; inherit mode reflects effective Codex config; managed restrictions disable invalid options.

### Step 8 — Full verification and documentation

Run the behavioral suite, Release build, portable publish, controlled live smoke matrix, accessibility checks, and update architecture/user docs.

Exit criterion: all acceptance criteria below pass with no warnings, unhandled prompts, or unrelated worktree changes.

## 12. Test plan

### 12.1 Protocol tests

- Classification of response, notification, server request, and malformed envelope.
- Integer, large integer, and string request IDs.
- Command approvals: every simple decision and structured amendment serialization.
- Network approval context rendering data.
- File approval decisions.
- Permission subset intersection and scope serialization.
- Optional/missing fields and unknown future fields.
- Duplicate request ID on one connection.
- Double-click/double-response rejection.
- Unsupported and malformed method errors.
- Response write cancellation and connection disposal.

### 12.2 Coordinator and lifecycle tests

- Handler attached once after initialization and detached on replacement.
- Request from an old connection cannot be answered on a new connection.
- Two parallel thread approvals queue without loss.
- `serverRequest/resolved` removes visible and queued prompts.
- Turn completion/interruption clears stale requests.
- Cancel-turn race is exact-once.
- Shutdown best-effort cancellation is bounded.
- Notification batching order is unaffected.

### 12.3 Policy tests

- All sandbox values serialize correctly for thread and turn shapes.
- `on-request`, `untrusted`, `never`, and inherit serialize correctly.
- Deprecated `on-failure` parses but is not offered.
- Explicit overrides apply to start, resume, fork, replacement thread, and follow-up turn.
- Managed requirements disable disallowed values.
- Unknown requirements fields are ignored.
- Config endpoint unavailable behavior remains safe.
- Risky selection confirmation rollback.
- Existing settings migrate to workspace-write/on-request.
- Explicit inherit survives a settings round trip.

### 12.4 Presentation tests

- Correct content for command, network, file, and permission prompts.
- Non-selected thread/project label and badge.
- Long command/path constraints.
- Focus entry, focus restoration, Escape behavior, and no accidental Enter approval.
- Queue position announcement.
- Light, dark, System, high contrast, 200% text scale, and compact window.
- No command or secret answer appears in logs or settings snapshots.

### 12.5 Controlled live smoke matrix

Run against the discovered local Codex version in a disposable test repository:

1. `workspace-write` + `on-request`: normal in-workspace edit completes without unnecessary approval.
2. Out-of-workspace write request: prompt appears; decline lets the turn continue or fail cleanly.
3. Network request: network-specific host/protocol prompt appears; decline is handled.
4. Read-only task attempting an edit: file/permission prompt is correctly scoped.
5. **Allow once**, **Allow for session**, **Decline**, and **Decline and stop** each produce the documented terminal item state.
6. Cancel the turn while a prompt is open.
7. Kill and restart app-server while a prompt is open.
8. Run two threads that request approval concurrently.
9. Verify `danger-full-access` and `never` only in the disposable repository after explicit tester confirmation.

Do not make the live smoke test part of the default deterministic suite. Gate it behind an environment variable, as the existing live app-server smoke test is.

## 13. Security and privacy requirements

- Fail closed on unknown, malformed, expired, or stale requests.
- Never treat timeout, window close, Escape, disconnect, or exception as approval.
- Never broaden a requested permission set during serialization.
- Never carry a session approval across an app-server connection boundary.
- Never answer a request using only thread ID; request ID plus connection generation is mandatory.
- Never place raw command text, environment content, user-input answers, or secrets in application logs.
- Show absolute paths exactly as supplied but do not canonicalize them by touching the filesystem merely to render a prompt.
- Keep full-access and never-ask warnings distinct because they control different risk layers.
- Respect organization requirements even when local settings contain a previously allowed value.
- Preserve the server as the final authority; UI validation is defense in depth, not enforcement.

## 14. Compatibility and rollout

### 14.1 Older app-server versions

- If `config/read` or `configRequirements/read` is unsupported, show policy provenance as unavailable.
- If an explicitly selected policy field is rejected, stop the turn and ask the user to upgrade Codex or select **Use Codex configuration**.
- Do not automatically retry without the field because that can change safety behavior.
- Continue accepting legacy response IDs and optional-field shapes.

### 14.2 Schema drift

- Base wire fixtures on the checked-in generated schemas.
- Add a maintenance test that compares handled server-request method names against `schemas/ServerRequest.json` and fails when a new method is added without an explicit supported/deferred classification.
- Keep unknown-method handling fail-closed.
- Record the Codex version used for the live smoke report.

### 14.3 Delivery slices

Recommended review/merge boundaries:

1. Transport and Core models.
2. Policy serialization and config reads.
3. Queue/lifecycle and command/file approvals.
4. Permission approvals and WPF polish.
5. Settings/persistence, live verification, and documentation.

Each slice should preserve a green existing suite. Do not merge a state where app-server requests are surfaced but cannot be declined or cancelled.

## 15. Acceptance criteria

The feature is complete when:

- All server request envelopes are classified without corrupting outgoing request correlation.
- Command, file-change, and permission approvals are usable from the WPF UI.
- Every request is answered at most once with its original ID and on its original connection.
- Unsupported requests receive a deterministic fail-closed error and never hang indefinitely.
- `serverRequest/resolved` and connection/turn lifecycle events remove stale prompts.
- Parallel-thread approvals queue correctly and identify their project/thread.
- Workspace-write/on-request/user is the migrated safe default.
- Users can choose read-only, workspace-write, full access, untrusted, on-request, never, or Codex-inherited values subject to managed requirements.
- Risky settings require explicit confirmation and remain visibly marked.
- The selected policy is applied consistently to start, resume, fork, replacement, and follow-up paths.
- SynthiaCode never edits `config.toml` or persists effective managed configuration.
- No approval payload secrets are written to logs or settings.
- Accessibility and responsive-layout checks pass.
- Existing 98 behavioral tests plus the new approval/policy tests pass.
- Release build and portable publish complete without warnings or errors.
- Controlled live smoke verifies real app-server approval behavior.

## 16. Review decisions requested

The following product choices should be confirmed before implementation begins:

1. **Migration default:** approve the recommended workspace-write/on-request default, or make existing installs inherit Codex config immediately. Recommendation: preserve workspace-write and add on-request.
2. **Auto reviewer:** expose `auto_review` now, or keep reviewer fixed to `user` for the first release. Recommendation: keep `user` fixed until manual approvals are stable.
3. **Persistent amendments:** expose exec/network policy amendments in the first UI, or limit the first release to once/session decisions. Recommendation: defer persistent amendments, but parse and display that they were proposed.
4. **Permission grants:** allow selective filesystem/network grants in the first release. Recommendation: yes; declining all is insufficient for useful parity.
5. **Deferred server requests:** respond method-not-supported for user-input/MCP/dynamic-tool requests until their dedicated UI arrives. Recommendation: yes, with a visible diagnostic event and no transport hang.

## 17. Primary references

- [Codex App Server: protocol and approvals](https://learn.chatgpt.com/docs/app-server#approvals)
- [Command execution approvals](https://learn.chatgpt.com/docs/app-server#command-execution-approvals)
- [File change approvals](https://learn.chatgpt.com/docs/app-server#file-change-approvals)
- [Permission requests](https://learn.chatgpt.com/docs/app-server#permission-requests)
- [Sandbox and approvals](https://learn.chatgpt.com/docs/agent-approvals-security#sandbox-and-approvals)
- [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference#configtoml)
- [Admin requirements through app-server](https://learn.chatgpt.com/docs/app-server#read-admin-requirements-configrequirementsread)
- Local generated contracts: `schemas/ServerRequest.json`, approval request/response schemas, `schemas/v2/TurnStartParams.json`, `ThreadStartParams.json`, `ThreadResumeParams.json`, `ThreadForkParams.json`, `ConfigReadResponse.json`, and `ConfigRequirementsReadResponse.json`.
