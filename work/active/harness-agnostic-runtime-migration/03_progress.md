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
- Phase 5 modernized the test architecture. See `evidence/phase-5-modernize-tests.html`.
- The solution now contains the Application, Codex harness, and in-memory harness projects defined by the approved plan.

## Current work

The migration record is active. The user approved the Phase 5 test modernization slice on 2026-08-09. This slice:

- converted all 307 delegate-driven behavior cases to direct xUnit facts in their feature classes;
- moved the UTF-8 echo process into `SynthiaCode.UnicodeEchoFixture` and returned the test assembly to a library;
- separated unit, protocol-contract, infrastructure-integration, and WPF tests with traits;
- enabled parallel test collections while serializing native/infrastructure and WPF collections; and
- added a manual clock and retained message probe for migrated timing and ordering tests;
- split the mixed legacy test owner into protocol, infrastructure, and WPF classes;
- added `SynthiaCode.Tests.Unit`, which targets `net10.0` without App, Infrastructure, Windows, or WPF references;
- removed 175 state-polling call sites and 16 local polling helpers; and
- added architecture rules that reject polling helpers, delayed polling loops, pure-test ownership in the Windows project, and mixed serial collections.

This explicit user scope has priority over the broader Phase 5 proof and documentation summary in `02_plan.md`.

The final Phase 5 Release build passed with zero warnings and zero errors. The focused category gates passed 39 of 39 unit tests, 43 of 43 protocol-contract tests, 84 of 84 infrastructure-integration tests, and 231 of 231 WPF tests. The final Release gate passed 397 of 397 tests on 2026-08-09. Earlier runs exposed stale metadata, a NuGet TLS restore failure, two ordering assumptions, a file lock from concurrent build commands, an incomplete source audit, empty generated boundary classes, six weak signal conversions, and one reconnect race. The implementation and verification sequence corrected each issue before the final gate.

Phase 5 is complete. The migration remains at the human gate before the next numbered stage.

## Remaining work

- Wait for human approval before the next numbered migration stage.
- Create `04_verification.md` after implementation reaches its final verification gate.
- Create `05_handoff.md` after all acceptance criteria pass.

## Decisions

The approved architecture decisions have one home in section 9 of `02_plan.md`.

- On 2026-08-09, the user approved the protocol-boundary slice as the next Phase 3 work. This explicit scope has priority over the broader Phase 3 summary in `02_plan.md`.
- On 2026-08-09, the user approved presentation decomposition as the Phase 4 work. This explicit scope has priority over the broader Phase 4 summary in `02_plan.md`.
- On 2026-08-09, the user approved test modernization as the Phase 5 work. This explicit scope has priority over the broader Phase 5 summary in `02_plan.md`.
- On 2026-08-09, the user approved the Phase 5 continuation to complete the legacy split, signal-based waits, portable unit project, and new architecture rules.
