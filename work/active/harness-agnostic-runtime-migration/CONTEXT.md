---
type: change-record
id: harness-agnostic-runtime-migration
title: Harness-agnostic runtime migration
status: active
current_stage: 03_progress
---

# Harness-agnostic runtime migration

One job: separate conversation behavior from the Codex transport without changing current user behavior.

## Inputs

- Working: `01_intake.md`
- Working: `02_plan.md`
- Working: `03_progress.md`
- Reference: `../../../_system/contracts/change-delivery.md`
- Reference: `../../../docs/current-architecture.md`
- Evidence: `evidence/phase-0-restore-confidence.html`
- Evidence: `evidence/phase-1-move-feature-slice.html`
- Evidence: `evidence/phase-2-separate-durable-state.html`
- Evidence: `evidence/phase-3-harden-codex-protocol-boundary.html`
- Evidence: `evidence/phase-4-decompose-presentation-by-feature.html`

Do NOT load: closed change records, all schemas, or unrelated product areas.

## Process

1. Read the approved plan and current progress.
2. Implement only the next incomplete migration slice.
3. Keep all current application behavior and source project paths stable.
4. Record verification in `04_verification.md` when the planned implementation is complete.
5. Record the accepted result in `05_handoff.md`, then move this record to `work/closed/`.

## Outputs

- Current state in `03_progress.md`
- Final evidence in `04_verification.md`
- Final result in `05_handoff.md`

## Human check

Confirm the next migration slice in `03_progress.md` before source edits continue.
