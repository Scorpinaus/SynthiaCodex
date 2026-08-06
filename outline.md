# Harness-Agnostic Runtime Migration

## 1. Task Summary

- **Title:** Decouple SynthiaCode from the Codex harness
- **Owner:** Codex
- **Status:** In progress
- **Last updated:** 2026-08-04
- **Target:** Post-0.1.0 architecture migration

### Objective

Make SynthiaCode's application workflows, conversation state, persistence, and base presentation independent of a specific agent harness while preserving the existing Codex app-server experience.

### Problem Statement

The repository has project-level layering, but the application boundary and Core state still expose Codex installation, request, notification, model, permission, skill, account, timeline, and conversation types. A second runtime would therefore require changes across Core, workflows, persistence, and WPF presentation rather than one isolated adapter.

### Desired Outcome

SynthiaCode owns neutral conversation semantics and local identity. Harness adapters own discovery, transport, protocol decoding, and optional provider features. Existing settings load as Codex conversations, and a second in-memory harness proves that base workflows do not require Codex-specific changes.

## 2. Scope

### In Scope

- Neutral harness descriptors, capabilities, commands, results, content, events, approvals, models, and feature contracts.
- An application-layer registry/session boundary and a Codex adapter over the existing app-server implementation.
- Migration of thread, turn, follow-up queue, conversation reduction, and persistence workflows to neutral contracts.
- Stable local conversation identity plus harness and remote conversation identity with legacy settings migration.
- Capability-driven presentation for optional runtime behavior and isolation of Codex-specific account, skills, configuration, and policy features.
- An in-memory second harness, shared contract tests, architecture tests, and updated documentation.

### Out of Scope

- Shipping a production Claude, Gemini, or other third-party harness adapter.
- Runtime loading of untrusted external assemblies or a public plugin ABI.
- Translating an existing remote conversation from one harness into another harness's native history.
- Changing existing safety defaults, approval semantics, or worktree ownership rules.

### Assumptions

- Existing conversations without provider metadata belong to the `codex` harness.
- A conversation remains pinned to its creating harness; changing harness creates a separate conversation.
- Git, worktree, terminal, attachments, speech, theme, and general workspace services remain host-owned facilities.
- Codex-only features remain available through optional harness features rather than defining the base contract.

### Open Questions

- No blocking product decision is required for the compatibility-first migration. A production second-harness selection UX can be refined after the in-memory proof establishes the boundary.

## 3. Current-State Evidence

- **Relevant entry points:** `AppServices.Create`, `IAppServerSessionCoordinator`, `MainViewModel`, `TurnExecutionUseCaseService`, `ThreadLifecycleUseCaseService`, `ConversationWorkflowController`, `CodexThreadService`, `AppSettings`, and `ThreadStore`.
- **Existing behavior:** Codex app-server owns remote threads and turns; SynthiaCode persists a bounded local projection and provides queueing, steering, rollback, approvals, skills, model settings, account status, worktrees, terminal, and attachments.
- **Related documentation:** `README.md`, `docs/current-architecture.md`, and `feature_parity.md`.
- **Known constraints:** Preserve JSON compatibility, fail closed for stale permissions, retain cancellation and notification ordering, avoid blocking the WPF dispatcher, and preserve the current dirty release-automation changes.
- **Baseline validation:** `dotnet test SynthiaCode.sln` passed 272 tests on 2026-08-04 after an escalated NuGet restore. The initial sandboxed restore failed with NU1301 and made no source changes.

## 4. Requirements

### Functional Requirements

1. Base application workflows depend only on neutral harness contracts.
2. The Codex app-server remains fully usable through a registered Codex adapter.
3. Protocol notifications are translated into semantic events before conversation state reduction.
4. Persisted conversations identify their local record, harness, and remote conversation separately.
5. Settings written before this migration load as Codex-backed conversations without losing transcript, queue, attachment, worktree, or shell state.
6. Optional UI behavior follows runtime capabilities and feature availability.
7. A deterministic in-memory harness can create, stream, cancel, persist, and restore a conversation through the same application boundary.

