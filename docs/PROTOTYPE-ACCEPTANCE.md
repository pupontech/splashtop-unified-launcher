# Prototype acceptance matrix and evidence checklist

## Purpose and honest status (2026-10-05)

This document defines the acceptance contract for a CSV-backed Windows prototype. It is a test plan, not a declaration that the UI or ZIP has passed. The current source snapshot contains a WPF project, CSV reader, JSON local-state-store source, and `scripts/Build-Prototype.ps1`, but no `MainWindow` implementation was found and there is no successful Windows build/smoke result or ZIP artifact. Treat these as in-progress scaffolding, not a runnable application. The Core suite was run on Linux with .NET SDK 10.0.401: 62 tests passed, 0 failed. That does not demonstrate Windows startup or end-to-end UI/persistence/import/package behavior.

Current status:

- Pure Core merge, query, filters, and metadata shape: implemented; 62 unit tests pass on Linux (.NET 10.0.12 runtime).
- A source-level CSV reader exists. It accepts one account ID as a method argument and supports the headers listed below; there is no user-facing multi-account import workflow or proof against a real export.
- A WPF project and JSON local-state-store source exist, but no `MainWindow` implementation was found in this snapshot. Favorites UI, restart behavior, and per-user isolation have not been run or proven.
- A PowerShell packaging script exists but has not been run on Windows; there is no verified self-contained ZIP or Windows smoke evidence.
- A launch-security library contains URI/console validators, but no user-facing configured-console or direct-launch action has been verified.
- Automatic authenticated/live inventory synchronization: not implemented or proven. CSV reader input is a user-provided snapshot and must never be labelled live.
- Direct `st-business:` launch compatibility: owner test not performed. The owner's stated Business client version 3.8.6.1 is a baseline to test, not a compatibility guarantee.
- Windows CI smoke test / ZIP artifact evidence: not present. `.github/workflows/core-ci.yml` currently builds/tests Core; it does not build an app, publish a ZIP, or launch a Windows UI.

A future completion report must attach actual Windows run/artifact evidence and mark each row below `PASS`, `FAIL`, or `NOT RUN`. Never turn a planned criterion or Linux Core test into a Windows/UI pass.

## CSV reader headers and synthetic examples

The reader source is `CsvInventoryReader.Read(csv, accountId)`. Its current source-level contract is one CSV per call plus a caller-supplied account ID; account identity is not read from CSV. Header matching is case-insensitive and trims whitespace around header names. The parser handles a leading UTF-8 BOM, quoted fields, commas, embedded CR/LF, and doubled quote escapes. These assumptions have not been verified against any current Splashtop export, and no Windows UI currently invokes the reader.

| Field | Current accepted header(s) | Behavior |
|---|---|---|
| Computer name | Required: `Name` or `Computer Name` | Blank name is an import error. |
| Computer ID | Optional: `ID` or `Computer ID` | Must be a non-negative whole number when present; absent/blank remains null. |
| MAC address | Optional: `MAC` or `MAC Address` | Empty remains null. |
| Group | Optional: `Group` | Empty remains null. |
| Status | Optional: `Status` | Known Core values parse case-insensitively; blank remains null; unknown text maps to `Unknown`, never `Online`. |
| Account identity | Not a CSV column | Supplied separately as `accountId` to the reader. |

Headers such as `SplashtopComputerId`, `MacAddress`, `GroupName`, `Hostname`, `LastOnlineUtc`, and `OperatingSystem` are not among the current reader's mapped aliases. Unknown columns are ignored. Do not call these supported unless implementation and tests change. Do not import favorites, aliases, or tags from the inventory CSV; local metadata belongs to a separate user-state path and is not part of the reader API.

### Synthetic examples only — not real Splashtop data

Every value below is invented. The repeated device ID, display name, and MAC are deliberate; the caller supplies the account ID separately for each file. These demonstrate only the reader's current header aliases, not a real Splashtop export.

`synthetic-account-alpha.csv` — call with `accountId = "demo-account-alpha"`:

```csv
Computer Name,Computer ID,MAC Address,Group,Status
SYNTHETIC-WS-01,10001,02:00:00:00:00:01,Lab,Online
```

`synthetic-account-beta.csv` — call with `accountId = "demo-account-beta"`:

```csv
Name,ID,MAC,Group,Status
SYNTHETIC-WS-01,10001,02:00:00:00:00:01,Field,Offline
```

Combining these into one view is an application acceptance requirement, not behavior demonstrated by the reader alone. The synthetic account IDs and device fields must never be described as live or real.

## Acceptance matrix

For manual tests using real inventories, the operator selects the accounts and files and keeps them local. Do not add real exports, device identifiers, emails, hostnames, MACs, screenshots, or logs to Git, CI artifacts, or this task. CI tests use only clearly synthetic fixtures. Record sanitized counts and outcomes, not inventory contents.

