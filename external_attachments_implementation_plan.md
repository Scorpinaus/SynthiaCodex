# External Attachments Implementation Plan

- **Prepared:** 19 July 2026
- **Repository baseline:** commit `319b67d` (`Add file folder attachments`)
- **Extends:** `file_folder_attachments_implementation_plan.md`
- **Scope:** Images, regular files, directories, and mixed attachment batches selected outside the active project/worktree
- **Status:** Core managed-snapshot implementation complete; advanced live roots and review/permission hardening remain deferred

## Implementation completion record — 19 July 2026

The implemented release delivers the default managed-snapshot path for external images, regular files, folders, and mixed batches without persisting the original external path or broadening the active permission policy.

### Completed

- File picker, folder picker, clipboard file-list paste, and Explorer drag/drop all use the same path-classification flow.
- Images continue through the validated managed-image store, whether selected inside or outside the workspace.
- Workspace files and folders remain live, workspace-relative references and are revalidated before send.
- External regular files are streamed into immutable content-addressed managed objects, deduplicate by content, survive source deletion, and reject empty, reparse-point, alternate-data-stream, missing, changing, and oversized inputs.
- External folders are copied into immutable content-addressed directory snapshots with deterministic file/directory manifests. Empty directories are preserved; reparse entries are rejected; and traversal is capped at 32 levels, 1,000 entries, and 100 MiB.
- Per-file source length/write-time checks detect mutation during folder copy. Failed imports clean their staging file or directory.
- Managed images serialize as `localImage`; managed files and folders serialize as absolute `mention` inputs. The same builder is used by start, steer, queued dispatch, and retries.
- Drafts, queues, conversation turns, forks, and settings snapshots deep-copy `SnapshotFileCount` and `SnapshotByteLength`; the attachment schema is now version 3 and legacy image aliases remain readable.
- Queue validation accepts kind-correct managed images/files/folders and enforces a 150 MiB managed-input aggregate limit. Composer deduplication includes attachment kind.
- Reference-aware cleanup keeps every file and empty directory beneath a referenced folder object and removes expired unreferenced managed files without touching original sources.
- Picker and composer help text now explains that outside files/folders are saved as managed snapshots.
- Existing app-server permission-request handling remains the access boundary for a managed `mention`; this implementation does not add writable roots, edit `config.toml`, switch profiles, or silently grant broader access.

### Verification completed

- TDD red/green coverage was added for managed file/folder imports, deterministic deduplication, source deletion survival, size rejection, empty-directory fidelity, cleanup, queue cloning/validation, workspace classification, prompt-part mapping, type-aware composer deduplication, and WPF external-snapshot guidance.
- The repository custom runner passes all 138 tests.
- Debug and Release rebuild results and executable paths are recorded in the implementation handoff for this change.

### Deferred follow-up scope

- Installed-runtime smoke tests for managed file/folder `mention` access and exact read-permission requests.
- Attachment-specific access-state/preflight UI and additional grant narrowing around permission requests. Existing exact-once permission approval remains unchanged.
- Interactive folder scan/review, exclusions, sensitive-file warnings, progress, and per-file selection. The shipped bounded snapshot imports the complete safe tree or fails atomically.
- Optional live external folders, DPAPI-protected external-root registry, and their advanced picker action.
- App-server history reconciliation for external mention paths, rollout flags/telemetry, and bounded thumbnail decoding.

## 1. Outcome

Allow a user to attach local content from outside the active workspace without weakening the selected Codex permission policy, persisting arbitrary source paths in plain text, following unsafe filesystem links, or making queued turns depend on an unowned source that may disappear.

The completed experience should support:

- external images through the existing managed-image snapshot flow;
- external regular files as durable managed snapshots;
- external folders as bounded managed snapshots;
- an optional advanced live read-only folder reference for large or frequently changing directories;
- mixed workspace/external image/file/folder batches from picker, paste, and drag/drop;
- text-only, attachment-only, and mixed start/steer requests;
- drafts, queued follow-ups, sent turns, forks, restarts, and cleanup;
- exact read-permission approval at turn or session scope; and
- actionable failure when a profile cannot read the managed object or live root.

“Upload” means copying content into SynthiaCode-owned storage. It does not mean sending bytes to a separate cloud upload API. The current app-server input contract has `localImage` and `mention`, but no generic file or directory upload part. Images continue to use `localImage`; managed files and managed folder snapshots are exposed to Codex through absolute `mention` paths.

