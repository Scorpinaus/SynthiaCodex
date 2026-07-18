# Phase 5E Turn Transcript Separation

**Status:** Implemented - 17 July 2026

This follow-up makes conversation ownership visually unambiguous without changing the turn protocol, persistence format, or thread lifecycle.

## Presentation

- [x] Render every conversation turn inside its own bordered, spaced container.
- [x] Give user messages, activities, and assistant responses separate surfaces.
- [x] Remove `ListBoxItem` selection chrome so it cannot visually merge adjacent turns.
- [x] Hide the activity surface when a turn has no activity.
- [x] Keep running activity expanded and completed historical activity collapsed.
- [x] Preserve transcript virtualization, follow-latest behavior, and the fixed composer.

## Themes and accessibility

- [x] Add dedicated light and dark conversation brushes.
- [x] Retain role labels, status, timestamp, wrapping, live response announcements, and automation names.
- [x] Keep message insets modest enough for narrow layouts and text scaling.

## Verification

- [x] Add regression coverage for activity visibility, summary, and expansion state.
- [x] Build the complete solution in Release with zero warnings and errors.
- [x] Run the complete behavioral suite (76 passing tests).
- [ ] Perform interactive light/dark transcript and scrolling verification.
