# Phase 2 Notes

## 2026-07-13 Project And Git Experience

- Decision: use `git.exe` process calls through `ProcessStartInfo.ArgumentList` rather than adding a Git library dependency.
- Rationale: this follows the implementation plan, preserves familiar Git behavior, and handles paths without constructing shell command strings.
- Implementation: added app-neutral Git models and `IGitService` in Core, with a process-backed `GitService` in Infrastructure.
- Implementation: repository state includes the detected root, current branch or detached-head label, and porcelain-v1 `-z` status entries.
- Learning: null-delimited porcelain output reverses rename fields, so the parser records both destination and original paths without relying on quoted display output.
- Implementation: added working-tree and staged diff views, including a `git diff --no-index` preview for untracked files.
- Implementation: added stage, unstage, commit, and selected-file discard operations. Every mutation refreshes repository state.
- Safety: discard shows the exact selected path and requires explicit confirmation. Tracked changes are restored from `HEAD`; confirmed untracked files are deleted only after resolving and validating their repository-relative paths.
- Safety: Git actions are disabled and rejected when the selected project is not inside a detected repository. The app does not commit or push automatically.
- UI: the main workspace now has Task and Changes tabs. Changes includes branch/status, changed files, diff modes, stage/unstage/discard controls, commit message entry, and editor/Explorer actions.
- Integration: Git status refreshes when a project is selected, after every Git mutation, and when a Codex turn completes.
- Verification: `dotnet build SynthiaCode.sln --no-restore` passed with 0 warnings and 0 errors.
- Verification: the console assertion runner passed all 26 tests, including temporary-repository coverage for status, spaces in paths, untracked diffs, staged diffs, rename metadata, stage, unstage, commit, discard, and non-repository refusal.
- Upgrade note: the solution now targets .NET 10 directly, so tests run on the installed .NET 10 runtime without a major-version roll-forward override.
