# SynthiaCode and ChatGPT Desktop Feature Parity

- **Audit date:** 24 July 2026
- **SynthiaCode baseline:** working tree based on commit `9b5a8bc`, including automatic first-message naming, manual thread rename, hover-visible sidebar chat actions, sidebar chat management, cross-chat search, find-in-chat, and the completed parity work recorded below
- **Comparison surface:** ChatGPT desktop app with Codex/local-project capabilities
- **Scope:** User-visible desktop functionality, local Codex workflows, and capabilities inherited through `codex app-server`

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
| Local coding loop | **Strong** | General and project chats, multi-turn work, queued follow-ups, streaming, models, permissions, terminal, Git changes, and worktrees are usable end to end. |
| Safety and approvals | **Near full** | The three composer permission modes and server-request approvals now map closely to ChatGPT desktop. |
| Git and worktree lifecycle | **Moderate** | Core isolation and file-level Git operations exist; chunk review, handoff, push, PR, snapshots, and setup actions do not. |
| Agent orchestration | **Partial** | Parallel top-level chats and collaboration activity exist, but subagent thread inspection and management are absent. |
| Context and multimodal input | **Near** | Per-chat context-window visibility plus image/file/folder picker, paste/drop, previews, queued lifecycle persistence, workspace mentions, and managed external snapshots are implemented; rich artifact viewing and remaining hardening are out of scope. |
| Tools and integrations | **Low** | Configured MCP/web activity can flow through app-server, but Browser, Chrome, plugins, connectors, skills management, and Scheduled are not product surfaces. |
| Desktop convenience | **Moderate** | Native Windows shell, themes, diagnostics, custom Codex instruction defaults, cross-chat search, find-in-chat, and core shortcuts exist; notifications, dictation, quick chat, deep links, and broader personalization do not. |

## Detailed parity matrix

