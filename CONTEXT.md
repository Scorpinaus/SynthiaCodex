# SynthiaCode change record library

Form: a record library with a five-stage pipeline in each change record.

The flow is: define the change, approve the plan, implement it, verify it, and close the record.

| Stage | File | Job | Human gate |
|---|---|---|---|
| 01 | `01_intake.md` | Define scope and baseline | Approve the requested outcome and limits. |
| 02 | `02_plan.md` | Define the implementation | Approve the plan before product edits. |
| 03 | `03_progress.md` | Record implementation state | Review the changed product surfaces. |
| 04 | `04_verification.md` | Record objective evidence | Confirm that the evidence is sufficient. |
| 05 | `05_handoff.md` | Record the final result | Accept the result and remaining risks. |

Factory: `_system/`, `docs/`, `assets/`, `schemas/`, and repository configuration.

Product: `src/`, active and closed change records under `work/`, and `portable/` build output.

Status is visible from `work/active/`, `work/closed/`, and the generated `work/_index/records.md` file.

## Human check

Open the selected change record and confirm its current stage before work starts.
