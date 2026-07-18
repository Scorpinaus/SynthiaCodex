# Phase 5F Selective Activity Projection

**Status:** Implemented - 18 July 2026

This follow-up separates the complete app-server diagnostic stream from the concise activity shown inside conversation turns.

## Projection policy

- [x] Keep raw protocol events and the diagnostic timeline complete and bounded.
- [x] Project visible activity through an explicit allowlist.
- [x] Show commands, file changes, tools, web searches, plan updates, collaboration, guidance, commentary, and actionable errors.
- [x] Suppress lifecycle bookkeeping, token usage, command/file output deltas, terminal interactions, reasoning events, final-answer events, and unknown notifications.
- [x] Use app-server item identity to update start, progress, and completion in one visible row.
- [x] Bound activity detail and summarize file paths rather than exposing full payloads or command output.

## Assistant messages

- [x] Route `commentary` messages to concise Assistant update activity.
- [x] Route `final_answer` messages to the assistant response.
- [x] Preserve response streaming for providers that omit message phase.
- [x] Reclassify an already-streamed message when the authoritative completed item supplies its phase.

## Compatibility and presentation

- [x] Persist stable item and activity keys additively in existing timeline records.
- [x] Recompute friendly Command, Files, Tool, Search, Plan, Agent, Update, Error, and Guidance labels.
- [x] Sanitize restored legacy activity and collapse duplicate command start/completion rows.
- [x] Keep full historical diagnostics available under Raw protocol events.

## Verification

- [x] Cover protocol-noise suppression and diagnostic retention.
- [x] Cover command/tool upserts and interleaved item identity.
- [x] Cover commentary/final/unknown-phase routing.
- [x] Cover file, search, plan, and collaboration projection.
- [x] Cover restored-history cleanup and JSON persistence of activity identity.
- [x] Run the complete behavioral suite (82 passing tests).
- [x] Build the complete solution in Release with zero warnings and errors.
