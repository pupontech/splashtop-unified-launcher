# Unified Remote Computer Browser (unofficial)

A Windows-only WPF utility planned to combine computer inventories from multiple Splashtop Business accounts and hand connections to the official installed Splashtop Business application. It will not implement remote desktop, and it will not require Splashtop Open API access.

This is an unofficial project and is not affiliated with or endorsed by Splashtop Inc. The official Splashtop Business client is required; this application is only a computer browser/launcher.

## Project status

Planning and current-documentation research are recorded. No application code has been implemented yet. The first validation gate is waiting on a human test in Windows with the user's installed Splashtop Business client and a known machine. The agent workspace is Linux and cannot truthfully perform that Windows/client test.

See:
- [Project specification](docs/PROJECT-SPEC.md)
- [Research and ToS review (2026-10-04)](docs/RESEARCH-2026-10-04.md)
- [Phase roadmap and gates](docs/ROADMAP.md)
- [Phase 5 validation report template](docs/VALIDATION-REPORT.md)

## Planned design

- Native WPF + current stable .NET for Windows 10/11; WebView2 is a hidden/account-management infrastructure surface, not the main UI.
- One persistent, isolated WebView2 profile per configured account. Login, MFA, SSO and new-device verification remain on Splashtop's real site; passwords are never collected or stored by this app.
- Normalized computer records from a provider abstraction; Splashtop-specific page parsing stays in `SplashtopUnified.Splashtop`.
- Local SQLite cache for non-sensitive inventory/UI metadata. Live status is never represented as current when only cached data is available.
- Launch only through the documented `st-business://` form after Windows-side validation, or through the official console's normal Connect action. No private APIs, copied cookies, undocumented URI arguments, or remote-desktop protocol.

## Planned usage

After implementation and validation, users will add account display name, email and console region/base URL, then sign into that account's WebView2 profile directly. The app will retain browser session data per profile, but not Splashtop passwords. The complete setup, caching paths and troubleshooting guide will be finalized after the Windows profile-isolation and parser gates pass.

## Security and validation status

No Splashtop API credentials or other API access are assumed. Current Splashtop Terms do not expressly mention web-console scraping by name in the passages reviewed; they do restrict certain collection/storage of third-party information and disruptive or specified monitoring uses. That is a legal/compliance risk, not a clearance. See the research report; seek written Splashtop guidance before enabling automated recurring collection. Never bypass MFA, CAPTCHA, SSO, device verification, TLS checks, or other security controls.

## Repository and board

This is a standalone Windows-only repository; it is not part of a cross-platform tools repository. Project tracking is on the `splashtop-unified-launcher` Hermes Kanban board. GitHub Issues are enabled for durable, repository-linked work items.
