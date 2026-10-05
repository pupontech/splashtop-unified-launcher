# Windows prototype quickstart (status and operator checklist)

## Important: no runnable ZIP is available in this checkout

As of the inspected source snapshot (2026-10-05), the repository has WPF project scaffolding, a CSV reader, a JSON local-state-store class, and a PowerShell package script. No `MainWindow` implementation was found, no Windows build or smoke test has been verified, and no ZIP artifact is available. These source files are not a runnable prototype. Do not try to run `SplashtopUnified.App.exe` until a verified Windows ZIP and run record are published.

This guide defines the safe first-use procedure for the prototype ZIP once a verified Windows artifact is actually published. The CSV parser and state-store source exist, but the user-facing flow and runtime behavior are unverified. Check `docs/PROTOTYPE-ACCEPTANCE.md` for the exact source-level header mapping, acceptance contract, and current evidence status.

## Intended scope and limitations

The authorized first prototype is a local CSV inventory browser for user-supplied real inventories, with multi-account grouping, search, filters, favorites, and non-secret per-user state. It is not a live Splashtop inventory connector. Automatic authenticated/live inventory synchronization is not implemented or proven. A CSV file is a snapshot; show it as imported/as-of-file, never as live status.

Opening a configured official Splashtop web console is a target feature, not implemented or proven here. If a released prototype offers it, verify it uses only the explicitly configured official HTTPS console host. Direct `st-business:` launch compatibility is untested by the owner. Do not assume the installed Splashtop Business client opens a device, and do not enter a real URI assembled from guessed parameters. The owner's reported client version 3.8.6.1 is a first test baseline only, not proof of compatibility. No passwords, API credentials, cookies, or tokens are needed for CSV-only use; never give them to this prototype.

## When an artifact is released

Only use a ZIP attached to a Windows CI run/release whose commit, run link, published bytes, SHA-256, and smoke evidence are available. The intended artifact name is `SplashtopUnified-Prototype-win-x64.zip`; this name is a target naming convention, not an artifact currently present. If no ZIP and SHA-256 are provided, stop: there is nothing verified to install.

1. On Windows 10/11 x64, download the ZIP and its SHA-256 value from the same trusted CI run/release. Do not use a third-party mirror.
2. Verify the hash in PowerShell before extracting (replace the path and expected digest with values supplied by the release record):

   ```powershell
   Get-FileHash -Algorithm SHA256 .\SplashtopUnified-Prototype-win-x64.zip
   ```

   The computed value must exactly match the release record. If it does not, delete the download and stop.
3. Extract it to a writable folder for your Windows user, for example `%LOCALAPPDATA%\Programs\SplashtopUnifiedPrototype`. Do not extract over another version. The app must not require administrator rights. If Windows SmartScreen or your organization blocks an unsigned build, follow your security policy; do not bypass the warning unless you independently trust the exact source and verified hash.
4. Start the delivered executable or `START.bat` from the extracted folder, following that artifact's README. The current package script targets `SplashtopUnified.App.exe` plus `START.bat`, but no artifact has verified those files or the first-run UI. Confirm the main window opens before selecting data.
5. Use only a CSV import action present in the verified release. The current source-level parser takes an account ID separately and recognizes `Name`/`Computer Name`, optional `ID`/`Computer ID`, `MAC`/`MAC Address`, `Group`, and `Status`; it has not been checked against a live current Splashtop export, and no UI invokes it in the inspected source snapshot. Keep each CSV local.
6. Choose only the intended account exports, assign each the correct account ID using a documented UI action, and verify displayed counts against local files. If the release offers no account-ID/import workflow, stop; do not assume multi-account import exists. If import reports missing IDs, malformed CSV, or warnings you do not understand, stop and keep source files unchanged. Never infer identity from device name or MAC.
7. Exercise search and account/group/status filters. Mark one known test row favorite, exit normally, then restart and verify it remains. The current store source targets `%LOCALAPPDATA%\SplashtopUnified\inventory.json`; it has not been tested at runtime, and the user-facing UI may not exist. Do not copy that file to another user's profile or share it.
8. Treat all CSV statuses as a point-in-time snapshot. Do not use an offline/online value to assert the present state of a machine. No live account sync is available/proven.
9. Use the app only for the functions shown as enabled and tested in the matching release. If a configured-console action is present, confirm its destination is the official HTTPS host you explicitly configured. If direct launch is absent/marked untested, launch the device using the official Splashtop client/web console manually. Do not enter credentials into this app. Complete MFA, SSO, CAPTCHA, and new-device checks only on the official Splashtop site/client.
10. To stop testing, close the app. Keep the original CSV private. If you need to reset favorites/local state, use a reset/export action only if that exact artifact documents it; otherwise do not delete files blindly—record the app version and seek operator guidance.

## Supported-header example for acceptance tests (synthetic only)

The parser source currently supports one CSV per reader call, with account identity supplied separately. These files exercise the source-level supported aliases; they do not prove a shipped import UI or match to current Splashtop export headers. Both rows are invented and must not be described as real account/device data.

`synthetic-account-alpha.csv` (reader argument `accountId = "demo-account-alpha"`):

```csv
Computer Name,Computer ID,MAC Address,Group,Status
SYNTHETIC-WS-01,10001,02:00:00:00:00:01,Lab,Online
```

`synthetic-account-beta.csv` (reader argument `accountId = "demo-account-beta"`):

```csv
Name,ID,MAC,Group,Status
SYNTHETIC-WS-01,10001,02:00:00:00:00:01,Field,Offline
```

The same name, numeric ID, and MAC across these files must remain distinct because the account ID is part of the composite identity. The header names shown here are only the parser's current source-level mappings, not verified vendor-export headers. Do not import favorites/aliases/tags from inventory CSV; they belong to separate local state.

## Privacy and data handling

- Use real exports only locally and only with authorization. Never attach them to a bug report, CI run, repository, or support chat.
- This prototype must not ask for or store a Splashtop password, API secret, session cookie, or authentication token. Do not provide them if prompted.
- Do not enable any automatic network/live sync assumption. A CSV import is a manual local snapshot.
- Package files must not contain user inventory, databases, logs, WebView2 profiles, cookies, credentials, or local preferences. Runtime state belongs to the current Windows user's local profile, not beside or inside the ZIP.
- If the app writes data beside its executable, claims CSV data is live, asks for secrets, silently imports a sample, or includes another user's state, close it and report the issue without sharing private files.

## Windows smoke record to check before use

The CI/release record must include the artifact commit and run URL, Windows runner image, .NET SDK, `win-x64` self-contained publish command/result, automated test counts, ZIP SHA-256 and byte size, archive file listing/privacy scan, extracted startup/UI Automation result, synthetic CSV import/search/filter/favorite result, restart persistence result, and any `NOT RUN` item. The source script is `scripts/Build-Prototype.ps1`; its existence is not a pass. A run that only builds/tests the Core library is not a Windows app smoke pass.

A CI synthetic fixture does not establish that a real Splashtop export is accepted or complete, and no CI result proves automatic sync or direct launch compatibility. Real CSV and direct-launch checks remain separate Windows operator tests with sanitized outcomes only.
