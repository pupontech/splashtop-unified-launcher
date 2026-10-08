# Unified Remote Computer Browser (unofficial)

A Windows WPF testing prototype that combines computer lists from **two Splashtop Business accounts** in a native unified window. Each account signs in through its own persistent, isolated WebView2 profile on the official console. Connections use the console's own controls and can hand off to the installed official Splashtop Business app.

This project is not affiliated with or endorsed by Splashtop Inc. It does not implement remote desktop or require Splashtop Open API access.

## Download and test

Use a versioned Windows x64 ZIP from [GitHub Releases](https://github.com/pupontech/splashtop-unified-launcher/releases). Extract the ZIP into a writable folder, follow its included README, and run its launcher. The official Microsoft WebView2 Evergreen Runtime and the official Splashtop Business app are required for browser login and native sessions respectively.

Only published release assets are testing downloads; a pushed candidate is not a verified release. New candidates must pass hosted Windows tests and both isolated WebView2 smoke runs before packaging is accepted.

1. Sign into each account's official console, completing any MFA/SSO/device verification normally.
2. Open the unified list and refresh. The list displays per-account refresh state; unknown totals remain indeterminate.
3. Select the intended account/computer and use Connect. Confirm the target and available connection choices in the unified window.
4. Compare inventory, presence and the session target against the real console during owner testing.

## Implemented in the current candidate

- Two isolated account browser profiles with engine-managed password saving/autofill enabled; the host does not extract or export passwords, cookies or tokens.
- Native searchable unified inventory with account labels, rendered status indicators and last-read metadata.
- Bounded pagination and virtual-scroll extraction. A no-total scroll list is complete only after it reaches the physical bottom and remains stable for two quiet 250ms intervals; indistinguishable row identities at different content positions remain incomplete. Duplicate rows across verified pages are retained.
- Parallel per-account refresh and request-correlated progress. A manual Refresh joins the exact matching automatic read; an unknown timeout requires Reload rather than starting overlapping work.
- Foreground connection prompts, immutable selected-row checks and fail-closed trusted-origin/protocol handling. Cached, stale, partial or failed inventory cannot authorize unified connections.
- Allowlisted local JSON inventory cache for historical display, not SQLite and not connection authority. Inventory names/groups remain plaintext metadata and may be sensitive; Notes and free-form diagnostics are not persisted.

## Verification limits

Hosted Windows CI tests the actual WPF build and installed WebView2 Runtime using **synthetic local fixtures**. It does not establish compatibility with current live Splashtop DOM, successful owner login/MFA, every account size, or the installed client's real session outcome. Unit/helper checks are not proof of the native foreground dialog or production cached-startup UI paths.

Status is read only from supported rendered semantic indicators. **Blue/gray color inference is not implemented**, absent indicators remain unknown, and cached/last-read status is not real-time monitoring. No arbitrary-number-of-accounts support or production-readiness claim is made.

## Security and compliance

No private APIs, credential export, guessed vendor URI parameters or modified vendor binaries. Vendor connection payloads remain opaque; untrusted origins and unrelated executable schemes are refused. Never bypass MFA, CAPTCHA, SSO, device verification or TLS protections.

The research report records unresolved legal/compliance considerations, not clearance. Seek written Splashtop guidance before automated recurring collection.

## Project documents

Earlier specification and roadmap documents include planned architecture and historical gates; they are not evidence that every planned feature shipped.

- [Focused prototype improvement scope and shared terms](docs/IMPROVEMENT-SCOPE.md)
- [Project specification](docs/PROJECT-SPEC.md)
- [Research and ToS review](docs/RESEARCH-2026-10-04.md)
- [Roadmap](docs/ROADMAP.md)
- [Official connection chooser](docs/OFFICIAL-CONNECTION-CHOOSER.md)

Development is tracked on PR #3 and the `splashtop-unified-launcher` Hermes Kanban board. GitHub Issues are enabled.
