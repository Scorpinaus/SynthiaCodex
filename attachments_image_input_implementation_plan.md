# Attachments and Image Input Implementation Plan

- **Prepared:** 19 July 2026
- **Repository baseline inspected:** `f1860a4`
- **Backlog target:** `feature_parity.md` -> P0 item 2
- **Scope:** Local image acquisition, multimodal prompt parts, composer and transcript previews, queue integration, model-capability validation, and restart-safe local persistence
- **Status:** Core implementation completed and verified on 19 July 2026; follow-up hardening is listed below

## Implementation completion record

The P0 local-image workflow described by this plan is implemented across the Core, Infrastructure, App, WPF, and custom test-runner projects.

### Delivered

- Added typed ordered `text`, `image`, and `localImage` protocol inputs for both `turn/start` and `turn/steer`, while retaining compatibility constructors for existing text-only callers.
- Parsed model `inputModalities` and fail closed when the selected model explicitly lacks image support.
- Added a content-addressed local attachment store under application data with streaming SHA-256 import, atomic staging/finalization, duplicate reuse, root-containment checks, signature/media/dimension validation, per-image and total-store ceilings, animated-GIF rejection, and reference-aware staging/orphan cleanup.
- Added multi-select picker, clipboard bitmap/file-list paste, handled routed file drag/drop, image-only submission, mixed text/image submission, and the same behavior for active-turn steering and Queue/Send now.
- Added compact composer previews with open, reorder, and remove actions plus transcript previews for sent turns.
- Added attachment metadata to queued follow-ups and conversation turns with deep cloning through settings snapshots, `ThreadStore`, forks, restores, and background queue delivery.
- Added per-project/thread unsent attachment-draft snapshots. Only relative managed storage keys and metadata are serialized; image bytes, data URLs, and original source paths are not written to settings.
- Rehydrate managed paths during startup, retain queued/sent images after original-source deletion, and clean only unreferenced managed objects.
- Preserved the existing failure contract: invalid input or request failure retains the draft; successful start/steer/queue acknowledgement clears its attachments.

### TDD and verification evidence

Tests were added before production implementation and initially failed on the absent attachment namespaces and protocol types. The completed suite covers ordered wire serialization, model modalities, managed copy/deduplication and invalid input, queue/turn/draft deep persistence, view-model capability validation, and WPF picker/preview/drop elements. The existing regression suite exposed and drove fixes for prompt retention and responsive composer layout.

Final verification result: **134 tests passed, 0 failed** using the repository's console test runner.

### Deliberate follow-up hardening

The core backlog outcome is delivered. Two non-blocking improvements remain explicitly tracked as **Near** parity in `feature_parity.md`:

1. Decode composer/transcript thumbnails through a bounded `BitmapImage` cache instead of relying on WPF's normal `Image.Source` path decoding.
2. Materialize `localImage` or bounded inline image parts that exist only in external app-server history. Locally originated sent images already restore from safe conversation snapshots and are not overwritten by text-only history reconciliation.

Generic documents remain outside this item because the current app-server user-input union has no generic file/document part.

## 1. Outcome

SynthiaCode should let a user add one or more local images to an idle prompt or active-turn follow-up by:

- choosing images through a Windows file picker;
- pasting image pixels or copied image files into the focused composer;
- dragging image files from Explorer or another Windows application onto the focused composer;
- reviewing, reordering, opening, or removing compact previews before submission; and
- sending the resulting ordered text and image input parts through both `turn/start` and `turn/steer`.

The same input must remain usable when it is queued, when a different thread is selected, after SynthiaCode restarts, and when a sent turn is restored from local settings or app-server history. The implementation must not put image bytes, data URLs, or arbitrary source paths into `settings.json`, must not depend on the original file continuing to exist, and must not silently send an image to a model that does not advertise image input.

The P0 feature is complete when the picker, paste, and drag/drop paths all converge on one validated attachment pipeline; the exact ordered input parts reach app-server; previews survive the required lifecycle; queued follow-ups retain their images; and corrupted, oversized, missing, unsupported, or unavailable attachments fail visibly without discarding the user's text.

## 2. Scope boundary and product decisions

### 2.1 P0 includes image attachments, not arbitrary document uploads

The current app-server `UserInput` union supports `text`, `image`, `localImage`, `skill`, and `mention`. It does not define a generic file/document input item. This plan therefore interprets “file/image picker” as a native file picker configured to select supported image files.

Do not pretend that a PDF, archive, source file, or office document is a first-class attachment by silently converting it to a text path. Files outside the selected workspace may be unreadable under the active permission policy, and copying arbitrary documents into the project would be an unexpected mutation. When a non-image is picked, pasted as a file, or dropped, reject it with a concise supported-format message and leave the current draft untouched.

Generic files can be a later feature after the app-server exposes a suitable user-input variant or SynthiaCode deliberately designs a separate “reference local file” workflow with clear sandbox semantics.

### 2.2 Emit `localImage`, not remote image URLs

Use app-server `localImage` parts for every picker, paste, and drag/drop image. The maintained Codex app-server README documents that inline data URLs are accepted for the `image` variant but remote HTTP(S) image URLs are rejected. SynthiaCode has no reason to inflate JSON-RPC requests with base64 for local files, so it should stage bytes locally and send an absolute managed path.