### Non-Functional Requirements

- **Reliability:** Preserve event ordering, recovery, queue durability, bounded histories, cancellation, and idempotent shutdown.
- **Performance:** Preserve notification batching and avoid additional per-delta UI dispatch or unbounded buffers.
- **Security and privacy:** Keep credentials provider-owned, never persist secrets, preserve approval defaults, and avoid raw sensitive provider configuration in neutral presentation state or logs.
- **Accessibility:** Capability-driven controls must retain existing keyboard, focus, high-contrast, and responsive behavior.
- **Compatibility:** Continue targeting Windows and .NET 10; preserve Codex app-server and legacy `settings.json` compatibility.

### Acceptance Criteria

- [ ] Core conversation state contains no Codex protocol notification or wire-method dependency.
- [ ] Application workflow inputs and outputs contain no Codex protocol request types.
- [ ] Existing Codex behavior passes the current test suite through the adapter.
- [ ] Legacy persisted threads acquire `HarnessId = "codex"` and a remote identity without data loss.
- [ ] Unsupported optional features are not invoked and are presented deterministically.
- [ ] A second in-memory harness passes the shared harness contract tests.
- [ ] `dotnet test SynthiaCode.sln` passes.
- [ ] `git diff --check` reports no errors.
- [ ] Architecture and user-facing documentation describe the new boundary.

## 5. Proposed Design

### Approach

Use a strangler migration. Introduce neutral contracts beside the existing Codex implementation, wrap the current coordinator in a Codex adapter, migrate one vertical workflow at a time, and keep protocol types inside the adapter. Avoid a public dynamic plugin ABI until two real adapters demonstrate the stable contract.

### Affected Components

| Area | Expected change | Reason |
| --- | --- | --- |
| `SynthiaCode.Core` | Neutral conversation, identity, capability, event, content, model, approval, and persistence types | Own product semantics without protocol knowledge |
| `SynthiaCode.Application` | New harness ports, registry, application session coordination, and use-case boundaries | Keep workflows independent of WPF and concrete transports |
| `SynthiaCode.Infrastructure` | Retain shared infrastructure and move Codex translation behind an adapter boundary | Isolate protocol and process behavior |
| `SynthiaCode.App` | Compose harnesses and consume neutral/capability-driven state | Prevent presentation from constructing protocol requests |
| `SynthiaCode.Tests` | Adapter, migration, architecture, fake-harness, failure, and cancellation coverage | Prove compatibility and extensibility |
| Documentation | Architecture, persistence, extension, and feature-availability guidance | Make the new ownership explicit |

### Data and Control Flow

```text
WPF intent
  -> neutral application command
  -> active harness session
  -> harness adapter
  -> provider protocol/process
  -> raw provider message
  -> adapter translation
  -> semantic HarnessEvent
  -> neutral conversation reducer
  -> persistence and WPF projection
```

Each persisted conversation has a SynthiaCode-owned local ID, a `HarnessId`, and an optional remote conversation ID. Optional features are obtained from the active session only after capability checks.

### Alternatives Considered

| Alternative | Benefit | Reason not selected |
| --- | --- | --- |
| Rename existing `Codex*` types only | Small textual change | Leaves wire assumptions and the monolithic coordinator intact |
| Put every operation on one harness interface | Simple discovery | Forces unsupported methods and lowest-common-denominator behavior |
| Dynamic assembly plugins immediately | External extensibility | Premature ABI and security burden before the contract is proven |
| Keep remote thread ID as the local key | Minimal persistence work | Risks collisions and prevents robust offline/local lifecycle ownership |

## 6. Implementation Plan

### Phase 1: Confirm Baseline

- [x] Trace the current behavior through the affected layers.
- [x] Record and preserve unrelated worktree changes.
- [x] Run the authoritative baseline suite: 272 tests passed.
- [x] Record compatibility and safety constraints.

### Phase 2: Neutral Boundary and Codex Adapter

