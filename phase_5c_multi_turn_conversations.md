# Phase 5C Multi-turn Conversation Checklist

**Status:** Complete - 15 July 2026

Phase 5C adds a persistent, turn-aware conversation experience before Phase 6. It does not add skills, plugins, MCP configuration, or automations.

## Conversation state

- [x] Add an app-neutral conversation-turn model with prompt, activity, response, status, and timestamps.
- [x] Route notifications by thread and turn without mixing response buffers.
- [x] Preserve active guidance inside the current turn.
- [x] Keep legacy final-response and timeline properties as compatibility projections.

## App-server history

- [x] Add typed `thread/read` support with `includeTurns: true`.
- [x] Parse persisted user and assistant messages from app-server turns.
- [x] Reconcile canonical history with locally captured activity without duplication.
- [x] Restore history from resume responses and tolerate unavailable/empty history.

## Persistence and compatibility

- [x] Persist bounded conversation turns through additive settings properties.
- [x] Deep-copy turn state in settings snapshots and thread-store mappings.
- [x] Synthesize a visible fallback turn from legacy prompt/response state.
- [x] Preserve independent history when switching, resuming, and forking threads.

## Conversation UI

- [x] Replace the singular transcript with a virtualized list of turns.
- [x] Show user prompt, collapsible activity, assistant response, status, and timestamp per turn.
- [x] Keep the composer fixed and relabel follow-up submissions appropriately.
- [x] Preserve the same-field active guidance workflow.
- [x] Verify readable light and dark theme states.

## Verification

- [x] Add reducer tests for multiple sequential and interleaved turns.
- [x] Add app-server request/response parsing tests for `thread/read`.
- [x] Add persistence, migration, thread-switching, and follow-up tests.
- [x] Verify bounded history and responsive rendering.
- [x] Pass the full behavioral suite and Release build with zero warnings/errors.
- [x] Refresh and verify the self-contained portable build.
