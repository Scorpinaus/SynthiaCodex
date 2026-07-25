# Phase 6A: Skills and Settings Implementation Plan

**Status:** Implemented and verified  
**Prepared:** 25 July 2026  
**Target runtime audited:** `codex-cli 0.145.0`  
**Scope:** Skills discovery and management, plus a typed effective-settings overview. Plugins and MCP management remain separate Phase 6 follow-ups.

## Implementation Result

Phase 6A was completed test-first on 25 July 2026:

- five red tests first established the protocol, compatibility, redaction, view-model, accessibility, and virtualization contract;
- typed Core records and Infrastructure parsers now expose `skills/list`, `skills/config/write`, and an allowlisted `config/read` result through the existing `AppServerSessionCoordinator`;
- `SkillsViewModel` uses absolute paths as identity, retains partial results, composes search/scope filtering, prevents duplicate row writes, performs authoritative forced refreshes after toggles, and debounces `skills/changed`;
- `SkillsSettingsView` is integrated beside the existing shared configuration surface and includes local Editor/Explorer actions plus a redacted, origin-aware effective-settings overview;
- context changes, hidden Settings, reconnects, stale requests, unsupported runtime methods, and shutdown subscriptions follow the bounded lifecycle described below;
- all 209 behavioral tests pass in Debug and Release, non-incremental Debug and Release builds complete with zero warnings and errors, and the self-contained portable publish gate succeeds.

The implemented scope deliberately leaves skill authoring/install, arbitrary roots, `$skill` composer invocation, MCP management, and plugins to later bounded phases.

## 1. Objective

Add a native Skills surface to SynthiaCode and finish the read-oriented portion of Phase 6 Settings without duplicating configuration features that already exist.

The implementation should:

- treat Codex app-server as the source of truth for discovered skills and effective configuration;
- show skills for the active project, worktree, or General workspace;
- expose skill metadata, scope, dependencies, load errors, paths, and effective enabled state;
- enable or disable a skill through `skills/config/write`, using its absolute `SKILL.md` path as identity;
- react to external skill changes through `skills/changed`;
- show a small allowlisted summary of effective Codex settings and their origins;
- preserve the existing isolated SynthiaCode `CODEX_HOME`, settings persistence, permission resolution, model controls, and shared-file editors;
- avoid adding a second configuration store or parsing arbitrary TOML in the UI.

## 2. Source and Protocol Baseline

This plan is based on three sources:

1. The repository implementation and its 204-test Debug baseline.
2. The generated app-server v2 schemas under `schemas/v2/`, produced for the runtime line currently used by the project.
3. Current official Codex documentation:
   - Skills: <https://developers.openai.com/codex/concepts/customization#skills>
   - Building and locating skills: <https://learn.chatgpt.com/docs/build-skills>
   - App-server methods: <https://learn.chatgpt.com/docs/app-server#api-overview>

The checked-in 0.145.0-compatible schemas define:

- `skills/list` with `cwds` and `forceReload`;
- `skills/config/write` with `path`, optional `name`, and `enabled`;
- `skills/changed` as an invalidation notification;
- skill scopes `user`, `repo`, `system`, and `admin`;
- metadata for name, description, short description, UI interface, path, scope, dependencies, enabled state, and per-working-directory errors.

Current public documentation also describes newer `skills/list` options such as extra user roots and `skills/extraRoots/set`. Those options are not present in the checked-in 0.145.0 schemas and are explicitly deferred until SynthiaCode refreshes its generated protocol baseline.

## 3. Audit: What Is Already Implemented

### 3.1 SynthiaCode settings persistence

Already complete:

- `AppSettings` stores SynthiaCode-owned UI and workflow preferences.
- `JsonSettingsStore` writes settings atomically.
- `CoalescingSettingsStore` snapshots and coalesces frequent saves.
- Existing settings files retain backward-compatible property names.
- Theme, layout, follow-up behavior, model, reasoning, service tier, instructions, attachment drafts, permissions, and thread state already persist.

