---
type: change-record
id: code-review-workflow
title: Dedicated code review workflow
status: closed
current_stage: closed
---

# Dedicated code review workflow

One job: preserve the completed change plan as historical implementation evidence.

## Inputs

- Working: `02_plan.md`
- Reference: `../../../feature_parity.md`
- Reference: `../../../docs/current-architecture.md`

Do NOT load: other closed records or unrelated source areas.

## Process

1. Read current product references before this historical plan.
2. Use `02_plan.md` only when implementation history is required.
3. Put new work in a new record under `work/active/`.

## Outputs

- Preserved plan: `02_plan.md`

## Human check

Compare historical claims with current product references before you reuse them.
