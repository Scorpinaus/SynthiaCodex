# SynthiaCode and ChatGPT Desktop Feature Parity

- **Audit date:** 7 August 2026
- **SynthiaCode baseline:** working tree based on commit `c5a0aa3` (`Added native dedicated inline coding review`), plus the structured inline-review slice recorded below
- **Comparison surface:** ChatGPT desktop app with Codex/local-project capabilities
- **Scope:** User-visible desktop functionality, local Codex workflows, and capabilities inherited through `codex app-server`

## Modern WPF redesign implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 0 | Recorded the presentation contract in automated tests; baseline Debug build is warning-free and all 187 pre-redesign behavioral tests pass. | **Complete** |
| Phase 1 | Added complete semantic light/graphite theme keys, neutral graphite palette values, compatibility aliases, and focused application resource composition. | **Complete** |
| Phase 2 | Added reusable button, input, navigation, pane, drawer, status, focus, icon, menu, tooltip, popup, splitter, and empty-state primitives. | **Complete** |
| Phase 3 | Replaced the workspace tabs with custom native-aware chrome, title-bar commands, persistent/drawer rail states, docked conversation and terminal regions, persistent/drawer inspector states, scrim/Esc dismissal, and top-priority approval hosting. | **Complete** |
| Phase 4 | Migrated the project rail to accessible search, neutral navigation rows, geometry icons, recycling lists, quiet actionable status, full-name tooltips, existing lifecycle menus, search results, empty states, and a pinned account anchor. | **Complete** |
| Phase 5 | Migrated the virtualized transcript to a quiet conversation canvas with bounded user/assistant surfaces, compact activity disclosure, modern empty state, floating jump action, existing Markdown/link security, progressive streaming, and manual-scroll preservation. | **Complete** |
| Phase 6 | Reworked the bounded composer styling while preserving model/reasoning/permission flyouts, queues, attachments, send/queue/steer shortcuts, and added a focus-contained approval sheet with monospaced risk detail and no accidental default-Enter approval. | **Complete** |
| Phase 7 | Added the lower dark terminal dock with start/stop/clear/close and maximize-within-workspace actions, keyboard splitter, per-thread state preservation, plus a narrow responsive changed-files/diff/commit inspector. | **Complete** |
| Phase 8 | Migrated the pinned account flyout, settings sections, context menus, tooltips, combo popups, and transient surfaces to active semantic theme resources with predictable focus/Esc behavior. | **Complete** |
| Phase 9 | Added a complete system-color high-contrast palette, system high-contrast selection, reduced-motion popup fallback, native-DPI vector icons, non-color state labels/structure, focus visuals, live-region metadata, focus-contained approval traversal, long-text wrapping/trimming, and keyboard-accessible drawers/splitters. | **Complete** |
| Phase 10 | Preserved recycling virtualization, pixel transcript scrolling, 50 ms terminal presentation batching, bounded terminal/history behavior, native splitter resizing, and avoided large-surface effects/layout transforms; the warning-free build and focused performance guardrail tests pass. | **Complete** |
| Phase 11 | Compared the implemented hierarchy and graphite palette with the approved concept, refreshed README/architecture documentation, completed rendered-WPF presentation checks, and produced warning-free Debug and Release executables after the complete regression gate. | **Complete** |
| Phase 12 | Aligned and vertically centered the Chats/Projects section headers with their actions, separated project disclosure from workspace/thread activation so the active chat is retained, and added bounded in-chat previews plus safe clickable reveal links for generated local PNG/JPEG/WebP/GIF images. | **Complete** |
| Phase 13 | Completed test-first generated-image expansion: generated-image cards expose a keyboard-accessible preview action, both the image and its embedded local link route through the validated image policy, and a resizable modal viewer loads the full image with aspect-ratio preservation, filename context, close action, and Esc dismissal. All 197 behavioral tests pass; Debug and Release rebuilds complete with zero warnings and errors. | **Complete** |
| Phase 14.1 | Added red-test coverage for commentary visibility, final-response replacement, presentation-state notifications, accessible channel labeling, and direct (non-expander) activity rendering. The expected pre-implementation compile failure confirms the production contract is not yet present. | **Complete** |
| Phase 14.2 | Implemented a directly visible, accessible Commentary channel for updates, commands, searches, plans, tools, and other activity. The channel hands off to the assistant surface when final response text arrives, while retained activity history and legacy response behavior remain intact. The responsive layout now verifies long content through both lifecycle states. | **Complete** |
| Phase 14.3 | Completed the full 199-test regression gate, including the repaired responsive lifecycle coverage. Rebuilt and verified both `src/SynthiaCode.App/bin/Debug/net10.0-windows/SynthiaCode.App.exe` and `src/SynthiaCode.App/bin/Release/net10.0-windows/SynthiaCode.App.exe`; both configurations completed with zero warnings and zero errors. | **Complete** |
| Phase 15.1 | Added red-test coverage requiring message-style Markdown commentary, retained commentary beside a final response, user-expandable completed work details, structured command/search rows, and a total-work duration summary. The expected compile failure confirms the new presentation contract is not yet implemented. | **Complete** |
| Phase 15.2 | Implemented retained, expandable work details above the final answer. Assistant updates render as full Markdown message text, commands/searches/tools retain structured rows, live work stays expanded, completed work collapses without losing detail, and the disclosure reports total turn duration from persisted timestamps. Responsive coverage verifies long content, links, collapse, and re-expansion. | **Complete** |
| Phase 15.3 | Completed the full 199-test regression gate. Rebuilt and verified `src/SynthiaCode.App/bin/Debug/net10.0-windows/SynthiaCode.App.exe` and `src/SynthiaCode.App/bin/Release/net10.0-windows/SynthiaCode.App.exe`; both configurations completed with zero warnings and zero errors. | **Complete** |
| Phase 16.1 | Added red-test coverage requiring the user date/edit/copy footer and assistant date/copy footer to render below and outside their respective message surfaces, while omitting the visible `You` and `Assistant` role labels. The expected focused-test failure confirms that external footer containers are not yet implemented. | **Complete** |
| Phase 16.2 | Moved the user date/edit/copy controls and assistant date/copy controls into separately named footer rows below their respective message surfaces, removed the visible role labels, preserved the previous-version badge and commentary/final-response presentation, and kept the prompt-edit/copy bindings intact. The focused rendered-layout and prompt-editing test groups pass. | **Complete** |
| Phase 16.3 | Completed the full 203-test regression gate in both Debug and Release. Non-incremental solution rebuilds produced verified Debug and Release executables; both configurations completed with zero warnings and zero errors. | **Complete** |
| Phase 17.1 | Added red-test coverage requiring a left-aligned assistant footer ordered Date, Copy, Fork and a per-response fork command that creates a new chat at the selected assistant turn by rolling back only later turns in the fork. The expected compile failure confirms the per-response command is not yet implemented. | **Complete** |
| Phase 17.2 | Left-aligned the assistant footer as Date, Copy, Fork and added a turn-aware, disabled-while-running fork command. Forking an earlier response duplicates the source thread, rolls back only later active turns in the new thread, preserves the selected response and local transcript, selects the new chat, and resets stale context usage when rollback changes history. Focused protocol and rendered-layout tests pass. The implementation also repaired a pre-existing synchronization defect that could leave any newly forked chat displaying an empty local transcript. | **Complete** |
| Phase 17.3 | Completed the full 204-test regression gate in both Debug and Release. Non-incremental solution rebuilds produced verified Debug and Release executables; both configurations completed with zero warnings and zero errors. | **Complete** |
| Phase 18.1 | Added red-test coverage requiring the generated-image Draw tool to expose accessible increase/decrease controls, report its current brush size, enforce 4-64 px bounds in 4 px steps, and apply the chosen width to the exported imagegen region guide. The expected compile failure confirms that the brush-size contract is not yet implemented. | **Complete** |
| Phase 18.2 | Added Draw-only decrease/increase brush controls with accessible names and a visible current-size readout. The brush starts at 18 px, changes in 4 px steps, clamps at 4-64 px, updates an existing freehand overlay immediately, and is scaled into the exported PNG guide while preserving the previous default rendering. Focused rendered-WPF and guide-output coverage passes. | **Complete** |
| Phase 18.3 | Completed the full 243-test regression gate in Debug and Release. One unrelated persisted-thread fake-transport timeout on the first Debug run passed immediately in isolation and on the complete rerun, so no product or harness change was required. | **Complete** |
| Phase 18.4 | Completed non-incremental Debug and Release solution rebuilds with zero warnings and errors. Because a running SynthiaCode instance holds the default Debug output, both verified executables were produced in the app project's ignored `artifacts/brush-size-build` configuration folders without interrupting the active app. | **Complete** |
| Phase 19.1 | Added rendered-WPF red-test coverage requiring a left-click anywhere on the user-message surface to copy that turn's exact submitted prompt. The focused test fails at the intended clipboard assertion before implementation. | **Complete** |
| Phase 19.2 | Made the user-message surface directly clickable with a hand cursor and explanatory tooltip. A click copies the exact stored prompt through the same clipboard helper as the footer Copy button, while inline prompt-edit mode is protected from incidental copies. The focused rendered-WPF and prompt-edit test groups pass. | **Complete** |
| Phase 19.3 | Completed the full 243-test regression gate in Debug and Release with all behavioral cases passing in both configurations. No additional product defects surfaced. | **Complete** |
| Phase 19.4 | Completed non-incremental Debug and Release solution rebuilds with zero warnings and errors, then verified the fresh executables at `src/SynthiaCode.App/bin/Debug/net10.0-windows/SynthiaCode.App.exe` and `src/SynthiaCode.App/bin/Release/net10.0-windows/SynthiaCode.App.exe`. | **Complete** |
| Phase 20.1 | Added red-test coverage for pinned streaming follow, deliberate scroll-away preservation during simultaneous content growth, comfortable near-latest resumption, explicit Jump-to-latest behavior, and fresh follow state when switching chats. The expected compile failure confirms the scroll-coordination contract is not yet implemented. | **Complete** |
| Phase 20.2 | Replaced the transcript's binary 24 px auto-follow check with a dedicated 72 px near-latest coordinator. Streaming content follows only while pinned; upward wheel, scrollbar, keyboard, touch, or trackpad movement pauses follow; later growth preserves the reading position; near-latest navigation and Jump resume it. Background end-scroll requests are coalesced, scrollbar updates are immediate, vertical panning is enabled, and a chat identity change resets stale follow state. Focused policy and rendered-WPF tests pass. | **Complete** |
| Phase 20.3 | Completed the full 246-test regression gate in Debug and Release. Fresh non-incremental solution builds produced and verified both standard `SynthiaCode.App.exe` artifacts with zero warnings and zero errors; no additional defects surfaced during the final gate. | **Complete** |
| Phase 21.1 | Researched CommonMark/GitHub/Markdown Extra syntax and WPF image/clipboard behavior, then recorded the native rendering and safety contract for remote images, a non-executable raw-HTML subset, nested lists, footnotes, definition lists, syntax highlighting, and per-block copy actions. | **Complete** |
| Phase 21.2 | Added seven focused rendered-WPF tests covering validated remote images, safe/inert raw HTML, real nested-list margins, superscript bottom-placed footnotes, Markdown Extra definition lists, language-driven syntax tokens, and exact per-code-block clipboard output. Against the untouched renderer, all 30 prior filtered tests pass and all seven new tests fail at their intended missing behavior. | **Complete** |
| Phase 21.3 | Implemented bounded HTTP(S) image cards with failure fallback, a native safe-HTML allowlist with literal executable-tag fallback, hierarchical list rows, navigable bottom footnotes, definition-list layout, themed multi-language syntax tokens, language labels, and accessible per-block copy actions. All 37 focused Markdown tests pass; the only legacy test adjustments replace brittle code-block child casts with semantic content assertions. | **Complete** |
| Phase 21.4 | Resolved two parser/copy defects found during review: footnote declarations inside fenced examples now remain literal, and code copy removes only the fence-separating line ending while preserving intentional trailing blank lines. All 259 tests pass in Debug and Release. Fresh non-incremental builds produced verified Debug and Release executables with zero warnings and errors. | **Complete** |
| Phase 22.1 | Added red-test coverage for structured collaboration-event projection, Active/Done agent grouping, receiver-thread identity and prompt/status retention, real `thread/read` transcript loading, exact active-turn steer/stop targeting, immediate stopped-state movement, transcript dismissal, and accessible rendered panel controls. The focused build fails at the intended missing agent-management contract before implementation. | **Complete** |
| Phase 22.2 | Implemented an accessible Active/Done agent panel from canonical `collabAgentToolCall` receiver/state data, durable restoration from parent `thread/read` history, real subagent transcript drill-in, and agent-specific Open, Steer, Stop, and Close controls. Steer and Stop discover and target the subagent's actual running turn; successful interruption immediately moves the row to Done. Focused projection, protocol restoration, command, and rendered-WPF coverage passes. Rendered verification also caught and repaired a runtime-only invalid GridLength value. | **Complete** |
| Phase 22.3 | Completed the full 266-test regression gate in Debug and Release. Fresh non-incremental solution rebuilds produced verified `SynthiaCode.App.exe` artifacts in both configurations with zero warnings and zero errors; both report file version `0.1.0.0`. `git diff --check` is clean. | **Complete** |
| Phase 23.1 | Added six red-test contracts for click-to-toggle dictation, non-destructive prompt transcription, active-turn guidance targeting, actionable microphone/recognizer failures, shutdown cleanup, and an accessible vector microphone surface with live status. The focused build fails at the intended missing speech-recognition types before implementation. | **Complete** |
| Phase 23.2 | Implemented continuous local Windows dictation through the installed speech recognizer selected for the current UI culture. The composer microphone toggles listening, appends finalized phrases to the prompt or active guidance without retaining audio/transcripts, exposes non-color and screen-reader state, reports recognizer/device/privacy failures, and releases recognition at shutdown. The focused Debug gate passes. | **Complete** |
| Phase 23.3 | Final accessibility review added a button-scoped regression and repaired the unavailable-recognizer state so its explanation remains readable while the microphone control is disabled. The full 272-test regression gate passes in Debug and Release; fresh non-incremental rebuilds completed with zero warnings and errors, and both standard `SynthiaCode.App.exe` artifacts plus the `System.Speech` runtime dependency were verified. | **Complete** |

