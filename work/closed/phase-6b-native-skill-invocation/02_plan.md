# Phase 6B: Native Skill Selection and Explicit Invocation

**Status:** Implemented and verified  
**Date:** 2026-07-25  
**Scope:** Composer-side discovery, native selection, `$` completion, and structured skill inputs for new turns and follow-ups.

## 1. Research findings

Official Codex guidance establishes two complementary behaviors:

1. Typing `$` in the composer explicitly invokes an enabled skill, and enabled skills can also appear in a command selector.
2. App-server clients should preserve the visible `$<skill-name>` marker and add a structured input item:

   ```json
   {
     "type": "skill",
     "name": "skill-creator",
     "path": "C:\\absolute\\path\\to\\skill-creator\\SKILL.md"
   }
   ```

   The structured item is recommended because it binds the invocation to the exact absolute `SKILL.md` path and avoids relying on model-side name resolution.

Sources:

- <https://learn.chatgpt.com/docs/reference/slash-commands#use-a-slash-command>
- <https://learn.chatgpt.com/docs/skills-and-plugins#build-skills>
- <https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md#skills>
- Checked-in `schemas/codex_app_server_protocol.v2.schemas.json`, `SkillUserInput`

## 2. Existing SynthiaCode foundation

Phase 6A already provides:

- workspace-aware `skills/list` discovery through the shared app-server session;
- absolute `SKILL.md` path identity, including duplicate-name preservation;
- enabled state, display metadata, scope, dependency, and load-error parsing;
- `skills/changed` invalidation and context-change handling;
- nonfatal fallback when the installed app-server does not support skill discovery.

Phase 6B reuses this source of truth. It does not scan skill folders directly, add a second app-server client, or persist Codex-owned enablement.

## 3. Product behavior

### 3.1 Native selector

- Add a Skills button to the composer action row.
- Opening it loads enabled skills for the active General, project, or worktree path.
- Show searchable, keyboard-operable rows with display name, `$name`, description, and scope.
- Typing `$` in the prompt opens the same selector and filters by the token after `$`.
- Selecting a row replaces the active `$query` token, or appends `$name` when opened from the button.
- Preserve duplicate-name rows by absolute path and scope.
- Selected invocations appear as removable chips above the prompt.

### 3.2 Explicit invocation

- Add a typed `CodexSkillInput` user-input model.
- Serialize it as `{ "type": "skill", "name": ..., "path": ... }`.
- Validate nonblank names and absolute paths.
- Add the structured skill item only when the matching `$name` marker remains in the submitted text.
- Resolve manually typed markers when exactly one enabled discovered skill has that name.
- Require selector disambiguation when duplicate enabled skills share a name.
- Preserve bound skill inputs for queued follow-ups so later dispatch uses the selected absolute path.
- Clear transient selected bindings only after successful send/queue.

## 4. TDD phases and parity checkpoints

### Phase 6B.1 — Selector and discovery

Write failing coverage first for:

- active-workspace enabled-skill projection;
- search and `$query` filtering;
- duplicate-name rows remaining distinct by path;
- selector selection/removal and prompt marker behavior;
- rendered WPF button, popup, list virtualization, accessibility, and keyboard hooks;
- context changes clearing stale bindings and `skills/changed` causing the next selector open to refresh.

Then implement the selector and update `feature_parity.md` with the completed checkpoint.

### Phase 6B.2 — Structured invocation

Write failing coverage first for:

- `CodexSkillInput` validation and JSON serialization;
- visible marker plus structured item on `turn/start` and `turn/steer`;
- manual unique-name resolution and duplicate-name validation;
- removal of a marker preventing stale invocation;
- queued-follow-up snapshot, restore, edit, steer, and later-start preservation.

Then implement the protocol and lifecycle changes and update `feature_parity.md` with the completed checkpoint.

## 5. Compatibility and safety

- If `skills/list` is unsupported, keep ordinary text and attachment submission available and show an actionable selector message.
- Disabled skills never appear as invocation candidates.
- Never read or render the complete `SKILL.md` body in the composer.
- Never infer a path for duplicate skill names.
- Never rewrite user prompt text during protocol serialization; marker insertion happens only through an explicit selector action.
- Existing model, permission, attachment, queue, thread, and transcript behavior remains authoritative.

## 6. Verification

After each logical slice:

1. run the focused tests;
2. inspect the focused diff and run `git diff --check`;
3. update the feature-parity phase row;
4. run the full console assertion suite;
5. run `dotnet test SynthiaCode.sln`;
6. build Debug and Release and verify both executables exist.

## 7. Completion record

- The selector and structured-invocation contracts were introduced through failing tests before production implementation.
- Six focused tests cover discovery projection, token behavior, rendered WPF accessibility/virtualization, start/steer serialization, exact resolution, and queued persistence.
- The full 215-test console suite passes in Debug and Release.
- Non-incremental Debug and Release solution builds complete with zero warnings and errors.
- `feature_parity.md` records the completed outcome for every Phase 6B checkpoint.
