# Focused improvement review

Baseline: deae440957f7bf4e16b4a0dc052561124b45ccae. Scope: owner-approved small reliability improvements, not a full architecture migration.

## Defect review (OCR delegation)

OCR v1.12.12 selected eight source/test/package entries. All eight were reviewed under its default correctness, concurrency, security, performance, maintainability and coverage rules. Documentation and project files were reviewed separately despite unsupported-extension exclusions; `.hermes/verification-state.json` is local evidence only and excluded from delivery.

- ConsoleInspection.cs: bounded waiting does not cancel WebView2; retains single-read ownership after timeout; only reclaims a completed timed-out operation at next entry; observes late faults without modifying newer request state; cancels unused deadline timer. Unsupported delay ranges were observed RED (underlying read started before rejection), then fixed to reject before starting.
- AccountWebViewPane.cs: inspection captures origin, document URL and navigation generation, rechecks after await, and rejects closure/navigation. Exceptions do not supply report text. Refresh returns the canonical typed kind directly.
- AccountBrowserWindow.cs: independent pane inspections start together, results retain input order and fixed failure text. Refresh retains event cleanup, parallel execution and window-identity checks.
- UnifiedInventoryWindow.cs: one typed refresh kind replaces redundant boolean parameters. Progress/value and existing messages are preserved.
- BrowserSmokeTest.cs and Build-AccountBrowser.ps1: new inspection recognition/no-row-data gate must be true in both Windows runtime environments before packaging.
- Classification tests: call the real classification interface directly, not reflection; all six existing behavior cases pass.
- Inspection tests: bounded hung read, busy while original task remains pending, recovery after completion/late fault, independent account, changed page, concurrent calls and invalid timeout. The tests use actual pending tasks, not fabricated WebView2 results.

No unresolved High/Critical defect was established in this changed scope. The initial worker's late-completion callback could manipulate newer ownership; the parent removed callback ownership mutation altogether in the green/refactor phase. No runtime reproduction of that narrow race is claimed.

## Spec axis

Current prototype authorization and owner-approved improvement scope apply, not every final-product requirement in PROJECT-SPEC.md. Inspection remains read-only; connection completeness/origin/identity guards are unchanged. Synthetic Windows verification does not close owner live acceptance. CONTRIBUTING.md and the historical validation template now explicitly distinguish superseded coding gates from pending live acceptance.

Summary: scope/gate documentation mismatch resolved; no final-product completeness claim.

## Standards axis

Judgment: duplicated refresh-state representations increased change fan-out. A single InventoryRefreshKind now travels through the pane, coordinator and UI. The inspection module concentrates deadline/ownership/error behavior behind a small result-returning interface. Reflection-based test plumbing and optional inclusion of an established source file were removed.

Summary: repeated enum conversion/contradictory flags removed; no MVVM/SQLite rewrite introduced.

## Existing follow-up, not fixed here

Confirmed fail-closed actionability gap: the inventory walker restores the first page, but Connect locates by index in currently rendered rows. Later-page and off-window targets may therefore be refused. ConsoleInventoryActions.cs checks full identity and unique matching row, so this is not evidence of wrong-target activation. A page-aware action locator requires its own behavioral fixture and design decision. No new cache corruption or unsafe activation was established; cache suite passes 36 tests.

## Evidence and limitations

Three fresh Luna probes verified gpt-6-luna. Two read-only Luna reports completed; the inspection lane produced source/tests but exceeded its timebox and was stopped. Parent verified no surviving lane process, recovered recorded RED recovery-test output, completed the module review and added the timeout-range RED/GREEN regression. No partial worker result was treated as final acceptance. Some requested Matt skills were absent in worker profiles; the parent loaded the installed skill and owns the final Spec/Standards assessment.

All seven local suites pass: 241 tests, zero skipped. Release WPF cross-build: zero warnings/errors. Hosted Windows proof and package verification are recorded separately for the final commit.

Optional independent Gemini concurrency consultation was unavailable because Antigravity is not authenticated. No Google credentials were requested, no dependency or profile changes were made, and this optional failure is not presented as a review pass.