## Architecture and release metadata implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 1 - Test contract | Added three focused xUnit regressions for the executable's release identity, the dated architecture boundary and suite description, and README release/documentation links. All three tests failed against the stale metadata for the intended reasons before implementation. | **Complete** |
| Phase 2 - Executable metadata | Replaced the SDK's implicit `1.0.0.0` identity with the intentional `0.1.0` release, including stable assembly/file/informational versions, product authorship, copyright, and repository metadata. The focused executable-identity regression passes. | **Complete** |
| Phase 3 - Architecture snapshot | Updated the dated architecture boundary through Phase 21 and the current product extensions, corrected xUnit ownership and suite metrics, removed stale dependency/count claims, and documented current Markdown, attachment, generated-image, chat, prompt, persistence, and queued-dispatch flows. The focused architecture regression passes. | **Complete** |
| Phase 4 - Public release surface | Published release `0.1.0` in the README and linked the current architecture and feature-parity records as the authoritative release documentation. All three focused metadata regressions pass. | **Complete** |
| Phase 5 - Verification | All 262 tests pass in Debug and Release. Fresh non-incremental solution builds completed with zero warnings and errors, and both standard executables were verified with file version `0.1.0.0` and product version `0.1.0` plus the source revision. No additional defects surfaced. | **Complete** |