Phase 6A must not introduce a second SynthiaCode settings file or persist Codex-owned skill state in `AppSettings`.

### 3.2 Codex runtime and account settings

Already complete:

- Codex executable discovery and version diagnostics.
- SynthiaCode's isolated runtime home at `%LOCALAPPDATA%\SynthiaCode\codex-home`.
- ChatGPT and device-code sign-in entry points, sign-out, account identity, plan context, rate limits, and diagnostics.
- Codex executable path and `CODEX_HOME` visibility in Settings diagnostics.
- `codex doctor` and refresh actions.

The existing isolated runtime-home policy is the current product behavior. Changing to the process-wide or user-default `CODEX_HOME` is not part of Phase 6A.

### 3.3 Models, instructions, and execution policy

Already complete:

- `model/list` discovery.
- Model, reasoning-effort, and service-tier selection.
- Per-chat capture of custom developer and optional base instructions.
- Three permission modes with `permissionProfile/list`.
- `config/read` for the effective execution-policy subset.
- `configRequirements/read` for managed restrictions.
- Consistent policy serialization across thread and turn lifecycle requests.

The Skills work must reuse the existing app-server session and active-workspace resolver. It must not add another model, profile, sandbox, or approval selector.

### 3.4 Shared Codex configuration

Already complete:

- Built-in editors for the isolated shared `AGENTS.md` and `config.toml`.
- Strict UTF-8 and 512 KiB bounds.
- Atomic replace and SHA-256 revision-based stale-write protection.
- Explicit save, refresh, Editor, and Explorer actions.
- Ordered shared/workspace provenance for `AGENTS.md` and `.codex/config.toml`.
- Preservation of unsaved edits during automatic refresh.
- Focused storage, view-model, and WPF tests.

This is the largest overlap with the original Phase 6 outline. It should remain intact. Phase 6A adds a typed effective-settings summary beside the raw editor; it does not replace or rebuild the editor.

### 3.5 Skill and MCP runtime behavior

Partially complete:

- Codex can already load skills through its runtime.
- Generic app-server notifications already flow through `AppServerSessionCoordinator`.
- MCP tool activity and progress can already appear in the task transcript.
- Generated schemas exist for skills, plugins, MCP status, and configuration APIs.

Missing:

- typed skill protocol models and parsers;
- coordinator methods for listing and enabling/disabling skills;
- a Skills view model;
- skill discovery, filtering, details, errors, and enabled-state UI;
- `skills/changed` invalidation handling;
- a typed effective-settings summary beyond the execution-policy subset;
- focused tests and documentation for these workflows.

## 4. Scope Boundaries

### In scope

- Skills for the currently active execution context.
- User, repository, admin, and system scope presentation.
- Readable metadata and dependency presentation.
- Search and scope filters.
- Manual refresh with forced disk rescan.
- Automatic invalidation after `skills/changed`.
- Enable/disable through `skills/config/write` by absolute path.
- Editor and Explorer deep links for local skill paths.
- Per-CWD discovery errors and partial-result presentation.
- Read-only effective settings for a small, non-sensitive allowlist.
- Configuration-origin labels when app-server supplies them.
- Runtime compatibility behavior for unsupported methods.

### Out of scope

- Creating or editing a skill inside SynthiaCode.
- Installing curated skills or downloading skills from repositories.
- Adding arbitrary skill roots.
- A composer `$skill` picker or autocomplete.
- Reading and rendering the complete `SKILL.md` body.
- Plugin browse, install, update, uninstall, or connector authorization.
- MCP add/remove/edit, OAuth, resource, or tool-call UI.
- Directly editing structured model, web-search, MCP, or plugin TOML.
- Replacing the existing raw shared-file editors.
- Changing SynthiaCode's isolated `CODEX_HOME`.
- Automatically restarting active threads when a skill changes.

Plugin endpoints are specifically deferred because the official app-server documentation still marks the production plugin list/read/install/uninstall methods as under development.

## 5. Product Decisions

### 5.1 Context is explicit

Skills are resolved for the same path SynthiaCode would use to start a task:

1. active worktree path;
2. active project/current-checkout path;
3. contained General workspace path.

The Skills header shows that path and labels it as Worktree, Project, or General. Switching the active thread or workspace invalidates the displayed result and loads the new context only when Settings is visible or the user explicitly refreshes.

The first implementation sends one CWD to `skills/list`. Multi-CWD aggregation is deferred because it creates duplicate rows, ambiguous enablement actions, and unnecessary scans when Settings only needs the active execution context.

### 5.2 Absolute path is the skill identity

Codex permits two skills with the same name to appear. SynthiaCode therefore:

- keys rows by normalized absolute `SKILL.md` path;
- uses `path` for `skills/config/write`;
- treats `name` as presentation metadata, not a unique identifier;
- preserves duplicate names and disambiguates them with scope and path.

### 5.3 Codex owns enabled state

Skill enablement is not stored in `AppSettings`. The UI sends:

```json
{
  "path": "C:\\absolute\\path\\to\\SKILL.md",
  "enabled": false
}
```

After a successful write, SynthiaCode uses `effectiveEnabled` from the response and then performs a forced refresh. On failure, the row returns to its last server-confirmed value and shows an actionable error.

This narrow write is an intentional exception to the original Phase 6 read-only posture because it uses a dedicated app-server operation rather than rewriting arbitrary TOML. The raw `config.toml` editor remains the advanced escape hatch.

### 5.4 Refresh is event-driven and bounded

- Initial load uses `forceReload: false`.
- The visible Refresh action uses `forceReload: true`.
- `skills/changed` is treated only as invalidation, as required by the protocol description.
- Notifications are debounced and followed by a new `skills/list` request.
- Concurrent loads are latest-request-wins; stale results never replace a newer CWD.
- Repeated notifications while Settings is hidden mark the cache stale without forcing foreground work.

### 5.5 Partial results stay usable

`skills/list` can return both skills and errors for a CWD. SynthiaCode shows valid skills and a non-modal warning summary at the same time. One malformed skill must not blank the entire directory.

### 5.6 Effective settings are allowlisted

The effective-settings summary reads only fields that are already relevant to the product:

- model;
- model provider when available;
- reasoning effort;
- service tier;
- active profile;
- sandbox mode;
- approval policy;
- approval reviewer;
- web-search mode;
- sandbox network access.

It also shows origin metadata for those keys when available and the existing executable path and isolated `CODEX_HOME`.

The parser must discard all other `config/read` content. Raw JSON, secrets, environment values, MCP headers, and unrelated configuration must not be stored, rendered, or logged.

## 6. Target Architecture

```text
DetailsView / SkillsSettingsView
        |
        +-- SkillsViewModel
        |     +-- active CWD resolver
        |     +-- search/scope projection
        |     +-- latest-wins refresh
        |     +-- enable/disable command
        |     +-- skills/changed invalidation
        |
        +-- CodexConfigurationViewModel (existing)
        |     +-- raw shared AGENTS.md/config.toml editing
        |     +-- source provenance
        |
        +-- EffectiveCodexSettingsViewModel
              +-- allowlisted config/read projection
              +-- origin labels

IAppServerSessionCoordinator
        |
CodexAppServerClient
        +-- skills/list
        +-- skills/config/write
        +-- config/read (typed safe subset)
```

The protocol types remain in Core, JSON serialization/parsing remains in Infrastructure, and WPF state remains in App.

## 7. Proposed Code Changes

### 7.1 Core

Add `src/SynthiaCode.Core/Codex/AppServer/CodexSkillModels.cs` with:

- `CodexSkillScope`;
- `CodexSkillListRequest`;
- `CodexSkillListResult`;
- `CodexSkillContextResult`;
- `CodexSkillMetadata`;
- `CodexSkillInterface`;
- `CodexSkillDependencies`;
- `CodexSkillToolDependency`;
- `CodexSkillLoadError`;
- `CodexSkillConfigWriteRequest`;
- `CodexSkillConfigWriteResult`.

