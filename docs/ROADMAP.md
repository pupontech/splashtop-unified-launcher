# Roadmap and gates

## Current status (source documentation; not release evidence)

The repository has moved beyond the earlier Core-only planning scope. The current candidate is a Windows WPF unified GUI testing prototype with two isolated persistent WebView2 account panes, a native merged/searchable console inventory list, per-account refresh progress, a per-user allowlisted JSON inventory cache, and guarded Connect/official-client handoff. Source-defined Windows smoke tests exercise synthetic local browser fixtures. That does not establish current live console DOM compatibility or a successful owner session.

The current source does not claim unlimited account support, background status monitoring, numeric device identity where the console does not render one, full compatibility with every console layout/account size, or production readiness. Cached/last-read status is historical, not monitoring; the inventory cache is JSON, not SQLite, and never authorizes Connect. Partial, failed, cached, stale, or ambiguous inventory remains unable to Connect. Trusted HTTPS/default-port/top-document and native handoff confirmation protections are hard safety boundaries, not roadmap items to relax.

Owner authorization in `PROTOTYPE-AUTHORIZATION.md` superseded the prior Core-only implementation restriction and deferred earlier owner gates as blockers to prototype coding. Historical gates below are retained as project history/context. They do not prohibit the current prototype scope and are not evidence that those earlier live tests occurred. Live validation remains pending and belongs to the owner. No release/version/CI-success claim is made here. The parent owns final Windows CI and release verification.

## Historical validation dependency chain (preserved; not a current implementation ban)

The earlier roadmap requested human validation before Splashtop integration: (1) documented Business URI behavior for owner-selected Account A, (2) Account B/team ambiguity, (3) persistent isolated WebView2 login/profile proof, (4) current console structure and complete discovery, and (5) Connect/launch behavior, current CSV/export compatibility, and a validation report. Those gates were explicitly deferred by the owner to permit a working prototype. Their questions remain useful for owner acceptance; no worker may sign into a live account or dispatch to an actual remote client under this work assignment.

The old planning milestones also named direct URI launch, official web-console Connect fallback, unified WPF UI, cache/metadata/filters/diagnostics, and packaging. These are historical planning labels rather than current status. Current source contains a unified GUI and guarded observed-console handoff path, but current live account and client behavior is still unverified. Do not assume direct URI details or successful remote session behavior beyond what the owner actually observes.

## Current delivery and verification sequence

1. Parent verifies the Windows build/tests, both isolated WebView2 synthetic smoke runs, packaging, ZIP integrity and payload manifest for the exact candidate commit. A workflow artifact or pushed commit alone is not a published/verified release.
2. Parent publishes or identifies the exact prerelease ZIP intended for testing, with its actual version/tag, asset name, commit and checksum. Do not guess or mint a version in documentation. If no suitable published prerelease exists, owner testing waits for that exact asset.
3. Owner follows `PROTOTYPE-QUICKSTART.md` and every row of `PROTOTYPE-ACCEPTANCE.md` using the exact fixed version. Owner performs normal login/MFA and verifies the two actual accounts, complete inventory, status semantics, cache presentation, failure/partial safeguards, chooser options and actual target/result in the official client/web flow.
4. Record sanitized outcomes, FAIL and NOT RUN rows, artifact identity, and residual blockers. Synthetic fixture tests remain explicitly distinct from live console DOM evidence. Any inventory mismatch, untrusted/incorrect target, incomplete read presented as complete, or failed safety guard blocks acceptance until resolved.
5. Only after parent verification and owner acceptance may maintainers decide whether another prerelease or release is justified. No production readiness follows automatically from CI or a successful prototype test.

## Current limits and follow-up evidence

- Owner live validation remains pending: actual two-account login/profile persistence, current console DOM and full row-count reconciliation, semantic status interpretation, and actual Business client/web connection target/outcome.
- Synthetic fixture success is not live console DOM evidence. Hosted CI can verify only the synthetic behaviors it actually executes.
- Status is read-only snapshot state; unknown/cached/last-read values are not real-time monitoring.
- Inventory cache is an allowlisted local JSON file, not SQLite; its contents can still include sensitive inventory labels/metadata and cannot authorize a connection.
- The current UX is deliberately fixed to two accounts. No unrestricted multi-account claim is made.
- Historical legal/ToS research is not legal clearance. Seek appropriate written vendor guidance before recurring automated collection.

## Tracking

Earlier Phase 1/5 issues and approval gates document the historical validation plan. Current progress and assignment remain with the parent/board; this roadmap does not change board cards. Do not infer a completed gate or dispatch from the existence of code, fixtures, or documentation.