## Shared Codex configuration implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 1 - Test contract | Added red-test coverage for atomic shared-file editing with stale-write protection, ordered shared/workspace provenance, editor and Explorer deep links, and accessible Settings controls. The expected compile failure confirms the production contract is not yet present. | **Complete** |
| Phase 2 - Configuration core | Added size-bounded UTF-8 storage for shared `AGENTS.md` and `config.toml`, atomic replace, revision-based stale-write rejection, missing-file creation, and root-to-leaf discovery of active workspace `AGENTS.md` and `.codex/config.toml` sources. The focused Core/Infrastructure build passes with zero warnings and errors. | **Complete** |
| Phase 3 - Desktop surface | Added multiline shared `AGENTS.md` and `config.toml` editors, explicit refresh/save state, raw-TOML safety guidance, actionable conflict messages, ordered source cards for shared/workspace provenance, and Editor/Explorer deep links. Automatic refresh retains unsaved edits, configuration contents are excluded from logs, and all four focused tests pass. | **Complete** |
| Phase 4 - Verification | All 203 tests pass in both Debug and Release. A pre-existing console-test polling race found by the final gate now retries only the transient concurrent-enumeration condition. Clean solution rebuilds produced verified Debug and Release executables with zero warnings and errors. | **Complete** |

## Phase 6A skills and settings implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 6A.1 - Test contract | Added red-test coverage for skill request/response shapes, duplicate-name path identity, load errors, path-based enablement, unsupported-method fallback, effective-setting redaction, invalidation refresh, filtering, accessibility, and list virtualization. | **Complete** |
| Phase 6A.2 - Typed protocol | Added Core skill/effective-configuration records and typed `skills/list`, `skills/config/write`, and allowlisted `config/read` operations through the existing app-server client and session coordinator. Unsupported methods remain nonfatal. | **Complete** |
| Phase 6A.3 - Desktop lifecycle | Added active-workspace skill discovery, search and scope filtering, metadata/dependency/error presentation, Editor/Explorer actions, authoritative enable/disable refresh, debounced `skills/changed` invalidation, and hidden-surface stale caching. | **Complete** |
| Phase 6A.4 - Effective settings | Added a read-only, origin-aware Settings summary for model, provider, reasoning, service tier, profile, sandbox, approvals, web search, and workspace network access. Unallowlisted configuration is discarded before presentation. Existing shared-file editors and permission/model controls remain unchanged. | **Complete** |
| Phase 6A.5 - Verification | All 209 behavioral tests pass in Debug and Release, including the new protocol, view-model, redaction, and rendered-WPF coverage. Non-incremental Debug and Release rebuilds complete with zero warnings and errors, and the self-contained portable publish gate succeeds. | **Complete** |

## Phase 6B native skill invocation implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 6B.1 - Native selector | Added a composer Skills button and `$` entry point backed by active-workspace `skills/list` discovery. The searchable, keyboard-operable, recycling-virtualized popup shows only enabled skills, preserves duplicate-name rows by absolute path and scope, inserts or replaces visible `$name` markers, and exposes removable selected-skill chips. Three focused selector, token, and rendered-WPF tests pass as part of the 212-test suite. | **Complete** |
| Phase 6B.2 - Explicit app-server input | Added typed `CodexSkillInput` validation and `{ type: "skill", name, path }` serialization while retaining the visible `$name` marker. Unique manual markers resolve from enabled metadata, duplicate names require path-aware picker selection, removed markers discard stale bindings, and start, steer, queued snapshots, restore, manual queued steer, and later queued dispatch preserve the exact absolute path. Three additional focused tests pass as part of the 215-test suite. | **Complete** |
| Phase 6B.3 - Verification | All 215 behavioral tests pass in Debug and Release. The complete solution test command succeeds, `git diff --check` is clean, and non-incremental Debug and Release rebuilds complete with zero warnings and errors. Both `SynthiaCode.App.exe` artifacts were verified after the rebuild. The final review also added coverage and fixes for full-token replacement when the caret is inside `$name`, and fail-closed composer discovery after a stale workspace refresh. | **Complete** |

## Phase 6C generated-image result implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 6C.1 - Test contract | Added red-test coverage for canonical `imageGeneration` completion events, duplicate-event suppression, coexistence with final-answer text, conversation persistence, `thread/read` restoration, and a rendered-WPF inline preview using paths with spaces and parentheses. The expected compile failure confirmed that generated-image state was absent from turns and snapshots. | **Complete** |
| Phase 6C.2 - Live, persisted, and restored rendering | Added first-class generated-image paths to conversation turns and snapshots, projected successful app-server `savedPath` results into the existing safe local-image Markdown surface before final response text, retained them across settings saves, forks, and history reconciliation, and restored them from canonical thread items. Invalid, failed, unsupported, UNC, and duplicate results remain excluded. Snapshot review also repaired two clone paths that previously dropped `IsSuperseded`. | **Complete** |
| Phase 6C.3 - Verification | All 218 behavioral tests pass in Debug and Release, including focused reducer, history, persistence, generated-image viewer, and rendered-WPF coverage. `git diff --check` is clean, and non-incremental Debug and Release solution rebuilds complete with zero warnings and errors. Both `SynthiaCode.App.exe` artifacts were verified after the rebuild. One transient five-second fake-transport timeout in an unrelated fork test passed immediately in isolation and on the complete Release rerun, so no product or harness change was required. | **Complete** |
| Phase 6C.4 - Runtime investigation and legacy recovery | Inspection of real app-server state confirmed that completed image events contain valid `savedPath` values but older local conversation snapshots have no generated-image field. Restore now recovers those existing images from bounded raw completion events. Live and legacy image diagnostics retain identifiers, status, and path while dropping multi-megabyte encoded `result` data; layout coverage now verifies a portrait image receives visible dimensions inside the actual virtualized transcript. | **Complete** |
| Phase 6C.5 - Follow-up verification | Focused legacy recovery, payload redaction, protocol, and full-transcript portrait-layout tests pass. All 218 behavioral tests pass in Debug and Release. Clean non-incremental executable rebuilds and final repository checks are complete. | **Complete** |

## Goal mode implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 1 - Protocol and ownership | Added strict typed models plus `thread/goal/set`, `thread/goal/get`, and `thread/goal/clear` operations, status parsing, optional budget and usage accounting, and typed updated/cleared notification routing. Goal durability remains owned by Codex app-server. | **Complete** |
| Phase 2 - Selected-chat workflow | Added per-chat load and reconnect refresh, stale-result rejection after chat switches, matching-thread notification handling, unsupported-runtime fallback, and set/edit/pause/resume/clear commands. Creating a goal starts its objective as the first ordinary prompt; later edits update only the goal. | **Complete** |
| Phase 3 - Native surface and verification | Added an accessible, responsive progress row above the composer with objective, status, usage, validation, and management controls. All 5 Goal cases, 15 notification cases, and 3 responsive-layout cases pass; the Release solution build completes with zero warnings and errors. The complete Debug suite is 282/288, and all six unrelated failures reproduce in the untouched `bab45a5` snapshot. | **Complete** |

## Multi-folder local project implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 1 - Durable project roots and migration | Added backward-compatible primary-plus-secondary project persistence, normalized primary-first paths, deep-copy support, validation, and atomic primary migration for project scopes, local chat workspaces, worktree associations, composer drafts, queued turns, and legacy attachment ownership. | **Complete** |
| Phase 2 - Codex and attachment routing | Added harness-neutral workspace-root context for start/resume/fork/turn commands, bounded `workspaceWrite` `writableRoots`, primary-only automatic configuration-discovery guidance, durable queued roots, and root-owned file/folder references that revalidate only while their folder remains attached. | **Complete** |
| Phase 3 - Native project and Git surfaces | Added an accessible Edit project folders dialog with Add, Remove, Make primary, Save, and validation actions. The Changes inspector now discovers distinct repositories across attached folders and worktrees, keeps the effective primary first, and routes diff, stage, unstage, discard, commit, Editor, and Explorer actions through an accessible repository selector. | **Complete** |
| Phase 4 - Verification | All 41 focused multi-folder cases pass, including migration, protocol, attachments, navigation, Git routing, and rendered-WPF accessibility. The complete Debug suite is 288/294; its same six legacy failures match the protected `1e68742` baseline, so this slice introduces no regression. The app build completes with zero warnings and errors. | **Complete** |

## Dedicated code review implementation parity

