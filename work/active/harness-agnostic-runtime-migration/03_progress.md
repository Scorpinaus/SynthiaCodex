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
- The solution now contains the Application, Codex harness, and in-memory harness projects defined by the approved plan.

## Current work

The migration record is active. The user approved a narrower Phase 3 protocol-boundary slice on 2026-08-09. This slice split the current Codex client into:

- `JsonRpcConnection`;
- request and response codecs that are grouped by feature;
- `CodexNotificationParser`;
- `CodexServerRequestParser`; and
- a public Codex client facade.

The request called these items "Phase 2 objectives." The surrounding request said Phase 3. This record treats the list as the approved Phase 3 scope.

The Phase 3 Release baseline passed 381 of 381 tests on 2026-08-09. The final Release gate passed 385 of 385 tests. The four added tests cover the public facade and internal parts, out-of-order response matching, notification and server-request routing, and invalid JSON failure. A follow-up-turn test failed during an earlier audit but passed both Phase 3 full-suite runs. The issue did not recur during this phase.

Phase 3 is complete. The migration remains at the human gate before the next numbered stage.

## Remaining work

- Complete the Phase 4 proof, full validation, and documentation items in `02_plan.md`.
- Create `04_verification.md` after implementation reaches its final verification gate.
- Create `05_handoff.md` after all acceptance criteria pass.

## Decisions

The approved architecture decisions have one home in section 9 of `02_plan.md`.

- On 2026-08-09, the user approved the protocol-boundary slice as the next Phase 3 work. This explicit scope has priority over the broader Phase 3 summary in `02_plan.md`.
