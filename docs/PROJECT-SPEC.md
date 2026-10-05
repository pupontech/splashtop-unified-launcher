# Project specification

## Product and boundary

Build a compact native Windows 10/11 technician tool that combines computer records from any number of configured Splashtop Business accounts into one searchable list. It is a launcher/frontend only. The installed, non-portable Splashtop Business app establishes every remote session. Do not implement remote desktop or replace the official client.

No Splashtop Open API access is available or required. Do not request/obtain API credentials, use private REST/GraphQL services, export cookies/tokens, or make authenticated requests outside the account's WebView2 context. Authentication, MFA, CAPTCHA, SSO and new-device verification happen normally on Splashtop's website. Never store a Splashtop password or bypass a security control.

The project has its own Git repository and is Windows-only by design. Stack: current stable .NET, WPF, Microsoft WebView2, MVVM, dependency injection, structured logging and SQLite for local non-sensitive cache/state. No Electron. WebViews are infrastructure/account management, not the primary screen.

## Account and profile rules

Support N accounts. Each account has display name, email, unique ID, console region/base URL, profile name, last successful sync and authentication state. Use one persistent isolated WebView2 profile per account. A single WebView2 user-data folder with distinct profile names is preferred; prove isolation on real Windows before depending on it, otherwise fall back to separate user-data folders. Profiles must isolate cookies, local/session storage, auth state and site cache while retaining each login across app restarts. SSO/popup windows stay in the initiating account profile. Account actions: add, reauthenticate, open console, clear that account's browser data, remove.

The app asks for only display name, account email and console region/base URL. It never asks for a password. Per-account URLs are configurable; detect region redirects where practical.

## Provider and parser boundaries

`ISplashtopComputerProvider` is the app-facing provider contract. Initial providers: `WebConsoleComputerProvider` and `CsvComputerProvider`. The parser/normalizer is versioned and all Splashtop DOM/data-structure knowledge must live in `SplashtopUnified.Splashtop`; other layers see normalized models only.

Permitted web data sources are restricted to the authenticated WebView2 page's rendered DOM, its own in-memory data already delivered to that page (e.g. rendered React props), and `/property/general/{id}?iframe=true` fetched from within that authenticated page context. No other endpoints. Use complete in-memory list data only if it is genuinely complete; otherwise handle every page and compare parsed count to the console-reported total. Do not treat a successful HTTP 200 redirect stub as a valid property result; `<title>Redirecting…</title>` means the device is gone. Parse status timestamps with and without seconds.

Use semantic/accessibility selectors, text, structural fallbacks and data-test IDs; do not rely exclusively on minified CSS classes. Be resilient to SPA reloads, stale dialogs and layout shifts; verify page/session state after every action. Tests use sanitized fixtures, not live Splashtop.

List status may refresh conservatively (default ~60 seconds); device details such as MAC/OS/notes/last-online are fetched once per device and cached, then fetched only on explicit request, newly seen device, or launch failure. Use limited concurrency, small delays and backoff. Never run 400 detail fetches on a timer. Observe status in-page if the site updates live; otherwise reload only as needed. Debounce commands and never overlap refreshes per account. Pause timed refresh after prolonged app minimization.

## Normalized identity and persistence

Computer identity is `(AccountId, SplashtopComputerId)`, not name or MAC. Duplicate names inside one account and across accounts remain distinct. MAC is an attribute; it may be shared by stale/reinstalled records. Fields remain nullable unless actually exposed: local ID, account, numeric console ID, team/group, name/hostname, MAC, status/source, last-online, logged-in user, notes, OS, official launch URI/method, web-console action target, last seen and updated timestamps.

Keep local favorites, aliases and tags in a separate metadata table keyed by `(AccountId, SplashtopComputerId)`; sync never overwrites metadata. Local SQLite may cache normalized inventory and non-sensitive UI state. Cached status must be labelled stale/last-known, never live. A missing device on one successful sync is marked not-seen, not deleted. Preserve prior cache when parser-health checks fail.

## Connection and URI security

The only initially allowed external scheme is `st-business:`. Official documentation publishes the remote-session URI form `st-business://com.splashtop.business?account=<email>&mac=<MAC>`; do not add undocumented parameters. Normalize and validate email, MAC and URI; check Windows `HKEY_CLASSES_ROOT\\st-business` registration before direct launch. Prefer direct launch only when a MAC is known and unique among entries in that account. If MAC is missing/shared, direct launch fails, or same-email/team targeting is unproven, use the official web-console Connect path in the correct account/team context.