- [ ] Add the Application project and neutral harness abstractions.
- [ ] Add registry, session, capability, feature, command, result, and semantic event contracts.
- [ ] Implement a Codex adapter over the existing session coordinator.
- [ ] Migrate thread and turn workflow services to neutral contracts.
- [ ] Add explicit failure, cancellation, and unsupported-capability handling.

### Phase 3: State, Persistence, and Presentation

- [ ] Move protocol decoding ahead of the neutral conversation reducer.
- [ ] Introduce local/harness/remote conversation identity and legacy migration.
- [ ] Migrate queue, transcript, timeline, models, inputs, approvals, and view-model state.
- [ ] Capability-gate optional actions and isolate Codex feature panels.
- [ ] Preserve focus, keyboard, responsive layout, and high contrast.

### Phase 4: Proof, Test, and Document

- [ ] Add the in-memory harness and shared contract tests.
- [ ] Add persistence migration and architecture dependency tests.
- [ ] Run focused test sets after each vertical slice.
- [ ] Run `dotnet test SynthiaCode.sln`.
- [ ] Update README and architecture documentation.
- [ ] Review the final diff and run `git diff --check`.

## 7. Validation Plan

| Validation | Command or method | Expected result | Status |
| --- | --- | --- | --- |
| Baseline suite | `dotnet test SynthiaCode.sln` | Existing behavior passes | 272 passed |
| Harness contract tests | Focused `dotnet test --filter` | Codex wrapper and in-memory harness satisfy the same base contract | Not run |
| Persistence tests | Focused settings/migration tests | Legacy and new settings round-trip without loss | Not run |
| Architecture tests | Focused dependency/name scan tests | Base Application/Core workflows have no protocol dependencies | Not run |
| Full suite | `dotnet test SynthiaCode.sln` | All tests pass | Not run after edits |
| Manual workflow | Launch Codex conversation; create, stream, steer/cancel, queue, restart | Current user flow remains operational | Not run |
| Accessibility/visual check | Exercise capability-hidden/disabled controls at compact/wide and high contrast | Layout and keyboard behavior remain stable | Not run |
| Final diff check | `git diff --check` | No whitespace errors | Not run |

## 8. Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- |
| Abstraction leaks Codex concepts | High | High | Architecture tests plus the in-memory second-harness proof |
| Legacy settings lose identity or transcript data | Medium | High | Additive fields, explicit migration, literal legacy fixtures, round-trip tests |
| Optional features become lowest-common-denominator UI | Medium | Medium | Capability and typed-feature composition |
| Notification translation changes ordering or deltas | Medium | High | Preserve existing batcher and add adapter/reducer sequence tests |
| Large migration obscures regressions | High | High | Small vertical slices with focused tests and compatibility wrappers |
| Existing release changes are overwritten | Low | High | Do not edit their files until documentation integration is required; inspect focused diffs |

## 9. Decisions

| Date | Decision | Rationale | Consequence |
| --- | --- | --- | --- |
| 2026-08-04 | Use compile-time harness registration first | Proves semantics without committing to a public plugin ABI | Dynamic loading is deferred |
| 2026-08-04 | Pin each conversation to its creating harness | Remote thread formats and history semantics are not interchangeable | Harness switching creates/imports a separate conversation |
| 2026-08-04 | Keep Codex extensions as optional features | Preserves product richness | Base UI depends only on capability-neutral contracts |
| 2026-08-04 | Use a strangler migration | Maintains a green, testable Codex path | Existing protocol classes remain temporarily inside the adapter |

## 10. Progress Log

| Date | Status | Notes |
| --- | --- | --- |
| 2026-08-04 | In progress | Goal created; safe-edit workflow loaded; dirty worktree recorded; baseline suite passed 272 tests. |

## 11. Completion Report

- **Outcome:** In progress.
- **Files/components changed:** This task plan only so far.
- **Validation completed:** Baseline `dotnet test SynthiaCode.sln` passed 272 tests.
- **Acceptance criteria:** Pending implementation.
- **Known limitations:** The current code remains Codex-shaped until the planned vertical slices land.
- **Follow-up work:** Will be updated when implementation completes.
