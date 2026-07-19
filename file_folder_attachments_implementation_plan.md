# File and Folder Attachments Implementation Plan

- **Prepared:** 19 July 2026
- **Repository baseline inspected:** `fcc935e`
- **Extends:** `attachments_image_input_implementation_plan.md`
- **Scope:** Workspace file and folder references, safe acquisition, typed prompt parts, queue/turn persistence, transcript presentation, and permission-aware external-file staging
- **Status:** Workspace file/folder core implemented and verified; deferred hardening is recorded below

## 0. Implementation completion record

**Completed:** 19 July 2026

The workspace-reference release described by this plan is implemented. The implementation deliberately retains the original boundary: images may be imported from any local file into the managed image store, while non-image files and folders must be contained by the active thread/worktree workspace. External non-image files, external folders, and recursive folder snapshots remain fail-closed.

### Delivered

- Added a backward-compatible tagged attachment model with `Image`, `File`, and `Folder` kinds; `ManagedCopy` and `WorkspaceReference` sources; generic count/folder/path limits; clone/display metadata; and settings schema version 2.
- Added `WorkspaceAttachmentResolver` with full-path normalization, separator-safe containment, workspace-root rejection, wildcard and alternate-data-stream rejection, missing/type validation, reparse-target containment, slash-normalized relative persistence, and send-time revalidation.
- Added app-server `CodexMentionInput` support for both `turn/start` and `turn/steer`, preserving text-first and preview-order serialization alongside existing `localImage` parts.
- Added dedicated image, file, and folder pickers. Clipboard file lists and Explorer drag/drop now classify mixed images, workspace files, and workspace folders through one orchestration path; clipboard bitmaps retain managed PNG import.
- Added a unified attachment strip and transcript presentation with image thumbnails or file/folder metadata, plus Open/reveal, reorder, and Remove behavior.
- Applied image capability checks only when an image is present, so text-only models can accept file/folder mentions.
- Renamed persisted/runtime collections to generic `Attachments`/`UserAttachments`, retained read compatibility for legacy `Images`/`UserImages`, and deep-cloned generic references through drafts, queues, turns, forks, and settings snapshots.
- Revalidated workspace references against the exact active/captured workspace before start, steer, and queued background dispatch. A stale or escaped queued reference moves the item to `NeedsAttention` without partial send.
- Restricted managed-object restore and orphan cleanup to managed copies so live workspace references are never mistaken for store objects.

### TDD and verification result

The tests were added first and initially failed on the missing tagged model, mention input, resolver, generic persistence properties, and WPF controls. After implementation, the custom console suite passes **136/136** tests. New coverage verifies exact start/steer mention serialization, workspace file/folder containment including sibling-prefix and workspace-root attacks, generic queue/settings deep copies, text-only model behavior, and the image/file/folder WPF acquisition surface.

### Plan deltas and deferred work

| Planned item | Completion | Notes |
| --- | --- | --- |
| Workspace file and folder references | Complete | Both serialize as version-matched app-server `mention` inputs. |
| Picker, clipboard file-list, and drag/drop acquisition | Complete | Mixed batches retain input order and report partial failures. |
| Start, steer, queue, background dispatch | Complete | All use one typed builder with workspace revalidation. |
| Draft/queue/turn/fork/settings persistence | Complete | Generic v2 properties plus legacy JSON read aliases; runtime absolute paths remain ignored. |
| Installed-runtime folder mention probe and feature flag | Deferred | Wire serialization is test-covered against the version-matched schema shape; the opt-in live installed-Codex compatibility smoke was not executed in this implementation run. |
| App-server history `mention` materialization | Deferred | Locally submitted/restored conversation snapshots render; attachments found only in remote history are not yet reconstructed. |
| Workspace fingerprint fields | Adapted | Resolution is scoped by existing project/thread identity and each queue's captured absolute workspace instead of adding a separate hash field. |
| Split Attach popup, keyboard chip shortcuts, separate Reveal action | Adapted | Three compact native buttons and existing reorder/remove commands ship now; folders reveal through Open. |
| Privacy-safe attachment telemetry and rollout flags | Deferred | No attachment paths or contents were added to logs; product flags/events remain follow-up hardening. |
| External regular-file snapshots/read-root policy integration | Deferred by design | Non-image external files are rejected. |
| Recursive external-folder snapshots | Out of scope by design | External folders are rejected. |

The acceptance criteria are satisfied for the workspace core except the explicitly deferred installed-runtime folder probe, app-server-only history materialization, and enhanced keyboard/accessibility controls. Those items remain in `feature_parity.md` as follow-up hardening rather than being reported as delivered.