### Projects, chats, and execution

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Start a chat without a project | First-class General scope with a managed app-data workspace, explicit and implicit creation, persistence, resume/fork/archive, attachments, queues, permissions, and per-thread terminal context | **Full** | General intentionally has no Git or assistant-worktree operations until a project is attached. |
| Open a local project/folder | Folder picker, recent projects, project grouping, and project-scoped app-server work | **Full** | None material for the local coding loop. |
| Multiple local chats per project | Collapsible Chats and Projects groups, per-project disclosure, independently persisted chats, and pinned-first ordering | **Full** | ChatGPT has broader bulk chat-management controls. |
| Multi-turn conversations | Restored history, follow-up turns, per-turn transcript/activity, cancellation, and recovery | **Full** | None material for normal local follow-ups. |
| Edit and resubmit user prompts | Completed prompts have an inline editor; resubmission uses `thread/rollback`, keeps the selected and later prompts/responses visible as Previous versions, reuses attachments, and continues the same thread from the edited prompt | **Full** | Conversation history rewinds while existing workspace file changes are intentionally kept and clearly disclosed, matching app-server rollback semantics. |
| Resume, fork, archive, unarchive | Typed app-server lifecycle flows and UI actions | **Full** | None material. |
| Rename chats | New General and project chats replace their placeholder title from the normalized first message after `turn/start` succeeds; manual chat menus also open a validated rename dialog. Both flows call typed `thread/name/set` and persist the acknowledged title locally | **Full** | Automatic titles are deterministic first-message names rather than a later model-generated summary. Project folder names remain filesystem-derived. |
| Pin, delete, and search chats | Hover- and selection-visible contextual actions; persisted sidebar pin/unpin with pinned-first ordering; confirmed delete; content search across General, project, and archived chats; current-chat occurrence search with next/previous wraparound and highlighting | **Full** | Because app-server has no permanent-delete method, delete first archives an active Codex thread and then removes SynthiaCode's local record; associated worktrees and branches are intentionally preserved. |
| Steer an active run | Active-turn guidance uses `turn/steer` | **Full** | None for steering itself. |
| Queue and manage follow-up messages | Per-thread persisted queues support Queue/Steer defaults, one-shot inversion, inline edit, reorder, manual send/steer, delete, and completion-driven FIFO dispatch | **Near** | Dispatch validates the captured workspace but does not yet refresh and re-resolve model-catalog and managed-permission policy immediately before a background start. |
| Parallel top-level chats | Multiple project threads can run and route notifications independently | **Near** | No dedicated global running-task manager or completion notification center. |
| Long-running/background work | Runs continue while SynthiaCode remains open; reconnect and shutdown are handled | **Partial** | No prevent-sleep setting, background inbox, OS completion notifications, or cloud continuation. |
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
| Streaming coding transcript | Batched streaming, distinct user messages, combined activity/assistant surfaces, raw diagnostics, bounded history, and Jump to latest | **Full** | None material for text coding tasks. |
| Assistant Markdown rendering | Headings, bold, italic, combined emphasis, strikethrough, inline and fenced code, ordered/unordered/task lists, block quotes, horizontal rules, aligned pipe tables, safe links/autolinks, escapes, and literal malformed-source fallback | **Near** | No remote Markdown images, raw HTML, nested-list layout, footnotes, definition lists, syntax highlighting, or per-code-block copy action. |
| Rich activity rows | Commands, complete file changes, tools, MCP calls, structured web-search actions, plans, collaboration, guidance, and errors are projected without client-side text truncation | **Near** | Some newer item families may appear only in raw diagnostics until allowlisted. |
| Integrated terminal | Per-thread ConPTY PowerShell sessions with start, input, clear, kill, working directory, and bounded output | **Partial** | ChatGPT can directly consume current terminal output and exposes reusable project actions; SynthiaCode does not wire terminal output into agent context or environment actions. |
| Git status and file diff | Working/staged views, changed-file selection, and refresh | **Near** | Diff is plain text rather than a structured hunk/code-review surface. |
| Stage, unstage, discard, commit | File-level actions with destructive confirmation and commit message UI | **Near** | No individual-hunk operations. |
| Push and pull request | Terminal can run Git commands, but no native push/PR flow | **Missing** | Add branch push and GitHub pull-request creation/status. |
| Inline review comments | No diff-row comments that become next-turn context | **Missing** | ChatGPT supports inline comments in its review pane. |
| Dedicated code review flow | General prompts can request review; no `/review` target picker or first-class findings UI | **Partial** | Add uncommitted/base-branch/commit targets and severity/file-line findings. |
| Editor and Explorer handoff | Open editor and reveal in Explorer are available | **Full** | None material on Windows. |
| Local environment setup/actions | No `.codex` setup-script or reusable action management UI | **Missing** | ChatGPT can configure worktree setup and one-click project actions. |
| Diagnostics | Codex discovery, auth/runtime diagnostics, refresh, and `codex doctor` are first-class UI | **Full** | This is stronger and more visible than a typical lightweight parity requirement. |

