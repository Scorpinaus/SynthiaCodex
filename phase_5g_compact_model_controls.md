# Phase 5G Compact Model, Reasoning, and Fast Controls

**Status:** Implemented - 18 July 2026

This phase removes the permanent Run settings expander and replaces it with one compact, ChatGPT-style composer control for selecting the model, reasoning effort, and Fast mode. The control is driven by the effective catalog returned for the authenticated Codex session so it remains correct across ChatGPT plans, API-key accounts, workspace policies, staged rollouts, and future catalog changes.

## Goals

- Remove the Run settings expander from the task composer.
- Keep the selected model, reasoning effort, and Fast state visible without crowding the composer.
- Present friendly model names and capability descriptions returned by `model/list`.
- Filter reasoning efforts and service tiers from the selected model's advertised capabilities.
- Reflect the effective access of the authenticated account without maintaining a hardcoded plan matrix.
- Preserve model, reasoning, and service-tier choices across restarts while safely reconciling stale selections.
- Keep thread creation, resume, fork, follow-up, guidance, cancellation, recovery, and existing settings files backward compatible.

## Product and entitlement rules

### Source of truth

Use the authenticated app-server session as the authority:

1. Call `account/read` to identify the authentication boundary and, for ChatGPT accounts, the reported `planType`.
2. Call `model/list` with `includeHidden: false` to obtain the effective model catalog for that session.
3. Treat each returned model's `supportedReasoningEfforts`, `defaultReasoningEffort`, and `serviceTiers` as its selectable capabilities.
4. Use `planType` only for context, diagnostics, and explanatory copy. Do not infer model or reasoning access from a client-side Free/Go/Plus/Pro/Business/Enterprise table.
5. For API-key accounts, do not show a ChatGPT plan label; availability follows the API organization and project associated with the key.

This distinction matters because effective access can also depend on client surface, workspace policy, account rollout, preview access, model retirement, and API project permissions.

### Capability interpretation

- A model returned by the visible catalog is selectable unless the app-server reports otherwise.
- `hidden` models are excluded by the request and defensively filtered if returned.
- `supportedReasoningEfforts` is the complete reasoning list shown for the selected model.
- `defaultReasoningEffort` is the fallback when the saved effort is missing or unsupported.
- A service tier whose ID is `fast` enables the Fast toggle for that model.
- `availabilityNux.message` is informational unless the protocol later adds an explicit unavailable state; it must not be treated as an authorization Boolean.
- `upgradeInfo` describes model migration and must not be used as an entitlement flag.
- Rate-limit and credit data can be displayed as usage context but must never authorize or filter a model.

## Recommended interaction

### Closed composer

Replace the current composer and side-button arrangement with a prompt area and compact footer:

```text
┌────────────────────────────────────────────────────────────────┐
│ Describe a task...                                             │
│                                                                │
├────────────────────────────────────────────────────────────────┤
│ [ GPT-5.6 Sol · High · Fast ▾ ]                   [ Run task ] │
└────────────────────────────────────────────────────────────────┘
```

The compact summary uses the catalog display name and current choices:

- `GPT-5.6 Sol · High`
- `GPT-5.6 Sol · High · Fast`
- `Loading models...` during initial catalog loading
- The saved protocol values when the catalog is temporarily unavailable

The full model name, descriptions, account boundary, and tier explanation remain available in a tooltip or accessible description.

### Main flyout

Clicking the compact control opens a transient flyout above the composer:

```text
Model options                                      ChatGPT Plus
───────────────────────────────────────────────────────────────
Model                                      GPT-5.6 Sol       ›
Reasoning                                  High              ›
Fast mode                                  [ On ]
                                             Faster responses;
                                             higher credit use.
───────────────────────────────────────────────────────────────
Refresh models
```

- Model and Reasoning open drill-down pages inside the same flyout.
- Fast is an immediate toggle rather than a separate model.
- Selection is immediate; there is no Apply button.
- The plan label is contextual and omitted for API-key accounts.
- Server-provided service-tier descriptions are preferred over hardcoded billing copy.

### Model drill-down

