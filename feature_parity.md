# SynthiaCode and ChatGPT Desktop Feature Parity

- **Audit date:** 19 July 2026
- **SynthiaCode baseline:** commit `68f1f90` (`Move permissions`)
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
| Local coding loop | **Strong** | Projects, chats, multi-turn work, streaming, models, permissions, terminal, Git changes, and worktrees are usable end to end. |
| Safety and approvals | **Near full** | The three composer permission modes and server-request approvals now map closely to ChatGPT desktop. |
| Git and worktree lifecycle | **Moderate** | Core isolation and file-level Git operations exist; chunk review, handoff, push, PR, snapshots, and setup actions do not. |
| Agent orchestration | **Partial** | Parallel top-level chats and collaboration activity exist, but subagent thread inspection and management are absent. |
| Context and multimodal input | **Low** | Text and workspace context work; attachments, screenshots, image input, and artifact workflows are missing. |
| Tools and integrations | **Low** | Configured MCP/web activity can flow through app-server, but Browser, Chrome, plugins, connectors, skills management, and Scheduled are not product surfaces. |
| Desktop convenience | **Moderate** | Native Windows shell, themes, diagnostics, and core shortcuts exist; search, notifications, dictation, quick chat, deep links, and personalization do not. |

## Detailed parity matrix

### Projects, chats, and execution

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Open a local project/folder | Folder picker, recent projects, project grouping, and project-scoped app-server work | **Full** | None material for the local coding loop. |
| Multiple local chats per project | Project/thread navigation with independently persisted threads | **Full** | ChatGPT has broader chat-management and search controls. |
| Multi-turn conversations | Restored history, follow-up turns, per-turn transcript/activity, cancellation, and recovery | **Full** | None material for normal local follow-ups. |
| Resume, fork, archive, unarchive | Typed app-server lifecycle flows and UI actions | **Full** | Permanent delete is not exposed. |
| Pin, delete, and search chats | Archive state exists; no complete pin/delete/search UI | **Partial** | Add sidebar pin/delete, cross-chat search, and find-in-chat. |
| Steer an active run | Active-turn guidance uses `turn/steer` | **Full** | None for steering itself. |
| Queue and manage follow-up messages | No editable/reorderable next-turn queue or follow-up-behavior setting | **Missing** | ChatGPT supports Steer versus Queue defaults and queued-message editing. |
| Parallel top-level chats | Multiple project threads can run and route notifications independently | **Near** | No dedicated global running-task manager or completion notification center. |
| Long-running/background work | Runs continue while SynthiaCode remains open; reconnect and shutdown are handled | **Partial** | No prevent-sleep setting, background inbox, OS completion notifications, or cloud continuation. |
| Local worktrees | Assistant-owned Git worktrees can be created, used per chat, listed, and safely removed | **Partial** | No branch picker, Local/Worktree handoff, managed snapshots/restore, permanent worktrees, `.worktreeinclude`, setup scripts, or configurable retention/root. |

### Models, permissions, and account

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Authenticated model catalog | Reads `model/list`, hides unavailable models, and uses server-advertised capabilities | **Full** | None material. |
| Reasoning selection | Filters reasoning options by the selected model and persists the preference | **Full** | ChatGPT may expose additional intelligence labels for eligible models. |
| Fast mode | Uses advertised service tiers and keeps Fast distinct from model choice | **Full** | None material for supported models. |
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
| Streaming coding transcript | Batched streaming, user/activity/assistant separation, raw diagnostics, bounded history, and Jump to latest | **Full** | None material for text coding tasks. |
| Rich activity rows | Commands, file changes, tools, MCP calls, web searches, plans, collaboration, guidance, and errors are projected | **Near** | Some newer item families may appear only in raw diagnostics until allowlisted. |
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
| Subagent execution | Collaboration notifications render as agent activity when Codex delegates | **Partial** | No Active/Done panel, agent-thread transcript, open/steer/stop controls, nicknames, or custom-agent management. |
| MCP tool execution | Configured MCP tool activity and progress are parsed and shown | **Partial** | No MCP list/add/remove/auth/status UI or elicitation-specific presentation. |
| Skills | Codex may load configured skills through its runtime | **Partial** | No Skills directory, enable/disable controls, install flow, `$skill` picker, or skill detail UI. |
| Plugins and app connectors | No SynthiaCode plugin/connector directory or authorization flow | **Missing** | ChatGPT supports plugins and connected services such as GitHub, Slack, Google Drive, Gmail, and calendars. |
| Web search | App-server web-search activity is rendered when the runtime uses it | **Partial** | No cached/live search control, source-focused result UI, or product-level availability setting. |
| Built-in Browser | No shared in-app browser, website permissions, comments, downloads, or browser developer mode | **Missing** | Requires a browser surface plus Browser tool/plugin integration. |
| Chrome integration | No Chrome extension or signed-in Chrome control | **Missing** | ChatGPT can operate existing Chrome sessions through its extension. |
| Computer Use | No screen/desktop control surface | **Missing** | ChatGPT can control supported desktop apps and browser UI with explicit permissions. |
| File attachments and image inputs | Composer is text-only | **Missing** | Add file picker, drag/drop/paste, image preview, protocol input parts, limits, and persistence rules. |
| Artifact/file viewer | Markdown text and safe links render; no document/spreadsheet/slide/PDF artifact viewer | **Missing** | ChatGPT can create and preview files in conversation. |
| Image generation, Sites, and visualizations | No dedicated generation or interactive artifact surfaces | **Missing** | These are broader ChatGPT capabilities rather than core local coding requirements. |
| Scheduled tasks | No create/manage/run history or recurring local project tasks | **Missing** | ChatGPT Scheduled supports local/worktree runs, chat continuity, skills, plugins, and RRULE schedules. |
| Remote/cloud connections | Local stdio app-server only; no SSH/device/cloud chat surface | **Missing** | ChatGPT supports remote connections, cloud environments, and cloud-operated work. |

