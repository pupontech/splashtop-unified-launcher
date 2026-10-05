# Official Splashtop Computers list — observed structure

Evidence source: Splashtop Business support article, "Web App"
(https://support-splashtopbusiness.splashtop.com/hc/en-us/articles/4630897874203-Web-App),
official screenshot of the Computers page:
https://support-splashtopbusiness.splashtop.com/hc/article_attachments/54288428720795
Viewed and transcribed from the published image on 2026-10-05.

## Observed layout

The authenticated console Computers page renders a **table**:

| Column header | Content |
|---|---|
| `Name` | Row name, with a leading OS icon cell |
| `Device Name` | Hostname / device name |
| `Group` | Group membership (e.g. `Default Group`, `Servers`, `VMs`) |
| `Notes` | Notes cell (empty in the screenshot) |

Each row also shows, on the right: a **Connect icon button** and a **`...` overflow menu**.
The toolbar above the table shows `Add Computer`, a `Business App` button, a refresh
control, two view-mode toggles, a filter control, and a search box.

## What the screenshot does NOT show

- No numeric console/computer ID column.
- No MAC address, OS string, last-online time, or logged-in user column.
- No total-count indicator or pagination control.

## Consequences for the unified list

1. Extraction may supply `name`, `deviceName`, `group`, `notes`, and whether a row exposes a
   Connect control. It must **not fabricate** an ID, MAC, status, or timestamp.
2. Because the list view exposes no numeric ID, extracted rows have **no stable composite
   identity**. They stay individually visible and must never be de-duplicated by name.
3. Any documented per-device details (MAC/OS/last-online) require the separately permitted
   property/general view, which is out of scope for this extraction step.
4. Because no total or pagination is visible, a first-run extraction cannot prove full-list
   completeness; the honest outcome is `incomplete` unless the page itself reports a total.
5. Connect must run in the owning account's own WebView by activating that row's Connect
   control, so the official client path is preserved.

This document is evidence of UI text and column semantics only. It is not a captured DOM,
and it authorizes no selectors beyond semantic header/label matching.