| Phase | Implemented outcome | Status |
| --- | --- | --- |
| Phase 1 - Research, plan, and red contract | Verified the current Codex `/review` behavior and generated Codex CLI 0.146.0 app-server schema, recorded the `review/start` target and lifecycle contract, wrote the implementation plan, and established a focused failing baseline before production code. | **Complete** |
| Phase 2 - Protocol, workflow, and native surface | Added typed uncommitted/base-branch/commit/custom targets, exact inline `review/start` transport, repository branch/commit discovery, an accessible native picker, a visible Review action plus exact `/review` routing, lifecycle/finding projection, and durable labeled review turns. | **Complete** |
| Phase 3 - Verification | All 43 code-review-filtered tests pass in Release. The complete Release suite is 296/302 with the same six protected-baseline failures and no new regression; the Release solution build completes with zero warnings and zero errors. | **Complete** |
| Phase 4 - Structured inline reviewer findings | Added bounded parsing for Codex's official plain-text review formatter and structured JSON fallback, immutable P0-P3/path/range findings derived from persisted responses, latest-review replacement semantics, unified-diff old/new line projection, repository/rename-aware matching, accessible inline finding cards, and an explicit unanchored fallback. | **Complete** |
| Phase 5 - Structured review verification | All six new structured-review behavioral cases and all 43 existing code-review-filtered cases pass in Release. The complete Release suite is 302/308 with exactly the same six protected-baseline failures and no new regression; the Release solution build succeeds with zero warnings and zero errors. | **Complete** |
| Phase 6 - User-authored inline comments | Added bounded repository-contained old/new-side comments, accessible diff-row authoring and editing, renamed-path projection, per-chat draft persistence beside attachments, deterministic prompt context for start and steer, structured queue snapshots and summaries, origin-aware captured-ID acknowledgement, and failure/in-flight retention. | **Complete** |
| Phase 7 - Inline comment verification | All 43 inline-comment-filtered tests and all 44 code-review-filtered tests pass in Release. The complete Release suite is 310/316 with exactly the same six protected-baseline failures and no new regression; an unrelated fork timeout on the first run passed 37/37 in isolation and the full rerun matched baseline. The Release solution build succeeds with zero warnings and zero errors. | **Complete** |

## Status legend

| Status | Meaning |
| --- | --- |
| **Full** | SynthiaCode supports the same practical user outcome. The layout may differ. |
| **Near** | The core workflow is present, with a smaller UX or a documented edge-case gap. |
| **Partial** | Some protocol/backend behavior exists, but the complete desktop workflow does not. |
| **Missing** | No implemented SynthiaCode product surface was found. |

## Executive assessment

| Area | Current parity | Assessment |
| --- | --- | --- |
| Local coding loop | **Strong** | General and multi-folder project chats, multi-turn work, persistent goals, queued follow-ups, streaming, models, permissions, terminal, multi-repository Git changes, and worktrees are usable end to end. |
| Safety and approvals | **Near full** | The three composer permission modes and server-request approvals now map closely to ChatGPT desktop. |
| Git and worktree lifecycle | **Moderate** | Core isolation, file-level Git operations, dedicated review targets, structured diff rows, inline reviewer findings, and user-authored comments exist; hunk operations, handoff, push, PR, snapshots, and setup actions do not. |
| Agent orchestration | **Near** | Parallel chats plus Active/Done subagent inspection, transcripts, steering, and stopping are usable; nicknames, live open-transcript refresh, and custom-agent management remain absent. |
| Context and multimodal input | **Near** | Per-chat context-window visibility plus image/file/folder picker, paste/drop, previews, queued lifecycle persistence, workspace mentions, and managed external snapshots are implemented; rich artifact viewing and remaining hardening are out of scope. |
| Tools and integrations | **Moderate** | Skills discovery and enablement are now native Settings surfaces, and configured MCP/web activity can flow through app-server. Browser, Chrome, plugin/connector management, MCP administration, and Scheduled remain absent. |
| Desktop convenience | **Moderate** | Native Windows shell, themes, diagnostics, local dictation, custom Codex instruction defaults, shared-configuration source links, cross-chat search, find-in-chat, and core shortcuts exist; Activity view, notifications, full Voice coordination, quick chat, general task deep links, and broader personalization do not. |

## Detailed parity matrix

### Projects, chats, and execution

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Start a chat without a project | First-class General scope with a managed app-data workspace, explicit and implicit creation, persistence, resume/fork/archive, attachments, queues, permissions, and per-thread terminal context | **Full** | General intentionally has no Git or assistant-worktree operations until a project is attached. |
| Open a local project/folder | Folder picker, recent projects, project grouping, and project-scoped app-server work | **Full** | None material for the local coding loop. |
| Multiple folders in one local project | Edit project folders supports durable primary/secondary roots, primary changes, bounded Codex access, root-owned attachments, and selectable Git repositories | **Full** | Automatic `AGENTS.md`, skills, and `config.toml` discovery intentionally remains primary-only, matching Codex. The integrated terminal remains chat/worktree-owned rather than opening one terminal per attached folder. |
| Multiple local chats per project | Collapsible Chats and Projects groups, per-project disclosure, independently persisted chats, and pinned-first ordering | **Full** | ChatGPT has broader bulk chat-management controls. |
| Multi-turn conversations | Restored history, follow-up turns, per-turn transcript/activity, cancellation, and recovery | **Full** | None material for normal local follow-ups. |
| Edit and resubmit user prompts | Completed prompts have an inline editor; resubmission uses `thread/rollback`, keeps the selected and later prompts/responses visible as Previous versions, reuses attachments, and continues the same thread from the edited prompt | **Full** | Conversation history rewinds while existing workspace file changes are intentionally kept and clearly disclosed, matching app-server rollback semantics. |
| Resume, fork, archive, unarchive | Typed app-server lifecycle flows and UI actions | **Full** | None material. |
| Ephemeral side chats | Per-response forks create durable chats and preserve the selected history point | **Missing** | Add in-memory `thread/fork` with `ephemeral: true` for focused side questions that do not enter stored chat listings. |
| Rename chats | New General and project chats replace their placeholder title from the normalized first message after `turn/start` succeeds; manual chat menus also open a validated rename dialog. Both flows call typed `thread/name/set` and persist the acknowledged title locally | **Full** | Automatic titles are deterministic first-message names rather than a later model-generated summary. Project folder names remain filesystem-derived. |
| Pin, delete, and search chats | Hover- and selection-visible contextual actions; persisted sidebar pin/unpin with pinned-first ordering; confirmed delete; content search across General, project, and archived chats; current-chat occurrence search with next/previous wraparound and highlighting | **Full** | Because app-server has no permanent-delete method, delete first archives an active Codex thread and then removes SynthiaCode's local record; associated worktrees and branches are intentionally preserved. |
| Steer an active run | Active-turn guidance uses `turn/steer` | **Full** | None for steering itself. |
| Queue and manage follow-up messages | Per-thread persisted queues support Queue/Steer defaults, one-shot inversion, inline edit, reorder, manual send/steer, delete, completion-driven FIFO dispatch, and dispatch-time catalog/policy revalidation | **Full** | Live real-runtime disconnect/reconnect smoke coverage remains validation hardening rather than a functional parity gap. |
| Parallel top-level chats | Multiple project threads can run and route notifications independently | **Near** | No Activity view or dedicated global running-task manager and completion notification center. |
| Persistent Goal mode | A server-owned per-chat objective loads above the composer and supports set, edit, pause, resume, clear, status, usage, reconnect refresh, and matching push updates | **Full** | SynthiaCode displays a runtime-provided token budget but does not expose budget editing in this first slice. |
| Long-running/background work | Runs continue while SynthiaCode remains open; persistent Goal mode, reconnect, and shutdown handling are implemented | **Partial** | No prevent-sleep setting, Activity inbox, OS completion notifications, or cloud continuation. |
| Local worktrees | Assistant-owned Git worktrees can be created, used per chat, listed, and safely removed | **Partial** | No branch picker, Local/Worktree handoff, managed snapshots/restore, permanent worktrees, `.worktreeinclude`, setup scripts, or configurable retention/root. |