Add `src/SynthiaCode.Core/Codex/Configuration/CodexEffectiveConfigurationModels.cs` with an allowlisted effective-settings snapshot and origin metadata. Do not expose a generic `JsonObject` in Core.

### 7.2 Infrastructure

Extend `CodexAppServerClient` with:

- `ListSkillsAsync(CodexSkillListRequest, CancellationToken)`;
- `WriteSkillConfigAsync(CodexSkillConfigWriteRequest, CancellationToken)`;
- a new typed effective-configuration read that does not alter the existing execution-policy method.

Parsing requirements:

- tolerate unknown fields;
- preserve unknown dependency types as strings;
- map unknown scope values to an explicit `Unknown` presentation value;
- reject missing required path/name/description fields per row without failing unrelated rows;
- keep per-CWD errors;
- never log response bodies;
- translate `-32601` into an unsupported-capability result rather than a fatal session failure.

The existing generic request correlation and notification routing are reused unchanged.

### 7.3 App-server coordinator

Extend `IAppServerSessionCoordinator` and `AppServerSessionCoordinator` with pass-through methods for:

- skills list;
- skill config write;
- safe effective-settings read.

Do not create a second app-server client. Skills and Settings must share the current initialized session and recovery lifecycle.

### 7.4 App view models

Add `SkillsViewModel` with:

- observable source and filtered skill collections;
- search text and scope filter;
- current context label/path;
- counts for all/enabled/disabled/errors;
- loading, empty, stale, unsupported, disconnected, partial-error, and ready states;
- refresh, toggle, open-in-editor, and reveal-in-Explorer commands;
- notification and session-state handlers;
- cancellation and generation IDs for latest-request-wins updates.

Add `SkillItemViewModel` only if row-specific busy/error state makes direct immutable records awkward. A row must prevent duplicate toggle requests while its write is in flight.

Add `EffectiveCodexSettingsViewModel` or extend `CodexConfigurationViewModel` only if the new responsibilities remain cohesive. The preferred design is a separate view model because raw file editing and effective server state have different refresh, failure, and ownership semantics.

`MainViewModel` remains shell wiring only:

- construct the new view models;
- expose them for binding;
- notify them when the active workspace changes;
- refresh them when Settings opens;
- dispose their subscriptions during shutdown.

### 7.5 WPF views

Add `src/SynthiaCode.App/Views/SkillsSettingsView.xaml` and its minimal code-behind.

Place a Skills card near the existing Codex configuration card in `DetailsView`. The card should contain:

- active-context label and path;
- search box;
- scope filter;
- Refresh action;
- enabled/total/error summary;
- virtualized skill list;
- clear empty, unsupported, disconnected, loading, and partial-error states.

Each row should show:

- interface display name, falling back to skill name;
- short description, falling back to description;
- scope badge;
- enabled toggle;
- path;
- optional dependency summary;
- optional icon only after applying the existing local-image safety policy;
- Editor and Explorer actions.

Accessibility:

- label search, filter, refresh, toggle, and path actions;
- include skill name and scope in toggle automation names;
- retain visible text for enabled/disabled state;
- preserve keyboard focus after refresh when the same path remains;
- do not use color as the only scope or error signal.

### 7.6 Test registration and documentation

Add `src/SynthiaCode.Tests/SkillsSettingsTests.cs` and register it in the existing console test runner.

Update after implementation:

- `README.md`;
- `docs/current-architecture.md`;
- `feature_parity.md`;
- the Phase 6 status in `implementation_plan.md`;
- the test-count references that are intended to remain current.

## 8. Implementation Slices

### Slice 0: Lock the contract with failing tests

Add focused red tests for:

- `skills/list` request shape;
- all documented metadata and error fields;
- duplicate-name preservation by path;
- `skills/config/write` path-based request and `effectiveEnabled` response;
- unsupported-method fallback;
- `skills/changed` invalidation;
- active-CWD switching and stale-result rejection;
- Settings UI controls and accessible names;
- effective-config allowlisting and redaction.