```text
‹ Model
───────────────────────────────────────────────────────────────
✓ GPT-5.6 Sol
  Strong coding model

  GPT-5.6 Luna
  Lighter model for higher-volume work
```

Use `displayName` and `description`, retain the protocol `model` value internally, and show an informational catalog message when one is supplied.

### Reasoning drill-down

```text
‹ Reasoning for GPT-5.6 Sol
───────────────────────────────────────────────────────────────
  Low       Faster responses for straightforward tasks
  Medium    Balanced reasoning and latency
✓ High      Deeper reasoning for complex work
```

Only the selected model's advertised reasoning efforts appear. Descriptions come from the catalog.

### Unsupported Fast state

Keep the Fast row visible but disabled when the selected model does not advertise a Fast tier:

```text
Fast mode                                  [ Off ]
Fast is not available for GPT-5.6 Luna on this account.
```

This provides clearer discovery than silently hiding the feature while still preventing an invalid request.

### Active-turn behavior

- Disable the model control while the selected turn is running so changes cannot be mistaken as affecting the in-flight request.
- Continue swapping the prompt area to guidance mode with Guide task and Cancel actions.
- Re-enable the selector when the turn reaches a terminal state; its values apply to the next follow-up.

## State and reconciliation rules

### Initial load

1. Restore saved model, reasoning, and service-tier preferences.
2. Establish the app-server session and read the account.
3. Load the visible model catalog.
4. Select the saved model when it is still returned.
5. Otherwise select the catalog model marked `isDefault`.
6. Otherwise select the first visible catalog entry.
7. Preserve the saved reasoning effort when supported by the selected model.
8. Otherwise select `defaultReasoningEffort`.
9. Otherwise select the first advertised reasoning effort.
10. Enable Fast only when the selected model advertises a `fast` service tier.

### Model change

When the user selects another model:

- Rebuild the reasoning list from that model.
- Preserve the current effort when supported.
- Otherwise fall back to the new model's default effort.
- Recalculate Fast availability.
- Turn Fast off if the new model does not support it.
- Update and persist the compact summary immediately.

### Account or catalog change

Invalidate and reload the catalog after:

- Sign-in or sign-out.
- `account/updated` notifications.
- A change in account type, ChatGPT identity, or plan type.
- App-server reconnection or Codex installation change.
- An explicit Refresh models action.

Do not persist a capability catalog across identities. Persist only the user's selected protocol values, then revalidate them against the new session.

### Stale selection during submission

If `turn/start` rejects a model, effort, or service tier as unavailable:

1. Preserve the prompt.
2. Refresh the account and catalog once.
3. Reconcile the selections.
4. Explain which option changed.
5. Require the user to confirm or resubmit rather than silently running a different model.

Do not create an automatic retry loop for authentication, quota, or general server errors.

### Catalog failure

- Keep the composer usable when `model/list` fails.
- Retain the last saved selection as a protocol fallback.
- Show a non-blocking catalog warning and a Retry action.
- Do not clear a previously loaded catalog when refresh fails.
- Disable Fast unless its capability was confirmed by the current authenticated catalog.

## Protocol and domain changes

### Typed model metadata

Extend `CodexModelOption` so the application retains:

- `Id`
- `Model`
- `DisplayName`
- `Description`
- `IsDefault`
- `Hidden`
- `DefaultReasoningEffort`
- typed reasoning options with value and description
- typed service-tier options with ID, name, and description
- optional availability message
- optional upgrade information only where useful to the picker

Introduce small app-neutral records such as `CodexReasoningOption` and `CodexServiceTierOption` rather than exposing JSON nodes to the WPF layer.

### Model-list parsing

Update `CodexAppServerClient.ListModelsAsync` to:

- Parse the complete capability metadata.
- Preserve display order returned by the catalog unless a deliberate product sort is later specified.
- Handle `nextCursor` pagination defensively.
- Deduplicate by protocol model ID/value without losing the default marker.
- Continue sending `includeHidden: false`.

### Service-tier override

Extend `CodexTurnStartRequest` with an explicit service-tier selection capable of representing:

- Inherit: omit `serviceTier`.
- Standard/off: send `serviceTier: null` to clear a prior Fast override.
- Fast: send `serviceTier: "fast"`.

The UI remains a two-state toggle, while the internal three-state representation preserves correct protocol semantics for untouched settings and explicit Fast-off transitions. Verify explicit-null clearing against the installed supported app-server version.

### Consistent model application

Use the selected model for:

- New thread creation.
- Thread resume.
- Thread fork.
- Initial turn submission.
- Follow-up turn submission.

Reasoning effort and service tier remain turn-level selections. Guidance sent to an active turn does not change them.

## View-model changes

Replace string-only picker state with capability-aware state in `TaskViewModel`:

- `ObservableCollection<CodexModelOption> ModelOptions`
- `CodexModelOption? SelectedModel`
- `ObservableCollection<CodexReasoningOption> ReasoningOptions`
- `CodexReasoningOption? SelectedReasoning`
- `bool IsFastModeEnabled`
- `bool IsFastModeAvailable`
- `string ModelSelectionSummary`
- `string FastModeDescription`
- `string? AccountPlanLabel`
- `bool IsModelCatalogLoading`
- `string? ModelCatalogError`
- `ComposerOptionsPage OptionsPage`

Add commands for opening the flyout, navigating between pages, selecting model/reasoning, toggling Fast, returning to the main page, refreshing the catalog, and retrying a failed load.

Keep popup placement and focus restoration in the view. Keep selection, filtering, fallback, and availability rules in the view model so they can be tested without WPF automation.

## Persistence and compatibility

Retain the stable settings properties:

- `LastModelOverride`
- `LastReasoningEffortOverride`

Add a service-tier preference that can represent inherit, standard, and fast. Copy it through `AppSettingsSnapshot` and use a default that allows existing settings files to deserialize without migration.

Persist selection changes through the existing coalescing settings store. Never persist model descriptions, supported efforts, service-tier availability, plan entitlements, or the complete catalog.

## WPF implementation

### Task composer

- Remove the Run settings `Expander` and Load models button from `TaskView.xaml`.
- Convert the composer card into a prompt/guidance area plus footer row.
- Add a compact summary button and keep Run task/Send follow-up on the right.
- Preserve Ctrl+Enter, text wrapping, prompt recovery, guidance mode, cancellation, and focus behavior.

### Flyout

Use an anchored WPF `Popup` rather than the existing text-only `ContextMenu` template:

- `Placement="Top"`
- `StaysOpen="False"`
- bounded height with scrolling
- main, model, and reasoning pages
- Escape closes
- Backspace or the Back action returns to the main page
- selection restores focus to the compact control or composer
- app/window deactivation closes the popup

### Styling and accessibility

Add palette-driven styles for the compact selector, flyout surface, option rows, selected indicators, descriptions, toggle, loading/error states, and Back/Refresh actions.

Provide:

- Automation names for the selector, plan context, model list, reasoning list, and Fast toggle.
- Toggle and selected-state automation semantics.
- Visible keyboard focus.
- At least 32 device-independent pixels per interactive row.
- Text wrapping for descriptions and availability messages.
- Correct light, dark, System theme, high-DPI, text-scale, and narrow-window behavior.

## Implementation sequence

1. Extend core model, reasoning, service-tier, and request records.
2. Parse full `model/list` metadata and service tiers in Infrastructure.
3. Add service-tier serialization and explicit clearing behavior.
4. Add account-aware catalog loading and invalidation in the application layer.
5. Introduce capability-aware selection and reconciliation state in `TaskViewModel`.
6. Update persistence and backward-compatible snapshots.
7. Replace Run settings with the compact composer footer.
8. Implement the anchored multi-page flyout and keyboard/focus behavior.
9. Add light/dark styles and accessibility metadata.
10. Add protocol, view-model, persistence, recovery, and presentation-state tests.
11. Update `docs/current-architecture.md` after implementation.
12. Run the complete test, Release build, portable publish, and interactive visual QA gates.

## Verification plan

### Protocol tests