### Models, permissions, and account

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Authenticated model catalog | Reads `model/list`, hides unavailable models, and uses server-advertised capabilities | **Full** | None material. |
| Reasoning selection | Filters reasoning options by the selected model and persists the preference | **Full** | ChatGPT may expose additional intelligence labels for eligible models. |
| Fast mode | Uses advertised service tiers and keeps Fast distinct from model choice | **Full** | None material for supported models. |
| Custom developer and base instructions | Settings provides validated multiline developer instructions plus an advanced base-instruction replacement; enabled values are captured per chat and sent through typed `thread/start`, `thread/resume`, and `thread/fork` fields | **Full** | Changes intentionally apply to future chats only. Base instructions remain off by default so Codex resolves the selected model's runtime-owned default. |
| Ask for approval | Composer mode resolves to `:workspace`, `on-request`, and `user`; legacy fallback is `workspace-write` | **Full** | None material. |
| Approve for me | Uses the same workspace boundary and `on-request`, with `auto_review` | **Full** | None material. |
| Custom permissions | Follows the `config.toml` default or selects a named profile from `permissionProfile/list` | **Full** | SynthiaCode deliberately does not edit profile rules. |
| Managed permission requirements | Sandbox, policy, reviewer, and profile restrictions fail closed | **Full** | None material. |
| Server-request approval UI | Global exact-once queue for command, file-change, and permission requests; once/session/decline/cancel and selective grants | **Near** | ChatGPT can identify/inspect richer originating agent context and additional app/tool approval families. |
| Change permissions during a run | Permission controls are disabled while the selected turn is active and apply to the next turn | **Near** | ChatGPT exposes its permission control directly beneath the composer and coordinates it with subagent inspection; SynthiaCode now matches the composer placement but has no agent-thread drill-in. |
| ChatGPT sign-in and account state | ChatGPT/device-code sign-in, sign-out, account identity, plan context, rate-limit windows, reset times, and credits | **Near** | No editable profile, avatar, activity insights, invitations, or profile cards. |
| API-key/local-provider experience | Codex diagnostics can detect runtime/auth state, but no complete provider-management UI was found | **Partial** | ChatGPT/Codex supports broader API-key and local-provider configuration through shared Codex configuration. |

### Coding, Git, terminal, and review

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Streaming coding transcript | Batched streaming, distinct user messages, live expanded work details with Markdown commentary and structured activity rows, a collapsed duration disclosure retained above completed final responses, raw diagnostics, bounded history, and Jump to latest | **Full** | None material for text coding tasks. |
| Assistant Markdown rendering | Headings, emphasis, strikethrough, inline/fenced code, hierarchical ordered/unordered/task lists, quotes, rules, aligned tables, safe links/autolinks, local and remote images, safe native HTML, footnotes, definition lists, themed syntax highlighting, per-block copy, escapes, and literal unsafe/malformed fallback | **Full** | Arbitrary executable, embedded, form, media, and browser-DOM HTML intentionally remains visible and inert. |
| Rich activity rows | Commands, complete file changes, tools, MCP calls, structured web-search actions, plans, collaboration, guidance, and errors are projected without client-side text truncation | **Near** | Some newer item families may appear only in raw diagnostics until allowlisted. |
| Integrated terminal | Per-thread ConPTY PowerShell sessions with start, input, clear, kill, working directory, and bounded output | **Partial** | ChatGPT can directly consume current terminal output and exposes reusable project actions; SynthiaCode does not wire terminal output into agent context or environment actions. |
| Git status and file diff | Working/staged views, changed-file selection, refresh, repository selection across attached folders/worktrees, old/new line-numbered unified-diff rows, latest-review annotations with unmatched fallback, and pending user-comment cards | **Near** | No per-hunk actions or Commit/Branch/Last turn diff loading. |
| Stage, unstage, discard, commit | Repository-scoped file actions with destructive confirmation and commit message UI | **Near** | No individual-hunk operations. |
| Push and pull request | Terminal can run Git commands, but no native push/PR flow | **Missing** | Add branch push and GitHub pull-request creation/status. |
| User-authored inline review comments | Users can add, edit, and remove comments on old/new diff rows; comments persist per chat with renamed-file context and travel through the next start, steer, or durable queued follow-up with captured-ID acknowledgement | **Full** | None material for the documented line-feedback outcome. |
| Dedicated code review flow | A native Review action and exact `/review` picker call app-server `review/start` for uncommitted changes, a base branch, a commit, or custom instructions; lifecycle and prioritized findings restore as labeled turns and the latest result is derived into typed P0-P3 file/range records for inline diff rendering | **Near** | Detached delivery is not exposed; app-server's plain-text review payload omits confidence; Commit/Branch/Last turn diff panes are not implemented. |
| Editor and Explorer handoff | Open editor and reveal in Explorer are available | **Full** | None material on Windows. |
| Local environment setup/actions | No `.codex` setup-script or reusable action management UI | **Missing** | ChatGPT can configure worktree setup and one-click project actions. |
| Diagnostics | Codex discovery, auth/runtime diagnostics, refresh, and `codex doctor` are first-class UI | **Full** | This is stronger and more visible than a typical lightweight parity requirement. |

### Agents, tools, integrations, and context

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| `AGENTS.md` and shared Codex configuration | Settings edits the isolated shared `CODEX_HOME` `AGENTS.md` and `config.toml` with atomic stale-write protection, shows the active shared/workspace source chain in precedence order, and opens or reveals every source | **Full** | Workspace `AGENTS.md` and `.codex/config.toml` sources deliberately deep-link to the external editor rather than being rewritten through the shared-file editor. |
| Context-window visibility | A live percentage-used indicator sits beside Send; hover details show used/remaining percentages, latest-context tokens versus the model window, and cumulative compactions per persisted chat; app-server compaction lifecycle events render in the transcript | **Full** | Older settings show unavailable usage until app-server sends the first `thread/tokenUsage/updated` notification. Compaction and summarization remain owned by Codex app-server. |
| Subagent execution | Structured collaboration notifications populate an Active/Done panel; each receiver thread can be opened through `thread/read`, inspected as a transcript, steered through `turn/steer`, and stopped through `turn/interrupt` | **Near** | Agent nicknames, continuously refreshed open transcripts, resume controls, and custom-agent management remain absent. |
| MCP tool execution | Configured MCP tool activity and progress are parsed and shown | **Partial** | No MCP list/add/remove/auth/status UI or elicitation-specific presentation. |
| Skills | Settings manages active-workspace discovery and enablement; the composer provides an enabled-skill selector, `$` completion, duplicate-path disambiguation, removable invocation chips, and exact structured skill inputs across turns and queued follow-ups | **Near** | Skill creation/install, arbitrary extra roots, and a full `SKILL.md` body editor remain absent. |
| Plugins and app connectors | No SynthiaCode plugin/connector directory or authorization flow | **Missing** | ChatGPT supports plugins and connected services such as GitHub, Slack, Google Drive, Gmail, and calendars. |
| Web search | App-server web-search activity is rendered when the runtime uses it | **Partial** | No cached/live search control, source-focused result UI, or product-level availability setting. |
| Built-in Browser | No shared in-app browser, website permissions, comments, downloads, or browser developer mode | **Missing** | Requires a browser surface plus Browser tool/plugin integration. |
| Chrome integration | No Chrome extension or signed-in Chrome control | **Missing** | ChatGPT can operate existing Chrome sessions through its extension. |
| Computer Use | No screen/desktop control surface | **Missing** | ChatGPT can control supported desktop apps and browser UI with explicit permissions. |
| File attachments and image inputs | Image/file/folder pickers, clipboard file-list paste, Explorer drag/drop, ordered previews, image capability checks, attachment-only/mixed input, queue/transcript persistence, contained live workspace references, and immutable managed snapshots for external images/files/folders | **Near** | Interactive folder review/exclusions, optional live external roots, app-server history mention materialization, bounded thumbnail decoding, attachment-specific permission preflight, and installed-runtime managed-mention smoke coverage remain follow-up work. |
| Artifact/file viewer | Rich assistant Markdown renders in the transcript, but there is no document/spreadsheet/slide/PDF artifact viewer | **Missing** | ChatGPT can create and preview files in conversation. |
| Image generation | Explicit skill invocation can run image generation; canonical and legacy-restored generated-image results render inline before final text, open in the expanded viewer, and survive conversation persistence and `thread/read` restoration without retaining encoded image bytes in diagnostics | **Near** | No dedicated generation settings, variant gallery, or image-editing workflow outside the skill/tool path. |
| Sites and visualizations | No dedicated interactive artifact surfaces | **Missing** | These are broader ChatGPT capabilities rather than core local coding requirements. |
| Scheduled tasks | No create/manage/run history or recurring local project tasks | **Missing** | ChatGPT Scheduled supports local/worktree runs, chat continuity, skills, plugins, and RRULE schedules. |
| Remote/cloud connections | Local stdio app-server only; no SSH/device/cloud chat surface | **Missing** | ChatGPT supports remote connections, cloud environments, and cloud-operated work. |