**Client-version compatibility:** do not hard-code a requirement for Splashtop Business 3.8.6.1 or branch launch behavior on one exact version. The goal is compatibility across supported installed Windows Business client versions that handle Splashtop's documented `st-business:` URI. Record and test the owner's 3.8.6.1 installation as the first baseline. One test cannot establish compatibility with every historical or unsupported release; report untested versions honestly and provide a useful fallback/error when the protocol is absent or rejected.

For WebView2 external launches, validate scheme, URI shape and initiating Splashtop origin; reject other protocols. Log no secrets. Direct C# launch uses `Process.Start` with `UseShellExecute = true` only after validation, never shell concatenation or commands derived from page content. If a target is offline, warn but allow an explicit user attempt. Errors must be concise and actionable; never fail silently. Locate fallback targets by numeric console ID, never name alone.

Research current documented shortcut behavior, but implement Remote Command/File Transfer URIs only if Splashtop documents those URI forms or safely exposes supported links. Otherwise route to “Open in Splashtop Web Console.”

## Main UI

Technician-oriented native list, compact and keyboard-accessible. Combine accounts by default; filter by account, group, status and favorites; search name, alias and tags; sort; compact mode; refresh; clear online/offline indicator; account badge/color; last-refresh time; Enter = Connect; double-click = Connect. Optional hide-offline-older-than-N-days is off by default. When account-local duplicate names exist, show enough context (group, last online, Properties ID) to distinguish them. Context menu only shows applicable actions: Connect, Open in web console, Refresh Status, Favorite toggle, Set Alias, copy known fields (name/hostname/MAC/URI), Properties.

Use a small Account Browser window only for login/MFA/SSO, session expiry, troubleshooting and management.

## Health, diagnostics and security

Each sync records page recognized, logged-in state, list/data located, reported total, located rows/entries, parsed/rejected counts, launch-info counts, parser version and warnings. A previously working account that suddenly returns zero or materially fewer than its reported total while still logged in is a parser failure: retain the old cache and show “Unable to read current Splashtop computer list” plus last successful sync time.

A developer-only fixture capture is disabled by default in production. Capture is limited to one selected computer and must sanitize cookies, tokens, script payloads/session data and unrelated personal information. WebView2 DevTools are off by default in production. Never log cookies, tokens, passwords, session IDs or full HTML dumps by default. Persistent WebView2 profiles reside under per-user `%LOCALAPPDATA%` permissions. Use DPAPI for sensitive local configuration if any is eventually necessary; the app contains no Splashtop credentials.

## Planned project layout

```text
/src
  /SplashtopUnified.App              # WPF views and view models
  /SplashtopUnified.Core             # models, provider/browser abstractions, merge/filter logic
  /SplashtopUnified.Infrastructure   # WebView2 host, URI launcher, registry checks, logging
  /SplashtopUnified.Splashtop         # all Splashtop page knowledge, parsers, providers
  /SplashtopUnified.Persistence       # SQLite
/tests
  /SplashtopUnified.Core.Tests
  /SplashtopUnified.Splashtop.Tests
  /Fixtures
```

Core must not reference WebView2. The planned implementation is intentionally gated; this file is requirements, not a claim that any of the components already exist.

## Required unit and integration tests

- URI generation/validation; MAC normalization; direct-vs-web method choice; two records sharing one MAC must use web fallback.
- Identity/merge by `(AccountId, SplashtopComputerId)`; duplicate names in one and multiple accounts.
- Search by name/alias/tags; account/group/status/favorites filters; stale-cache labels.
- Parser fixtures, pagination/full-list completeness, partial-list failure, malformed/empty DOM, redirect stub, expired session.
- Sync never overwrites local metadata.
- WebView2 profile isolation integration: cookies/storage do not cross profiles, both sessions persist over app restart.

## Manual acceptance (Windows)

Test real account sign-in, MFA/new-device verification, account-specific Business-app launch, same-email/two-team behavior if applicable, complete counts against console totals, status, filters, session expiration/reauth, restart persistence, duplicate names, same-MAC reinstall-orphan case, Business app closed/already running, unregistered protocol and offline target. Never invent a test machine; owner selects known test machines.

## Release and limitations

Final package should be self-contained unless measured constraints justify framework-dependent publishing, include WebView2 Evergreen runtime presence/bootstrap handling, and be an installer or portable ZIP. Document unsigned-build SmartScreen warnings. Windows behavior is verified on GitHub-hosted Windows runners and then user-tested on their own Windows machine; do not create/provision a VM for live account testing.
