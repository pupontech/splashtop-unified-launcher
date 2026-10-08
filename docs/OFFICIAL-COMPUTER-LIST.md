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
- **No confirmed per-row presence indicator.** The screenshot shows an OS icon in the first
  cell but no verified online/offline text or badge, so presence is *unproven* from this
  evidence and must be treated as optional.

## Consequences for the unified list

1. Extraction may supply `name`, `deviceName`, `group`, `notes`, whether a row exposes a
   Connect control, and row presence **only if the row itself states it**. Nothing else is
   carried and nothing is inferred: no ID, MAC, OS, last-online time or timestamp.
2. Because the list view exposes no numeric ID, extracted rows have **no stable composite
   identity**. They stay individually visible and must never be de-duplicated by name.
3. Presence is read **only** from an indicator the row itself carries, via its own
   `aria-label`, `title` or `img`/`svg` `alt` text. This evidence does not confirm that such
   an indicator exists in the live console, so the extractor must report no status when it
   finds none, and a device name that merely contains a word like "online" is never treated
   as status. A blanket `incomplete`/blank status is the correct outcome until the live
   console shows otherwise.
4. A row-level online/offline value is **not** a stable identity and must never be merged
   on; it is display state that changes between reads and is deliberately excluded from the
   row de-duplication key.
5. Any documented per-device details (MAC/OS/last-online) require the separately permitted
   property/general view, which is out of scope for this extraction step.
6. Because the support screenshot has no total or pagination, the image alone cannot prove full-list completeness. At runtime, a no-total virtual-scroll read may be complete only after a bounded walk reaches the physical bottom and remains unchanged through two quiet 250ms checks, with no repeated indistinguishable row identity at a different content position. A reported total must reconcile and a verified pager must reach disabled Next. Scroll height alone is not a row count or completeness proof; otherwise the read remains `incomplete`.
7. Connect must run in the owning account's own WebView by activating that row's Connect
   control, so the official client path is preserved.

This document is evidence of UI text and column semantics only. It is not a captured DOM,
and it authorizes no selectors beyond semantic header/label matching.

## Owner-reported live layout mismatch

An owner screenshot of the embedded signed-in **My Computers** page shows the heading
`Computer Name`, rather than the older support image's `Name`, followed by distinct
`Device Name` and `Group` headings. Icon actions are unlabeled visually. Horizontal
clipping means the screenshot does not establish whether a `Notes` column exists.
No private row values, account identifiers or screenshot are stored here.

The prior reader required all four exact headings, including `Name` and `Notes`.
A trusted-origin replay of the exact prior production script reproduced the reported
failure by changing only the synthetic heading from `Name` to `Computer Name`: five
visible synthetic rows became zero rows / `Unavailable`. This is a recognition defect,
not evidence that the owner needs to authenticate again.

Recognition and row/action interpretation must use the same unique semantic header
map: `Name` or `Computer Name`, `Device Name`, `Group`, and optional `Notes`. Blank
icon/action columns cannot shift field interpretation; ambiguous headers remain
unsupported. Recognized rows without proven complete inventory remain incomplete,
not unavailable and not eligible for unified Connect. A visible bare toolbar number
is not enough to invent a DOM selector or bypass completeness reconciliation.