Keep a typed `CodexImageInput` protocol model for forward compatibility and history parsing, but the P0 UI must not accept a pasted web URL as an attachment and must not download remote content.

### 2.3 Supported formats and bounded product limits

Accept these conservative formats after signature and decoder validation:

- PNG (`.png`)
- JPEG (`.jpg`, `.jpeg`)
- WebP (`.webp`)
- non-animated GIF (`.gif`)

The OpenAI image-input guide identifies the same input formats. SynthiaCode should apply smaller desktop-client limits than the service maximum so preview decoding, settings snapshots, queued dispatch, and disk use stay predictable.

Initial product constants:

| Limit | Initial value | Behavior when exceeded |
| --- | ---: | --- |
| Images per composer/queued item | 10 | Reject the additions that would cross the limit. |
| Encoded bytes per image | 20 MiB | Reject before full decode. |
| Encoded bytes per draft/queued item | 50 MiB | Reject the additions that would cross the limit. |
| Decoded dimensions | 50 megapixels and 32,768 px on either axis | Reject as unsafe to preview. |
| Managed attachment-store ceiling | 1 GiB | Refuse new imports after orphan cleanup; never evict referenced files. |
| Preview decode edge | 320 device-independent pixels | Decode a thumbnail, not a full-resolution WPF bitmap. |

These are SynthiaCode safety limits, not claims about the upstream API. Keep them in one `AttachmentLimits` type and include the actual limit in validation errors. Animated GIFs are rejected for P0 because app-server/API requirements call for non-animated GIFs and WPF preview behavior otherwise becomes ambiguous.

### 2.4 Submission rules

- Permit text-only, image-only, or text-plus-image input. The UI should encourage a descriptive prompt but must not synthesize model-visible text for an image-only submission.
- Build the protocol list as one normalized text part first, when non-empty, followed by local-image parts in the order shown by the preview strip. The current composer does not support inline interleaving, so the UI must not imply that an image sits at a particular caret position.
- Use the same part order for start, steer, queue, retry, and restore paths.
- Clear text and attachments only after local queue insertion succeeds or app-server acknowledges `turn/start`/`turn/steer`.
- If validation or transport fails, retain the complete draft and focus.
- Changing to a text-only model does not discard attachments. It marks the draft invalid and disables Send/Queue/Steer until the user removes the images or selects an image-capable model.

### 2.5 Acquisition behavior

**Picker**

- Add an `Attach images` button next to the composer options.
- Use `Microsoft.Win32.OpenFileDialog`, `Multiselect = true`, and a filter for the four supported extensions.
- Start in the last successfully used image folder for the current process only. Do not persist that folder because it can expose sensitive paths in settings.

**Paste**

- Intercept Paste only while the active `PromptBox` or `GuidanceBox` owns keyboard focus.
- If the clipboard contains a supported file-drop list, import those image files.
- Otherwise, if it contains bitmap data, encode the clipboard `BitmapSource` as PNG and import it.
- Otherwise, leave the normal WPF text-paste command untouched.
- Catch clipboard-busy and COM/external exceptions, report that paste could not be read, and never consume the text fallback accidentally.

**Drag/drop**

- Enable copy-only drops on the composer surface, not the entire window.
- Register handlers with `handledEventsToo: true`; WPF `TextBox` handles its built-in drag/drop events and otherwise masks file drops.
- Accept only `DataFormats.FileDrop` regular files. Reject directories, shell shortcuts, URLs, and non-image files.
- Set the drag effect to `Copy` only after the complete candidate set passes cheap type/count checks. Show a visible drop-target state and an accessible status string.

**Batch errors**

Validate every candidate independently, attach the valid images in original order, and show one summary for rejected items. Never fail silently and never display full source paths in the status bar or logs. Duplicate content in the same draft should be skipped with a non-error notice.

## 3. Research findings and parity contract