| ID | Area | Procedure | Required result / evidence | Current status |
|---|---|---|---|---|
| CSV-01 | Header and parsing | Parse both synthetic one-account files above with their respective external account IDs; test header aliases, reordered columns, UTF-8 BOM, quoted comma, escaped quote, embedded newline, and CRLF. | Supported aliases map to the correct model fields; malformed rows and nonnumeric nonempty IDs give record-specific errors; unknown status never becomes Online. This parser-level test does not prove a UI import path. | Source and parser test project exist; end-to-end/Windows result NOT RUN |
| CSV-02 | Real CSV / multi-account | On Windows, import operator-selected current CSV exports for at least two accounts, each assigned its correct account ID. Compare source and accepted counts locally; redact identifiers in evidence. | Both datasets appear in one inventory with account-specific identity `(AccountId, SplashtopComputerId)`; names and shared MACs do not merge records; source files stay unchanged and local. Actual vendor-header mapping and complete counts are recorded without publishing PII. | NOT RUN — no Windows import UI or current export supplied |
| CSV-03 | Invalid / duplicate identity | In synthetic fixtures, include missing ID, nonnumeric ID, duplicate composite ID, same ID in another account, duplicate name, and shared MAC. | Missing ID remains non-identity; invalid nonempty ID errors; identity uses account + numeric ID, never name/MAC; same ID in another account, duplicate names, and shared MAC stay distinct. | Core identity/merge cases partly covered by passing Core tests; CSV/UI integration NOT RUN |
| CSV-04 | Search | Search by mixed-case name substring and by a local alias; also test tags if the UI offers tags. | Name search is case-insensitive; local alias/tag search works if offered. Importing the same inventory must not overwrite local metadata. | Core query supports name/alias/tags and tests pass; UI/import integration NOT RUN |
| CSV-05 | Filters | Combine account, group, status, and favorites filters; clear each; include null/unknown values. | Filters compose correctly; empty selections do not accidentally show all rows; null status/group does not match a selected value. | Core query tests pass for corresponding logic; UI/import integration NOT RUN |
| CSV-06 | Favorite lifecycle | Mark a row favorite, filter Favorites only, toggle off, re-import the same CSV, and restart. | Favorite is keyed by `(AccountId, SplashtopComputerId)`, persists across re-import/restart, does not leak to a duplicate name/MAC in another account, and can be removed. | Core metadata/query logic and JSON store source exist; integrated persistence NOT RUN |
| CSV-07 | Startup import | Start the extracted app with no prior state; import two synthetic CSV files with distinct supplied account IDs; close and reopen. Repeat after extracting to a path containing spaces. | App opens as a standard user; import outcomes are clear; both accounts remain in one view after restart; empty/missing/malformed CSV fails safely. | NOT RUN — no verified `MainWindow` or Windows build |
| CSV-08 | Per-user persistence | Add a favorite and alias, exit cleanly, restart as the same Windows user, then launch as another standard user. | First user's metadata returns; second user sees independent state. Current store source targets `%LOCALAPPDATA%\SplashtopUnified\inventory.json`; verify it is per-user, not beside the ZIP or in a shared profile. Confirm no password, token, or cookie fields are stored. | JSON store source exists; no Windows runtime/isolation test |
| CSV-09 | Snapshot freshness | Import CSV, wait, inspect displayed status and refresh/source labels. | CSV status is visibly an imported/as-of-file snapshot, never `Live`; no periodic sync is implied. Current reader leaves `StatusSource` unset, so UI must make the snapshot boundary clear. | NOT RUN — no verified UI |
| ZIP-01 | Windows x64 self-contained ZIP | On a clean Windows 10/11 x64 runner, run `scripts/Build-Prototype.ps1` with the current commit SHA. Extract its ZIP to a fresh temp path containing spaces. | Script completes; ZIP opens; `SplashtopUnified.App.exe`, `START.bat`, README, and manifest are present; manifest hashes match payload; record ZIP SHA-256, byte count, commit, run URL, runner, and exit codes. The executable launches without separately installing the .NET runtime. | Build script source exists but has not been run; no verified ZIP |
| ZIP-02 | Real startup/UI smoke | From the extracted ZIP on Windows, launch as a standard user and inspect the actual main window with UI Automation (not process existence alone). Exercise import, search, account/group/status/favorite filters, and restart persistence using synthetic CSV only. | Main window is responsive; controls and expected rows are verified; no unhandled exception; attach redacted UIA/test results. Any `--smoke-test` mode must finish successfully without hanging and must exercise the intended startup path. | NOT RUN — no `MainWindow` implementation/build evidence |
| ZIP-03 | Privacy exclusion / package hygiene | Inspect ZIP entry list. Seed unique synthetic sentinel values in local CSV/state after extraction, close app, compare package and archive again, and scan archive/log bytes. | No operator CSV, inventory JSON, database/cache, generated logs, credentials, cookies/tokens, browser profiles, local preferences, or real identifiers in ZIP. Sentinels stay only in operator input/per-user state and never enter ZIP/logs; package hash stays unchanged after use. Sample data may be included only if clearly labelled and opt-in; no silent seeding. | NOT RUN — packaging script has no verified privacy-scan result |
| ZIP-04 | Per-user file placement | Observe writes under standard user A, then run under standard user B. | Writable state remains under the current user's `%LOCALAPPDATA%\SplashtopUnified\inventory.json`; no writes are required beside executable, under `Program Files`, or to another user's profile. | Store source exists; no runtime test |
| SAFE-01 | Network / secrets boundary | Use CSV-only mode with networking observed; scan source, logs, archive, and test output for synthetic secret markers. | CSV parsing is local; no credential prompt/storage, private API, cookie/token logging, or guessed URI parameter. Console opening is separate and only to a validated configured official URL. | Parser/validators are source-level only; end-to-end check NOT RUN |
| CONSOLE-01 | Official console action | Configure an account with an explicitly supplied official HTTPS region/base URL and invoke the app's console action. Inspect actual destination. | Opens only the configured official HTTPS host/path allowed by validator; no inferred region, arbitrary redirect, private endpoint, or guessed parameter. | Validator source exists; user-facing action and Windows test NOT RUN |
| LIVE-01 | Automatic inventory synchronization | Inspect implemented features and their evidence; do not sign in or infer from a local file. | Label as not implemented/proven. A CSV import is manual local data input, not live sync. | NOT IMPLEMENTED / NOT PROVEN |
| LAUNCH-01 | Direct launch compatibility | Owner tests only after selecting a known test machine on their Windows system with their installed client; record client version and sanitized result. Never generate guessed URI parameters. | Keep direct launch disabled/unclaimed until owner observes documented URI opening the expected target. If present before that, require explicit per-action confirmation and a clear unverified-compatibility warning; reject non-allowlisted schemes. Test any web-console/manual fallback separately. | OWNER TEST NOT PERFORMED; version 3.8.6.1 is untested |

