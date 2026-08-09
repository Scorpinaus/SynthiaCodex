---
type: change-record
id: change-slug
title: Change title
status: briefed
current_stage: 01_intake
---

# Change record

One job: carry one repository change from intake through handoff.

## Inputs

- Working: `01_intake.md`
- Reference: `../../../_system/contracts/change-delivery.md`
- Reference: `../../../feature_parity.md`
- Reference: `../../../docs/current-architecture.md`

Do NOT load: unrelated records, the complete schema collection, or the complete source tree.

## Process

1. Complete and approve `01_intake.md`.
2. Complete and approve `02_plan.md` before product edits.
3. Record implementation state in `03_progress.md`.
4. Record commands and results in `04_verification.md`.
5. Complete `05_handoff.md`, then move the record from `active/` to `closed/`.

## Outputs

- `01_intake.md`
- `02_plan.md`
- `03_progress.md`
- `04_verification.md`
- `05_handoff.md`

## Human check

Read the current numbered file and approve it before the next stage starts.