### Desktop experience

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Native Windows application | WPF, single-process guard, responsive three-pane shell, and native file dialogs | **Full** | SynthiaCode is intentionally Windows-only. |
| Appearance | System, light, and dark themes | **Partial** | No accent/background/foreground editor, font selection, or theme sharing. |
| Keyboard shortcuts | Core project, submit, navigation, terminal, settings, refresh, cross-chat search (`Ctrl+K`), and find-in-chat (`Ctrl+F`) shortcuts | **Partial** | No command palette, searchable/customizable shortcut editor, or next/previous chat navigation. |
| Account and settings pane | Custom Codex instructions, shared configuration editors/provenance, active-workspace skills, a redacted origin-aware effective Codex settings summary, account, appearance, runtime, doctor, diagnostics, and about information | **Near** | ChatGPT still has substantially broader plugin, connector, automation, personalization, and application settings. |
| Notifications | Status bar and in-app state only | **Missing** | No OS completion notifications or notification preferences. |
| Activity view | Chats remain distributed across General/project/sidebar groups | **Missing** | Add a bell/inbox surface for unread, running, blocked, ready, and needs-input work with filters and mark-read state. |
| Local dictation | A composer microphone toggles continuous local Windows recognition, appends finalized phrases to the prompt or active-turn guidance, announces state accessibly, and surfaces recognizer/device/privacy failures without persisting audio or transcript data | **Near** | Recognition depends on an installed Windows speech recognizer for the current UI language rather than ChatGPT's cloud transcription and automatic language handling. |
| ChatGPT Voice coordination | No live bidirectional voice session or cross-thread voice control | **Missing** | ChatGPT Voice can start, check, and steer work in other Chat, Work, and Codex threads; local dictation is not equivalent. |
| Quick chat, pop-out, always-on-top | No compact or detached chat window | **Missing** | ChatGPT can keep a chat beside another app. |
| Deep links | No registered SynthiaCode URL scheme | **Missing** | ChatGPT supports links to chats, settings, skills, Scheduled, plugins, and connections. |
| Personalization and memories | Custom developer instructions and an advanced base-instruction override are editable and persisted; no personality, suggested prompts, or cross-chat memories | **Partial** | Instruction defaults are Codex-specific and apply to future chats rather than providing the broader ChatGPT personalization surface. |
| Chat profile, usage insights, and pets | Basic account/rate-limit view only | **Partial** | Profile analytics/cards and pets are non-core gaps. |

## What changed in this recheck

The 7 August current-feature recheck found and classified newly documented Codex surfaces:

1. **Goal mode moved from Missing to Full** for the local-chat outcome: server-owned goals now load per chat, render above the composer, and support set/edit/pause/resume/clear, usage, reconnect, and push updates.
2. **Multi-folder local projects moved from Missing to Full** for the local-project outcome: projects now retain one primary plus secondary roots, migrate existing scoped state, authorize bounded multi-root turns, preserve attachment ownership, and select among attached Git repositories.
3. **Activity view remains Missing.** Parallel execution exists, but there is no combined unread/running/blocked/needs-input inbox or completion notification center.
4. **ChatGPT Voice coordination remains Missing.** The implemented Windows recognizer is useful local dictation, not a GPT-Live conversation that can coordinate other threads.
5. **Ephemeral side chats remain Missing.** Current forks are durable; app-server now supports in-memory forks that do not enter stored thread lists.
6. **User-authored inline review comments moved from Missing to Full** for the documented line-feedback outcome: accessible old/new-side comments persist per chat and travel through start, steer, and durable queued follow-ups with exact captured context.

Phase 6A skills and effective settings moved from backend-only behavior to **Near** desktop parity:

1. Settings now discovers skills for the same active General, project, or worktree path used by task execution, preserves duplicate names by absolute `SKILL.md` path, and shows scope, description, dependencies, path, enabled state, and partial discovery errors.
2. Search and scope filtering compose over a recycling-virtualized list. Rows expose accessible enable/disable, Editor, and Explorer actions; enablement uses typed `skills/config/write`, treats `effectiveEnabled` as authoritative, and performs a forced rescan.
3. `skills/changed` is treated as debounced invalidation. Visible Settings refreshes through the existing app-server session, while hidden Settings only marks its cached result stale. Context changes and reconnects cannot apply results for an older workspace.
4. A separate read-only effective-settings view shows only an explicit safe allowlist and available origins. MCP headers, environment values, raw JSON, and all other configuration are discarded before reaching presentation or logs.
5. Existing SynthiaCode settings persistence, isolated `CODEX_HOME`, model/reasoning/service-tier controls, permission resolver, and atomic shared `AGENTS.md`/`config.toml` editors remain the owners of their previous behavior.
6. Five focused tests cover the typed protocol, nonfatal compatibility fallback, redaction, view-model lifecycle, and rendered WPF surface as part of the 209-test regression suite.

Custom Codex instructions moved from absent to **Full** for the local app-server outcome:

1. Settings now provides explicit, multiline developer instructions and a separately gated advanced base-instruction replacement, with validation, a 64 KiB UTF-8 limit per field, save/reset actions, and a warning that values are stored as plain text.
2. Disabled or blank overrides are omitted. In particular, leaving base instructions disabled preserves the selected model's normal Codex base instructions instead of reading or rewriting `models_cache.json`.
3. New chats capture the currently saved defaults; resume and fork reuse the source chat's captured values, so later settings edits never silently alter existing conversations. Legacy chats continue with no explicit override.
4. Typed `thread/start`, `thread/resume`, and `thread/fork` requests serialize `developerInstructions` and `baseInstructions`; older runtimes that reject these fields receive an actionable update-or-disable error.
5. Settings JSON round trips, coalesced snapshots, thread storage/presentation conversions, General/project creation paths, implicit first-prompt creation, resume-failure recovery, and forks all retain instruction state without logging instruction contents.
6. Protocol, persistence, lifecycle, validation, legacy-compatibility, unsupported-runtime, and rendered-WPF regressions are included in the 187-test suite.

Chat rename moved from absent to **Full** for both sidebar scopes:

1. A newly created General or project chat now carries an explicit placeholder marker. After its first `turn/start` succeeds, SynthiaCode normalizes the first message to a single-line title, sends typed `thread/name/set`, and replaces the placeholder only after app-server acknowledgement.
2. The placeholder marker is persisted and cleared by either automatic or manual rename, so follow-ups never rename the chat again and forked, restored legacy, or manually named chats are not overwritten. Attachment-only first messages fall back to the first attachment display name.
3. General chats under **Chats** and project-scoped chats under **Projects** expose the same manual Rename action in their contextual menus.
4. Manual Rename opens a themed, keyboard-friendly dialog prefilled with the current display title; Cancel leaves the chat unchanged, whitespace is trimmed, blank names are rejected, and submitting the current explicit title avoids an unnecessary request.
5. Successful automatic and manual changes update SynthiaCode persistence, recency, selected-title presentation, navigation, and cross-chat search results. Automatic rename failure is isolated from the already-started turn.
6. Protocol serialization, storage normalization, shared command routing across both scopes, rendered WPF menu placement and visibility, manual rename lifecycle, first-message naming, persistence, and exactly-once follow-up behavior are protected by seven focused tests in the 182-test regression suite.
7. Project directory labels are intentionally unchanged because they remain derived from their filesystem folder names; “both Chats and Projects” refers to chat threads in those two navigation groups.