- Parse display names, descriptions, default efforts, reasoning descriptions, and service tiers.
- Exclude or defensively filter hidden entries.
- Handle model-list pagination and duplicate model records.
- Serialize the selected model and reasoning effort.
- Serialize `serviceTier: "fast"` when enabled.
- Serialize explicit null when Fast is turned off after an override.
- Omit the service-tier property for inherited untouched state.

### View-model tests

- Restore valid persisted selections.
- Fall back to the catalog default when the saved model is unavailable.
- Filter reasoning efforts after every model change.
- Preserve a reasoning effort supported by both models.
- Fall back to the new model's default effort when needed.
- Enable Fast only for an advertised `fast` tier.
- Turn Fast off after switching to an unsupported model.
- Keep plan type informational rather than using it to authorize options.
- Invalidate the catalog after account changes and reconnection.
- Preserve cached catalog data on refresh failure.
- Preserve the prompt and reconcile after a stale-capability rejection.

### Persistence tests

- Round-trip model, reasoning, and service-tier preferences.
- Deep-copy the new preference through `AppSettingsSnapshot`.
- Load legacy settings with no service-tier property.
- Do not persist account entitlements or catalog capability data.

### Interactive QA

- ChatGPT account with an available plan label.
- API-key account with no ChatGPT plan label.
- Models with different reasoning lists.
- Models with and without Fast.
- Sign-out/sign-in to another identity.
- Offline catalog refresh and retry.
- Active-turn disabled state and next-follow-up behavior.
- Keyboard-only navigation, Escape/Back behavior, focus restoration, and screen reader names.
- Light, dark, System, 820-by-700 compact layout, text scaling, and high DPI.

### Release gates

```powershell
dotnet test NativeCodexAssistant.sln
dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj
.\scripts\publish-portable.cmd
```

The Release build must complete with zero warnings and errors, all behavioral assertions must pass, and the portable application must be launch-verified.

## Acceptance criteria

- Run settings and the visible Load models button are removed.
- One compact selector displays the exact configuration that the next turn will send.
- Model options reflect the effective authenticated catalog, not a hardcoded plan matrix.
- The ChatGPT plan is shown only as context and API-key accounts are identified correctly.
- Friendly model names and descriptions come from the catalog.
- Reasoning efforts are filtered per selected model.
- Fast is enabled only for models advertising a Fast service tier.
- Invalid saved selections reconcile predictably without silently changing a submitted task.
- Account changes, reconnects, refreshes, and stale-catalog errors are handled safely.
- Model, reasoning, and service-tier preferences survive restart without persisting entitlements.
- The selector cannot misrepresent changes as affecting an active turn.
- Existing thread, conversation, guidance, cancellation, recovery, theme, and keyboard behavior remains intact.
- The full behavioral suite, Release build, portable publish, and interactive visual QA gates pass.

## Implementation result

- Added typed model descriptions, default/supported reasoning efforts, availability copy, service tiers, pagination, and duplicate filtering to the app-server catalog boundary.
- Added inherit/standard/fast turn semantics and backward-compatible service-tier persistence.
- Added account-aware catalog loading, invalidation, selection reconciliation, immediate preference saving, and consistent model application for thread and turn lifecycle requests.
- Replaced Run settings with the compact composer summary and anchored main/model/reasoning flyout.
- Added active-turn disabling, plan context, Fast availability explanations, loading/error states, keyboard Back/Escape handling, focus restoration, and automation names.
- Added Phase 5G protocol, selection, persistence, and responsive WPF regression coverage.
- Resolved a catalog-refresh defect where reused model record references skipped reasoning reconciliation.
- Verified all 98 behavioral assertions in Debug and Release.
- Built the complete solution in Debug and Release with zero warnings and errors.
- Published and launch-verified the self-contained `win-x64` portable application.

## Non-goals

- Maintaining a client-side plan-to-model or plan-to-reasoning matrix.
- Editing Codex `config.toml` from this flyout.
- Displaying or predicting subscription quotas from plan names.
- Treating rate limits as model authorization.
- Adding approval, sandbox, personality, voice, or attachment controls to this phase.
- Implementing every catalog service tier beyond the requested Fast toggle.