## Exact Windows CI smoke evidence checklist

The existing `.github/workflows/core-ci.yml` is Core-only. `scripts/Build-Prototype.ps1` now exists in source, but it has not been run on Windows and is not evidence of a built ZIP. A Windows workflow must create a separately identifiable `prototype-win-x64` job and retain all evidence under the same commit/run. On `windows-latest`, first install the pinned .NET 10 SDK and run the solution's restore/build/test steps. Then invoke the existing package script with the exact source commit:

```powershell
dotnet restore SplashtopUnified.sln
dotnet build SplashtopUnified.sln --configuration Release --no-restore
dotnet test SplashtopUnified.sln --configuration Release --no-build --no-restore --logger 'trx;LogFileName=tests.trx'
pwsh -NoProfile -File scripts/Build-Prototype.ps1 -SourceCommit $env:GITHUB_SHA
```

The script is configured for Release `win-x64` self-contained publish and emits `artifacts/prototype/SplashtopUnified-Prototype-win-x64.zip`; it also runs the app with `--smoke-test` and checks hashes for the archive payload. These are source-defined actions, not executed evidence. At the inspected source snapshot, `App.OnStartup` performs a synthetic parser check and then shows `MainWindow` for `--smoke-test`, while the package script waits for that process to exit. No `MainWindow` definition was found. Resolve and exercise this startup/smoke contract before treating the script as green.

The script's current package contents are copied from its publish directory plus generated `README.txt`, `START.bat`, and `manifest.json`; its manifest records the source commit and SHA-256 of payload files. It does not replace the required privacy scan. Before publishing the CI artifact, inspect the ZIP entry list and scan archive bytes for seeded synthetic sentinels. Fail on any user-selected CSV, inventory JSON/database/cache, logs, browser/WebView2 profile data, credentials, tokens, cookies, private keys, or per-user configuration. Record ZIP path, byte count, outer ZIP SHA-256, entry listing, run URL, commit SHA, runner image, SDK/runtime identifier, command exit codes, test totals, manifest verification, and redacted smoke results.

On the Windows runner, after extraction to a fresh temporary directory whose path contains spaces:

1. Verify ZIP integrity, expected executable, `START.bat`, README, manifest and manifest payload hashes; record outer ZIP hash/size.
2. Extract to a fresh path containing spaces. Launch as an ordinary runner user; use UI Automation to wait for the actual main window and verify it is responsive. A live process alone is insufficient evidence.
3. Import only the two synthetic CSV files above, each with the intended account ID. Verify both rows, name search, account/group/status filters, favorite toggle, and that same-name/same-ID/shared-MAC rows remain distinct across account identities.
4. Exit normally and restart. Verify the favorite and local metadata persist for that Windows user. Verify a second isolated user/profile does not see them when available; otherwise mark it NOT RUN and retain the owner manual test.
5. Compare package SHA-256 before and after use; inspect ZIP/package for sentinels, data files, logs, profiles, or secrets. Inspect `%LOCALAPPDATA%\SplashtopUnified\inventory.json` for the expected user-only state. Do not upload CSV, state JSON, logs, screenshots, or unsanitized UIA tree; retain redacted summaries only.
6. Attach workflow/run identifiers, synthetic-only test logs, package listing, artifact size/hash, manifest verification, and smoke result. Mark UI, privacy, persistence, runtime, and WebView2 rows independently; do not infer one from another.

A GitHub-hosted Windows CI pass proves only the synthetic-runner behaviors it actually executes. It does not prove owner's real CSV header compatibility, live account synchronization, MFA/session persistence, the owner's client URI compatibility, or production readiness.
