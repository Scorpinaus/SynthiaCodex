# SynthiaCode Permission Modes Implementation Plan

- **Status:** Implemented 19 July 2026
- **Date:** 19 July 2026
- **Target:** SynthiaCode / SynthiaCode
- **Scope:** Replace the low-level execution-policy selectors with ChatGPT-aligned permission modes: **Ask for approval**, **Approve for me**, and **Custom** profiles resolved from `config.toml`
- **Implementation principle:** TDD, backward-compatible settings migration, fail-closed policy resolution, and no direct edits to Codex configuration

## 1. Recommendation

Replace the two primary settings shown today—**Filesystem sandbox** and **Server-request approvals**—with one permission-mode selector that matches the current ChatGPT desktop and Codex terminology:

1. **Ask for approval**
2. **Approve for me**
3. **Custom**

The first two are stable presets. **Custom** delegates filesystem and network boundaries to the active Codex permission profile loaded from `config.toml`. When custom profiles are available, SynthiaCode should list them and allow one to be selected without editing the configuration file.

Keep the resolved sandbox, approval policy, reviewer, permission-profile ID, configuration source, and managed restrictions visible as read-only diagnostics. Do not keep the low-level sandbox and approval-policy selectors as co-equal primary controls; they permit combinations that do not map cleanly to the ChatGPT permission modes.

The default for new and normally migrated installations should be **Ask for approval**.

## 2. Confirmed product and protocol behavior

### 2.1 Official permission-mode semantics

OpenAI documents two controls that work together:

- The sandbox or permission profile defines the filesystem and network boundary.
- The approval policy and reviewer determine whether an escalation pauses for the user or goes to automatic review.

**Approve for me** retains the same workspace boundary as **Ask for approval**. It changes who reviews eligible escalation requests; it does not grant additional filesystem or network access.

Permission profiles are configured with `default_permissions` and `[permissions.<id>]`. They do not compose with legacy `sandbox_mode` or `[sandbox_workspace_write]` overrides. If a client supplies a sandbox override, Codex uses the legacy sandbox path rather than the selected permission profile.

Authoritative references:

- [Permission modes](https://learn.chatgpt.com/docs/permission-modes.md)
- [Permission profiles](https://learn.chatgpt.com/docs/permissions.md)
- [Agent approvals and security](https://learn.chatgpt.com/docs/agent-approvals-security.md)
- [Configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)

### 2.2 Installed app-server schema findings

The implementation should target the generated schema from the locally discovered `codex-cli 0.144.4`, while retaining compatibility with older app-server versions.

Both the stable and experimental bundles were checked during planning:

```powershell
codex app-server generate-json-schema --out <temporary-directory>
codex app-server generate-json-schema --experimental --out <temporary-directory>
```

`permissionProfile/list` and the related lifecycle fields are present in the stable bundle. The generated v2 schemas confirm:

- `permissionProfile/list` accepts `cwd`, `cursor`, and `limit`.
- Each returned profile has `id`, optional `description`, and `allowed`.
- `thread/start`, `thread/resume`, and `thread/fork` accept `permissionProfile`.
- `turn/start` accepts `permissionProfile` for the turn and subsequent turns.
- `permissionProfile` cannot be combined with legacy `sandbox` or `sandboxPolicy` in the same request.
- `configRequirements/read` exposes `allowedApprovalsReviewers`, `allowedPermissionProfiles`, `allowedApprovalPolicies`, and `allowedSandboxModes`.
- Thread state can expose `activePermissionProfile`, including its `id` and optional parent profile.
- `approvalsReviewer` supports `user` and `auto_review`; `guardian_subagent` is legacy compatibility only.

This means custom-profile support is no longer a speculative configuration-only feature. It has an explicit app-server discovery and request path.

## 3. Product model and exact mapping

### 3.1 User-facing modes

| Mode | Permission boundary | Approval policy | Reviewer | Request behavior |
| --- | --- | --- | --- | --- |
| **Ask for approval** | Built-in `:workspace` profile when supported; legacy `workspace-write` fallback | `on-request` | `user` | User reviews boundary-crossing requests |
| **Approve for me** | Built-in `:workspace` profile when supported; legacy `workspace-write` fallback | `on-request` | `auto_review` | Automatic reviewer handles eligible requests; user queue remains available for human-required requests |
| **Custom — config.toml default** | Omit permission-profile and sandbox override | Omit | Omit | Codex resolves `default_permissions`, approval policy, and reviewer from its normal configuration layers |
| **Custom — named profile** | Send selected profile ID | Omit | Omit | Codex uses the selected configured profile while approval policy/reviewer continue to follow configuration |

### 3.2 Why Ask and Approve should prefer `:workspace`

For app-server versions that expose permission profiles, both presets should use the built-in `:workspace` profile rather than sending a legacy sandbox override. This aligns SynthiaCode with the current permission-profile model and allows Codex to report active-profile provenance.

For an older app-server that does not support `permissionProfile/list` or rejects the profile request path, resolve the two presets through the existing legacy representation:

```text
Sandbox = workspace-write
ApprovalPolicy = on-request
ApprovalsReviewer = user | auto_review
PermissionProfile = omitted
```

Do not silently fall back from **Approve for me** to `user`. If the installed app-server cannot accept `auto_review`, disable the mode with an explanation and retain the last valid selection.

### 3.3 Custom semantics

Custom has two sub-selections:

- **Use config.toml default**: omit `permissionProfile`, legacy sandbox, approval policy, and reviewer.
- **Named profile**: send only the selected profile ID; omit legacy sandbox, approval policy, and reviewer.

The named-profile list comes from `permissionProfile/list`, not from parsing TOML in SynthiaCode. This preserves Codex configuration precedence, project-local layers, managed requirements, and future schema behavior.

SynthiaCode must not write `config.toml`. It may provide **Open config.toml** and **Refresh profiles** actions.

### 3.4 Explicit invariants

Introduce one resolved selection object with constructor validation:

```csharp
public sealed record CodexResolvedPermissionMode(
    CodexPermissionMode Mode,
    string? PermissionProfileId,
    CodexSandbox? LegacySandbox,
    CodexApprovalPolicy? ApprovalPolicy,
    CodexApprovalsReviewer? ApprovalsReviewer,
    bool UsesLegacyFallback);
```

Enforce these invariants:

1. `PermissionProfileId` and `LegacySandbox` are mutually exclusive.
2. Ask always resolves to `on-request` + `user`.
3. Approve always resolves to `on-request` + `auto_review`.
4. Custom never supplies a SynthiaCode approval-policy or reviewer override.
5. Custom default supplies neither a profile ID nor a sandbox override.
6. A named custom profile must exist in the latest catalog and have `Allowed == true`.
7. No disallowed or stale value is serialized.
8. `turn/steer` never changes permissions.

## 4. UX specification

### 4.1 Settings card

Replace the two existing combo boxes in `DetailsView.xaml` with:

```text
Permissions

● Ask for approval
  Work inside this project and ask me before crossing its boundary.

○ Approve for me
  Keep the same project boundary and automatically review eligible requests.

○ Custom
  Follow config.toml or select one of its named permission profiles.

Effective: :workspace · Ask when requested · Reviewed by you
```

Selection may be implemented as radio-card rows or one compact selector with descriptive content. Radio cards are preferred because the reviewer distinction is safety-relevant and should remain visible.

### 4.2 Custom expansion

When **Custom** is selected, expand a profile selector containing:

1. **Use config.toml default**
2. Built-in and named profiles returned by `permissionProfile/list`

Each profile row shows:

- Display ID
- Optional description
- Allowed/managed status
- Built-in or configured provenance when known

Disallowed profiles remain visible but disabled, with a managed-policy explanation. A stale persisted profile that no longer exists is shown as unavailable and is not sent.

### 4.3 Effective summary

Show read-only effective state beneath the selector:

- Active permission profile ID, if reported
- Parent profile, if reported
- Effective approval policy
- Effective reviewer: **You** or **Automatic review**
- Legacy sandbox summary when the old path is active
- Workspace-write network state when available
- Configuration origin or managed-requirement warning

For **Custom — config.toml default**, it is acceptable to show “Resolved when the next thread starts” until app-server returns an `activePermissionProfile`. Do not guess a default profile from list ordering.

### 4.4 Confirmation behavior

The three requested modes do not include Full access. Do not expose `:danger-full-access` as a normal custom row merely because it appears in the profile catalog.

If a later decision exposes Full access, it remains a separate explicitly confirmed mode. This plan does not remove the existing defensive confirmation code until no migration state can reach it.

### 4.5 Approval queue behavior under automatic review

SynthiaCode must keep its server-request queue active in **Approve for me**. Automatic review only handles eligible requests. Human-required, unsupported-review, or policy-directed requests may still arrive and must be displayed normally.

The UI should indicate the active reviewer in approval context where helpful, but approval response serialization itself remains unchanged.

## 5. Capability discovery and compatibility

### 5.1 Session capability state

Add a per-app-server-connection capability record:

```csharp
public sealed record CodexPermissionCapabilities(
    bool SupportsPermissionProfiles,
    bool SupportsAutoReview,
    IReadOnlyList<CodexPermissionProfileSummary> Profiles);
```

Discovery sequence after connection:

1. Call `permissionProfile/list` with the selected project `cwd`.
2. Follow pagination until `nextCursor` is empty.
3. If successful, enable the modern profile request path.
4. If method-not-found is returned, record legacy-only support without treating the connection as failed.
5. Read `configRequirements/read` and validate reviewers, policies, sandboxes, and profiles.
6. Refresh when project `cwd`, Codex installation/version, app-server generation, or configuration changes.

Do not put discovery on the critical task-submission path. The previous approval implementation already found that waiting synchronously for optional configuration RPCs can block older servers and tests. Warm discovery in the background and use a bounded, explicit state transition.

### 5.2 Behavior before discovery completes

- Preserve the saved selection.
- Disable task submission only if serializing the saved mode would be ambiguous or unsafe.
- Ask/Approve may use the already-known legacy fallback when the server is known to be older.
- Custom default can always be represented safely by omitting overrides.
- A named custom profile must wait until the catalog confirms that profile is present and allowed.

### 5.3 Older app-server behavior

| Selection | Older server behavior |
| --- | --- |
| Ask for approval | Legacy `workspace-write` + `on-request` + `user` |
| Approve for me | Available only if `auto_review` is accepted or positively discovered; otherwise disabled |
| Custom default | Omit all overrides and inherit legacy `config.toml` settings |
| Custom named profile | Disabled with “Update Codex to use named permission profiles” |

## 6. Managed requirements

Extend `CodexExecutionPolicyRequirements` to model:

- Allowed sandbox modes
- Allowed approval policies
- Allowed approval reviewers
- Allowed permission profiles and their boolean allow/deny values
- Managed default profile, when provided

Mode availability is computed as a conjunction:

```text
Ask available = workspace profile/fallback allowed
                AND on-request allowed
                AND user reviewer allowed

Approve available = workspace profile/fallback allowed
                    AND on-request allowed
                    AND auto_review reviewer allowed

Custom named available = profile catalog says allowed
                         AND requirements do not deny it
```

Custom default remains selectable only when Codex can resolve a compliant effective configuration. If requirements make the configured default invalid, surface the app-server error and do not substitute a broader profile.

If a previously saved selection becomes disallowed:

1. Do not serialize it.
2. Preserve it in settings for possible later restoration.
3. Mark it unavailable in the UI.
4. Select no replacement silently.
5. Require the user to choose an allowed mode, unless Codex explicitly reports a managed default that can be adopted without broadening access.

This replaces the current behavior that automatically resets a disallowed low-level override to inheritance.

## 7. Settings and migration design

### 7.1 New persisted fields

Add stable string-backed fields:

```csharp
public string? PermissionMode { get; set; }
public string? CustomPermissionProfileId { get; set; }
public int ExecutionPolicySchemaVersion { get; set; }
```

Protocol values:

- `ask-for-approval`
- `approve-for-me`
- `custom`

`CustomPermissionProfileId == null` means **Use config.toml default**.

Do not initialize `PermissionMode` at the property declaration. A missing value must remain distinguishable from a user-selected value so migration can inspect legacy fields.

### 7.2 Migration table

| Existing state | Migrated mode | Notes |
| --- | --- | --- |
| `workspace-write` + `on-request` | Ask for approval | Matches the current hardcoded `user` reviewer |
| Both overrides `null` | Custom / config default | Preserves explicit inheritance |
| Any other sandbox/policy combination | Transitional custom legacy state | Preserve exactly; do not reinterpret or broaden |
| Missing legacy fields from pre-approval settings | Ask for approval | Safe new-installation/default migration |

For nonstandard legacy combinations, retain `SandboxModeOverride` and `ApprovalPolicyOverride` until the user explicitly selects one of the new modes. Present the selection as **Custom · Legacy override** with a summary and a one-way “Use config.toml instead” action.

Add an internal migration-only value such as `custom-legacy` if necessary, but do not offer it as a new selectable mode.

### 7.3 Reviewer persistence

Do not add a standalone reviewer setting for normal use:

- Ask derives `user`.
- Approve derives `auto_review`.
- Custom derives no override.

This prevents mode and reviewer fields from drifting apart.

### 7.4 Migration safety

- Migrate through a pure function with table-driven tests.
- Persist the migrated result only after initialization succeeds.
- Preserve unknown string values without crashing.
- Make migration idempotent.
- Continue cloning legacy and new settings in `AppSettingsSnapshot` until the legacy compatibility window ends.

## 8. Core and protocol changes

### 8.1 Core models

Add:

- `CodexPermissionMode`
- `CodexPermissionProfileSummary`
- `CodexPermissionProfileListRequest/Result`
- `CodexPermissionCapabilities`
- `CodexResolvedPermissionMode`
- `CodexActivePermissionProfile`

Extend:

- `CodexExecutionPolicyConfig` with active/default profile information when reliably exposed
- `CodexExecutionPolicyRequirements` with reviewer and profile restrictions
- Thread start/resume/fork and turn-start request models with `PermissionProfileId`
- Thread result models with `ActivePermissionProfile` where available

### 8.2 Serialization rules

For `thread/start`, `thread/resume`, and `thread/fork`:

```json
{
  "permissionProfile": ":workspace",
  "approvalPolicy": "on-request",
  "approvalsReviewer": "user"
}
```

For `turn/start`, use the same `permissionProfile` field. Never include `sandboxPolicy` in the same request.

Legacy fallback continues to use:

- `sandbox` for thread start/resume/fork
- `sandboxPolicy` for turn start

Add a serializer guard that throws before writing if a request contains both a permission profile and a legacy sandbox value.

### 8.3 Profile listing

Implement paginated `permissionProfile/list`:

- Request is scoped to project `cwd`.
- Deduplicate profile IDs defensively while preserving server order.
- Retain disallowed profiles for presentation.
- Ignore unknown response fields.
- Treat an unavailable method as capability absence, not connection corruption.

## 9. Application and lifecycle integration

### 9.1 Single policy resolver

Create one resolver used by every lifecycle path. It receives:

- Saved mode
- Selected custom profile
- Current profile catalog
- Managed requirements
- App-server capabilities

It returns either:

- A valid `CodexResolvedPermissionMode`, or
- A typed unavailable result with a user-facing reason

Use that result consistently for:

- New thread start
- Thread resume
- Thread fork
- Initial turn
- Follow-up turn
- Replacement thread after resume failure

Remove every hardcoded `CodexApprovalsReviewer.User` from `MainViewModel`.

### 9.2 Connection and project lifecycle

- Cache capability/profile results per app-server generation and normalized project `cwd`.
- Invalidate after reconnect, Codex installation changes, project changes, or explicit refresh.
- Do not answer an old connection's discovery request through a new client.
- Keep configuration read failures nonfatal for Ask and Custom default.
- Mark named custom selection unavailable until rediscovered after reconnect.

### 9.3 Running turns

Disable changing permission mode while the selected turn is active. A changed mode applies to the next turn and must not be described as affecting an in-flight action.

## 10. File-by-file change map

### Core

- `src/SynthiaCode.Core/Codex/AppServer/CodexApprovalModels.cs`
  - Add permission-mode, capability, requirement, profile-summary, and active-profile records.
  - Consider splitting execution-policy models into `CodexPermissionModels.cs` to avoid further concentration.
- `src/SynthiaCode.Core/Codex/AppServer/CodexAppServerModels.cs`
  - Add mutually exclusive `PermissionProfileId` to thread/turn lifecycle records.
  - Extend relevant results with active-profile provenance.
- `src/SynthiaCode.Core/Settings/AppSettings.cs`
  - Add new mode/profile/schema fields while retaining legacy fields for migration.
- `src/SynthiaCode.Core/Settings/AppSettingsSnapshot.cs`
  - Clone all new and retained migration fields.

### Infrastructure

- `src/SynthiaCode.Infrastructure/Codex/CodexAppServerClient.cs`
  - Implement `permissionProfile/list` pagination and tolerant method-not-found handling.
  - Parse reviewer/profile requirements.
  - Serialize profile IDs on start/resume/fork/turn.
  - Enforce profile/sandbox mutual exclusion before transport writes.
  - Parse active permission profile from thread results.

### Application services

- `src/SynthiaCode.App/Services/IAppServerSessionCoordinator.cs`
  - Add profile-list/capability operations.
- `src/SynthiaCode.App/Services/AppServerSessionCoordinator.cs`
  - Cache capability state by client generation or expose generation-safe reads.
  - Clear profile state with connection replacement.

### View models

- `src/SynthiaCode.App/ViewModels/ExecutionPolicyViewModel.cs`
  - Replace low-level selection state with mode cards, custom profile selection, effective summary, and availability reasons.
  - Rename to `PermissionModeViewModel` only if the rename improves clarity without unnecessary churn.
- `src/SynthiaCode.App/ViewModels/MainViewModel.cs`
  - Compose the resolver, remove hardcoded reviewers, route resolved selection to all lifecycle requests, and refresh profiles outside the task critical path.

### Views

- `src/SynthiaCode.App/Views/DetailsView.xaml`
  - Replace the two combo boxes with three permission-mode choices.
  - Add custom profile expansion, effective summary, disabled-reason text, refresh, and config shortcut.
- Existing theme resources should be reused unless radio cards need one small semantic selected/disabled style.

### Tests

- `src/SynthiaCode.Tests/ApprovalProtocolTests.cs`
  - Add profile-list and exact serialization coverage.
- `src/SynthiaCode.Tests/ApprovalPresentationTests.cs`
  - Replace low-level selector assertions with permission-mode and custom-profile assertions.
- Add `PermissionModeTests.cs`
  - Resolver, requirements, capabilities, migration, and view-model behavior.
- `src/SynthiaCode.Tests/Program.cs`
  - Register the new test group.

### Documentation

- `README.md`
  - Explain the three modes and Custom/config ownership.
- `docs/current-architecture.md`
  - Document permission-profile discovery and the single resolver.
- `implementation_plan.md`
  - Mark the permission-mode refinement delivered only after all gates pass.
- `server_request_approvals_implementation_plan.md`
  - Add a cross-reference rather than rewriting the completed approval transport history.

## 11. TDD implementation sequence

### Step 1 — Lock profile protocol behavior with failing tests

Add fake-transport tests for:

- `permissionProfile/list` method and `cwd`
- Pagination and profile-order preservation
- `id`, `description`, and `allowed` parsing
- Unknown fields ignored
- Method-not-found converted to legacy capability state
- Reviewer and permission-profile requirements parsed

**Exit criterion:** failures are confined to missing profile protocol support.

### Step 2 — Lock lifecycle serialization with failing tests

For thread start/resume/fork and turn start, assert:

- Ask emits `:workspace`, `on-request`, and `user`
- Approve emits `:workspace`, `on-request`, and `auto_review`
- Custom default omits profile, sandbox, policy, and reviewer
- Named Custom emits only its profile ID among boundary fields
- Profile plus sandbox is rejected before a transport write
- Legacy fallback emits the established sandbox shapes

**Exit criterion:** every lifecycle path has an exact JSON expectation.

### Step 3 — Implement Core models and client support

Add profile records, lifecycle fields, list operation, tolerant capability detection, requirement parsing, mutual-exclusion guards, and active-profile parsing.

**Exit criterion:** Steps 1 and 2 pass without modifying view models.

### Step 4 — Lock the resolver with table-driven failing tests

Cover:

- All three modes under modern capability support
- Ask legacy fallback
- Approve unavailable without auto-review support
- Custom default always omits overrides
- Named profile existence/allowed validation
- Reviewer/profile managed restrictions
- Stale profile behavior
- No silent fallback to broader access

**Exit criterion:** one pure resolver owns all mapping decisions.

### Step 5 — Implement capability and resolution services

Implement the resolver and generation/project-scoped capability state. Ensure discovery is cancellable, non-blocking for normal task submission, and invalidated on lifecycle changes.

**Exit criterion:** resolver tests pass and older-server tests remain green.

### Step 6 — Lock settings migration with failing tests

Use literal legacy JSON and round trips for:

- Safe existing defaults to Ask
- Explicit inheritance to Custom default
- Nonstandard legacy combinations preserved exactly
- Unknown values preserved
- Idempotent migration
- Custom named profile round trip

**Exit criterion:** no existing settings file can silently broaden access.

### Step 7 — Implement settings and view-model migration

Add new fields, migration function, mode/profile state, allowed/disabled reasons, and effective summary. Retain migration-only legacy state.

**Exit criterion:** settings and view-model tests pass independently of WPF.

### Step 8 — Lock MainViewModel lifecycle integration

Extend existing lifecycle tests to prove the resolved mode reaches:

- New thread
- Resume
- Fork
- First and follow-up turns
- Replacement thread after resume failure

Add a static regression forbidding hardcoded `CodexApprovalsReviewer.User` in `MainViewModel`.

**Exit criterion:** no lifecycle path bypasses the resolver.

### Step 9 — Implement and test the WPF surface

Add presentation tests for:

- Exact three mode labels
- Descriptions and effective reviewer
- Custom expansion and profile descriptions
- Disabled managed profiles
- Keyboard navigation and focus
- Screen-reader names
- Compact width, long profile IDs, dark/light/System themes, and high text scale
- Mode controls disabled during active turns

**Exit criterion:** the low-level primary dropdowns are removed and every mode is understandable without raw diagnostics.

### Step 10 — Full verification and documentation

Run:

1. Focused new test groups
2. Complete console behavioral suite
3. Debug solution build
4. Release solution build
5. `git diff --check`
6. Controlled live smoke with the discovered Codex version
7. Optional older-Codex compatibility smoke if a supported fixture/binary is available

Update documentation only after behavior is verified.

## 12. Detailed test matrix

### Protocol

- Profile list empty, one page, and multiple pages
- Duplicate IDs defensively deduplicated
- Disallowed profiles retained
- String descriptions and missing descriptions
- Profile field on every lifecycle request
- Mutual exclusion on every lifecycle request
- `user` and `auto_review` exact wire values
- Custom omission exactness
- Active profile parsing

### Resolver and requirements

- Modern Ask and Approve
- Legacy Ask
- Legacy Approve supported/unsupported
- Custom default
- Custom named allowed, denied, missing, and stale
- `on-request` denied
- `user` denied
- `auto_review` denied
- `:workspace` denied
- Managed default available/unavailable
- Reconnect invalidation
- Project-local profile catalog changes

### Migration and persistence

- Pre-approval settings file
- Current workspace/on-request settings
- Explicit null inheritance
- Read-only/on-request
- Workspace/untrusted
- Full-access/never
- Unknown values
- Repeated migration
- Snapshot and JSON round trip

### Presentation

- Mode label and description correctness
- Effective user versus automatic reviewer
- Config-default pending resolution
- Named profile details
- Disabled reason visible
- No hidden automatic broadening
- Keyboard selection does not accidentally approve a pending server request
- Approval overlay still supersedes settings interaction

### Lifecycle and recovery

- Parallel threads use the mode resolved at their request time
- A reconnect cannot apply stale profile discovery
- Switching project refreshes project-local profiles
- An in-flight turn retains its original mode
- The next turn receives a changed mode
- Shutdown cancels discovery without delaying disposal
- Config/profile endpoint failure does not block Ask or Custom default

## 13. Live smoke matrix

Use a disposable version-controlled workspace:

1. **Ask for approval**: in-workspace edit proceeds; out-of-workspace request reaches the user queue.
2. **Approve for me**: same workspace boundary; eligible escalation is automatically reviewed.
3. **Custom default**: configured `default_permissions` is reported as active.
4. **Custom named**: selected profile is reported as active and its deny rules are enforced.
5. **Managed profile denial**: disallowed profile is disabled and never sent.
6. **Network rule**: custom profile allow/deny behavior matches its configured domains.
7. **Reconnect**: selected mode survives; profile catalog is refreshed for the new generation.
8. **Legacy fallback**: Ask serializes workspace-write when profile listing is unsupported.

Never run the live matrix against a production repository or with a profile that grants unbounded access.

## 14. Observability and privacy

Add structured events without logging command text, secrets, profile rule contents, or approval payloads:

- `permission_profile_catalog_loaded`
- `permission_profile_catalog_unavailable`
- `permission_mode_resolved`
- `permission_mode_unavailable`
- `permission_mode_migrated`

Safe fields include mode, profile ID, legacy-fallback flag, Codex version, catalog count, and elapsed time. Profile IDs are user-controlled strings; sanitize or hash them if logs can leave the machine.

## 15. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Permission profiles are beta and may evolve | Isolate parsing/serialization, ignore unknown fields, gate by capability, retain legacy fallback |
| Profile and sandbox accidentally sent together | Core invariant plus pre-write serializer guard and exact JSON tests |
| Auto-review is mistaken for broader access | Keep the same `:workspace` boundary and state reviewer distinction in UI |
| Custom silently becomes Full access | Never infer from catalog order; require explicit profile ID/default and respect managed restrictions |
| Migration changes a nonstandard legacy policy | Preserve it as transitional Custom legacy state until explicit user action |
| Optional discovery blocks tasks | Run outside critical submission path with cancellation and cached capability state |
| Project-local config changes profile catalog | Scope cache by normalized `cwd` and provide refresh/invalidation |
| Managed policy changes mid-session | Revalidate before serialization and never send unavailable selections |
| Named profile disappears | Mark stale; do not substitute another profile |

## 16. Acceptance criteria

The work is complete when:

- The primary UI presents exactly Ask for approval, Approve for me, and Custom.
- Ask uses workspace boundaries, `on-request`, and the user reviewer.
- Approve uses the identical workspace boundary, `on-request`, and automatic review.
- Custom default follows Codex configuration without SynthiaCode policy/reviewer/sandbox overrides.
- Custom named profiles come from `permissionProfile/list` and respect `allowed` plus managed requirements.
- Permission profile and legacy sandbox fields are never sent together.
- One resolver controls every thread and turn lifecycle path.
- No hardcoded user reviewer remains in `MainViewModel`.
- Existing safe settings migrate predictably and nonstandard settings are preserved without broadening access.
- The approval queue continues to handle human-required requests in every mode.
- Older app-server behavior is explicit and tested.
- The complete behavioral suite passes.
- Debug and Release builds succeed with zero warnings.
- Final diff validation and documentation updates are complete.

## 17. Explicit non-goals

- Editing or rewriting `config.toml`
- Providing a visual editor for filesystem/network profile rules
- Exposing Full access as one of the three requested modes
- Replacing the existing server-request approval queue
- Applying permission changes to an in-flight turn
- Persisting managed requirements or profile catalogs as authority
- Supporting legacy `guardian_subagent` as a selectable reviewer

## 18. Recommended delivery slices

1. **Protocol and resolver:** profile listing, lifecycle fields, requirements, exact mappings, legacy fallback.
2. **Persistence and migration:** new mode settings and preservation of legacy combinations.
3. **Settings UX:** three modes, Custom profile list, effective/managed summary.
4. **Lifecycle hardening:** all request paths, reconnect/project invalidation, active-profile provenance.
5. **Verification and docs:** full test/build/live gates and user-facing documentation.

Do not merge a slice that can select a new mode before its lifecycle serialization and managed-requirement tests are complete.