Chat management and search moved from **Partial** to **Full** for the requested desktop outcome:

1. General and project chat action menus expose Pin/Unpin and Delete. Their `⋯` buttons appear when the chat row is hovered, remain visible for the selected row, and stay hidden on idle unselected rows. Pin state persists in existing settings data, updates the action label, and sorts pinned chats ahead of newer unpinned chats in both sidebar scopes.
2. Delete requires explicit destructive confirmation. Unarchived Codex threads are archived through app-server before SynthiaCode removes the local chat, queue, draft, terminal, and in-memory routing state; assistant worktrees and Git branches are deliberately preserved.
3. The sidebar search field searches titles, previews, final responses, and user/assistant transcript content across General, project, and archived chats. Results include scope and matching context, retain pinned-first ordering, and switch to the owning scope and chat when opened.
4. Find-in-chat counts case-insensitive occurrences in both user and assistant messages, supports next/previous wraparound, scrolls to and highlights the current matching turn, and clears transient match state when closed.
5. `Ctrl+K` opens/focuses cross-chat search and `Ctrl+F` opens/focuses find-in-chat; Enter/Shift+Enter navigate matches and Escape closes the find bar.
6. A focused rendered-WPF hover/selection regression plus five persistence, command, main-lifecycle, cross-scope search, and occurrence-navigation tests and existing automation/layout assertions protect the features in the current 182-test regression suite. The full suite also caught and fixed a pinned-label layout regression so long sidebar titles remain width-constrained and wrap correctly.

Editable user prompts moved from absent to **Full** parity for the Codex-style local-thread outcome:

1. Every completed active user prompt exposes an inline Edit action with change-aware Resubmit and Cancel controls.
2. Resubmission calls the typed `thread/rollback` app-server flow for the selected turn plus every later active turn, then starts the edited prompt on the same thread with the original prompt attachments.
3. Rolled-back prompts, assistant responses, activity, attachments, and timestamps remain visible and persisted as **Previous version** transcript entries; later follow-ups continue from the replacement turn rather than the superseded history.
4. Previous versions cannot be edited again, active runs disable editing, unchanged or blank edits cannot submit, and rollback failures leave the original history active.
5. The editor explains Codex rollback semantics before submission: conversational context rewinds, but workspace file changes remain. Protocol, reducer, view-model, two-turn integration, persistence metadata, and WPF-surface coverage are included in the 169-test regression suite.

Chat and project navigation now follows the compact Codex-style disclosure pattern:

1. The former General navigation group is presented as **Chats**, matching the user-facing conversation terminology while retaining the protocol's internal thread model.
2. Chats and Projects have independent, accessible disclosure controls and live chevrons; both start expanded and can be collapsed or reopened without changing selection or data.
3. Individual projects retain their existing per-project disclosure, creation actions, counts, running indicators, and chat lists inside the top-level Projects group.
4. Navigation tooltips, empty states, action labels, Git guidance, and the no-selection title now use chat-oriented wording consistently.
5. Focused view-model tests and rendered-WPF tests cover independent toggling, command wiring, labels, disclosure state, and content visibility as part of the 161-test suite.

Assistant answer Markdown moved from basic text/link rendering to **Full** parity for common technical responses:

1. Inline rendering now supports bold, italic, combined bold/italic, strikethrough, styled inline code, safe links/autolinks, and backslash-escaped Markdown punctuation.
2. Block rendering now supports six ATX heading levels, ordered and unordered lists, checked and unchecked task lists, multi-line block quotes, horizontal rules, and backtick or tilde fenced code blocks with horizontal scrolling.
3. Pipe tables retain bold/code/link formatting inside cells, honor left/center/right delimiter alignment, use responsive themed grids, and stay within the transcript width.
4. Invalid tables, unmatched emphasis, and unclosed code fences remain visible rather than being partially consumed; focused parser tests, malformed-input tests, and responsive transcript coverage protect these behaviors.
5. Validated HTTP(S) images render in bounded cards with safe source links and nonfatal failure fallback; unsupported schemes remain literal.
6. A native raw-HTML allowlist supports semantic emphasis, deletion, code, line breaks, safe anchors/images, paragraphs, headings, quotes, and preformatted text without hosting executable browser content.
7. Contiguous nested lists use real depth margins; superscript references navigate to bottom-placed footnotes; Markdown Extra term/definition groups use accessible native layouts.
8. Common fenced-code language identifiers produce themed comment/string/number/keyword tokens, a normalized language label, and an accessible copy action scoped to the exact block source.

Activity presentation now follows the combined Codex-style assistant outcome more closely:

1. Each turn keeps a distinct user message while activity is nested at the top of the corresponding assistant message card.
2. The activity expander retains live auto-expansion, historical collapse, stable lifecycle rows, and a divider from the final answer.
3. User-facing activity no longer receives a 600-character ellipsis, and file changes retain every reported path rather than replacing paths after the fourth with a count.
4. Completed web-search rows prefer the protocol's complete structured query list, page URL, or find-in-page pattern and URL, with the display query retained as a compatibility fallback.
5. Long details wrap within the transcript; reducer, persistence, visual-containment, responsive-width, timestamp, and copy-action regressions are covered by the 161-test behavioral suite.

Context-window visibility moved from absent to **Full** parity for the live local-chat outcome:

1. A compact percentage-used indicator now sits in the bottom composer action row immediately beside Send.
2. Its hover details show percentage used, percentage remaining, compact latest-context token usage versus the model context window, and the chat's compaction count.
3. SynthiaCode now subscribes to `thread/tokenUsage/updated` and calculates latest-context usage as `tokenUsage.last.totalTokens - tokenUsage.last.reasoningOutputTokens`, matching Codex context-window semantics rather than cumulative session usage. Missing reasoning usage defaults to zero, and oversized values clamp the result to zero.
4. Current `contextCompaction` item lifecycles and legacy `thread/compacted` notifications are counted without duplicate completed items, remain available in diagnostics, stay isolated by chat, and render as user-facing context activity in the owning turn.
5. Codex app-server remains the sole compaction and summarization owner. SynthiaCode does not use token thresholds to start compaction, replace conversation content, or generate a local summary.
6. Token/window snapshots and cumulative compaction counts persist through settings snapshots, chat restoration, switching, shutdown saves, and forks; eight focused reducer, edge-case, ownership, persistence, subscription, formatting, and rendered-WPF tests protect the feature.

Projectless threads moved from **Missing** to **Full** for the local conversation outcome:

1. A dedicated collapsible Chats group and New action create chats without adding or selecting a project; first prompt submission also creates the General scope implicitly.
2. General threads use a contained shared `%LOCALAPPDATA%\SynthiaCode\workspaces\general` root, and every thread/turn lifecycle request receives the correct absolute `cwd`.
3. Explicit scope identity keeps General persistence, active selection, drafts, queued follow-ups, notifications, and navigation separate from project threads while legacy settings default to Project.
4. Resume, fork, archive, unarchive, attachments, permission discovery, and isolated terminal sessions work in General; Git and assistant-worktree mutations remain project-only with a clear empty state.
5. General-workspace initialization fails closed without disabling project-thread creation, and existing project/current-checkout/worktree flows remain covered by the full regression suite.

The permissions area moved from **partial** to **full/near-full functional parity**:

1. The primary control now lives under the composer, alongside model controls.
2. Ask for approval and Approve for me share the same workspace boundary and differ only by reviewer.
3. Custom follows `config.toml` or selects a discovered named permission profile.
4. Managed reviewer/profile restrictions are enforced and unavailable profiles are disabled.
5. Permission profile and legacy sandbox fields are mutually exclusive on every lifecycle request.
6. Unknown, stale, and disallowed selections fail closed.
7. Human-required server requests retain the global approval queue and exact-once response behavior.