## 2. Pre-implementation baseline (historical)

At planning time, the workspace attachment release provided most of the reusable pipeline:

- `AttachmentReference` is tagged by `AttachmentKind` and `AttachmentSourceKind`.
- `LocalAttachmentStore` content-addresses, stages, validates, deduplicates, restores, and cleans up managed images.
- `WorkspaceAttachmentResolver` safely resolves file/folder references inside the owning workspace and blocks lexical and reparse-point escapes.
- `CodexMentionInput` serializes ordered `mention` parts for start and steer.
- `MainViewModel.AddAttachmentPathsAsync` classifies mixed paths, but only images may originate outside the workspace.
- drafts, queues, turns, snapshots, transcript chips, and cleanup already carry generic attachment metadata.
- background queue dispatch revalidates attachments against its captured workspace.
- SynthiaCode already parses `item/permissions/requestApproval` and can grant the requested subset for one turn or the session.

The reported screenshot error was produced before request construction because a non-image candidate was routed to `WorkspaceAttachmentResolver`, which correctly rejected an external path. The completed implementation now classifies that path for managed snapshot import instead.

## 3. Contract and permission findings

### 3.1 Filesystem permissions

Codex permission profiles support exact absolute paths with `read`, `write`, or `deny`. A read grant allows listing and reading without allowing modification. Direct absolute Windows paths and UNC paths are supported. More-specific denies continue to take precedence.

