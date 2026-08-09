# Dedicated Code Review Workflow Implementation Plan

## Goal

Implement one parity gap: a native, dedicated Codex code-review workflow in SynthiaCode. A user can invoke **Review** or submit exactly `/review`, choose an official review target, and receive the dedicated reviewer result as a labeled turn in the current chat.

This slice uses the Codex app-server `review/start` method. It does not emulate review with a normal prompt.

## Official behavior researched

Current Codex documentation and the locally generated app-server schema for Codex CLI 0.146.0 establish the contract:

- `/review` is available for projects inside a Git repository.
- Reviews report prioritized, actionable findings without changing the working tree.
- `review/start` accepts exactly one of four targets:
  - `uncommittedChanges` for staged, unstaged, and untracked changes;
  - `baseBranch` with a `branch` value;
  - `commit` with a `sha` and optional human-readable `title`;
  - `custom` with free-form `instructions`.
- Inline delivery runs in the current chat and returns the review turn plus `reviewThreadId`.
- Review lifecycle is streamed through `enteredReviewMode` and `exitedReviewMode` items. The final review text is carried by `exitedReviewMode.review`.

Primary source: [Code review](https://learn.chatgpt.com/docs/code-review)

## Delivered boundary

The completed slice will include:

1. Typed app-server request, target, delivery, and result models.
2. Exact `review/start` JSON serialization and response validation.
3. Git repository validation plus local branch and recent-commit discovery for the picker.
4. A keyboard-accessible native Windows target picker for all four official target types.
5. A visible **Review** composer action and exact `/review` interception.
6. Inline review execution through the current Codex chat and existing cancellation path.
7. First-class projection of review lifecycle and final findings into a labeled transcript turn.
8. Review metadata persistence and restoration from both local snapshots and app-server history.
9. Focused protocol, reducer, Git parsing, presentation, and orchestration tests.

The following remain separate parity gaps:

- inline diff comments;
- structured findings parsed into independent severity/file-line objects;
- per-hunk stage, unstage, or revert actions;
- detached review delivery and review-delivery settings;
- push, pull-request creation, and pull-request feedback context;
- review across a non-primary repository in one multi-folder thread.

## Architecture

### Core protocol

Add review models under `SynthiaCode.Core.Codex.AppServer`:

- `CodexReviewTargetKind`;
- validated `CodexReviewTarget` factories for uncommitted, base branch, commit, and custom targets;
- `CodexReviewDelivery` with inline as the implemented delivery;
- `CodexReviewStartRequest` and `CodexReviewStartResult`.

The target owns its display label so the pending transcript turn and target picker use one consistent description.

### App-server boundary

Add `StartReviewAsync` to `CodexAppServerClient`, expose it through a narrow `ICodexReviewFeature`, and implement it in `AppServerSessionCoordinator`.

Serialization must match the generated schema exactly. Required result fields are `turn.id` and `reviewThreadId`; missing values fail with `CodexAppServerProtocolException`.

### Git discovery

Extend `IGitService` with one read-only review-catalog operation. `GitService` will:

- resolve and validate the repository root;
- enumerate local and remote branch names, excluding symbolic `HEAD` aliases and the current branch;
- enumerate a bounded set of recent commits with full SHA, short SHA, and subject;
- return stable, deduplicated results suitable for selection.

Parsing helpers stay internal and receive direct tests. No Git state is changed.

### Native picker

Add a `CodeReviewWindow` opened through `IUserInteractionService`. It will expose four radio-button targets, contextual branch/commit selectors, custom-instruction input, validation, Cancel, and Start review.

Accessibility requirements:

- named window and controls through UI Automation;
- keyboard focus moves to the active target input;
- default and cancel buttons work without a mouse;
- validation is announced as an assertive live region;
- target-specific controls are disabled when inactive.

### Execution and conversation projection

Add a small `CodeReviewUseCaseService` that owns the non-visual transition:

1. create a pending review turn;
2. call `review/start` with inline delivery;
3. require the returned review thread to match the current thread;
4. bind/register the returned turn ID;
5. fail and release pending state atomically if startup fails.

`MainViewModel` remains responsible for repository discovery, showing the picker, ensuring a Codex session/thread, updating visible running state, and status messages.

`CodexThreadService` will recognize `enteredReviewMode` and `exitedReviewMode`. It will mark the conversation turn as a code review, show review lifecycle activity, and use the completed `review` text as the assistant response. `CodexAppServerClient` history parsing will restore the same information from stored thread items.

`CodexConversationTurn` and its snapshot gain append-only review metadata. The task transcript displays a **Code review** pill and review scope while preserving normal Markdown rendering for the prioritized findings.

### Composer integration

Add `ICodeReviewActions` to the task-workspace boundary and a `StartCodeReviewCommand` to `TaskViewModel`.

- The visible **Review** button invokes the picker directly.
- Submitting a trimmed prompt equal to `/review` invokes the same command instead of `turn/start`.
- Other prompts containing `/review` remain ordinary prompts.
- Review is enabled only for a ready, non-archived Codex project chat with no active turn. Repository validity is checked when invoked so UI state cannot become stale.

## Test-first sequence

Before production implementation, add focused tests that fail for the missing behavior:

1. Protocol tests for every target JSON shape, inline delivery, result parsing, and invalid/missing values.
2. Notification/reducer tests proving review items become a labeled review turn with final findings and survive snapshot restoration.
3. History parsing tests proving `enteredReviewMode` and `exitedReviewMode` restore review metadata and response text.
4. Git catalog parsing tests for branches, symbolic aliases, deduplication, and commit subjects.
5. Task presentation tests for Review command state and exact `/review` routing.
6. Use-case tests for pending/bound/failed review transitions and detached-thread rejection.
7. XAML/source boundary assertions for the visible accessible Review action and picker controls.

Run the focused tests once before implementation and retain their failure output as the red baseline.

## Implementation order

- [x] Add and run failing focused tests.
- [x] Add typed review protocol models and `review/start` client/coordinator feature.
- [x] Add Git review catalog discovery.
- [x] Add review lifecycle projection and persistence.
- [x] Add the execution use case and application wiring.
- [x] Add the native picker, composer action, and exact `/review` routing.
- [x] Run focused tests until green.
- [x] Run the complete test suite and release build.
- [x] Update parity inventory, README, and current architecture.
- [x] Run `git diff --check` and inspect final scope.

## Verification gates

The slice is complete only when:

- all focused code-review tests pass;
- the full existing test suite introduces no new failures;
- the Release build completes with zero warnings and zero errors;
- protocol payloads match the generated Codex 0.146.0 schema;
- `/review` and the Review button reach the same picker and execution path;
- review results render and restore as review turns;
- `git diff --check` is clean;
- parity documentation accurately preserves the remaining review gaps.

## Verification result

- The pre-implementation focused run failed at the new missing review contracts, establishing the intended red baseline.
- All 43 tests selected by the `code review` filter pass in Release, including the four exact protocol target shapes and the end-to-end streamed-finding workflow.
- The complete Release suite reports 296 passed and the same six pre-existing failures as the protected baseline; no new failure was introduced.
- The Release solution build succeeds with zero warnings and zero errors.
