# SynthiaCode

SynthiaCode is a Windows desktop client for Codex. The repository produces application source, tests, documentation, protocol schemas, and a portable build.

This workspace uses ICM. Folders carry context, files carry state, and people approve each stage boundary.

## Rules

- Always use ASD-STE100 Simplified Technical English.
- Preserve the application layout under `src/`.
- Read the current change record before you edit product files.
- Do not load all closed records or all schemas without a task need.
- Do not edit generated indexes or schema JSON by hand.

## Route by task

| Task | Read first |
|---|---|
| Understand the change workflow | `CONTEXT.md` |
| Continue current work | `work/CONTEXT.md`, then the record in `work/active/` |
| Start a change | `_system/templates/change-record/CONTEXT.md` |
| Find completed work | `work/_index/records.md` |
| Apply repository delivery rules | `_system/contracts/change-delivery.md` |
| Understand the product | `README.md` and `docs/current-architecture.md` |
| Select a parity gap | `feature_parity.md` |
| Work with the Codex protocol | `schemas/README.md` |
| Build, test, or release | `README.md` and the applicable file in `docs/` |

## Human gate

Do not continue to the next numbered stage until a person approves the current output.