Exit condition: failures identify missing production types and bindings, not test harness errors.

### Slice 1: Typed protocol support

Implement Core records, app-server serialization/parsing, coordinator pass-through methods, and fake-transport tests.

Exit condition:

- protocol tests pass;
- no WPF changes yet;
- existing execution-policy reads behave exactly as before;
- unsupported skills methods do not disconnect the app-server session.

### Slice 2: Skills state and refresh lifecycle

Implement `SkillsViewModel`, active-context resolution, filters, manual refresh, `skills/changed` debounce, latest-request-wins behavior, and row-level enable/disable state.

Exit condition:

- view-model tests pass for General, project, and worktree paths;
- partial errors retain valid rows;
- a failed toggle rolls back to confirmed state;
- hidden Settings does not trigger repeated scans.

### Slice 3: Native Skills settings UI

Implement `SkillsSettingsView`, integrate it into `DetailsView`, add virtualization and accessibility, and wire Editor/Explorer actions through the existing user-interaction service.

Exit condition:

- the Skills card is keyboard-operable at narrow and normal widths;
- empty, disconnected, unsupported, loading, partial-error, and populated states are covered;
- duplicate names are visibly distinguishable;
- long paths and descriptions wrap or trim without widening the inspector.

### Slice 4: Typed effective-settings overview

Add the separate allowlisted `config/read` projection and a compact read-only overview in Settings.

Do not remove:

- composer model/reasoning/service-tier controls;
- the permission selector;
- raw `AGENTS.md` and `config.toml` editors;
- runtime/account diagnostics.

Exit condition:

- displayed values match the active CWD's effective configuration;
- origin labels update when the active workspace changes;
- managed or missing values are represented explicitly;
- unallowlisted and sensitive values never enter presentation state or logs.

### Slice 5: Compatibility, regression, and documentation

Run the complete behavioral suite, clean Debug and Release builds, and the portable publish gate. Manually verify against the installed Codex runtime.

Update the architecture and parity documents only after the implementation and verification results are known.

## 9. Test Plan

### Protocol tests

- `skills/list` sends the exact active CWD and correct `forceReload`.
- Empty or missing CWD arrays follow the chosen client contract.
- User, repo, system, admin, and unknown scopes parse safely.
- Interface fields and dependency fields are optional and tolerant.
- Per-CWD errors retain path and message.
- Duplicate names with different paths both survive.
- Enable/disable sends an absolute `path`, not a name-only selector.
- `effectiveEnabled` is authoritative.
- `-32601` produces an unsupported state.
- Cancellation removes pending requests without harming the session.

### View-model tests

- General, project, and worktree contexts resolve to the correct CWD.
- Switching contexts cancels or supersedes the previous result.
- Search matches display name, canonical name, description, scope, dependency, and path.
- Scope filtering composes with search.
- Valid rows remain visible when another skill fails to load.
- `skills/changed` debounces repeated events.
- Hidden Settings marks data stale and visible Settings refreshes once.
- Toggle success refreshes and preserves selection/focus identity.
- Toggle failure restores the previous enabled state.
- Open/Reveal commands validate local paths and report failures without crashing.
- Shutdown cancels in-flight refresh and detaches notification handlers.

### WPF tests

- Skills search, scope filter, refresh, list, and toggles exist.
- The list uses recycling virtualization.
- Interactive elements have accessible names.
- Long names, descriptions, and Windows paths remain bounded.
- Empty, unsupported, disconnected, loading, partial-error, and ready states are mutually understandable.
- Existing shared configuration editors and provenance remain present.

### Effective-settings tests

- Only the allowlisted keys are parsed.
- Origins are associated with the correct keys.
- Missing values render as inherited or unavailable, not as false defaults.
- Arbitrary additional configuration is discarded.
- Raw response bodies and sensitive values are absent from logs.
- The existing execution-policy parser and resolver tests remain unchanged and passing.

### Manual verification