1. [Codex app-server turns](https://learn.chatgpt.com/docs/app-server#turns) define `input` as a list that can contain `{type: "text"}`, `{type: "image", url: ...}`, and `{type: "localImage", path: ...}`. Both `turn/start` and `turn/steer` take the input list.
2. The current [open-source app-server README](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md) is more specific about current ingress behavior: `image` accepts inline data URLs and rejects remote HTTP(S) URLs. It also defines restored `userMessage.content` as the same text/image/localImage list.
3. [Codex image-input guidance](https://learn.chatgpt.com/docs/image-inputs) describes attaching visual context, dragging images into the composer, and writing a prompt that explains what to inspect and what outcome is wanted.
4. `model/list` advertises `inputModalities`. The checked-in `schemas/v2/ModelListResponse.json` defines canonical `text` and `image` values, while the current client ignores the field. The [Codex model interface documentation](https://github.com/openai/codex/blob/main/codex-rs/docs/codex_mcp_interface.md#models) also identifies it as the model capability source.
5. The checked-in `TurnStartParams.json` and `TurnSteerParams.json` independently confirm the `UserInput` discriminated union and field names. The vendored schemas are the version-matched source for serialization tests.
6. [OpenAI image-input requirements](https://developers.openai.com/api/docs/guides/images-vision#image-input-requirements) list PNG, JPEG, WebP, and non-animated GIF. SynthiaCode should validate these formats locally and apply its own lower client limits.
7. WPF supplies the native building blocks: [`OpenFileDialog`](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/how-to-open-common-system-dialog-box), [`DataFormats.FileDrop`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.dataformats.filedrop), and the [drag/drop routed-event system](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/drag-and-drop). Microsoft notes that text controls handle drag/drop events, which is why the composer needs `AddHandler(..., handledEventsToo: true)`.
8. WPF exposes [`Clipboard.ContainsImage`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.clipboard.containsimage) and [`Clipboard.ContainsFileDropList`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.clipboard.containsfiledroplist). Clipboard access remains on the UI STA thread.
9. Preview decoding should use bounded dimensions and eager caching. [`BitmapCacheOption.OnLoad`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.bitmapcacheoption) loads bytes while the stream is open so preview controls do not hold persistent file handles.

### Parity contract

For this P0 item, “parity” means the practical local coding outcome rather than every ChatGPT attachment feature:

- local visual references can be acquired through the expected desktop gestures;
- image-capable Codex models receive actual image input parts, not prose containing a filename;
- selected images are visible and removable before sending;
- queued and restored local conversations retain useful image context and previews;
- model capability and invalid/missing data fail closed; and
- private image bytes remain local except when app-server sends them through the authenticated Codex runtime as part of the requested turn.

Screenshots of other applications, remote/cloud attachments, arbitrary documents, image generation, and a general artifact viewer remain separate backlog items.

## 4. Current implementation and gaps

### 4.1 Protocol client is text-only

`src/SynthiaCode.Core/Codex/AppServer/CodexAppServerModels.cs` defines:

- `CodexTurnStartRequest(..., string Prompt, ...)`
- `CodexTurnSteerRequest(..., string Prompt)`

`src/SynthiaCode.Infrastructure/Codex/CodexAppServerClient.cs` hard-codes a one-element text array in both `StartTurnAsync` and `SteerTurnAsync`. It also rejects an empty `Prompt`, so image-only turns are impossible.

### 4.2 Model capability is discarded

`ListModelsAsync` parses reasoning efforts, service tiers, availability, and speed tiers into `CodexModelOption`, but does not read `inputModalities`. The composer therefore cannot tell whether the selected model supports images.

### 4.3 Composer state has only strings

`TaskViewModel` exposes separate `Prompt` and `SteeringText` string buffers. `TaskView.xaml` swaps two WPF `TextBox` controls based on `IsTurnRunning`. There is no attachment collection, acquisition command, validity summary, preview strip, or drop target.

The existing idle/active split means attachment state must be scoped deliberately. A global collection would reproduce cross-thread leakage when users switch among parallel chats. Attachment drafts must follow the same focused project/thread/composer target as the text buffer.

### 4.4 Queued follow-ups persist only text

`CodexFollowUpQueue.Enqueue`, `QueuedFollowUp`, and `QueuedFollowUpSnapshot` store `Text` plus captured turn options. The queue's 64 KiB/256 KiB bounds account only for UTF-8 text. Queue start and send-now steering reconstruct text-only requests.

Images must become part of the immutable queued payload. Reordering queue rows must not reorder images inside a row, while editing a row must allow text changes and attachment removal without changing captured turn options.

### 4.5 Conversation snapshots discard images

`CodexConversationTurn` and `CodexConversationTurnSnapshot` keep `UserPrompt` only. `ParseConversationTurns` filters restored `userMessage.content` to `type == "text"`, so image history is dropped. `BeginTurn`, pending-turn reconciliation, cloning, and settings snapshots all assume a string prompt.

### 4.6 Settings are atomic but unsuitable for binary payloads

`JsonSettingsStore` already writes through a temporary file and promotes it atomically. `CoalescingSettingsStore` and `AppSettingsSnapshot` create immutable save snapshots. These are good foundations for attachment metadata, but `settings.json` must not absorb base64 image bytes or multi-megabyte payloads.

`SystemPaths.AppDataDirectory` gives a suitable app-owned root under `%LOCALAPPDATA%\SynthiaCode`. A separate content store should live there and settings should contain small, cloneable references only.

## 5. Target architecture

### 5.1 Typed app-server input model

Add a closed protocol-facing union in Core:

```csharp
public abstract record CodexUserInput;

public sealed record CodexTextInput(string Text) : CodexUserInput;

public sealed record CodexImageInput(string DataUrl) : CodexUserInput;

public sealed record CodexLocalImageInput(string Path) : CodexUserInput;
```

`CodexImageInput` exists to mirror the schema and parse compatible history; P0 UI acquisition produces only `CodexLocalImageInput`.

Change request records to take a non-empty `IReadOnlyList<CodexUserInput> Inputs`. Validate that the list contains at least one non-empty text item or image item. Add temporary text convenience factories rather than keeping two independent serialization paths:

```csharp
CodexTurnStartRequest.FromText(...);
CodexTurnSteerRequest.FromText(...);
```

`CodexAppServerClient` should serialize with a single `WriteUserInputs` helper, preserve order, use exact schema field names, reject unknown runtime types, and never log part contents or paths.

### 5.2 Product input and attachment references

Keep protocol inputs separate from persisted product metadata. Introduce:

```csharp
public sealed record AttachmentReference(
    string Id,
    string StorageKey,
    string DisplayName,
    string MediaType,
    long ByteLength,
    int PixelWidth,
    int PixelHeight,
    string ContentSha256);

public sealed class ComposerInputSnapshot
{
    public string Text { get; set; } = string.Empty;
    public List<AttachmentReference> Images { get; set; } = [];
}
```

Rules:

- `StorageKey` is a normalized relative key owned by `IAttachmentStore`, never an arbitrary absolute source path.
- `Id` identifies a UI reference; `ContentSha256` identifies bytes and supports deduplication. Two rows may reference the same stored object safely.
- `DisplayName` is only the leaf name, sanitized for control characters and length. Clipboard images use `Pasted image <local timestamp>.png`.
- The settings serializer stores metadata only. It never stores `ImageSource`, streams, source paths, data URLs, or raw bytes.
- Clone methods must deep-copy lists and immutable references in `AppSettingsSnapshot`, `ThreadStore`, queue snapshots, and turn snapshots.

### 5.3 Attachment store

Add `IAttachmentStore` to Core and `LocalAttachmentStore` to Infrastructure. Compose it in `AppServices` and inject it into `MainViewModel` or a narrower coordinator.

Proposed layout:

```text
%LOCALAPPDATA%\SynthiaCode\attachments\
  objects\ab\<sha256>.png
  staging\<guid>.tmp
```

Import algorithm:

1. Open a source file for read with explicit sharing and async/sequential options; do not follow with later reads from the original path.
2. Check regular-file status and encoded length before copying.
3. Stream into a unique staging file while computing SHA-256 and enforcing the byte limit.
4. Inspect magic bytes and decode metadata with a bounded WPF decoder. Verify canonical media type, dimensions, frame count, and supported format. The extension is a hint only.
5. Choose the canonical extension from decoded content, not the source filename.
6. Flush and close the staging file.
7. Resolve the final content-addressed path, re-check that it is contained beneath the configured object root, then atomically move it if no identical object exists. If it exists, delete only the private staging file.
8. Return an immutable `AttachmentReference` with no source path.

Clipboard images should be encoded directly to a staging PNG through the same validation/finalization path. The service should accept streams so storage tests do not depend on WPF clipboard APIs.

Containment and filesystem safety:

- Resolve all roots once with `Path.GetFullPath`.
- Reject rooted, parent-traversing, empty, or invalid `StorageKey` values on restore.
- Never delete or open a path produced by combining an unchecked storage key.
- Do not expose attachment objects as workspace files or add them to Git.
- Do not preserve alternate data streams, source ACLs, executable metadata, or source filenames on disk; copying only image bytes prevents later source replacement.
- Use best-effort cleanup for staging files, but surface import failures before mutating composer state.

### 5.4 Preview loader

Add a small `IAttachmentPreviewService`/WPF implementation that resolves only managed references through `IAttachmentStore` and returns a frozen, thumbnail-sized `BitmapSource`.

- Open with `FileShare.Read`.
- Set `DecodePixelWidth` or `DecodePixelHeight` to the preview edge while preserving aspect ratio.
- Use `BitmapCacheOption.OnLoad`, close the stream, and `Freeze()` the bitmap.
- Cache thumbnails by `StorageKey` with a bounded item/byte count and weak or explicit eviction.
- Return an unavailable placeholder on missing/corrupt files; never crash the transcript or hold a lock that blocks cleanup.
- Load previews asynchronously and keep file/decoder work off the UI thread. Marshal only the frozen result back to the dispatcher.

### 5.5 Draft workspace and persistence

Do not put draft attachments directly on global `TaskViewModel`. Add a `ComposerDraftWorkspace` keyed by a stable draft scope:

- existing thread: its app-server thread ID;
- new, not-yet-started chat: a generated draft ID associated with the selected project;
- idle and active-turn composer modes share the selected thread scope but hold separate `NewTurn` and `ActiveFollowUp` drafts so switching run state does not leak or erase attachments.

Persist draft snapshots in a new top-level `AppSettings.ComposerDrafts` list containing draft ID, normalized project path, optional thread ID, mode, text, image references, and `UpdatedAt`.

Behavior:

- Save after successful add/remove/reorder and debounce ordinary text changes through the existing coalescing store.
- On first successful thread creation, atomically rekey the pre-thread draft to the returned thread ID before the next save.
- On successful start/steer or queue insertion, clear the matching draft and persist once.
- On thread/project selection, bind `TaskViewModel` to the target draft collections before raising command states.
- On restart, restore only valid managed references. Keep missing references as visible `Unavailable` chips so the user can remove them; do not silently drop them.
- Bound persisted drafts and prune empty drafts older than a short grace period. Never prune a non-empty draft just because it is old.

This also fixes the current loss of unsent prompt text during selection/restart and prevents one thread's pasted image from appearing in another thread's composer.

### 5.6 Model capability validation

Extend `CodexModelOption` with `IReadOnlySet<CodexInputModality>` or an immutable list parsed from `inputModalities`. Unknown future strings should be retained separately or ignored without being treated as image support.

Expose:

```csharp
public bool SupportsImageInput => InputModalities.Contains(CodexInputModality.Image);
```

Rules:

- If `SelectedModel` is loaded, require advertised `image` support whenever a draft has images.
- If the catalog is not loaded and the user has selected no explicit model, allow attachment composition but refresh the catalog before submission. If capability still cannot be established, fail closed with “Reload models to verify image support.”
- Revalidate capability immediately before interactive start, steer, queued dispatch, and manual queue send.
- Capture the model in queued turn options as today, then refresh/re-resolve capability at dispatch as part of the existing P0 queue-hardening work.
- If a queued item's captured model no longer advertises image input, mark it `NeedsAttention`; do not drop images, choose a different model, or skip the queue head.

### 5.7 Queue integration

Evolve queued payloads compatibly:

- Keep `QueuedFollowUpSnapshot.Text` for old settings and human-readable previews.
- Add `Images` or a nested `Input` snapshot. On restore, absent images mean a legacy text-only row.
- Change queue byte validation to account for metadata plus referenced image byte totals; do not count shared stored bytes twice against the global store ceiling, but do enforce the per-item total for user predictability.
- Extend `Enqueue`, `Edit`, `Snapshot`, and `Clone` to preserve attachment order and deep-copy references.
- Add per-image chips/thumbnails to each queue row. During edit, text remains editable and images can be removed/reordered; importing new images into an existing queued item may be deferred unless it falls out naturally from the shared composer control.
- A `Starting` item remains immutable. Its managed objects cannot be orphan-cleaned until it is acknowledged and represented in the turn snapshot.
- `SendQueuedFollowUpNowAsync` and `StartQueuedFollowUpAsync` must resolve all managed references to absolute paths and validate existence, containment, format metadata, total limits, and model capability before marking/sending.
- A missing or invalid image marks the head `NeedsAttention` and preserves the item. It never degrades to text-only.

### 5.8 Conversation and history integration

Add input metadata to `CodexConversationTurn` and `CodexConversationTurnSnapshot` while preserving `UserPrompt` as the compatibility/display text field.

Suggested shape:

```csharp
public string UserPrompt { get; set; } = string.Empty; // legacy/display text
public List<AttachmentReference> UserImages { get; set; } = [];
```

The app-server's current user message content can contain multiple user messages/steers in one turn. For P0, render the initial submitted images beside the user prompt and render later steered images in the existing guidance activity row with thumbnails/count metadata. A later refactor can model every user-message item separately.

History parsing rules:

- Preserve content order in an intermediate `CodexUserInput` list.
- Parse `text`, `image`, and `localImage`; ignore skill/mention as image attachments but keep their text behavior unchanged.
- For `localImage` beneath the managed store, reconstruct the reference from stored metadata or re-inspect it.
- For a pre-existing Codex thread whose `localImage` points outside the managed store, import a bounded copy before persisting a SynthiaCode snapshot. If import is unavailable, show an unavailable external-image placeholder and avoid saving the arbitrary absolute source path in settings.
- Never persist an inline data URL. If a bounded supported data URL appears in history, materialize it through the attachment store; otherwise show an unavailable placeholder.
- During `ReconcileHistory`, do not overwrite locally known image references with an empty parsed list from an older or partial response.

Transcript UX:

- Show a responsive thumbnail strip beneath the user prompt.
- Each preview includes an accessible name, dimensions/size tooltip, Open action, and unavailable state.
- `Open` may launch the managed local copy through the existing user-interaction boundary after validating it still resolves inside the store.
- Do not expose a Copy path action in P0; the managed path is an implementation detail.

### 5.9 Lifecycle and garbage collection

Use reference-aware cleanup rather than age-only cache eviction.

At startup, after settings load and snapshot recovery:

1. Gather every referenced `StorageKey` from composer drafts, queued follow-ups, and conversation turns.
2. Delete stale files in `staging` older than 24 hours.
3. Delete unreferenced object files only after a seven-day orphan grace period.
4. Never delete a referenced object, even when the store exceeds its ceiling.
5. If referenced objects alone exceed the ceiling, refuse new imports and report how the user can free space (remove drafts/attachments or delete chats when that feature exists).

Run the same cleanup after a draft/queue image is removed and after a future permanent thread deletion, but debounce it and keep it off the UI thread. Archiving a thread does not release its references. A failed cleanup is logged without full paths and does not block normal conversation use.

Because objects are content-addressed, reference scanning must operate on storage keys rather than reference IDs. Deleting one of two duplicate attachments must not delete the shared object.

## 6. Detailed user experience

### 6.1 Composer layout

Add an attachment strip between the text box and the existing model/permission/action row.

Each chip/card shows:

- a 64-96 px thumbnail;
- sanitized display name with ellipsis;
- image dimensions and encoded size;
- Remove button;
- keyboard reorder controls or an accessible context action; and
- an error overlay for unavailable/invalid content.

The strip wraps horizontally in normal layout and becomes a horizontal scroller in compact layout. The text box keeps its present minimum/maximum height. Adding thumbnails must not push the Send and Cancel controls off-screen at the 800 px minimum window width.

The `Attach images` button should remain available during an active turn because both Queue and Steer support image inputs. Model/options controls can remain disabled during a turn as they are today.

### 6.2 Focus, keyboard, and accessibility

- `Ctrl+V` attaches clipboard images only when a composer text box is focused; normal text paste remains unchanged.
- Keep `Ctrl+Enter` and `Ctrl+Shift+Enter` behavior. Both commands validate the current draft parts rather than only `string.IsNullOrWhiteSpace`.
- After adding images, return focus to the originating text box.
- Removing a chip moves focus to the next chip, previous chip, or Attach button.
- Every thumbnail has an automation name such as `Image attachment 2 of 3, checkout.png, 1440 by 900`.
- The drop target announces `Drop 2 images to attach` and validation failures through a status/live region, not color alone.
- Escape while dragging or while the picker is cancelled makes no draft changes.

### 6.3 Error messages

Use actionable categories:

- `checkout.pdf is not a supported image. Use PNG, JPEG, WebP, or non-animated GIF.`
- `diagram.png is 24.3 MiB; the per-image limit is 20 MiB.`
- `screenshot.png could not be decoded as a valid PNG.`
- `This model does not accept image input. Remove the images or choose an image-capable model.`
- `An attached image is no longer available. Remove it or attach it again.`
- `Attachment storage is full. Remove unused draft or chat images before attaching another image.`

Status and telemetry must use the sanitized leaf display name at most, never an original full path, content hash, clipboard contents, prompt text, or image bytes.

## 7. Implementation phases

### Phase 0 - Lock the contract with failing tests

1. Add v2 schema fixture tests for mixed text/localImage input in `turn/start` and `turn/steer`.
2. Add a model-list fixture that contains `inputModalities: ["text", "image"]` and a text-only model.
3. Add backward-compatibility fixtures for settings with no attachment fields and queue/turn snapshots that contain only `Text`/`UserPrompt`.
4. Add corrupt, wrong-extension, animated GIF, oversized, duplicate, and traversal storage fixtures.

Exit criterion: tests fail because typed input, capability, reference, and store APIs do not exist.

### Phase 1 - Typed protocol parts and model capability

1. Add `CodexUserInput` variants and change start/steer request models.
2. Centralize exact JSON serialization in `CodexAppServerClient`.
3. Parse `inputModalities` into `CodexModelOption` and expose image support.
4. Parse all supported `userMessage.content` input variants into an intermediate representation.
5. Keep text-only convenience factories so unrelated tests and callers migrate without duplicating JSON logic.

Exit criterion: exact protocol tests pass for text-only, image-only, mixed, ordered multi-image, invalid-empty, and unsupported-type inputs.

### Phase 2 - Managed attachment store and preview service

1. Add Core interfaces/models and Infrastructure implementations.
2. Implement streaming import, content hashing, format/dimension/frame validation, atomic finalization, deduplication, containment checks, and cleanup.
3. Implement clipboard-stream PNG import without a source filename/path dependency.
4. Implement bounded asynchronous WPF thumbnail decoding with `OnLoad` and frozen results.
5. Wire services through `AppServices` and test doubles through the existing manual test composition.

Exit criterion: storage tests prove copied bytes survive source deletion/replacement, duplicate imports share an object, invalid inputs leave no committed object, and cleanup cannot escape the store root or remove referenced objects.

### Phase 3 - Thread-scoped drafts and acquisition UI

1. Add `ComposerDraftWorkspace`, persisted draft snapshots, clone support, and project/thread rekey behavior.
2. Bind `TaskViewModel` to a scoped idle or active draft rather than global attachment state.
3. Add Attach/Remove/Reorder/Open commands and derived validation/command state.
4. Add the preview strip and responsive/accessibility resources in `TaskView.xaml` and theme dictionaries.
5. Add picker, paste, and drag/drop adapters in the WPF App layer. All three call the same import command.
6. Persist attachment mutations immediately and text changes through coalescing/debounce.

Exit criterion: switching between threads and idle/active composer modes never cross-contaminates draft images; restart restores draft metadata/previews; normal text paste and shortcuts still work.

### Phase 4 - Start, steer, and queue end-to-end

1. Replace prompt-string validation with draft-part validation in `SubmitPromptAsync`, `CanSteerTurn`, and command state.
2. Resolve managed references immediately before requests and pass typed parts to app-server.
3. Preserve the complete draft on validation or request failure; clear only after acknowledgement.
4. Extend queue models, clones, bounds, rows, edit behavior, send-now, and background dispatch.
5. Add capability and attachment revalidation before queued dispatch; use `NeedsAttention` for failures.

Exit criterion: picker, paste, and drop images all work through new turns, active steering, queueing, manual queue send, and completion-driven background dispatch.

### Phase 5 - Transcript/history persistence and cleanup

1. Add image references to live turn and snapshot models.
2. Render user-message and guidance image previews.
3. Reconcile app-server localImage/data-image history through the managed store without persisting arbitrary paths or data URLs.
4. Deep-copy attachment fields through `AppSettingsSnapshot`, `ThreadStore`, fork, resume, and background persistence paths.
5. Add startup/orphan cleanup, store-ceiling behavior, privacy-safe diagnostics, and missing-file placeholders.

Exit criterion: sent images remain previewable after restart/resume and source-file deletion; legacy settings still load; orphan cleanup is reference-safe.

### Phase 6 - Live verification and parity update

1. Generate/compare schemas with the installed Codex version if the repo's schema refresh workflow requires it; do not hand-edit generated schema files.
2. Run a live app-server smoke test with a small PNG through `turn/start` and `turn/steer` on an image-capable model.
3. Run a live queue/restart smoke: queue an image follow-up, restart SynthiaCode, explicitly send it, and verify the managed path still resolves.
4. Test Windows Explorer drag/drop, copied screenshot pixels, copied Explorer files, multi-select picker, high-DPI preview, light/dark themes, and compact width.
5. After all acceptance criteria pass, change the feature-parity matrix/backlog status from Missing to Full or Near with any deliberate residual gap.

## 8. File-by-file change map

| Area | Files | Planned change |
| --- | --- | --- |
| Protocol domain | `src/SynthiaCode.Core/Codex/AppServer/CodexAppServerModels.cs` | Add typed user-input union; change turn request payloads; add input-modality model data. |
| Conversation domain | `src/SynthiaCode.Core/Codex/AppServer/CodexConversationTurn.cs` | Store/clone user image references and compatibility display text. |
| Thread projection | `src/SynthiaCode.Core/Codex/AppServer/CodexThreadService.cs` | Begin/reconcile/snapshot turns with multimodal input; retain guidance image metadata. |
| Queue domain | `src/SynthiaCode.Core/Codex/AppServer/CodexFollowUpQueue.cs` | Persist and validate attachment references; preserve them through edit/state transitions. |
| New attachment domain | `src/SynthiaCode.Core/Attachments/*` | Limits, references, validation results, store/preview contracts, draft workspace. |
| Settings | `src/SynthiaCode.Core/Settings/AppSettings.cs` | Add backward-compatible composer draft and attachment metadata fields. |
| Snapshot/store clones | `AppSettingsSnapshot.cs`, `ThreadStore.cs` | Deep-copy new draft, queue, and turn attachment metadata. |
| Protocol transport | `src/SynthiaCode.Infrastructure/Codex/CodexAppServerClient.cs` | Serialize ordered input lists; parse model modalities and multimodal history. |
| New local store | `src/SynthiaCode.Infrastructure/Attachments/*` | Atomic content-addressed storage, validation, path resolution, reference-aware cleanup. |
| Composition | `src/SynthiaCode.App/AppServices.cs`, `App.xaml.cs` | Construct and inject attachment services. |
| WPF acquisition | `src/SynthiaCode.App/Services/*` | Image picker, clipboard reader/encoder, and preview service adapters. |
| Orchestration | `src/SynthiaCode.App/ViewModels/MainViewModel.cs` | Scope drafts, acquire/import inputs, build requests, revalidate queue items, persist/clear state. |
| Composer VM | `src/SynthiaCode.App/ViewModels/TaskViewModel.cs` | Attachment collections/commands, validation, capability state, labels, accessibility summaries. |
| Composer UI | `src/SynthiaCode.App/Views/TaskView.xaml(.cs)` | Preview strip, Attach button, paste interception, handled drag/drop routing, drop visual state. |
| Themes | `src/SynthiaCode.App/Themes/*.xaml` | Attachment/drop/error surface resources for light and dark themes. |
| Tests | `src/SynthiaCode.Tests/AttachmentInputTests.cs` plus existing suites | Domain/store/protocol/orchestration/persistence/WPF regression coverage. |
| Backlog docs | `feature_parity.md` | Update only after verified implementation. |

Prefer a focused `AttachmentInputTests.cs` suite registered from `Program.cs`; keep exact app-server wire assertions near existing protocol tests and responsive/accessibility assertions near `ResponsiveLayoutTests`.

## 9. Test matrix

### 9.1 Domain and protocol

- Text-only request remains byte-for-byte compatible in meaning.
- Image-only request is accepted and emits one `localImage` item.
- Mixed input serializes text first and images in preview order.
- Start and steer share the same serialization helper.
- Empty/whitespace-only text with no images is rejected.
- Unknown input subtype fails before transport write.
- `model/list` parses image-capable, text-only, missing/default modalities, and unknown future modalities safely.
- Restored user-message parsing retains text/localImage order and does not persist inline data URLs.

### 9.2 Storage and safety

- PNG/JPEG/WebP/non-animated GIF signatures and dimensions are accepted.
- Animated GIF, corrupt bytes, extension/signature mismatch, unsupported BMP/SVG/PDF, excessive bytes, dimensions, or frame count are rejected.
- Source deletion or replacement after import does not change the stored object.
- Duplicate bytes deduplicate across filenames and drafts.
- Concurrent identical imports converge without corrupting the object.
- Interrupted import leaves at most a stale private staging file eligible for cleanup.
- Traversal/rooted storage keys and paths outside the attachment root are rejected.
- Cleanup preserves referenced duplicates and deletes only old unreferenced objects.
- The store ceiling blocks new imports without evicting referenced content.

### 9.3 Composer and persistence

- Picker cancel is a no-op; multi-select preserves order.
- Clipboard bitmap becomes a managed PNG; copied image files use file import.
- Clipboard text still pastes normally.
- Busy clipboard and failed image encode preserve the draft.
- Drop accepts file-drop images, uses Copy, and reports partial rejection.
- Add/remove/reorder updates Send state and persists immediately.
- Image-only draft can send; text-only behavior remains unchanged.
- Thread switch, project switch, new-thread draft, active/idle transition, and restart isolate drafts correctly.
- Changing to a text-only model marks images invalid without deleting them.
- Unavailable restored images render placeholders and block submission.

### 9.4 Queue and lifecycle

- Queued mixed input survives settings round-trip and immutable save snapshots.
- Background thread dispatch uses its own ordered images even when another thread is selected.
- Successful acknowledgement moves image ownership from draft/queue representation to the turn snapshot before cleanup can run.
- Rejected start/steer keeps draft/queue references.
- Missing/corrupt/capability-stale queue head becomes `NeedsAttention` and pauses FIFO.
- `Starting` items restore as `NeedsAttention` with images intact.
- Queue limits include referenced encoded bytes and never silently drop excess images.

### 9.5 Transcript and WPF

- User-turn previews render in light/dark themes and compact/normal layout.
- Thumbnails do not keep files locked after decode.
- Missing/corrupt preview cannot crash transcript scrolling.
- Remove/reorder/open controls have accessible names and keyboard focus behavior.
- `Ctrl+Enter`, `Ctrl+Shift+Enter`, Escape, model flyout keys, and Jump to latest remain correct.
- Drag/drop handlers receive file events even when the inner TextBox marks them handled.
- No full path, prompt text, image bytes, data URL, or content hash appears in log/test diagnostic output.

### 9.6 Live smoke matrix

| Scenario | Expected result |
| --- | --- |
| Start with text + PNG | Model can describe the correct image; transcript shows the thumbnail. |
| Steer active turn with pasted screenshot | Same active turn accepts guidance and image; no new turn starts. |
| Queue two image follow-ups in thread A, work in thread B | Thread A drains FIFO after success with correct image order. |
| Restart with queued/draft image after deleting original source | Managed copy remains available; no source-path dependency. |
| Select text-only model with attached image | Send is blocked locally with a capability message; no request is written. |
| Delete managed object manually, then restore | Placeholder appears; submission/dispatch fails closed and preserves metadata. |

## 10. Acceptance criteria

All of the following must be true before the backlog item is called complete:

1. Picker, clipboard-image paste, copied-file paste, and Explorer drag/drop use one import/validation pipeline.
2. Start and steer emit schema-correct ordered input parts, including image-only input.
3. The selected model's advertised image modality is parsed and enforced before every foreground/background request.
4. Composer previews can be opened, reordered, and removed; invalid/unavailable state is visible and accessible.
5. Draft attachments are scoped to the correct project/thread and do not appear in another mounted or selected composer.
6. Queue entries persist text, images, captured options, order, and recovery state; failures never degrade to text-only.
7. Turn snapshots and app-server history preserve image references and render them after restart/resume.
8. Original files may be moved, replaced, or deleted after import without changing the queued/sent bytes.
9. `settings.json` contains only bounded metadata and relative managed storage keys, never raw bytes, data URLs, or arbitrary original full paths.
10. Imports, path resolution, and cleanup enforce size/format/containment rules and cannot overwrite workspace or user files.
11. Legacy settings, text-only requests, existing queue behavior, multi-turn restoration, and parallel thread routing remain compatible.
12. Automated suites, `dotnet build SynthiaCode.sln`, `git diff --check`, and the live Codex smoke matrix pass.

## 11. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Image bytes make settings large or corrupt saves. | Store content as app-owned files; persist small references through existing atomic settings snapshots. |
| Original file changes between preview and send. | Copy once during import and send the immutable managed copy. |
| Decoder bombs or huge images freeze WPF. | Enforce encoded/dimension limits, inspect off the UI thread, and decode bounded thumbnails. |
| Clipboard/drop code breaks normal text editing. | Scope interception to focused composers and fall through to standard Paste when no supported image data exists. |
| Global draft state leaks attachments across parallel chats. | Key drafts by project/thread/mode and rebind explicitly on selection. |
| Model changes while an image is queued. | Capture the model, refresh/revalidate modalities at dispatch, and mark the head `NeedsAttention`. |
| Content-addressed cleanup deletes a shared image. | Reference-scan by storage key across every persisted owner before deletion. |
| App-server history contains data URLs or external paths. | Materialize bounded bytes into the managed store; never persist data URLs/arbitrary source paths. |
| Remote HTTP image behavior differs from older docs/schema shape. | P0 emits only `localImage`; cover current maintained app-server behavior with live smoke tests. |
| Constructor and snapshot changes cause a wide regression surface. | Add compatibility factories/optional fields, migrate one path at a time, and preserve legacy `Prompt`/`Text` display fields during transition. |

## 12. Recommended implementation sequence

Implement in vertical, testable slices rather than starting with the WPF picker:

1. Typed input parts + exact protocol tests + model modalities.
2. Managed attachment store + validation/cleanup tests.
3. Thread-scoped draft state + settings compatibility.
4. One end-to-end picker-to-`turn/start` path.
5. Paste and drag/drop using the proven importer.
6. `turn/steer` and queued follow-up integration.
7. Transcript/history restoration and reference cleanup.
8. Responsive/accessibility polish and live Codex validation.

This order proves the irreversible contracts—wire shape, file ownership, containment, and persistence—before adding multiple acquisition gestures. It also keeps each pull request reviewable and leaves text-only behavior working after every phase.

## 13. Deliberate follow-ups outside P0

- Arbitrary documents and source-file attachment semantics.
- Remote image URL download or authenticated cloud-file import.
- Built-in screenshot/appshot capture.
- Image annotation, crop, compression, or editing.
- Inline caret-positioned multimodal parts in a rich-text composer.
- Full artifact/file viewer and export/share workflow.
- User-configurable attachment limits/cache location.
- Permanent chat deletion UI that releases stored attachment references immediately.

None of these are required to complete the core local coding image-input experience described by `feature_parity.md`.
