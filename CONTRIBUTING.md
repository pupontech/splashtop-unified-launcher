# Contributor and agent guardrails

- Read `README.md`, `docs/PROJECT-SPEC.md`, `docs/ROADMAP.md`, and the latest validation report before work.
- Do not start Phase 6 before Phase 5 is complete and the owner explicitly approves the report.
- Never call Splashtop Open APIs, private REST/GraphQL endpoints, or authenticated endpoints outside the WebView2 page context. Never extract or replay cookies/tokens. Never bypass MFA, CAPTCHA, SSO or new-device verification.
- Never invent a computer or test result. The owner selects machines and completes live Windows sign-in/remote-session tests.
- Keep all Splashtop DOM/data knowledge inside `SplashtopUnified.Splashtop`. Unit tests use sanitized fixtures.
- Never add undocumented `st-business:` arguments. The only initial allowed protocol is the validated documented remote-session URI.
- Never log passwords, cookies, tokens, session identifiers, full HTML dumps or production page script payloads.
- No sample account/device identifiers that look real. Use obvious synthetic fixtures and sanitize owner evidence before commit.
- Test Windows-only behavior on GitHub-hosted Windows runners; do not provision VMs or install software on the owner's machine. The owner performs final real-account acceptance.
- If a code change is eventually authorized, keep branches isolated, run full tests and Windows CI, and verify the exact final tree before reporting completion.
