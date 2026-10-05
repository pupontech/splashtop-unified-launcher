# Roadmap and gates

## Status

**Phase 0 — current-documentation research: complete (2026-10-04).** Findings and ToS risks are in `RESEARCH-2026-10-04.md`.

**Owner direction (2026-10-05):** defer live URI-behavior testing until later and start code. The currently authorized source scope is a platform-neutral Core foundation only: normalized domain models/identity, pure merge/query/filter logic, and unit tests with synthetic data. This bounded foundation must not access Splashtop, WebView2, credentials, or account/device data.

**Phase 1 — still blocked on owner validation.** URI behavior is deferred as requested. No URI, opaque shortcut value, account sign-in, or real machine has been tested. The human gates and Phase 5 checkpoint still govern all Splashtop integration work.

Do not implement or dispatch WebView2 hosting/profile authentication, console parsing/providers, direct or web-console connection, CSV import, SQLite persistence, WPF UI, or packaging before the corresponding validation gates and the required Phase 5 owner approval. Core-only work does not complete or waive Phases 1–5.

## Dependency chain

### Phase 1 — documented URI, Account A (HUMAN GATE)
On the owner's Windows device, with the official installed (not portable/Store) Business app and a known Account A machine chosen by the owner, test only Splashtop's documented remote-session URI format using the owner's own email and that machine's MAC locally. Ensure the Business app is authenticated and “Stay logged in” is enabled. Verify that the URI opens the official client and the selected target. Record version and pass/fail plus a sanitized outcome; do not put the real URI or account/device identifiers in the repo/chat. If it fails or targets the wrong machine, stop and document evidence—do not invent URI arguments.

### Phase 2 — Account B and team ambiguity (HUMAN GATE)
Only after Phase 1 is recorded, repeat with an owner-selected Account B machine while both intended accounts remain in the official client. Determine whether these are separate Splashtop logins or one email across teams; when same-email/two-team applies, test both team targets explicitly. If URI cannot select the team safely, record web-console launch as the intended method for that case.

### Phase 3 — WebView2 profile proof (HUMAN GATE)
Build the smallest throwaway Windows profile proof (not the production UI), using separate persistent WebView2 profiles. The owner signs into each official console normally, completes MFA/SSO/new-device verification, verifies simultaneous authenticated state, restarts the proof app and verifies both remain authenticated and isolated. Capture no secrets. Do not bypass security controls.

### Phase 4 — current console inspection and complete parser
Inspect only approved user-session sources in the WebView2 page: rendered DOM, its own delivered in-memory component data, and the permitted `property/general/{id}?iframe=true` page. Establish complete discovery for the 400+ device account (parsed vs console total, explicit pagination if necessary); record parser version and sanitized fixtures. Respect rate and legal constraints. No cookies/tokens, private APIs or other endpoints.

### Phase 5 — launch data, Connect behavior and validation report (CHECKPOINT)
Determine whether permitted pages expose MAC/direct URI, what the official Connect action does, whether shared-MAC orphan entries choose the live target, whether status updates without reload, and whether web-console Connect fallback works. Verify CSV headers from an actual current export if one is supplied. Complete every section of `VALIDATION-REPORT.md`, including account A/B, same-email teams, profile persistence/new-device verification, counts, and the ToS finding. Deliver the report and **stop**. No Phase 6 work until the owner explicitly approves the report and architecture.

### Owner approval gate — required before implementation
After Phase 5, wait for an explicit approval of the findings and any architecture changes. Keep Phase 6 and all later implementation tasks blocked behind this gate until approval.

### Phase 6 — direct URI launch
Implement validated `st-business:` URI generation/launch only for proven cases, plus registry checks, origin/scheme allowlist and actionable failure handling. Shared or unavailable MAC uses fallback.

### Phase 7 — official web-console Connect fallback
Use the account's authenticated WebView2 profile, locate by numeric console ID (never name alone), invoke the normal Connect action and allow only validated official URI handoff. Report not-found/expired/offline states.

### Phase 8 — native unified WPF device browser
Build the compact, keyboard-accessible multi-account UI and separate account browser.

### Phase 9 — cache, metadata, filters and diagnostics
SQLite cache and staleness; separate favorites/aliases/tags; parser health; safe diagnostics; conservative refresh; account management; failure and reauth behavior.

### Phase 10 — packaging and release verification
Self-contained publish unless justified otherwise; WebView2 Evergreen runtime presence/bootstrap; installer or portable ZIP; unsigned SmartScreen note; GitHub Actions Windows runner verification; artifact/bytes verification. Owner performs final live Windows acceptance.

## Kanban and GitHub tracking

The Phase 1 task starts `blocked` with `needs_input`. Later human-only tasks remain unassigned and dependency-waiting until their predecessor completes; when a human gate becomes actionable, block it for owner input before any dispatch. The Phase 5 approval task is the required parent of Phase 6. No worker has been assigned or dispatched. The Phase 1 GitHub Issue is https://github.com/pupontech/splashtop-unified-launcher/issues/1.