Reference: [Codex filesystem permissions](https://learn.chatgpt.com/docs/permissions#filesystem-permissions).

Legacy `sandbox_workspace_write.writable_roots` adds writable roots, not read-only roots. SynthiaCode must not use it for external attachment access because it grants more authority than the feature requires.

Reference: [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference#configtoml).

### 3.2 Runtime permission requests

The built-in `request_permissions` tool sends `item/permissions/requestApproval` with requested filesystem permissions. The client may grant only a subset and may scope that grant to the turn or session. SynthiaCode already has the response path needed to present and answer this request.

Reference: [Codex app-server permission requests](https://learn.chatgpt.com/docs/app-server#permission-requests).

There is no documented client method for proactively granting arbitrary filesystem access. The reliable paths are:

1. an already-selected named profile explicitly grants read access; or
2. Codex requests exact additional read access during the turn and SynthiaCode approves it.

The plan must therefore distinguish “attachment imported” from “Codex access verified.” A chip may be safely stored before the runtime has requested permission.

### 3.3 Wire inputs

Use the existing mapping:

| Attachment | Managed representation | Wire input |
| --- | --- | --- |
| External image | Validated immutable image object | `localImage` |
| External regular file | Immutable managed file object | `mention` |
| External folder snapshot | Immutable managed directory tree | `mention` |
| External live folder | Protected root identity plus live path | `mention` |

Do not invent `file`, `folder`, `input_file`, or archive-specific app-server inputs. Do not inline arbitrary file contents into the prompt.

## 4. Product and semantic decisions

### 4.1 Default behavior

- **Images:** snapshot immediately, matching current behavior.
- **External files:** snapshot immediately. Later source edits or deletion do not change the attachment.
- **External folders:** scan, show a bounded review, then snapshot selected regular files. Later source edits do not change the attachment.
- **Live folder reference:** an explicit advanced action for content that is too large or intentionally live. It requires an exact read grant and is revalidated before every use.

The preview chip must label semantics clearly:

- `Managed snapshot — copied when attached`
- `Live external folder — current contents are used when sent`

### 4.2 No silent permission broadening

SynthiaCode must never:

- switch to full access;
- replace the selected named/managed permission profile;
- add an external path as a writable root;
- edit `config.toml` automatically;
- approve a broader parent directory than requested; or
- grant session scope without an explicit user choice or existing auto-review policy.

### 4.3 Source-path privacy

Managed file and folder snapshots persist no original absolute source path. Keep the path only in memory during import and store:

- a content-addressed storage key;
- sanitized display name;
- media/type and size metadata;
- content hash or folder manifest hash; and
- snapshot summary counts.

Optional live roots require persistence across queued sends and restarts. Store an opaque `ExternalRootId` on the attachment and keep the corresponding absolute root in a separate Windows user-bound protected registry, encrypted with DPAPI `CurrentUser`. Never serialize the clear path into `settings.json`, queue snapshots, logs, or telemetry.

## 5. Attachment domain changes

### 5.1 Source kinds

Extend the source tag without changing existing numeric values:

```csharp
public enum AttachmentSourceKind
{
    ManagedCopy = 0,
    WorkspaceReference = 1,
    ExternalLiveReference = 2
}
```

`ManagedCopy` covers managed images, external files, and external folder snapshots. `AttachmentKind` remains `Image`, `File`, or `Folder`.

### 5.2 Reference metadata

Add only fields needed for durable semantics:

```csharp
public string? ExternalRootId { get; set; }       // live reference only
public string? ExternalRelativePath { get; set; } // optional child under live root
public int SnapshotFileCount { get; set; }        // managed folder only
public long SnapshotByteLength { get; set; }      // managed folder only
```

Do not add `OriginalSourcePath` or a redundant per-reference schema field. Increment the existing app-level `AttachmentSchemaVersion` to 3 and continue to keep every resolved absolute path runtime-only and `[JsonIgnore]`.

### 5.3 Invariants

- Managed image: valid storage key, image signature/metadata, regular stored file.
- Managed file: valid storage key, regular stored file, non-empty content unless explicitly supported later.
- Managed folder: valid folder storage key, validated manifest, no reparse entries, bounded file count/depth/bytes.
- Workspace reference: non-rooted normalized path under the owning workspace.
- External live reference: opaque root ID plus an optional non-rooted normalized child path; a null child refers to the registered root itself.
- Unknown or contradictory tag combinations fail closed during load and queue validation.

### 5.4 Limits

Centralize and test initial values:

| Limit | Initial value |
| --- | ---: |
| Attachments per input | 20 |
| Images per input | 10 |
| Folder attachments per input | 5 |
| External managed file | 25 MiB |
| External managed folder aggregate | 100 MiB |
| External managed folder files | 1,000 |
| Folder traversal depth | 32 |
| Managed bytes per input | 150 MiB |
| Relative path UTF-8 bytes | 4 KiB |
| Display name | 120 characters |
| Managed store | 1 GiB initially |

Limits must be enforced while streaming or walking, not only from initial metadata.

## 6. Managed object-store evolution

Replace the image-only contract with an object-store contract while keeping compatibility adapters during migration:

```csharp
public interface IAttachmentObjectStore
{
    Task<AttachmentReference> ImportImageAsync(...);
    Task<AttachmentReference> ImportFileAsync(...);
    Task<AttachmentReference> ImportFolderAsync(
        FolderImportPlan plan,
        IProgress<FolderImportProgress>? progress,
        CancellationToken cancellationToken);
    string ResolvePath(AttachmentReference attachment);
    Task CleanupAsync(IEnumerable<AttachmentReference> references, ...);
}
```

Suggested layout:

```text
attachments/
  objects/
    images/<prefix>/<sha>.<ext>
    files/<prefix>/<sha>.<sanitized-ext>
    folders/<manifest-sha>/
      manifest.json
      content/...
  staging/
    <operation-id>/
  roots/
    protected-roots.json.dpapi
```

### 6.1 External file import

1. Resolve the selected file without following an unsafe link outside the selected leaf.
2. Require a normal regular file and reject devices, pipes, directories, alternate streams, broken links, and inaccessible paths.
3. Capture initial length and last-write metadata.
4. Stream to staging while hashing and enforcing the byte limit.
5. Re-stat the source and reject or retry once if it changed during copy.
6. Flush, atomically move to the content-addressed object path, and deduplicate.
7. Return metadata without retaining the source path.

Do not parse, execute, render, or trust generic file contents during import. Media type is advisory metadata only.

### 6.2 Folder scan and review

Folder selection becomes a two-step operation:

1. **Scan:** cancellably enumerate metadata without copying contents.
2. **Review:** show included count/bytes, excluded entries, warnings, and import limits.
3. **Import:** copy the reviewed set into a staging tree and create a deterministic manifest.

Hard exclusions:

- reparse points, symlinks, junctions, mount points, devices, pipes, and sockets;
- alternate data streams;
- paths that cannot be represented safely relative to the selected root; and
- entries that disappear or change type during the operation.

Default soft exclusions, always shown and user-overridable where safe:

- VCS metadata such as `.git`, `.hg`, and `.svn`;
- dependency/build caches such as `node_modules`, `.venv`, `bin`, `obj`, `.next`, and `dist`;
- files ignored by a discovered root `.gitignore`; and
- likely secrets such as `.env`, private keys, and credential stores.

Sensitive candidates must be unchecked by default and require an explicit inclusion action. The UI must never display file contents during review.

### 6.3 Deterministic folder manifest

The manifest contains normalized relative path, length, SHA-256, and a sanitized type label for each copied regular file. Sort paths ordinally before hashing. The folder object identity is the SHA-256 of the canonical manifest.

On collision/deduplication, verify the existing manifest before reuse. Staging is removed on cancellation or failure; interrupted staging is cleaned after 24 hours.

## 7. Protected external-root registry

Add `IExternalAttachmentRootRegistry` for optional live references:

```csharp
public interface IExternalAttachmentRootRegistry
{
    ExternalRootRegistration Register(string absolutePath);
    string Resolve(string rootId);
    void RemoveUnreferenced(IReadOnlySet<string> referencedRootIds);
}
```

Requirements:

- normalized root path encrypted using Windows DPAPI `CurrentUser`;
- random opaque root ID, never derived from the path;
- atomic registry writes;
- current-user ACL where practical;
- no filenames or paths in logs;
- reparse-aware validation on registration and every resolution;
- exact-root read approval, never its parent; and
- reference-aware cleanup after drafts, queues, and turns no longer use the root.

If DPAPI or registry loading fails, retain the chip as unavailable and require reselection. Do not fall back to plaintext.

## 8. Permission and access state

### 8.1 Explicit access state

Add a runtime validation result rather than a single boolean:

```csharp
public enum AttachmentAccessState
{
    Ready,
    RequiresReadApproval,
    Missing,
    Changed,
    PermissionBlocked,
    Invalid
}
```

Managed objects and live roots outside the workspace normally begin as `RequiresReadApproval` unless an installed-runtime probe proves the active profile already grants exact read access.

### 8.2 Permission modes

- **Ask for approval:** allow the turn; display that Codex may request exact read access. Route the resulting request to the user.
- **Approve for me:** allow the turn; auto-review remains responsible for the exact request. SynthiaCode does not preapprove it.
- **Custom named profile:** if SynthiaCode cannot inspect effective filesystem rules, label access as unverified. Allow only after a live read probe succeeds or the runtime requests and receives permission.
- **Never/no request permissions:** block send with an actionable message unless a live probe proves the object is already readable.
- **Managed restrictions:** obey allowed profiles/reviewers/policies and fail closed.

The current `permissionProfile/list` summary exposes ID, description, and allowed state, not effective filesystem entries. Do not infer read coverage from a profile name.

### 8.3 Approval presentation

When an approval request matches an attachment object or registered live root:

- show the attachment display name and kind;
- show `Read only` and whether the request covers one file or a directory tree;
- default to turn scope;
- offer session scope only as the existing explicit action;
- grant only requested `read` entries that match attachment-owned paths; and
- reject unexpected `write`, broader parent, glob, root, or unrelated entries.

This attachment-aware narrowing is additional validation around the existing exact-once approval response.

## 9. Acquisition UX

### 9.1 Attach actions

Keep the current actions but change their semantics:

- `Attach images...` — external paths already supported as managed images.
- `Attach files...` — workspace files remain live references; external files become managed snapshots.
- `Attach folder...` — workspace folders remain live references; external folders open the snapshot review.
- `Reference external folder...` — advanced read-only live reference.

The picker should start in the active workspace but must no longer constrain navigation.

### 9.2 Mixed paste and drag/drop

All acquisition paths converge on one batch pipeline. Preserve shell order and classify each candidate independently:

1. supported image -> managed image;
2. regular file inside workspace -> workspace reference;
3. regular file outside workspace -> managed file;
4. directory inside workspace -> workspace reference;
5. directory outside workspace -> pending folder scan/review;
6. invalid/special/inaccessible -> reason-coded rejection.

A mixed drop containing an external folder may add immediate candidates first, then open one combined folder review. Cancellation of folder review must not remove already accepted files/images or the previous draft.

### 9.3 Preview chips

Add source and access badges:

- `Workspace live`
- `Managed snapshot`
- `External live · permission may be required`
- `Unavailable`

Managed folder chips show file count and total size. All chips retain Open/Reveal, reorder, and Remove. Opening a managed folder reveals its immutable snapshot; opening a live folder resolves the protected root.

## 10. Request construction and runtime behavior

Extend the existing ordered builder:

1. normalized text part when non-empty;
2. managed image -> `localImage` resolved from the object store;
3. workspace file/folder -> current contained `mention`;
4. managed external file -> object-store file `mention`;
5. managed external folder -> snapshot content directory `mention`;
6. external live folder -> protected-root `mention` after revalidation.

Before start or steer:

- validate every object/storage key and live root;
- reapply image capability checks only to images;
- calculate permission readiness;
- preserve preview order;
- send the whole batch or none; and
- retain text and chips on validation, approval, transport, or persistence failure.

If the runtime cannot request needed read access, fail before sending. If permission is declined during a turn, keep the historical turn attachment metadata and show a clear permission-denied result; never retry automatically.

## 11. Queue, restart, and lifecycle behavior

### 11.1 Queued follow-ups

Queue snapshots deep-clone managed keys and external root IDs. Before background dispatch:

1. re-resolve the captured thread/worktree workspace;
2. validate all managed objects;
3. resolve and revalidate protected live roots;
4. refresh model and permission policy;
5. determine whether interaction is required for read approval; and
6. start only when the current policy permits the approval path.

A background item that needs human approval may enter `NeedsAttention` with `External attachment requires read approval`; it must not open a surprise global prompt while another thread is selected unless existing approval UX explicitly supports that behavior.

### 11.2 Restarts and forks

- Managed snapshots rehydrate from storage keys.
- Live roots resolve from protected root IDs.
- Missing/corrupt objects or roots become unavailable placeholders.
- Forks deep-copy references without duplicating managed bytes.
- Source deletion never affects managed snapshots.
- Live source deletion or movement makes the attachment unavailable.

### 11.3 Cleanup

Cleanup scans drafts, queued follow-ups, conversation turns, and forked threads before removing:

- managed files;
- managed folder trees/manifests;
- managed images; and
- protected live-root registrations.

Keep the existing grace period for objects and use atomic best-effort cleanup. Never traverse or delete the original external source.

## 12. Persistence and migration

Increment the app attachment schema and preserve compatibility:

- existing managed images remain `ManagedCopy/Image`;
- existing workspace references remain unchanged;
- new managed files/folders use storage keys only;
- live references use opaque root IDs only;
- runtime paths remain ignored by JSON; and
- legacy `Images`/`UserImages` aliases continue to read but not write.

Migration is idempotent. Unknown future source kinds remain visible as unavailable metadata rather than being interpreted as workspace paths.

## 13. TDD implementation sequence

Use the repository’s custom console test runner. Add each test slice first, capture the intended red failure, implement only that slice, and rerun the narrow attachment/permission suite before the full suite.

### Phase 0 — Runtime contract spike

1. Add an opt-in live smoke test for a managed external file `mention`.
2. Verify Codex can request exact read permission for the managed path.
3. Verify turn- and session-scoped approval responses.
4. Verify a managed external folder directory can be mentioned and listed.
5. Test custom/never policies that cannot request permission.

Exit criterion: at least one supported Codex runtime proves the permission-request path. If it does not, require an explicitly configured read profile and do not ship a misleading automatic-access UX.

### Phase 1 — Generic managed file store

1. Add failing tests for streaming limits, empty files, source mutation, hashing, deduplication, atomic staging, key containment, and cleanup.
2. Split the image-specific store API into typed image/file imports.
3. Keep current image behavior byte-for-byte compatible.
4. Add managed-file persistence and transcript tests.

Exit criterion: an external file survives source deletion and no source path is persisted.

### Phase 2 — Permission readiness and approval narrowing

1. Test attachment access-state calculation for every permission mode.
2. Test exact read-only grant matching.
3. Reject write, broader parent, unrelated, glob, and root grants.
4. Test decline, cancellation, turn scope, session scope, stale request, and exact-once response.
5. Add live app-server approval smoke coverage.

Exit criterion: no attachment path can cause SynthiaCode to grant more filesystem access than the runtime requested and the attachment owns.

### Phase 3 — External files in acquisition/UI

1. Test picker, clipboard, drop, mixed ordering, duplicate hashing, partial failure, and cancellation.
2. Test workspace files remain live while external files snapshot.
3. Add source/access badges and error states.
4. Integrate start, steer, queue, draft, turn, fork, and cleanup.

Exit criterion: external regular files work across every lifecycle path without broadening permissions.

### Phase 4 — Folder scan and snapshot

1. Add a filesystem abstraction and tests for traversal limits, reparse entries, races, inaccessible files, exclusions, secret warnings, deterministic manifests, deduplication, and cancellation.
2. Implement scan/review/import with progress.
3. Add managed-folder chips and request building.
4. Test large/unsafe folders fail without leaving staging data.

Exit criterion: a reviewed external folder snapshot is immutable, bounded, safe, and restorable.

### Phase 5 — Optional live external roots

1. Test DPAPI registry round-trip, corruption, atomicity, opaque IDs, cleanup, and path non-disclosure.
2. Test live-root reparse containment and exact read approval.
3. Add the advanced `Reference external folder...` action and warning copy.
4. Test source mutation, deletion, queued dispatch, restart, and fork behavior.

Exit criterion: a live root is never redirected, persisted in plaintext, or granted write access.

### Phase 6 — History, accessibility, and rollout

1. Reconcile app-server history `mention` paths to owned managed objects or protected roots.
2. Redact unknown external absolute paths into unavailable placeholders.
3. Add keyboard and automation coverage for scan/review and access badges.
4. Run Debug/Release full suites and live runtime smoke tests.
5. Update both implementation plans, feature parity, and architecture docs.

## 14. Test matrix

### Managed files

- normal, empty, maximum-size, oversized, locked, deleted, and changing files;
- duplicate content with different names/extensions;
- misleading extension and binary content;
- staging interruption and orphan cleanup;
- storage-key traversal and case behavior; and
- original source deletion after import.

### Managed folders

- empty folder and normal nested tree;
- maximum depth/count/aggregate bytes;
- reparse, junction, symlink, mount, cycle, and broken-link cases;
- access denied and file mutation during snapshot;
- deterministic manifest and content deduplication;
- VCS/cache/ignored/sensitive review categories;
- cancellation before scan, during scan, during copy, and before commit; and
- cleanup never touches the source folder.

### Permissions

- already-readable path;
- exact file read request;
- exact folder-tree read request;
- broader parent, filesystem root, glob, write, and unrelated requests rejected;
- turn/session grants;
- user decline and auto-review result;
- managed restriction disallowing the reviewer/profile;
- custom profile with unknown effective rules; and
- approval unavailable for background queue dispatch.

### Lifecycle and privacy

- picker/paste/drop mixed order;
- start/steer/queue/background parity;
- attachment-only request;
- text-only model accepts managed file/folder mentions;
- draft, turn, fork, restart, and cleanup deep copies;
- no source absolute path in settings or logs;
- DPAPI registry corruption and missing root;
- missing managed object placeholder; and
- failed send retains the complete composer state.

### WPF

- folder review dialog progress/cancel/confirm;
- access badges and polite live-region errors;
- keyboard navigation and reorder/remove;
- high contrast and compact layout;
- external file/folder automation names exclude full paths; and
- mixed partial success summary is readable and actionable.

## 15. File-level change map

| Area | Expected change |
| --- | --- |
| `Core/Attachments/AttachmentModels.cs` | New source kind, metadata, limits, access states, store and root-registry contracts |
| New `Core/Attachments/FolderImportModels.cs` | Scan plan, candidates, warnings, progress, and results |
| `Infrastructure/Attachments/LocalAttachmentStore.cs` | Split image/file/folder imports or replace with object-store implementation |
| New `Infrastructure/Attachments/FolderSnapshotService.cs` | Bounded scan, review plan, copy, manifest, race checks |
| New `Infrastructure/Attachments/ExternalAttachmentRootRegistry.cs` | DPAPI-protected live-root mapping and cleanup |
| `Infrastructure/Attachments/WorkspaceAttachmentResolver.cs` | Reusable filesystem classification/reparse helpers, without weakening containment |
| `Core/Codex/AppServer/CodexApprovalModels.cs` | Typed filesystem permission entries and attachment-aware grant validation |
| `Infrastructure/Codex/CodexAppServerClient.cs` | Preserve exact permission payloads and history mention reconciliation |
| `App/ViewModels/ExecutionPolicyViewModel.cs` | External-attachment readiness and policy messaging |
| `App/ViewModels/MainViewModel.cs` | Unified external acquisition, import, revalidation, request building, queue behavior |
| `App/ViewModels/TaskViewModel.cs` | Managed byte/folder limits and access-state presentation |
| `App/Views/TaskView.xaml(.cs)` | External actions, badges, folder review, mixed paste/drop |
| `Core/Settings/*` | Schema migration, protected-root IDs, generic cleanup references |
| `Tests/AttachmentInputTests.cs` | Domain, acquisition, object-store, lifecycle, and WPF coverage |
| `Tests/ApprovalProtocolTests.cs` | Exact read-grant matching and runtime approval coverage |
| Docs | Completion record, feature parity, architecture, limits, and privacy semantics |

## 16. Observability and privacy

Add reason-coded events only:

- `external_attachment_import_started/completed/failed` — kind, source action, bytes/count buckets;
- `folder_snapshot_reviewed` — included/excluded counts and warning categories;
- `external_attachment_access_state` — state and permission mode;
- `external_attachment_permission_requested/resolved` — scope, access kind, decision; and
- `external_attachment_cleanup` — object/root counts.

Never log source paths, filenames, relative paths, hashes usable as cross-event identifiers, file contents, clipboard contents, or manifest entries.

## 17. Rollout

Use independent flags:

1. `EnableExternalManagedFiles`
2. `EnableExternalAttachmentPermissionRequests`
3. `EnableExternalFolderSnapshots`
4. `EnableExternalLiveFolders`

Recommended rollout order:

1. internal/dev runtime smoke;
2. managed external files with named-profile access only;
3. exact runtime permission requests;
4. bounded external folder snapshots;
5. advanced live external folders; and
6. default-on after telemetry shows low permission and import failure rates.

Disabling a flag must not erase persisted objects. Existing chips remain viewable/removable but cannot be newly added or resent when their capability is disabled.

## 18. Acceptance criteria

The external attachment capability is complete when:

1. External images, regular files, and folders can be added by picker, clipboard file list, and drag/drop.
2. External files and default external folders are durable managed snapshots.
3. Optional live folders are clearly labeled and require exact read-only access.
4. No original managed-source path appears in settings, queues, logs, or telemetry.
5. Folder traversal cannot escape through reparse points and is bounded by count, depth, and bytes.
6. Generic file/folder content is never silently treated as an image or inlined into prompt text.
7. The active permission policy is never replaced or broadened to write/full access.
8. Exact read requests work at turn/session scope and broader requests fail closed.
9. Start, steer, queue, retry, background dispatch, forks, and restarts preserve identical attachment semantics.
10. Failed import, validation, permission, transport, and persistence operations retain the user’s existing draft.
11. Managed object cleanup is reference-aware and never deletes an original external source.
12. Legacy image/workspace attachment settings remain compatible.
13. The complete custom suite and new live smoke tests pass; Debug and Release builds complete with zero warnings.

## 19. Principal risks and mitigations

| Risk | Mitigation |
| --- | --- |
| `mention` does not itself grant read access | Explicit access state, exact runtime request flow, named-profile fallback, live smoke gate |
| Managed store root exposes unrelated attachments | Request/grant the exact object file or folder, not the store parent |
| Folder contains secrets or huge caches | Scan/review, hard limits, visible exclusions, sensitive candidates unchecked by default |
| Reparse point escapes selected folder | Never follow reparse entries; canonical validation at scan and copy time |
| Source changes during snapshot | Stat/hash race checks, deterministic manifest, fail/retry once |
| Queue needs interactive approval in background | Mark `NeedsAttention`; no surprise partial or cross-thread send |
| Named profile rules are opaque to the client | Do not infer; use live probe/request or fail closed |
| Plaintext external path leaks through persistence | Managed snapshots retain no source; live roots use DPAPI-protected opaque registry |
| Automatic cleanup removes shared objects | Global reference scan plus grace period and manifest verification |
| Snapshot semantics surprise the user | Source badge and explicit managed/live explanatory copy |

## 20. Recommended delivery order

Deliver the feature in reviewable vertical slices:

1. Prove managed external `mention` plus exact permission request against the installed Codex runtime.
2. Generalize the managed store and ship external regular-file snapshots.
3. Add attachment-aware permission readiness and exact grant narrowing.
4. Integrate picker/paste/drop, lifecycle persistence, queue behavior, and UI badges.
5. Add bounded folder scan/review and managed folder snapshots.
6. Add the protected-root registry and optional live external folders.
7. Add history reconciliation, accessibility hardening, rollout flags, full regression, and documentation completion records.

This ordering provides useful external-file support early, proves the permission boundary before recursive folder work, and leaves the highest-risk live-root capability until managed snapshots and approval narrowing are already reliable.
