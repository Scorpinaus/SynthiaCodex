# Structured Review Findings in the Git Diff Implementation Plan

## Goal

Implement one Codex parity gap: project the latest dedicated Codex review into typed findings and render each finding as an inline annotation on its matching row in SynthiaCode's Git diff.

This slice builds on the existing `review/start` workflow. It does not add user-authored comments, hunk mutations, detached review delivery, or new review targets.

## Official behavior researched

Current OpenAI documentation and source establish the behavior to match:

- Codex's review pane presents line-specific feedback alongside the diff and supports Unstaged, Staged, Commit, Branch, and Last turn review scopes.
- The built-in reviewer must produce a structured object containing `findings`, an overall verdict and explanation, and confidence values.
- Every finding has a short title prefixed with `[P0]` through `[P3]`, a one-paragraph Markdown body, a numeric priority from 0 through 3, a confidence score, an absolute file path, and a tight 1-based line range that overlaps the diff.
- App-server 0.146 exposes inline-review completion through `exitedReviewMode.review` as one plain-text value rather than as the original structured object.
- Codex formats that value deterministically: the overall explanation, then `Review comment:` or `Full review comments:`, then entries in the form `- [P1] Title — absolute-path:start-end` with the body indented on following lines.
- Codex records a final assistant message containing the same human-readable review, so preserving the existing transcript response remains correct.

Primary sources:

- [Code review](https://learn.chatgpt.com/docs/code-review)
- [Built-in reviewer output schema](https://github.com/openai/codex/blob/main/codex-rs/core/review_prompt.md)
- [App-server review lifecycle](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Review output formatting](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/review_format.rs)

The standalone CLI probe could not run a live review because that shell does not inherit the desktop app's authenticated session. It failed read-only with HTTP 401 and changed no project files. The checked-in schema and upstream formatter provide the exact transport and display contracts without relying on a guessed sample.

## Delivered boundary

The completed slice will include:

1. An immutable `CodexReviewFinding` model with title, body, priority, optional confidence, source path, and start/end lines.
2. A bounded parser for both the exact app-server plain-text format and the reviewer's structured JSON fallback.
3. Stable validation and deduplication for malformed, duplicated, or partially structured reviewer output.
4. A unified-diff parser that retains display text plus old/new 1-based line numbers for headers, context, additions, removals, and metadata.
5. Projection of the latest non-superseded review turn in the active chat into the Git inspector without duplicating persisted state.
6. Repository-aware path matching for Windows paths, slash normalization, renamed files, and repository-relative fallback paths.
7. Inline finding cards anchored to the matching new-side line, with an old-side fallback for deletion-only findings.
8. A visible, accessible priority label, finding title, body, and file-line location that do not depend on color alone.
9. Preservation of the raw review response in the existing Markdown transcript and the raw diff string for compatibility.
10. Focused parser, reducer/projection, diff-line, view-model, and XAML source-boundary tests.

## Explicitly out of scope

- user-authored inline comments and carrying those comments into the next prompt;
- per-hunk stage, unstage, or revert actions;
- detached reviews and review-delivery settings;
- adding Branch, Commit, or Last turn diff loading to the Git inspector;
- review findings for a different repository than the repository selected in the inspector;
- confidence display when app-server's plain-text review omits confidence;
- push, pull-request creation, pull-request feedback, and worktree lifecycle.

## Architecture

### Review finding parser

Add a core parser beside the existing app-server conversation models. Parsing is deterministic and never mutates the transcript:

1. Attempt the documented structured JSON object, including extraction from surrounding text for compatibility with older reviewer output.
2. Otherwise parse the exact Codex plain-text findings block from right to left at the location separator so Windows drive-letter colons remain valid.
3. Derive priority from the numeric field when available and from the required `[P0]` through `[P3]` title prefix otherwise.
4. Reject invalid priorities, blank paths/titles, non-positive or reversed ranges, non-finite/out-of-range confidence, and unbounded excess input.
5. Preserve Markdown bodies as text and deduplicate identical findings in first-seen order.

The latest active-chat review remains the source of truth. Findings are derived from the already persisted `AssistantResponse`, so restored and forked conversations regain annotations without adding a second persistence representation or changing every snapshot clone path.

### Unified diff model

Add a small Git-core model and parser:

- `GitDiffLineKind` identifies header, hunk, context, addition, removal, and metadata rows.
- `GitDiffLine` retains the original text and nullable old/new line numbers.
- Hunk headers update old/new counters; context advances both, additions advance new, removals advance old, and non-hunk metadata remains unnumbered.
- Invalid or non-unified text remains visible as metadata instead of being dropped.

The parser is presentation-neutral and does not know about review findings.

### Active-chat projection

`TaskViewModel.ApplyConversationSnapshot` already raises `ConversationTurns` after every live, restored, forked, or selected-thread snapshot. `MainViewModel` will use that existing notification to send the latest non-superseded review turn's parsed findings to `GitViewModel`.

`GitViewModel` will:

- retain the raw `SelectedDiff` property;
- expose structured diff rows for the selected file;
- normalize and filter findings against the currently selected repository and both the current and original file paths;
- attach findings to a row whose new-side line intersects the finding range, falling back to old-side lines for removal-only ranges;
- expose unmatched findings for the selected file in a small non-inline section so valid reviewer feedback is not silently lost when the currently loaded diff side cannot anchor it;
- rebuild the projection when the review, repository, file, diff side, or diff text changes.

Only the latest non-superseded review turn is projected. Starting a newer review clears older annotations until the newer result arrives, avoiding stale feedback.

### Native diff rendering

Replace the plain read-only diff `TextBox` with a recycling-virtualized row list. Each row displays old/new line numbers, the unchanged diff text, and zero or more annotation cards directly beneath the anchor row.

The presentation will use existing theme resources and include:

- non-color row prefixes and line numbers;
- a textual `P0`, `P1`, `P2`, or `P3` badge;
- wrapping title, body, and location text;
- UI Automation names for the diff and each finding;
- a fallback message for empty/loading/error diff text;
- an unmatched-findings region with an explanatory label.

## Test-first sequence

Before production implementation, add focused tests that fail for the missing contracts:

1. Plain-text parser tests for one/many findings, P0-P3, Windows and Unix paths, line ranges, multiline bodies, and overall text exclusion.
2. Structured JSON tests for priority/confidence preservation, surrounding prose extraction, malformed records, range validation, deduplication, and bounded output.
3. Conversation tests proving findings derive from completed review text and reappear from the existing snapshot without new persisted fields.
4. Unified-diff tests for multiple hunks, context/add/remove numbering, no-newline metadata, malformed input, and CRLF normalization.
5. Git view-model tests for repository/file matching, renamed files, new-side and deletion-only anchoring, unmatched findings, review replacement, repository switching, and stale async diff loads.
6. XAML/source-boundary tests for the virtualized structured diff, textual priority labels, inline finding cards, automation names, and removal of the plain diff `TextBox` binding.

Run the focused tests before implementation and retain their failure output as the red baseline.

## Implementation order

- [x] Add and run failing focused tests.
- [x] Add structured finding models and the bounded dual-format parser.
- [x] Add unified-diff models and parser.
- [x] Add active-chat review projection into `GitViewModel`.
- [x] Add inline and unmatched finding presentation to `GitView.xaml`.
- [x] Run focused tests until green.
- [x] Run the complete Release test suite and compare it with the protected six-failure baseline.
- [x] Run the Release solution build with zero warnings and errors.
- [x] Update feature parity, README/current architecture where applicable, and this completion record.
- [x] Run `git diff --check`, inspect the final diff, and verify no unrelated files changed.

## Verification gates

The slice is complete only when:

- the pre-implementation focused run demonstrates the new missing contracts;
- all focused structured-review tests pass;
- existing persisted review turns restore the same typed annotations solely from their response text;
- findings appear only for the matching selected repository and file;
- every valid selected-file finding is either inline or explicitly shown as unmatched;
- the raw review transcript and raw diff remain available;
- the full suite introduces no failures beyond the six protected pre-existing failures;
- the Release build completes with zero warnings and zero errors;
- `git diff --check` is clean;
- parity documentation leaves all adjacent Git-review gaps open.

## Verification result

- The pre-implementation focused build failed only on the intentionally absent finding, parser, diff-row, projection, and UI contracts.
- Six new behavioral cases cover the official plain-text and JSON review forms, validation and deduplication, latest-review restoration, multi-hunk diff numbering, repository/rename-aware anchoring, unmatched findings, and the rendered XAML boundary.
- The structured-review Release filter passes 41/41 runner cases, including the six new cases and global invariant checks.
- The existing code-review Release filter remains green at 43/43.
- The complete Release suite is 302/308. Its six failures are exactly the protected baseline failures for auth-actionability and five app-server timing/restart scenarios; this slice adds no failure.
- The Release solution build succeeds with zero warnings and zero errors.
- `git diff --check` is clean, and the final file audit contains only this planned feature, its tests, and its documentation updates.