- No skills installed.
- Built-in/system skills only.
- User skill under the user's skill directory.
- Repository skill at the root and a nested `.agents/skills` directory.
- Duplicate skill names in different scopes.
- Invalid `SKILL.md` next to valid skills.
- Skill with UI metadata and tool dependencies.
- Enable, disable, restart, and re-enable.
- External edit causes `skills/changed`.
- Project switch, worktree switch, and General chat.
- Path with spaces and Unicode.
- App-server unavailable or too old.
- Narrow inspector, high DPI, light, dark, and high contrast.

## 10. Compatibility and Failure Behavior

- The minimum supported behavior is capability detection, not a hard Codex version string check.
- If `skills/list` is unsupported, Settings shows the installed version and an upgrade hint; the rest of the app remains usable.
- If `skills/config/write` alone is unsupported, the directory remains read-only and Editor/Explorer actions remain available.
- App-server reconnect marks skill and effective-setting data stale and refreshes once after reconnection when Settings is visible.
- A malformed skill produces a partial warning, not a settings-wide failure.
- A config write never silently edits by name when a path is available.
- No skill content or effective-config response body is written to logs.

## 11. Risks and Mitigations

### Protocol drift

Risk: public app-server documentation can advance faster than the checked-in generated schemas.

Mitigation:

- implement the 0.145.0 checked-in request shapes;
- tolerate additive response fields;
- use `-32601` capability fallback;
- refresh schemas as an explicit future maintenance step;
- do not send newer extra-root fields until their schemas are adopted.

### Duplicate or unstable identity

Risk: skill names are not unique.

Mitigation: normalize and key by absolute path everywhere, including row state, selection restoration, writes, and tests.

### Configuration ownership confusion

Risk: users may not know whether a toggle edits SynthiaCode settings or Codex settings.

Mitigation: label the section as Codex Skills, state that changes affect the isolated SynthiaCode Codex runtime, and never mirror enabled state into `AppSettings`.

### Settings panel growth

Risk: adding Skills and effective settings makes the existing inspector too dense.

Mitigation: use a dedicated child view, collapsed advanced sections, search/filter controls, and a virtualized list. A broader Settings navigation redesign is not required for this slice.

### Refresh storms

Risk: file watchers and workspace changes can trigger repeated skill scans.

Mitigation: debounce invalidations, load on visibility, use one active CWD, cancel stale requests, and keep manual forced refresh explicit.

### Unsafe metadata rendering

Risk: icons or paths supplied by a skill could reference unsafe or missing locations.

Mitigation: treat metadata as untrusted display data, reuse the local-image resource policy, validate paths before actions, and fall back to text without failing the row.

## 12. Acceptance Criteria

Phase 6A Skills and Settings is complete when:

- Settings lists skills for the active General, project, or worktree context.
- Every row shows a stable identity, name, description, scope, path, and enabled state.
- Duplicate names and partial load errors are presented correctly.
- Users can search, filter, refresh, open, and reveal skills.
- Supported runtimes can enable and disable a skill through `skills/config/write`.
- External skill changes invalidate and refresh the visible list without restarting SynthiaCode.
- Unsupported or disconnected runtimes degrade to clear, nonfatal states.
- Settings shows an allowlisted effective Codex configuration summary with origin information.
- Existing raw shared-file editing, provenance, model controls, permissions, diagnostics, authentication, and settings persistence remain unchanged.
- No new Codex-owned state is duplicated into `AppSettings`.
- The full behavioral suite, Debug build, Release build, and portable publish verification pass.

## 13. Recommended First Implementation PR

Keep the first implementation reviewable:

1. Add failing protocol and view-model tests.
2. Add typed skill models.
3. Implement `skills/list` and `skills/config/write`.
4. Add coordinator pass-through methods.
5. Add `SkillsViewModel` with active-CWD refresh and invalidation.
6. Add the virtualized Skills card to Settings.
7. Verify the focused tests and complete regression suite.

Defer the effective-settings overview to the next PR if the first PR becomes larger than the existing feature-test pattern comfortably supports. This preserves a usable vertical slice while keeping all previously completed Settings work intact.