### Desktop experience

| Feature | SynthiaCode | Status | Remaining difference |
| --- | --- | --- | --- |
| Native Windows application | WPF, single-process guard, responsive three-pane shell, and native file dialogs | **Full** | SynthiaCode is intentionally Windows-only. |
| Appearance | System, light, and dark themes | **Partial** | No accent/background/foreground editor, font selection, or theme sharing. |
| Keyboard shortcuts | Core project, submit, navigation, terminal, settings, and refresh shortcuts | **Partial** | No command palette, searchable/customizable shortcut editor, chat navigation, or find/search shortcuts. |
| Account and settings pane | Account, appearance, Codex runtime, doctor, diagnostics, and about information | **Near** | ChatGPT has substantially broader settings. |
| Notifications | Status bar and in-app state only | **Missing** | No OS completion notifications or notification preferences. |
| Dictation/voice input | No speech input | **Missing** | ChatGPT desktop supports dictation. |
| Quick chat, pop-out, always-on-top | No compact or detached chat window | **Missing** | ChatGPT can keep a chat beside another app. |
| Deep links | No registered SynthiaCode URL scheme | **Missing** | ChatGPT supports links to chats, settings, skills, Scheduled, plugins, and connections. |
| Personalization and memories | No personality, custom-instruction editor, suggested prompts, or cross-chat memories | **Missing** | Codex can still inherit repository/user instructions from files. |
| Chat profile, usage insights, and pets | Basic account/rate-limit view only | **Partial** | Profile analytics/cards and pets are non-core gaps. |

## What changed in this recheck

The permissions area moved from **partial** to **full/near-full functional parity**:

1. The primary control now lives under the composer, alongside model controls.
2. Ask for approval and Approve for me share the same workspace boundary and differ only by reviewer.
3. Custom follows `config.toml` or selects a discovered named permission profile.
4. Managed reviewer/profile restrictions are enforced and unavailable profiles are disabled.
5. Permission profile and legacy sandbox fields are mutually exclusive on every lifecycle request.
6. Unknown, stale, and disallowed selections fail closed.
7. Human-required server requests retain the global approval queue and exact-once response behavior.

## Recommended parity backlog

### P0 — Complete the core local coding experience

1. **Queued follow-ups:** add Steer/Queue behavior, an editable next-turn queue, and a default preference.
2. **Attachments and image input:** support file/image picker, paste/drag-drop, prompt input parts, previews, and safe persistence.
3. **Structured Git review:** add hunk staging/revert, inline diff comments, and a dedicated review target flow.
4. **Push and pull requests:** add native branch push and GitHub PR creation/status.
5. **Worktree lifecycle:** add starting-branch selection, setup scripts/actions, Local/Worktree handoff, snapshots/restore, and retention settings.

### P1 — Make parallel and long-running work first class

1. **Subagent panel:** show Active/Done agents with inspect, open, steer, and stop controls.
2. **Chat management:** add pin, delete, cross-chat search, find-in-chat, and running-task filtering.
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

Keep SynthiaCode's parity target focused on the **local coding loop**, not every ChatGPT feature. The best next release is the P0 set: queued follow-ups, attachments, structured review/PR workflows, and complete worktree lifecycle. Those close the largest everyday workflow gaps without requiring SynthiaCode to become a browser, connector marketplace, automation platform, or general artifact suite.

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