### Agents, tools, integrations, and context

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| `AGENTS.md` and shared Codex configuration | Inherited by the launched Codex runtime | **Near** | No editor, provenance view, or configuration deep links. |
| Context-window visibility | A live percentage-used indicator sits beside Send; hover details show used/remaining percentages, latest-context tokens versus the model window, and cumulative compactions per persisted chat; app-server compaction lifecycle events render in the transcript | **Full** | Older settings show unavailable usage until app-server sends the first `thread/tokenUsage/updated` notification. Compaction and summarization remain owned by Codex app-server. |
| Subagent execution | Collaboration notifications render as agent activity when Codex delegates | **Partial** | No Active/Done panel, agent-thread transcript, open/steer/stop controls, nicknames, or custom-agent management. |
| MCP tool execution | Configured MCP tool activity and progress are parsed and shown | **Partial** | No MCP list/add/remove/auth/status UI or elicitation-specific presentation. |
| Skills | Codex may load configured skills through its runtime | **Partial** | No Skills directory, enable/disable controls, install flow, `$skill` picker, or skill detail UI. |
| Plugins and app connectors | No SynthiaCode plugin/connector directory or authorization flow | **Missing** | ChatGPT supports plugins and connected services such as GitHub, Slack, Google Drive, Gmail, and calendars. |
| Web search | App-server web-search activity is rendered when the runtime uses it | **Partial** | No cached/live search control, source-focused result UI, or product-level availability setting. |
| Built-in Browser | No shared in-app browser, website permissions, comments, downloads, or browser developer mode | **Missing** | Requires a browser surface plus Browser tool/plugin integration. |
| Chrome integration | No Chrome extension or signed-in Chrome control | **Missing** | ChatGPT can operate existing Chrome sessions through its extension. |
| Computer Use | No screen/desktop control surface | **Missing** | ChatGPT can control supported desktop apps and browser UI with explicit permissions. |
| File attachments and image inputs | Image/file/folder pickers, clipboard file-list paste, Explorer drag/drop, ordered previews, image capability checks, attachment-only/mixed input, queue/transcript persistence, contained live workspace references, and immutable managed snapshots for external images/files/folders | **Near** | Interactive folder review/exclusions, optional live external roots, app-server history mention materialization, bounded thumbnail decoding, attachment-specific permission preflight, and installed-runtime managed-mention smoke coverage remain follow-up work. |
| Artifact/file viewer | Rich assistant Markdown renders in the transcript, but there is no document/spreadsheet/slide/PDF artifact viewer | **Missing** | ChatGPT can create and preview files in conversation. |
| Image generation, Sites, and visualizations | No dedicated generation or interactive artifact surfaces | **Missing** | These are broader ChatGPT capabilities rather than core local coding requirements. |
| Scheduled tasks | No create/manage/run history or recurring local project tasks | **Missing** | ChatGPT Scheduled supports local/worktree runs, chat continuity, skills, plugins, and RRULE schedules. |
| Remote/cloud connections | Local stdio app-server only; no SSH/device/cloud chat surface | **Missing** | ChatGPT supports remote connections, cloud environments, and cloud-operated work. |

### Desktop experience

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Native Windows application | WPF, single-process guard, responsive three-pane shell, and native file dialogs | **Full** | SynthiaCode is intentionally Windows-only. |
| Appearance | System, light, and dark themes | **Partial** | No accent/background/foreground editor, font selection, or theme sharing. |
| Keyboard shortcuts | Core project, submit, navigation, terminal, settings, refresh, cross-chat search (`Ctrl+K`), and find-in-chat (`Ctrl+F`) shortcuts | **Partial** | No command palette, searchable/customizable shortcut editor, or next/previous chat navigation. |
| Account and settings pane | Custom Codex instructions, account, appearance, Codex runtime, doctor, diagnostics, and about information | **Near** | ChatGPT has substantially broader settings. |
| Notifications | Status bar and in-app state only | **Missing** | No OS completion notifications or notification preferences. |
| Dictation/voice input | No speech input | **Missing** | ChatGPT desktop supports dictation. |
| Quick chat, pop-out, always-on-top | No compact or detached chat window | **Missing** | ChatGPT can keep a chat beside another app. |
| Deep links | No registered SynthiaCode URL scheme | **Missing** | ChatGPT supports links to chats, settings, skills, Scheduled, plugins, and connections. |
| Personalization and memories | Custom developer instructions and an advanced base-instruction override are editable and persisted; no personality, suggested prompts, or cross-chat memories | **Partial** | Instruction defaults are Codex-specific and apply to future chats rather than providing the broader ChatGPT personalization surface. |
| Chat profile, usage insights, and pets | Basic account/rate-limit view only | **Partial** | Profile analytics/cards and pets are non-core gaps. |

## What changed in this recheck

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

Assistant answer Markdown moved from basic text/link rendering to **Near** parity for common technical responses:

1. Inline rendering now supports bold, italic, combined bold/italic, strikethrough, styled inline code, safe links/autolinks, and backslash-escaped Markdown punctuation.
2. Block rendering now supports six ATX heading levels, ordered and unordered lists, checked and unchecked task lists, multi-line block quotes, horizontal rules, and backtick or tilde fenced code blocks with horizontal scrolling.
3. Pipe tables retain bold/code/link formatting inside cells, honor left/center/right delimiter alignment, use responsive themed grids, and stay within the transcript width.
4. Invalid tables, unmatched emphasis, and unclosed code fences remain visible rather than being partially consumed; focused parser tests, malformed-input tests, and responsive transcript coverage protect these behaviors.
5. Remaining Markdown gaps are explicitly limited to remote images, raw HTML, nested-list layout, footnotes, definition lists, syntax highlighting, and code-block-specific copy controls.

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

P0 queued follow-ups moved from **Missing** to **Near**:

1. Queue is the persisted default, Steer remains selectable, and `Ctrl+Shift+Enter` inverts the choice once.
2. Each thread owns a persisted queue that is visible above the composer and supports inline edit, reorder, manual send/steer, and delete.
3. Successful completions drain one FIFO item on the owning thread, even when another running thread is selected.
4. Failed or cancelled turns pause the queue; interrupted `Starting` items restore as `NeedsAttention` and are never retried automatically.
5. Queue mutations persist immediately, and archive/worktree removal is blocked while queued work remains.

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

1. **Queued follow-ups hardening (core implemented):** refresh and re-resolve model availability and managed permission policy immediately before background dispatch, then add live disconnect/reconnect smoke coverage.
2. **Attachments and image input (managed external core implemented):** add installed-runtime managed file/folder mention smoke coverage, attachment-specific permission preflight/narrowing, interactive folder review/exclusions, bounded thumbnail decoding, and app-server history attachment materialization. Optional live external roots remain deferred.
3. **Structured Git review:** add hunk staging/revert, inline diff comments, and a dedicated review target flow.
4. **Push and pull requests:** add native branch push and GitHub PR creation/status.
5. **Worktree lifecycle:** add starting-branch selection, setup scripts/actions, Local/Worktree handoff, snapshots/restore, and retention settings.

### P1 — Make parallel and long-running work first class

1. **Subagent panel:** show Active/Done agents with inspect, open, steer, and stop controls.
2. **Chat management (core implemented):** add running-task filtering and optional bulk chat-management actions.
3. **Notifications/background reliability:** completion notifications, prevent-sleep, and a task inbox.
4. **Terminal integration:** expose current terminal output to Codex and add reusable project actions.
5. **MCP and skills visibility:** show configured servers/skills, health, provenance, and enablement without owning their configuration semantics unnecessarily.

### P2 — Expand into the ChatGPT ecosystem

1. Browser and Chrome control.
2. Plugin/connector directory and authorization.
3. Scheduled tasks and run history.
4. Remote connections and cloud chats.
5. Artifact viewer, visualizations, image generation, and Sites.
6. Rich appearance, shortcut customization, dictation, quick chat, deep links, personalization, and memories.

## Product recommendation

Keep SynthiaCode's parity target focused on the **local coding loop**, not every ChatGPT feature. With the core queued-follow-up workflow implemented, the best next release is the remaining P0 set: attachments, structured review/PR workflows, complete worktree lifecycle, and the smaller queued-dispatch policy-revalidation hardening item. Those close the largest everyday workflow gaps without requiring SynthiaCode to become a browser, connector marketplace, automation platform, or general artifact suite.

## Audit sources

SynthiaCode evidence was taken from the current repository implementation, tests, `README.md`, and `docs/current-architecture.md`.

Current ChatGPT/Codex behavior was checked against the official OpenAI manual and these source pages:

- [ChatGPT desktop app commands](https://learn.chatgpt.com/docs/reference/commands)
- [ChatGPT desktop app settings](https://learn.chatgpt.com/docs/reference/settings)
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
- [Codex app-server protocol](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
