# Generated image edit implementation plan

## Goal

Let a user continue from an image produced by the Codex `imagegen` skill without manually locating and reattaching the file.

## User flow

1. A completed `imageGeneration` item continues to render as a safe local preview.
2. Its card exposes a keyboard-accessible **Edit image** action when the file is available and the conversation is idle.
3. Activating the action imports the generated file through the existing validated, app-managed attachment store.
4. The composer is primed with `$imagegen Edit this image: ` so the user can describe the change and explicitly submit it.
5. Import failures leave the conversation intact and surface a concise status message.

## Implementation

- Add an image-edit command surface to `MarkdownTextBlock` and bind it only on assistant response content.
- Route the path through `TaskViewModel`, where command availability follows turn state and file availability.
- In `MainViewModel`, reuse `IAttachmentStore.ImportFileAsync` and the normal composer attachment collection; do not bypass image validation or persist the original generated path as a draft attachment.
- Preserve the existing preview/open behavior and app-server image-generation event model.

## Verification

- Renderer coverage verifies the action label, accessibility name, path routing, and coexistence with preview/open.
- View-model coverage verifies that editing is unavailable while a turn runs or when the generated file is missing.
- Run the behavioral suite and Debug/Release builds, plus `git diff --check`.
