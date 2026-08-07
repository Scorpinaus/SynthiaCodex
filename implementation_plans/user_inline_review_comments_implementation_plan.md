# User-Authored Inline Review Comments Implementation Plan

## Goal

Implement one Codex parity gap: let a user attach feedback to a specific old- or new-side line in SynthiaCode's Git diff and carry those comments, with their file and line context, into the next start, steer, or queued follow-up.

This slice builds on the structured diff and inline reviewer findings delivered at baseline commit `255da90`. It does not add hunk stage/unstage/revert, Commit/Branch/Last turn diff loading, detached reviews, push, or pull-request workflows.

## Official behavior researched

The current official OpenAI Code review documentation establishes the user-facing contract:

- the app review pane is the place to understand changes and give line-specific feedback;
- the pane reflects the repository state, including user and Codex changes;
- the same surface is used to decide what to stage, revert, commit, or push;
- Codex's own findings appear as inline comments in that pane.

Primary source:

- [Code review](https://learn.chatgpt.com/docs/code-review)

The public documentation does not specify a dedicated app-server input type for a user-authored line comment. SynthiaCode will therefore preserve the comments as typed local state and serialize them deterministically into the user text supplied to the existing start/steer protocol. This is a client-side projection, not a guessed app-server method.

## Delivered boundary

The completed slice will include:

1. A bounded `GitInlineComment` core model with stable identity, repository root, current and optional original relative paths, old/new side, 1-based line number, captured diff text, body, and timestamps.
2. Deterministic normalization, validation, deduplication, and prompt formatting for at most 100 comments and a bounded aggregate payload.
3. An accessible Add comment action on commentable diff rows, plus inline save/cancel editing.
4. Editable and removable pending comment cards with visible file, side, and line context that do not depend on color.
5. Projection of saved comments back onto matching repository/file/side/line rows after diff refreshes, file changes, repository changes, and renamed-file diffs.
6. A per-chat pending-comments summary so comments on another file or repository remain discoverable.
7. Durable per-chat draft persistence alongside existing attachment drafts, including new-chat-to-created-thread migration and deletion cleanup.
8. Comment-only start, queue, and steer submissions.
9. Capture of an immutable comment snapshot for each attempted submission; only those captured comments clear after the remote start/steer acknowledgement or successful durable enqueue.
10. Preservation of comments when validation, transport, or durable queue persistence fails, and preservation of comments added while a request is in flight.
11. Queued-follow-up snapshot, clone, restore, display-summary, manual-steer, and background-dispatch support.
12. A readable deterministic review-comment section in the actual user prompt, so local transcript state and app-server history receive the same context exactly once.

## Explicitly out of scope

- hunk stage, unstage, and revert operations;
- changing Git index or working-tree content from a comment action;
- Commit, Branch, or Last turn diff loading;
- detached review delivery and review-delivery settings;
- resolving or replying to Codex-authored findings;
- GitHub pull-request review comments;
- comments on files that are not present in a loaded repository diff;
- editing a comment after it has been durably queued or submitted;
- inferring a new app-server protocol method that is absent from official documentation.

## Architecture

### Core comment contract

Add a presentation-neutral model under `SynthiaCode.Core.Git`:

- `GitDiffSide` distinguishes `Old` and `New` coordinates.
- `GitInlineComment` stores only normalized local metadata and user text.
- validation rejects blank/oversized bodies, invalid IDs, non-absolute repository roots, rooted or escaping relative paths, invalid line numbers, malformed timestamps, duplicates, and excessive count/aggregate bytes;
- restored invalid records are skipped deterministically, while interactive creation fails with an actionable message;
- the captured diff line is bounded and remains evidence for deletion-only comments even when the working-tree line no longer exists.

`GitInlineCommentPromptFormatter` will append a stable, human-readable `Inline review comments` section to the typed prompt. Every entry includes an absolute repository-contained path, optional original path, side, line, captured diff text, and exact body. The same formatted text is used for local conversation state and the harness command.

### Diff-row authoring and projection

`GitViewModel` remains the owner of the review-pane interaction:

- header, hunk, and metadata rows are not commentable;
- additions target the new side, removals target the old side, and context targets the new side;
- Add comment opens a row-local bounded editor;
- save validates and adds a stable comment; cancel is non-mutating;
- pending comment cards support edit, cancel, save, and remove;
- a comment matches by normalized repository root, current/original path, side, and exact line;
- all pending comments remain available in a summary collection even when their file is not selected;
- every committed mutation raises one change notification for shell persistence and composer command-state refresh.

The view model will expose immutable snapshots to callers and accept replacement snapshots when the selected chat changes. Submission clearing removes only captured IDs, preventing a newly authored in-flight comment from being lost.

### Per-chat persistence

Extend the existing composer-draft snapshot rather than create a competing thread-key scheme. Its attachment and review-comment lists share the existing scope/project/thread identity and timestamp.

Capture logic will update one list without deleting the other. A draft record is removed only when both lists are empty. Existing settings files remain compatible because the new list defaults to empty. The settings storage mapper deep-copies every comment.

Before a project or chat selection changes, `MainViewModel` captures the current comment draft. After selection, it restores the selected draft into `GitViewModel`. The existing transition from a null new-chat draft to the newly created thread automatically moves both attachments and comments because they share the record.

### Start, steer, and queue lifecycle

Before each live submission, `MainViewModel` captures the current comment snapshots and builds the effective user prompt through the core formatter.

- Start: the effective prompt is used for both the local pending turn and harness `turn/start`; captured comments clear after the start acknowledgement.
- Steer: the effective guidance is used for both local guidance and harness `turn/steer`; captured comments clear after acknowledgement.
- Queue: original text and typed comments are persisted together; captured comments clear only after the queue snapshot is durably saved.
- Comment-only operations are valid because the formatter produces a non-empty effective prompt.
- Failures leave the relevant live or queued comments intact.

Queued items deep-copy comment records through every settings boundary. Manual steering and background FIFO dispatch format the same effective text. The queued item is removed only through the existing acknowledgement/persistence lifecycle.

### Native presentation

Extend `GitView.xaml` with:

- a keyboard-accessible Add comment button on each commentable row;
- a row-local multiline editor with Save and Cancel;
- a distinct user-comment card template with location, edit/remove actions, and UI Automation names;
- a bounded pending-comments summary below the diff;
- a visible count explaining that pending comments accompany the next composer submission.

Extend queued follow-up cards in `TaskView.xaml` with a non-color summary such as `2 inline comments` so the captured context is visible after the live draft clears.

## Test-first sequence

Before production implementation, add focused tests that fail for the missing contracts:

1. Core validation and formatter tests for old/new sides, renamed paths, multiline bodies, deterministic order, bounds, invalid restore filtering, comment-only prompts, and no duplicate serialization.
2. Git view-model tests for addition/removal/context side selection, add/cancel/edit/remove, projection after refresh and repository/file switching, renamed-file matching, mutation notifications, and captured-ID clearing.
3. Composer-draft tests proving attachment/comment coexistence, thread isolation, null-thread migration, clone depth, restore filtering, and empty-record cleanup.
4. Queue tests proving clone/restore, count display, comment-only enqueue, byte/count validation, manual steer, background dispatch, restart persistence, and failure retention.
5. Main workflow tests proving start, steer, and queue carry comments exactly once; clear only after acknowledgement; preserve failed and in-flight-new comments; and use identical local/harness prompt text.
6. XAML/source-boundary tests for accessible row actions, inline editing, pending summaries, queued summaries, and non-color location/side labels.

Run the new filter against untouched production code and retain the intended failure output as the red baseline.

## Implementation order

- [x] Add and run failing focused tests.
- [x] Add the bounded core comment model and prompt formatter.
- [x] Add Git diff-row authoring, projection, editing, and captured-ID clearing.
- [x] Add per-chat composer-draft persistence and deep-copy boundaries.
- [x] Add queue snapshot, validation, presentation, and dispatch support.
- [x] Integrate start, steer, and queue lifecycle semantics.
- [x] Add accessible inline and summary presentation.
- [x] Run focused tests until green.
- [x] Run existing code-review and queued-follow-up filters.
- [x] Run the complete Release suite and compare it with the protected baseline.
- [x] Run a warning-free Release solution build.
- [x] Update README, current architecture, feature parity, and this completion record.
- [x] Run `git diff --check` and audit the final file set.

## Verification gates

The slice is complete only when:

- the red run demonstrates the intended missing contracts before production code;
- a keyboard user can add, edit, cancel, and remove a line-specific comment;
- old/new coordinates and renamed paths remain correct after diff reprojection;
- comments are isolated and restored per chat;
- attachments and comments cannot erase one another's draft record;
- start, steer, queue, manual queued steer, and background dispatch receive the same deterministic comment context;
- comment-only submissions work;
- a failed operation does not lose comments;
- acknowledgement clears only the captured comment IDs;
- queued comments survive settings clone/restore and remain visibly disclosed;
- all focused tests pass;
- no failures are introduced beyond the protected full-suite baseline;
- the Release build completes with zero warnings and errors;
- `git diff --check` is clean;
- every adjacent Git-review gap remains open in parity documentation.

## Verification result

- The red focused run failed at the intended missing `GitInlineComment`, Git view-model command/collection, draft, and queue contracts before production implementation.
- The completed inline-comment filter passes 43/43, including domain validation, renamed old/new-side formatting, diff authoring, per-chat persistence, origin-aware captured-ID removal, durable queue restoration/dispatch, workflow wiring, and accessible XAML surfaces.
- The existing code-review filter passes 44/44. The queued-follow-up filter is 45/46 with only the protected background-dispatch fake-transport timeout.
- The complete Release suite is 310/316 with exactly the same six protected-baseline failures and no new product regression. One unrelated fork fake-transport timeout on the first run passed 37/37 in isolation; the complete rerun matched the six-failure baseline exactly.
- `dotnet build SynthiaCode.sln -c Release --no-restore` completes with zero warnings and zero errors.
- README, architecture, and feature-parity records now describe the implemented behavior and retain hunk actions, alternate diff scopes, detached review, push, and PR work as separate gaps.
- `git diff --check` is clean; the final source, test, plan, and documentation file set is limited to this inline-comment slice.
