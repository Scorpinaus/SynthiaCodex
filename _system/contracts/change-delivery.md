# Project Operating Parameters

This file defines how implementation tasks must be carried out in the SynthiaCode repository. A task-specific plan belongs in `work/active/<change-slug>/02_plan.md`; this file supplies the standing rules used while executing that plan.

## Project Context

- Product: SynthiaCode, a lightweight Windows-native WPF desktop client for `codex app-server`.
- Platform: Windows desktop.
- Runtime: .NET 10 with Windows Desktop support.
- Solution: `SynthiaCode.sln`.
- Primary areas: `src/SynthiaCode.App`, `src/SynthiaCode.Core`, `src/SynthiaCode.Infrastructure`, and `src/SynthiaCode.Tests`.
- Authoritative validation command: `dotnet test SynthiaCode.sln`.

## Operating Principles

1. Read the relevant code, documentation, and existing tests before proposing or making changes.
2. Preserve existing behavior unless the active task explicitly changes it.
3. Prefer the smallest cohesive change that completely satisfies the stated acceptance criteria.
4. Keep presentation, domain behavior, infrastructure, and tests in their established project boundaries.
5. Reuse existing controls, services, patterns, theme resources, and terminology before introducing new abstractions.
6. Treat accessibility, keyboard navigation, high-contrast behavior, localization readiness, and responsive layouts as product requirements for UI work.
7. Never expose secrets, raw sensitive configuration, or private task content in logs or diagnostics.

## Scope and Authority

- The active task and its acceptance criteria define the allowed scope.
- `README.md`, repository documentation, source code, tests, and schemas are evidence for current behavior.
- Record assumptions in the selected record's `01_intake.md` or `02_plan.md` when requirements are incomplete but a low-risk default permits progress.
- Stop and request direction when a missing decision would materially change product behavior, architecture, security, data handling, or user-visible semantics.
- Do not modify unrelated worktree changes, generated output, or portable artifacts unless the task explicitly requires it.

## Implementation Workflow

1. **Orient**: identify the affected user flow, project layers, tests, and documentation.
2. **Baseline**: confirm current behavior and note relevant existing failures before editing.
3. **Plan**: complete the selected record's `02_plan.md`, including acceptance criteria and validation.
4. **Implement**: make small, reviewable changes that follow existing naming and architectural conventions.
5. **Validate incrementally**: run the narrowest relevant tests after each logical unit.
6. **Validate comprehensively**: run `dotnet test SynthiaCode.sln` when practical before completion.
7. **Review**: inspect the final diff for accidental scope growth, dead code, debugging output, and missing documentation.
8. **Report**: summarize the outcome, validation performed, remaining risks, and any follow-up work.

## Engineering Standards

### Code

- Follow the style and nullability conventions already present in the affected project.
- Keep public APIs deliberate and minimal.
- Prefer explicit state transitions and cancellation-aware asynchronous code.
- Keep UI-thread work bounded; do not block the WPF dispatcher with I/O or long-running work.
- Preserve app-server protocol compatibility and fail closed when permissions or effective settings are stale or ambiguous.

### Tests

- Add or update behavioral tests for every observable behavior change.
- Include success, failure, cancellation, and boundary cases where applicable.
- Prefer deterministic tests that do not depend on network access, machine-specific paths, timing races, or mutable user state.
- Do not weaken or remove an assertion merely to make a failing test pass.

### Documentation

- Update user-facing documentation when commands, settings, workflows, or visible behavior change.
- Update implementation documentation when architectural responsibilities or protocol assumptions change.
- Keep examples runnable and use Windows/PowerShell conventions where the repository does so.

## Change Safety

- Inspect `git status` before editing and preserve unrelated changes.
- Read a file immediately before modifying it.
- Apply focused edits and inspect the resulting diff.
- Avoid destructive Git or filesystem operations unless they are explicitly requested and the exact targets are verified.
- Do not commit, push, publish, or create a pull request unless the task explicitly asks for it.

## Completion Criteria

A task is complete only when:

- all in-scope acceptance criteria are satisfied;
- relevant tests pass, or any pre-existing/environmental failures are clearly identified;
- the final diff contains no unintended changes;
- documentation is current for affected behavior;
- known limitations, risks, and deferred work are recorded; and
- the selected record's numbered files show the final implementation and validation status.

## Precedence

If instructions conflict, follow them in this order:

1. explicit user or task requirements;
2. repository-level agent or policy instructions;
3. the active change record's approved `02_plan.md`;
4. this file;
5. existing conventions inferred from nearby code.
