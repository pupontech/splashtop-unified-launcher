# Phase 5 validation report (not yet complete)

**Historical validation template:** live Phase 5 acceptance remains unrecorded here. This template is not evidence of live validation and its original phase dependencies are not a current implementation ban: `PROTOTYPE-AUTHORIZATION.md` superseded those coding gates. Current synthetic Windows build/runtime evidence and owner acceptance are tracked separately through the roadmap and prototype acceptance checklist. Retain `Not tested` instead of guessing.

## Environment

- Report date: pending Phase 5
- Splashtop Business version tested by URI: **Not tested**; the owner reports the installed client is **3.8.6.1**. This is the first compatibility baseline, not a hard version restriction.
- Web-console URLs tested and region redirects seen: **Not tested**. Candidate user-visible pages from project requirements: `https://my.splashtop.com/computers`, `https://my.splashtop.eu/computers`, and `/property/general/{id}?iframe=true` on the correct account's region host. Do not assume a region until observed.
- Current stable engineering baseline from public docs (not a user-device test): .NET 10 LTS; Microsoft.Web.WebView2 1.0.4258.31 was the latest package shown 2026-10-04. Recheck before pinning dependencies.

## Terms of Service review

- Global Terms: https://www.splashtop.com/legal/terms-of-service
- EU Terms: https://www.splashtop.com/legal/terms-of-service-eu
- Finding as of 2026-10-04: The reviewed Terms do not expressly name web-console automation/scraping. They include restrictions concerning collection/storage of third-party information without consent, disruption/harm, and specified use for monitoring Splashtop Services/Software or competitive purposes. The exact application of those clauses to an authorized user's local inventory browser and periodic status refresh is not established here. This is not legal clearance. Obtain written Splashtop/customer compliance guidance before broad recurring collection; minimize scope/rate and never collect unrelated personal data. See `RESEARCH-2026-10-04.md` for sourced analysis.
- Live/tenant-specific counsel determination: **Pending**.

## Required live results

| Validation item | Result |
|---|---|
| Direct URI result — Account A | Not tested; Phase 1 awaits owner-selected known machine and Windows test |
| Direct URI result — Account B / both client accounts | Not tested; Phase 2 awaits Phase 1 |
| Same-email / multi-team result | Not applicable or not tested; determine account/team arrangement in Phase 2 |
| WebView2 dual-login, restart persistence, MFA/new-device verification | Owner live validation not recorded; synthetic browser runtime proof does not establish real-account sign-in |
| Computer-list discovery method and completeness (parsed vs console total) | Not tested; Phase 4 |
| MAC extractable? From where? | Not tested; Phase 5 |
| `st-business:` URI present in page DOM? | Not tested; Phase 5 |
| What the web Connect button actually does | Not tested; Phase 5 |
| Shared-MAC reinstall-orphan launch result | Not tested; owner-selected test needed |
| Web-console Connect fallback result | Not tested; Phase 5 |
| Live updates vs reload needed for status | Not tested; Phase 4/5 |
| Current CSV export columns/identifiers | Not tested; no actual current export supplied |

## Parser completeness and health

- Account(s) tested: pending
- Console-reported total(s): pending
- Parsed entries per account: pending
- Page recognition / login detection: pending
- Pagination or in-memory-list proof: pending
- Parsed / rejected / launch-info counts: pending
- Parser version and fixture IDs: pending
- Sanitization review and whether any data was excluded: pending

## Risks and architecture changes

1. **URI targeting:** documentation exposes account email + MAC but no team parameter. If an email can represent more than one team, use the web-console path unless live tests prove exact selection.
2. **MAC collisions and reinstall orphans:** MAC is not identity. Use `(AccountId, SplashtopComputerId)` for records and web fallback for shared MACs unless the current direct URI is proven to select the correct record.
3. **Fragile SPA and completeness:** a visible first page is insufficient. Compare to console totals and preserve the last good cache on partial/parser failures.
4. **Compliance:** the current Terms do not expressly authorize automated collection. Obtain written guidance before recurring synchronization.
5. **Historical implementation gate:** the original Phase 6 approval sequence below is retained as history. It does not block the owner-authorized prototype work; see `PROTOTYPE-AUTHORIZATION.md`. Owner live acceptance remains pending.

## Approval

- Owner review decision: **Pending**
- Approved architecture changes: **Pending**
- Date/record: **Pending**
