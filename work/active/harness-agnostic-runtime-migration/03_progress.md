---
type: change-stage
stage: 03_progress
status: active
last_updated: 2026-08-09
---

# Progress

## Completed work

- Phase 0 restored the test baseline. See `evidence/phase-0-restore-confidence.html`.
- Phase 1 moved one complete feature slice. See `evidence/phase-1-move-feature-slice.html`.
- Phase 2 separated durable state. See `evidence/phase-2-separate-durable-state.html`.
- Phase 3 hardened the Codex protocol boundary. See `evidence/phase-3-harden-codex-protocol-boundary.html`.
- Phase 4 decomposed presentation by feature. See `evidence/phase-4-decompose-presentation-by-feature.html`.
- The solution now contains the Application, Codex harness, and in-memory harness projects defined by the approved plan.

## Current work

The migration record is active. The user approved a narrower Phase 4 presentation slice on 2026-08-09. This slice:

- split `TaskView` and its code-behind into conversation, composer, agents, goals, and options controls;
- split task presentation into matching feature models while retaining the root compatibility facade;
- moved the Markdown document model and parser to the WPF-free `SynthiaCode.Presentation` project;
- kept external-link and local-image policy separate from the WPF renderer; and
- divided Git presentation into repository selection, changes and diff, actions, and push-planning components.

This explicit user scope has priority over the broader Phase 4 proof and documentation summary in `02_plan.md`.

The Phase 4 App Release build passed with zero warnings and zero errors. The 307-case behavior set passed after its namescope baseline was updated for the new feature controls. The five added Phase 4 tests cover the pure Presentation project, a Markdown table edge case, task control and model composition, parser-policy-renderer separation, and Git component composition. The final Release gate passed 390 of 390 tests on 2026-08-09. One persistence restore test timed out in the first broad run but passed all later behavior and full-suite runs.

Phase 4 is complete. The migration remains at the human gate before the next numbered stage.

## Remaining work

- Wait for human approval before the next numbered migration stage.
- Create `04_verification.md` after implementation reaches its final verification gate.
- Create `05_handoff.md` after all acceptance criteria pass.

## Decisions

The approved architecture decisions have one home in section 9 of `02_plan.md`.

- On 2026-08-09, the user approved the protocol-boundary slice as the next Phase 3 work. This explicit scope has priority over the broader Phase 3 summary in `02_plan.md`.
- On 2026-08-09, the user approved presentation decomposition as the Phase 4 work. This explicit scope has priority over the broader Phase 4 summary in `02_plan.md`.