## 1. Outcome

Extend SynthiaCode's completed image-attachment workflow so a user can add files and folders to an idle prompt, active-turn steer, or queued follow-up. The agent must receive a real path reference it can inspect with Codex filesystem tools; SynthiaCode must not pretend that arbitrary files or directories are images, silently inline unbounded content, or broaden the selected permission policy.

The completed experience should support:

- selecting one or more workspace files;
- selecting a workspace folder;
- pasting or dragging a mixed set of images, files, and folders;
- reviewing a single ordered attachment strip with type-specific previews;
- sending text-only, attachment-only, or mixed text/attachment turns;
- retaining references in unsent drafts, queued follow-ups, sent turns, forks, and restarts;
- revalidating live workspace references immediately before start, steer, or queued dispatch; and
- failing visibly when a path is missing, has moved outside the active workspace, crosses a reparse-point boundary, or is unavailable under the effective permission policy.

Images keep their current snapshot semantics: SynthiaCode owns a managed copy and sends a `localImage` input. Workspace files and folders use live-reference semantics: they identify content under the turn's active `cwd`, and Codex reads the content when it needs it.

## 2. Protocol and research findings

### 2.1 Published behavior

The official [Codex prompting guidance](https://learn.chatgpt.com/docs/prompting#cli-workflow-good-when-you-want-a-transcript--shell-commands) documents `@` and `/mention` for inserting file paths from the workspace. This supports a workspace-reference design rather than a generic upload design.

The official [Codex app-server turn documentation](https://learn.chatgpt.com/docs/app-server#turns) publicly lists `text`, `image`, and `localImage` turn inputs and documents sandbox read access. A `workspaceWrite` policy may use full read access or restricted readable roots. SynthiaCode must therefore treat path readability as part of submission validation and must never weaken a named or managed permission profile to make an attachment work.

### 2.2 Version-matched repository schema

The checked-in generated schemas are the wire contract for the Codex version against which this repository was built:

- `schemas/v2/TurnStartParams.json`
- `schemas/v2/TurnSteerParams.json`

Both schemas include a `UserInput` variant shaped as:

```json
{
  "type": "mention",
  "name": "src/App.xaml.cs",
  "path": "D:\\Project\\SynthiaCode\\src\\SynthiaCode.App\\App.xaml.cs"
}
```

The schemas do not define separate generic `file`, `folder`, or uploaded-document variants. `mention` is therefore the proposed file-reference wire type. The schema accepts a path string but does not state that directories are supported. Directory mentions require a live compatibility test before the feature flag is enabled; the fallback is a text path reference scoped to the active workspace.

### 2.3 Contract decision

Use the following mapping:

| UI attachment | Storage semantics | Wire input |
| --- | --- | --- |
| Image chosen/pasted/dropped | Managed immutable copy | `localImage` |
| File inside active workspace | Live workspace-relative reference | `mention` |
| Folder inside active workspace | Live workspace-relative reference | `mention` after compatibility proof; otherwise normalized text reference |
| External regular file | Deferred managed snapshot, permission-gated | `mention` to managed copy only when readable-root support is proven |
| External folder | Not accepted in the first release | None |

Do not use `localImage` for non-images. Do not serialize an invented `file` or `folder` input. Do not use the Responses API `input_file` shape in app-server requests; it is a different API contract.

## 3. Product scope and decisions

### 3.1 First release: workspace references

The reliable first release accepts regular files and directories that resolve inside the active turn workspace:

- current-checkout chat: selected project root;
- worktree chat: that chat's worktree root;
- queued follow-up: the captured `QueuedTurnOptionsSnapshot.WorkspacePath`;
- resumed chat: the restored thread workspace after it has been validated.

Workspace file/folder attachments are live references, not byte snapshots. If a referenced file changes after it is attached but before the turn begins, Codex sees the current content. The UI must state this in the tooltip: `Workspace reference — current contents are used when sent.`

This behavior matches normal `@file` use and avoids copying repository content, dirtying Git status, duplicating source trees, or persisting source bytes.

### 3.2 External files

External regular files are a second, separately gated increment. SynthiaCode may copy them into the content-addressed managed store, but only when it can prove that the effective sandbox can read the attachment-store root without broadening a named or managed permission profile.

Implementation gate:

1. Extend permission/config discovery to expose effective read-access mode and readable roots when app-server provides them.
2. If the active policy already permits the managed store, import and attach the file snapshot.
3. If SynthiaCode owns an explicit legacy `workspaceWrite` policy, it may add the single attachment-store root as read-only access after showing the scope in the permission summary.
4. If a named/custom/managed profile hides or denies its read roots, fail closed with `This permission profile cannot read managed external attachments. Move the file into the workspace or choose an allowed profile.`
5. Never replace the user's selected profile, switch to full access, or copy an external file into the repository automatically.

External files must not block the workspace-reference release. Ship them behind `EnableExternalFileAttachments` only after the permission tests and live smoke tests pass.

### 3.3 External folders

Do not recursively copy external folders in this implementation. Recursive snapshotting creates ambiguous scope and substantial risk:

- junction, symlink, and reparse-point escapes;
- `.git`, dependency caches, build output, secrets, and very large trees;
- inconsistent snapshots while files are changing;
- unbounded hashing, disk use, and cleanup work; and
- unclear ignore-file semantics.

A later explicit `Snapshot folder` feature would need its own review screen, include/exclude rules, file/count/byte limits, cancellation, and archive format. It is not a small extension of path mentions.

### 3.4 Supported file types

Allow any regular workspace file. A path mention gives Codex filesystem context; SynthiaCode does not need to claim that the model can directly decode every binary format. Show media/type metadata when confidently known, otherwise `File`.

Do not inspect or log file content during attachment. Do not block common repository-sensitive names such as `.env` or credentials files: the user already selected a workspace and may legitimately need help with those files. The permission boundary and an explicit user gesture are the control. Never auto-attach sensitive-looking files during folder selection.

Special filesystem entries are rejected:

- devices, named pipes, sockets, alternate data streams, and non-file/non-directory entries;
- wildcard paths;
- broken symbolic links or junctions;
- paths whose resolved target leaves the active workspace; and
- the workspace root itself when selected as a folder, because that adds no context beyond the existing project.

## 4. Unified attachment domain

The current `AttachmentReference` assumes every attachment is a managed image. Refactor it into a backward-compatible tagged record instead of creating parallel image/file collections.

### 4.1 Target model

```csharp
public enum AttachmentKind
{
    Image = 0,
    File = 1,
    Folder = 2
}

public enum AttachmentSourceKind
{
    ManagedCopy = 0,
    WorkspaceReference = 1
}

public sealed class AttachmentReference
{
    public string Id { get; set; }
    public AttachmentKind Kind { get; set; }
    public AttachmentSourceKind SourceKind { get; set; }
    public string DisplayName { get; set; }
    public string MediaType { get; set; }

    // ManagedCopy only
    public string? StorageKey { get; set; }
    public long ByteLength { get; set; }
    public string? ContentSha256 { get; set; }

    // WorkspaceReference only; slash-normalized and never rooted
    public string? WorkspaceRelativePath { get; set; }

    // Image only
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    [JsonIgnore]
    public string? ResolvedPath { get; set; }
}
```

Required invariants:

- `Image` is `ManagedCopy`, has a valid `StorageKey`, and resolves to a regular file.
- Managed external `File` is `ManagedCopy`, has a valid `StorageKey`, and resolves to a regular file.
- Workspace `File`/`Folder` is `WorkspaceReference`, has no `StorageKey`, and stores only `WorkspaceRelativePath`.
- `WorkspaceRelativePath` is not rooted, contains no empty/`.`/`..` segment, and is resolved against the specific thread/queue workspace every time it is used.
- Display names are leaves only, control-character-free, and capped.
- Runtime absolute paths remain `[JsonIgnore]`; `settings.json` never receives an original external source path.

### 4.2 Limits

Keep limits centralized in `AttachmentLimits`:

| Limit | Proposed value | Applies to |
| --- | ---: | --- |
| Total attachments per input | 20 | All kinds combined |
| Images per input | 10 | Existing limit retained |
| Workspace folders per input | 5 | Prevent overly broad context |
| Workspace-relative path UTF-8 bytes | 4 KiB | Each file/folder reference |
| Managed image bytes | 20 MiB | Existing behavior |
| Managed external file bytes | 25 MiB | Permission-gated second increment |
| Managed bytes per input | 100 MiB | Images plus external file snapshots |
| Managed-store ceiling | 1 GiB initially | All managed objects |

Workspace references do not count their file size toward managed-byte limits because no bytes are copied. Do not recursively calculate folder size or file count during normal selection.

### 4.3 Duplicate identity

- Managed copy: duplicate by `ContentSha256` and kind.
- Workspace reference: duplicate by normalized relative path and kind, using case-insensitive comparison on Windows.
- A file and folder cannot share the same resolved path in a valid filesystem.
- Reordering changes only presentation/wire order, never attachment identity.

## 5. Path resolution and security boundary

Add a Core contract and Infrastructure implementation dedicated to workspace paths:

```csharp
public interface IWorkspaceAttachmentResolver
{
    WorkspaceAttachmentResolution Resolve(
        string workspaceRoot,
        string candidatePath,
        AttachmentKind expectedKind);

    WorkspaceAttachmentResolution Revalidate(
        string workspaceRoot,
        AttachmentReference attachment);
}
```

Resolution algorithm:

1. Validate non-empty input and reject wildcards and alternate data stream syntax.
2. Normalize `workspaceRoot` and candidate with `Path.GetFullPath`.
3. Require an existing regular file or directory according to `expectedKind`.
4. Walk every path component from the root to the target and inspect `FileAttributes.ReparsePoint`.
5. Resolve symlinks/junctions with `ResolveLinkTarget(returnFinalTarget: true)`.
6. Require both the lexical path and fully resolved target to remain under the resolved workspace root.
7. Compute `Path.GetRelativePath`, normalize separators to `/`, and reject rooted or traversal results.
8. Return sanitized metadata without reading contents.

Do not use string-prefix containment without a separator boundary. Use `OrdinalIgnoreCase` on Windows and retain a platform abstraction for tests. Reject inaccessible paths rather than interpreting access-denied as missing.

At request time, always resolve from the queue/thread's captured workspace—not the currently selected project. This prevents a background queued item from being redirected by UI selection or worktree changes.

## 6. Protocol changes

### 6.1 Typed input

Add:

```csharp
public sealed record CodexMentionInput(string Name, string Path) : CodexUserInput;
```

Extend `ValidateUserInputs` and `WriteUserInputs` to accept it and serialize exactly:

```json
{ "type": "mention", "name": "src/App.xaml.cs", "path": "D:\\...\\src\\App.xaml.cs" }
```

Validation:

- `Name` is non-empty, display-safe, and bounded.
- `Path` is absolute at the protocol boundary.
- Request construction, not the serializer, proves existence and containment.
- An empty request remains invalid.
- Unknown union cases still fail closed.

### 6.2 Input ordering

Retain the current composer ordering contract:

1. one normalized text part when non-empty;
2. all attachments in preview order;
3. images become `localImage`;
4. workspace files become `mention`;
5. workspace folders become `mention` only when the compatibility gate passes; otherwise append a deterministic text suffix such as `Workspace folder reference: @src/Feature` and do not claim native folder-mention support.

Do not inject file contents into the text part. Do not synthesize model-visible prose for a file-only request except the documented folder fallback.

### 6.3 Capability gate

The current `model/list.inputModalities` gate applies only to image attachments. File and folder mentions are text/tool context and must remain available on a text-only model.

Replace `CanSubmitAttachments` with validation results that can express multiple independent failures:

- selected model cannot accept one or more images;
- workspace reference is missing or escaped;
- external managed copy is unavailable under the effective policy;
- count or managed-byte limit exceeded; or
- native folder mention is unavailable and fallback is disabled.

### 6.4 Runtime compatibility

Add a disabled-by-default live smoke test controlled by `SYNTHIACODE_RUN_LIVE_MENTION_SMOKE=1`:

1. start a temporary thread in a temporary workspace;
2. create a uniquely named file and folder;
3. send a file-only `mention` and require the agent to report the known marker;
4. send a folder reference containing two known filenames and require both names;
5. test `turn/steer` with a file mention;
6. delete the temporary thread/workspace using existing safe ownership rules.

The generated schema test is mandatory even when the live smoke test is skipped. If the installed Codex rejects `mention`, keep the UI feature disabled and show a version/capability message rather than silently degrading file semantics.

## 7. Acquisition and composer UX

### 7.1 Attach menu

Replace `Attach images` with an `Attach` split button or compact popup:

- `Images…` — existing multi-select filtered picker;
- `Files…` — multi-select `OpenFileDialog`, initial directory is active workspace;
- `Folder…` — `OpenFolderDialog`, initially single-select;
- concise help text: `Files and folders must be inside this chat's workspace.`

Cancel makes no state change. Do not persist the last picker folder.

### 7.2 Drag/drop

Keep routed handlers registered with `handledEventsToo: true`, but replace the current unconditional FileDrop `Copy` effect with cheap preflight classification:

- images may come from any local file path and follow managed image import;
- regular files/folders must resolve inside the active workspace for the first release;
- mixed batches are accepted item-by-item in their original shell order;
- drag effect is `Copy` only when at least one candidate is eligible;
- rejected items produce one privacy-safe summary after drop;
- directories are never recursively enumerated during `DragOver`.

Add a visible and accessible drop-target state: `Drop images, workspace files, or workspace folders`.

### 7.3 Paste

When the focused composer receives Ctrl+V:

1. If the clipboard has a file-drop list, classify each entry as image/file/folder and add eligible items.
2. Otherwise, if it has bitmap pixels, preserve the existing PNG import.
3. Otherwise, allow normal text paste untouched.

Catch clipboard-busy, COM, encoding, path, and access exceptions. Consume Ctrl+V only after an attachment representation was successfully read; failed attachment paste must not destroy a text fallback.

### 7.4 Unified preview strip

Rename image-specific bindings and automation labels to generic attachments. Use type-specific templates:

- image: bounded thumbnail, filename, dimensions/size;
- file: document/code icon, filename, workspace-relative parent, optional size/type;
- folder: folder icon, relative path, `Live workspace reference` label;
- missing: warning icon, retained display name, actionable unavailability text.

All kinds support Open, Reveal in Explorer, reorder, and Remove. Image Open uses the managed copy; workspace file/folder Open/Reveal resolves against the attachment's owning workspace at command time.

Keyboard/accessibility requirements:

- every chip is reachable without entering every decorative element;
- Delete removes the focused chip after confirmation only when required by existing UX conventions;
- Alt+Left/Alt+Right reorder, with an announced position change;
- automation names include kind and display name, never a full external path;
- validation uses a polite live region;
- high-contrast themes do not rely on icon color alone.

## 8. Draft, queue, turn, and settings persistence

### 8.1 Rename image-only collections

Refactor these concepts to generic `Attachments`:

| Current | Target |
| --- | --- |
| `ComposerAttachmentDraftSnapshot.Images` | `Attachments` |
| `QueuedFollowUpSnapshot.Images` | `Attachments` |
| `QueuedFollowUp.Images` | `Attachments` |
| `CodexConversationTurn.UserImages` | `UserAttachments` |
| `CodexConversationTurnSnapshot.UserImages` | `UserAttachments` |
| `BuildUserInputs(... images ...)` | `BuildUserInputs(... attachments ...)` |

### 8.2 Backward-compatible JSON migration

Existing settings contain `Images` and `UserImages`. Do not break them.

Add `AttachmentSchemaVersion = 2` and a load-time normalizer:

1. Deserialize legacy image properties into nullable legacy fields.
2. If `Attachments`/`UserAttachments` is absent, clone legacy items into the new list.
3. For legacy records, set `Kind = Image` and `SourceKind = ManagedCopy` when enum fields are absent.
4. Validate managed storage keys and mark unavailable entries; never discard a corrupt reference silently.
5. Save only the new property names after the next normal settings save.
6. Keep the migration idempotent and covered with pre-feature JSON fixtures.

Avoid a polymorphic JSON hierarchy for this migration; a tagged DTO gives stable defaults and simpler legacy loading.

### 8.3 Draft scoping

Retain current per-project/thread attachment draft scoping, but bind workspace references to the exact active workspace identity. A worktree thread and current-checkout thread under the same project must not resolve the same relative path against different roots accidentally.

Add to the draft snapshot:

- `WorkspacePathFingerprint`: hash of normalized workspace path for comparison without displaying it;
- `WorkspaceMode`: local/worktree;
- attachments in UI order.

On selection:

- if the workspace fingerprint matches, rehydrate references;
- if a thread worktree moved or was removed, retain chips as unavailable and require explicit removal/reselection;
- never redirect a stale reference to the selected project root.

### 8.4 Queued follow-ups

Queue insertion deep-clones the generic list. Immediately before background dispatch:

1. reload/re-resolve the captured workspace;
2. revalidate every workspace attachment and managed copy;
3. re-resolve model image capability;
4. re-resolve the captured permission policy according to queued-follow-up hardening rules;
5. build inputs only after all checks pass.

Missing/moved/escaped references move the item to `NeedsAttention`, preserve its attachments, and show the first actionable error. Never silently omit one item from a queued batch.

### 8.5 Conversation history

Sent turns store attachment metadata in preview order. Transcript restoration resolves workspace references against the turn's recorded workspace identity and managed items through `IAttachmentStore`.

Extend app-server history parsing:

- `localImage` -> managed/local image reconciliation as already planned;
- `mention` -> file/folder reference when the path can be safely relativized to the recorded thread workspace;
- absolute mention outside the known workspace -> unavailable external placeholder, never a persisted arbitrary source path;
- unknown input types -> ignore for presentation but retain raw protocol diagnostics.

Folder/file transcript chips are historical references, not guarantees that old content is unchanged. Label missing historical workspace references without crashing the transcript.

## 9. Managed store evolution

Split image validation from generic object storage:

```text
IAttachmentObjectStore
  ImportImageAsync(...)       -> validates image signature/dimensions
  ImportExternalFileAsync(...) -> regular-file snapshot, permission-gated
  ResolveManagedPath(...)
  CleanupAsync(references)

IWorkspaceAttachmentResolver
  Resolve(...)
  Revalidate(...)
```

For external regular files:

- stream and hash; never call `ReadAllBytesAsync`;
- cap size before and during copy;
- preserve a sanitized extension only for usability;
- object key remains content-addressed;
- reject source changes detected between initial stat and completed copy, or retry once with a clear status;
- use staging + atomic move and existing deduplication;
- never execute, parse, preview, or trust file contents during import;
- mark the Windows managed-store directory user-private where practical;
- scan cleanup references from drafts, queues, and turns across all kinds.

Workspace references have no managed object and do not participate in object cleanup.

## 10. Orchestration changes

Create one acquisition pipeline in `MainViewModel`:

```csharp
Task<AttachmentBatchResult> AddAttachmentPathsAsync(
    IEnumerable<string> paths,
    AttachmentAcquisitionSource source,
    CancellationToken cancellationToken = default);
```

Per candidate:

1. determine file/directory without following an unsafe link;
2. detect supported image signature/extension candidate;
3. image -> current managed image import;
4. workspace file/folder -> resolver and live reference;
5. external regular file -> permission-gated snapshot when feature enabled;
6. otherwise reject with an actionable reason;
7. add only after domain limits and duplicate checks pass.

`AttachmentBatchResult` contains counts and reason codes, not full paths, for status and telemetry. Valid items remain ordered even when invalid items are interleaved.

Replace image-specific request paths in:

- `SubmitPromptAsync`;
- `SendSteerAsync`;
- `QueueActiveFollowUpAsync`;
- `SendQueuedFollowUpNowAsync`;
- `StartQueuedFollowUpAsync`;
- thread begin/bind/history reconciliation;
- draft capture/restore; and
- startup rehydration/cleanup.

Submission clears the exact attachment draft only after app-server acknowledgement or durable queue insertion. Any validation, policy, transport, or persistence failure retains text and every chip.

## 11. TDD implementation sequence

Follow the repository's console-runner pattern and add tests before each production slice.

### Phase 0 — Contract spike

1. Add schema assertions that both checked-in turn schemas contain `mention` with required `name`, `path`, and `type`.
2. Add `CodexMentionInput` serialization tests for start and steer; run them red before implementation.
3. Add the opt-in live file/folder mention smoke test.

Exit criterion: generated-schema tests pass and at least file mention is proven against a supported installed Codex. Folder mention remains feature-gated until its live probe passes.

### Phase 1 — Unified domain and legacy migration

1. Add attachment kind/source enums, invariants, clone behavior, and display metadata tests.
2. Add JSON fixtures for legacy `Images`/`UserImages` settings.
3. Add v2 snapshot, queue, thread, fork, and settings deep-copy tests.
4. Implement migration and rename collections.

Exit criterion: old settings load without loss, new settings write only v2 fields, and image behavior remains unchanged.

### Phase 2 — Workspace path resolver

1. Test normal files/folders, root equality, sibling-prefix attacks, `..`, mixed separators, missing paths, access errors, and case differences.
2. Test symlink/junction/reparse escapes with a filesystem abstraction where host privileges make real links unreliable.
3. Test worktree-relative resolution and captured-workspace revalidation.
4. Implement resolver and metadata factory.

Exit criterion: no accepted reference can resolve outside its owning workspace, including through a reparse point.

### Phase 3 — Composer acquisition and presentation

1. Test batch classifier ordering and mixed partial success.
2. Test combined/image/folder limits and duplicate identity.
3. Test file/folder picker elements, paste precedence, routed drop registration, generic automation labels, and responsive layout.
4. Implement Attach menu, file/folder pickers, unified paste/drop pipeline, chips, Open/Reveal/reorder/remove, and accessible error state.

Exit criterion: picker, paste, and drop converge on one pipeline; normal text paste and existing image paths do not regress.

### Phase 4 — Start, steer, and queue

1. Test exact mixed ordering: text, image, file, folder.
2. Test file-only/folder-only start and steer.
3. Test that text-only models allow mentions but reject images.
4. Test missing queued references become `NeedsAttention` without partial send.
5. Implement generic `BuildUserInputs` and dispatch revalidation.

Exit criterion: every send path emits identical semantics and clears only after acknowledgement/durable queue insertion.

### Phase 5 — Persistence and history

1. Test restart hydration for workspace files/folders and managed images.
2. Test moved worktrees and deleted/moved paths produce unavailable placeholders.
3. Test app-server `mention` history reconciliation and outside-workspace redaction.
4. Update cleanup reference scanning and migration finalization.

Exit criterion: drafts, queues, turns, forks, and restarts preserve safe references without persisting arbitrary absolute source paths.

### Phase 6 — External regular files

1. Add effective read-root parsing and permission-policy tests.
2. Add managed generic-file streaming, source-race, deduplication, limit, and cleanup tests.
3. Test named/managed profiles fail closed when readable scope is unknown.
4. Enable the external file picker path behind its feature flag only after live sandbox read smoke coverage passes.

Exit criterion: external files never broaden permissions implicitly and remain usable after original-source deletion when the selected policy permits them.

## 12. Test matrix

### Domain and path safety

- Legacy image reference defaults to managed `Image`.
- Workspace file/folder round-trips with only a relative path.
- Rooted, traversal, wildcard, ADS, empty, broken-link, and reparse-escape paths fail.
- `D:\repo2` is not contained by `D:\repo`.
- Duplicate path casing collapses on Windows.
- Worktree references resolve in the worktree, not the main checkout.

### Acquisition

- File picker accepts multiple workspace files in picker order.
- Folder picker accepts one non-root workspace folder.
- Mixed image/file/folder drop preserves shell order.
- External folder, device, and unsupported external file paths produce explicit batch reasons.
- Clipboard bitmap still becomes a PNG; text clipboard still pastes text.
- One invalid item does not discard valid items or the existing draft.

### Protocol and model behavior

- Start and steer serialize `mention` exactly.
- Image-only, file-only, folder-only, and mixed requests work.
- Text is first and attachments retain preview order.
- Text-only model accepts file/folder mentions and rejects image attachments.
- Unsupported runtime mention capability disables file/folder send rather than converting to an invented type.

### Queue and lifecycle

- Queue deep-clones attachments.
- Changed workspace file remains a live reference.
- Deleted/escaped queued reference pauses as `NeedsAttention`.
- Background dispatch uses captured workspace while another thread is selected.
- Successful acknowledgement clears; failed validation/transport preserves.
- Fork and resume never redirect stale references to another workspace.

### Persistence and privacy

- Legacy settings migrate idempotently.
- Settings contain no runtime `ResolvedPath` or original external path.
- Managed source deletion does not break images/external snapshots.
- Missing workspace references render placeholders after restart.
- Cleanup deletes only unreferenced managed objects.
- Logs and telemetry contain kind/reason/count but not content or full paths.

### WPF and accessibility

- Generic Attach menu and three acquisition actions exist.
- Type-specific chips render and remain within responsive composer bounds.
- Open/Reveal/reorder/remove work for each kind.
- Keyboard reordering announces updated position.
- Drop target and validation live region expose useful automation names.
- High-contrast rendering differentiates file/folder/missing without color alone.

## 13. File-level change map

| Area | Expected files | Change |
| --- | --- | --- |
| Attachment domain | `src/SynthiaCode.Core/Attachments/*` | Tagged kinds/sources, invariants, limits, batch results, workspace resolver contract |
| Protocol models | `CodexAppServerModels.cs` | Add `CodexMentionInput` |
| Protocol client | `CodexAppServerClient.cs` | Validate/serialize mention; parse mention history |
| Conversation | `CodexConversationTurn.cs`, `CodexThreadService.cs` | Generic user attachments and reconciliation |
| Queue | `CodexFollowUpQueue.cs` | Generic attachment collections, limits, NeedsAttention validation |
| Settings | `AppSettings.cs`, `AppSettingsSnapshot.cs`, `ThreadStore.cs` | v2 fields, legacy migration, deep copies, workspace identity |
| Path safety | new `Infrastructure/Attachments/WorkspaceAttachmentResolver.cs` | Canonicalization, reparse containment, metadata |
| Managed store | `LocalAttachmentStore.cs` or split store files | Separate image validation from permission-gated generic file copy |
| App services | `AppServices.cs` | Register resolver and revised store contracts |
| Task state | `TaskViewModel.cs` | Generic collection, validation result, commands, labels |
| Orchestration | `MainViewModel.cs` | Unified acquisition, request building, workspace/policy revalidation |
| WPF | `TaskView.xaml`, `TaskView.xaml.cs` | Attach menu, pickers, mixed drop/paste, chip templates, accessibility |
| Tests | `AttachmentInputTests.cs` plus migration/permission/layout suites | TDD coverage described above |
| Backlog/docs | `feature_parity.md`, this plan, architecture docs | Mark delivered scope and explicit external-folder boundary |

## 14. Observability and rollout

Add privacy-safe events:

- `attachment_batch_completed`: source, counts by kind, accepted/rejected totals;
- `attachment_reference_invalid`: kind and reason code;
- `attachment_mention_unsupported`: installed Codex version/capability only;
- `attachment_queue_needs_attention`: kind and reason code;
- `external_attachment_permission_blocked`: permission mode/profile ID only when already non-sensitive.

Never log attachment content, clipboard content, filenames, relative paths, or absolute paths.

Rollout flags:

1. `EnableWorkspaceFileAttachments`
2. `EnableWorkspaceFolderAttachments` after folder mention proof
3. `EnableExternalFileAttachments` after permission/read-root proof

Keep image attachments enabled independently so an issue in mentions can be rolled back without removing the completed image workflow.

## 15. Acceptance criteria

The workspace file/folder extension is complete when:

1. Files can be added through picker, clipboard file list, and drag/drop.
2. Folders can be added through folder picker, clipboard file list, and drag/drop.
3. Only paths safely contained in the owning active workspace are accepted in the first release.
4. Symlink, junction, reparse-point, traversal, sibling-prefix, and stale-workspace escapes fail closed.
5. Start, steer, queue, retry, and background dispatch use the same ordered typed input builder.
6. File mentions serialize as the version-matched schema specifies; folder behavior is enabled only after compatibility proof.
7. Text-only models accept file/folder references while image capability checks remain enforced.
8. Drafts, queues, turns, forks, and restarts preserve generic attachments without persisting runtime absolute paths.
9. Queued missing/moved references become `NeedsAttention` and are never partially or silently omitted.
10. Images retain all current managed-copy, preview, validation, and persistence behavior.
11. The composer and transcript show accessible type-specific chips with Open, Reveal, reorder, and Remove.
12. Existing settings migrate without image loss and new settings use the v2 generic fields.
13. The complete custom test suite, new schema/path/migration/WPF tests, Debug build, and Release build pass with zero warnings.

External regular files are complete only when effective read access is provable and no named or managed permission profile is broadened. Recursive external-folder snapshotting is explicitly not part of these acceptance criteria.

## 16. Principal risks and mitigations

| Risk | Mitigation |
| --- | --- |
| `mention` exists in generated schema but is absent from public turn examples | Schema test, installed-runtime live smoke, feature gate, fail closed |
| Directory mention semantics differ across Codex versions | Separate folder flag and deterministic workspace-text fallback |
| Relative path resolves against the wrong worktree | Persist/capture workspace identity and resolve only against owning queue/thread cwd |
| Junction or symlink escapes workspace | Resolve every reparse component and verify final target containment |
| Queue sends stale/deleted paths | Immediate pre-dispatch revalidation and `NeedsAttention` state |
| Generic refactor loses legacy images | Tagged default values, explicit legacy properties, idempotent fixture-tested migration |
| External managed file is unreadable under profile | Effective read-root gate; never override named/managed profile |
| Folder selection expands huge or sensitive trees | Pass one live folder reference; never recursively enumerate or copy |
| Logs expose private filenames | Structured reason codes and counts only |
| File chips make composer too tall | Compact horizontal chips, bounded strip, responsive layout tests |

## 17. Recommended delivery order

Deliver this as reviewable vertical slices:

1. Protocol `mention` model, schema tests, and live capability probe.
2. Tagged generic domain plus legacy migration.
3. Workspace resolver and path-security tests.
4. File picker and mixed acquisition with generic chips.
5. Folder picker behind compatibility flag.
6. Start/steer/queue/background integration.
7. Restart/history/worktree persistence and unavailable states.
8. Permission-aware external regular-file snapshots behind a separate flag.

This order proves the uncertain wire contract and the irreversible path-security boundary before expanding the UI. It also preserves the existing image workflow after every slice and keeps external-folder recursion out of a feature that should remain predictable and safe.
