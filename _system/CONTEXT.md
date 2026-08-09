# _system — configure change delivery

One job: hold the stable contract and blank template for repository changes.

## Inputs

- Reference: `../CONTEXT.md`
- Reference: `../docs/current-architecture.md`
- Reference: `../feature_parity.md`

Do NOT load: active or closed change records unless the current task requires them.

## Process

1. Apply `contracts/change-delivery.md` to all repository changes.
2. Start each new change by copying `templates/change-record/`.
3. Keep method files free of task-specific facts.

## Outputs

- A configured change record under `../work/active/<change-slug>/`

## Human check

Confirm that a new record is a complete template copy before product work starts.
