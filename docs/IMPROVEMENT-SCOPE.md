# Focused prototype improvement scope

## Approved outcome

Improve the reliability of the existing two-account Windows prototype before broader architecture work. The owner approved small, behavior-preserving changes and observable test seams in this session. This is not authorization to claim production readiness or live acceptance.

## Shared language

- **Inspection**: a read-only structural survey. It neither walks inventory nor authorizes Connect.
- **Refresh**: the bounded inventory walk in the owning account page.
- **Complete**: recognized inventory with proven completeness, not merely a successful script invocation.
- **Partial**: recognized rows without proven completeness. Rows may be useful, but cannot authorize Connect.
- **Unavailable**: the current page does not provide a recognized usable inventory.
- **Reload required**: the inventory operation's outcome is uncertain; do not retry a possibly active page walk blindly.
- **Cached/last-read**: historical inventory preserved for display, never connection authorization.
- **Inspection timeout**: stop waiting for a read-only script, without pretending WebView2 cancelled it. Prevent additional scripts while the original remains outstanding.

## Vertical slices and interfaces

1. Inspection results: a nonresponding account cannot prevent the other account's survey; bounded waiting; no overlapping underlying inspections per pane; late faults are observed; navigation invalidates the survey; failures reveal no exception payload.
2. Refresh outcomes: carry one typed classification from the account pane to the UI; remove redundant enums and contradictory outcome flags without changing safety decisions.
3. Inventory identity and cache: review completeness, duplicate preservation, stale-result handling and connection revalidation. Apply only demonstrated defects with a failing behavioral regression first; record broader limitations separately.

Tests observe returned results, preserved inventories and permitted/refused actions. Synthetic fixtures must be clearly invented. Windows-only behavior is verified on hosted Windows runners, followed by owner live acceptance.

## Preserved decisions

Two accounts, JSON cache, official account-isolated WebView2 authentication and guarded official-client handoff remain the prototype scope. No private API calls, credential extraction, live account access by agents, guessed identifiers, or relaxed completeness/connection checks. MVVM, N accounts, provider modularization and SQLite remain future work, not prerequisites for these slices.

## Follow-up requiring its own tested slice

The inventory review found an existing fail-closed actionability gap: pagination collects a flat list and restores the first page, but Connect revalidates against currently rendered rows. A later-page selection can therefore return `no-row` or `identity-mismatch`. This is not proof of wrong-target activation. Page-aware locating/revalidation needs a deterministic pager/action regression before implementation; do not weaken identity checks or invent numeric identifiers. Cache and identity review did not establish a new unsafe activation or cache defect.

## Acceptance and delivery

Run focused RED/GREEN tests for new behavior, existing policy suites, the WPF build, a scoped defect review and separate Matt Pocock Spec/Standards assessment. Verify the exact commit on Windows and verify packaged bytes before offering a new testing ZIP. Synthetic proof does not establish live inventory or native session success.
