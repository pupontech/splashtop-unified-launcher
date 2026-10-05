# Windows prototype quickstart

1. Download the versioned ZIP and verify its published SHA-256 with `Get-FileHash -Algorithm SHA256`.
2. Extract the complete ZIP into a writable folder. Double-click `START.bat`. Windows 10/11 x64; no .NET SDK/runtime installation or administrator rights required. This is an unsigned prototype; follow your organization's SmartScreen policy.
3. Add an account display name, optional email, and official HTTPS Splashtop console URL. No password is requested.
4. Select that account in the import selector and import your local CSV. Repeat for other accounts; their inventories appear in one combined list.
5. Search or filter by account/status/favorites. Group names can be searched; a dedicated group selector is not included in this first prototype.
6. Favorites and aliases require an actual computer ID. ID-less rows remain visible, but these editors are disabled rather than inventing an identity. Local data persists in `%LOCALAPPDATA%\SplashtopUnified\Prototype\inventory.json`; treat this file as private.
7. Open Console opens the configured official account console in your browser. It does not connect to or target the selected device. Use the official Splashtop app/console for remote sessions.

## CSV headers

Required: `Name` or `Computer Name`. Optional: `ID` or `Computer ID`, `MAC` or `MAC Address`, `Group`, `Status`. Quoted commas/newlines and UTF-8 BOM are supported. Unknown numeric IDs remain absent; malformed IDs and duplicate complete IDs reject the import without replacing prior inventory. Header mappings have not been verified against every current Splashtop export.

Synthetic example (not real device data):

```csv
Name,ID,MAC,Group,Status
SYNTHETIC-WS-01,10001,02:00:00:00:00:01,Lab,Online
```

## Verified scope and limits

Hosted Windows CI exercises Release build, Core/CSV tests, actual shared UI-import-pipeline regression assertions, executable startup/close, and synthetic JSON save/reload. Full interactive click-by-click UI automation is not claimed. Live Splashtop account/client behavior remains untested.

This is a manual local inventory browser, not authenticated automatic synchronization. Imported online/offline status is last-known CSV data, never proof of current device status. No direct `st-business:` session launching is included. No private APIs, cookies, credential storage, or MFA bypass. Keep your real CSV/state local and out of bug reports.