P0 queued follow-ups moved from **Missing** to **Full**:

1. Queue is the persisted default, Steer remains selectable, and `Ctrl+Shift+Enter` inverts the choice once.
2. Each thread owns a persisted queue that is visible above the composer and supports inline edit, reorder, manual send/steer, and delete.
3. Successful completions drain one FIFO item on the owning thread, even when another running thread is selected.
4. Failed or cancelled turns pause the queue; interrupted `Starting` items restore as `NeedsAttention` and are never retried automatically.
5. Queue mutations persist immediately, and archive/worktree removal is blocked while queued work remains.
6. Queued snapshots now retain logical permission intent; dispatch refreshes the model catalog, managed requirements, workspace config, and permission profiles inside the per-thread gate, then fails closed if the captured model, reasoning, Fast tier, or permission choice is no longer allowed.

Queued-dispatch hardening TDD ledger:

| Phase | Implemented checkpoint | Status |
| --- | --- | --- |
| Phase 1 - Red coverage | Added focused coverage for refreshed model/reasoning/Fast validation, managed permission-policy re-resolution, removed named profiles, and an awaited in-gate preflight immediately before `turn/start`. The focused build fails on the intentionally missing production contracts. | **Complete** |
| Phase 2 - Implementation | Added logical permission intent to queued snapshots and every clone/storage boundary; implemented fail-closed catalog and managed-policy resolution; and added an awaited background preflight that refreshes `model/list`, requirements, workspace config, and permission profiles immediately before `turn/start`. Focused implementation and persistence coverage passes 12/12. | **Complete** |
| Phase 3 - Verification | Focused implementation/persistence coverage passed 12/12; the complete Debug and Release suites each passed 252/252; Debug and Release Rebuild targets completed with zero warnings/errors; and both `SynthiaCode.App.exe` outputs were verified. | **Complete** |

P0 attachments and image input moved from **Missing** to **Near**:

1. The composer accepts PNG, JPEG, WebP, and non-animated GIF through a multi-select picker, clipboard image/file paste, and handled routed drag/drop.
2. Ordered image previews can be opened, moved, or removed; sent turns render their image previews in the transcript.
3. `turn/start` and `turn/steer` now send typed ordered text/`localImage` parts and permit image-only requests.
4. Model `inputModalities` blocks unsupported image submission without discarding the draft.
5. Content-addressed managed images and external file/folder snapshots survive source deletion, deduplicate, enforce type-specific size/dimension/depth/count/store limits, and persist safely in drafts, queued follow-ups, and conversation snapshots.
6. Startup rehydrates managed paths and performs reference-aware staging/orphan cleanup, including files and empty directories beneath folder objects; unavailable objects fail visibly.
7. Workspace files and folders now share the ordered attachment strip and can be added through dedicated pickers, clipboard file lists, or Explorer drag/drop.
8. File/folder references are stored as workspace-relative live references, revalidated against the owning thread or queued workspace, and serialized as app-server `mention` inputs for start and steer.
9. Containment rejects the workspace root, sibling-prefix escapes, wildcards, alternate data streams, missing paths, and reparse targets outside the workspace. External files/folders are classified separately and imported without weakening workspace containment.
10. Generic attachment metadata, including folder file-count/byte summaries, persists through drafts, queues, turns, forks, and attachment schema v3 settings snapshots while legacy `Images`/`UserImages` settings continue to load.
11. Text-only models continue to accept file/folder mentions; image capability gating applies only when an image is present.
12. External regular files stream into immutable managed objects with a 25 MiB per-file limit; external folder trees use deterministic snapshots capped at 32 levels, 1,000 entries, and 100 MiB.
13. Folder snapshots reject reparse entries, preserve empty directories, detect file mutation during copy, clean failed staging trees, and never retain the original external source path.
14. Managed images remain `localImage` inputs while managed files/folders use `mention` inputs across start, steer, queue, retry, and background dispatch paths.
15. Existing exact permission-request approval remains the Codex access boundary for managed mention paths; SynthiaCode does not add writable roots, switch profiles, or edit `config.toml`.

## Recommended parity backlog

### P0 — Complete the core local coding experience

1. **Attachments and image input (managed external core implemented):** add installed-runtime managed file/folder mention smoke coverage, attachment-specific permission preflight/narrowing, interactive folder review/exclusions, bounded thumbnail decoding, and app-server history attachment materialization. Optional live external roots remain deferred.
2. **Interactive Git review (structured findings and user comments implemented):** add hunk staging/revert and Commit/Branch/Last turn diff loading on top of the implemented multi-repository selector, dedicated review targets, typed inline reviewer annotations, and user-authored old/new-side comments.
3. **Push and pull requests:** add native branch push and GitHub PR creation/status.
4. **Worktree lifecycle:** add starting-branch selection, setup scripts/actions, Local/Worktree handoff, snapshots/restore, and retention settings.

### P1 — Make parallel and long-running work first class

1. **Subagent management (core implemented):** add nicknames, live open-transcript refresh, explicit resume, and custom-agent management on top of the Active/Done inspect/open/steer/stop panel.
2. **Chat management (core implemented):** add running-task filtering and optional bulk chat-management actions.
3. **Activity and notifications:** add unread/running/blocked/needs-input filters, mark-read state, completion alerts, prevent-sleep, and live real-runtime queued-dispatch disconnect/reconnect smoke coverage.
4. **Ephemeral side chats:** use in-memory forks for focused questions without creating durable sidebar entries.
5. **Terminal integration:** expose current terminal output to Codex and add reusable project actions.
6. **MCP visibility:** show configured servers, health, authentication state, and provenance without owning their configuration semantics unnecessarily; add optional skill invocation UX only after the composer contract is defined.

### P2 — Expand into the ChatGPT ecosystem

1. Browser and Chrome control.
2. Plugin/connector directory and authorization.
3. Scheduled tasks and run history.
4. Remote connections and cloud chats.
5. Artifact viewer, visualizations, Sites, and richer native image-generation controls.
6. Full ChatGPT Voice coordination across threads.
7. Rich appearance, shortcut customization, quick chat, deep links, personalization, and memories.

## Product recommendation

Keep SynthiaCode's parity target focused on the **local coding loop**, not every ChatGPT feature. With queued follow-ups, persistent Goal mode, multi-folder local projects, and user-authored inline review comments at Full functional parity, the best next release is hunk-level review plus push/PR workflows, followed by complete worktree lifecycle. Those close the largest everyday workflow gaps without requiring SynthiaCode to become a browser, connector marketplace, automation platform, or general artifact suite.

## Audit sources

SynthiaCode evidence was taken from the current repository implementation, tests, `README.md`, and `docs/current-architecture.md`.

Current ChatGPT/Codex behavior was checked against the official OpenAI manual and these source pages:

- [ChatGPT desktop app commands](https://learn.chatgpt.com/docs/reference/commands)
- [ChatGPT desktop app settings](https://learn.chatgpt.com/docs/reference/settings)
- [What's new](https://learn.chatgpt.com/docs/whats-new)
- [Long-running work and Goal mode](https://learn.chatgpt.com/docs/long-running-work)
- [Notifications and Activity view](https://learn.chatgpt.com/docs/notifications)
- [Permissions modes](https://learn.chatgpt.com/docs/permission-modes)
- [Named permission profiles](https://learn.chatgpt.com/docs/permissions)
- [Integrated terminal](https://learn.chatgpt.com/docs/integrated-terminal)
- [Local environments and Git tools](https://learn.chatgpt.com/docs/environments/local-environment)
- [Worktrees](https://learn.chatgpt.com/docs/environments/git-worktrees)
- [Scheduled tasks](https://learn.chatgpt.com/docs/automations)
- [Browser](https://learn.chatgpt.com/docs/browser)
- [Plugins](https://learn.chatgpt.com/docs/plugins)
- [Projects and chats](https://learn.chatgpt.com/docs/projects)
- [Image inputs](https://learn.chatgpt.com/docs/image-inputs)
- [Code review](https://learn.chatgpt.com/docs/code-review)
- [Codex app-server protocol](https://learn.chatgpt.com/docs/app-server)
