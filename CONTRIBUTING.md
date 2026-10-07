# Contributor and agent guardrails

- Read `README.md`, `docs/PROJECT-SPEC.md`, `docs/ROADMAP.md`, `docs/PROTOTYPE-AUTHORIZATION.md`, and `docs/IMPROVEMENT-SCOPE.md` before work. The project specification describes the final product; the authorization and improvement scope define current prototype work.
- Prior Phase 5/6 owner-before-implementation gates were superseded for prototype coding by `docs/PROTOTYPE-AUTHORIZATION.md`. Live account/session acceptance still belongs to the owner; synthetic CI never closes that gate.
- Never call Splashtop Open APIs, private REST/GraphQL endpoints, or authenticated endpoints outside the WebView2 page context. Never extract or replay cookies/tokens. Never bypass MFA, CAPTCHA, SSO or new-device verification.
- Never invent a computer or test result. The owner selects machines and completes live Windows sign-in/remote-session tests.
- Keep Splashtop DOM/data knowledge centralized in the prototype's parser/action modules. `SplashtopUnified.Splashtop` is the planned final module, not a requirement to restructure the current prototype before a focused fix. Tests use sanitized, explicitly synthetic fixtures.
- Never add undocumented `st-business:` arguments. The only initial allowed protocol is the validated documented remote-session URI.
- Never log passwords, cookies, tokens, session identifiers, full HTML dumps or production page script payloads.
- No sample account/device identifiers that look real. Use obvious synthetic fixtures and sanitize owner evidence before commit.
- Test Windows-only behavior on GitHub-hosted Windows runners; do not provision VMs or install software on the owner's machine. The owner performs final real-account acceptance.
- If a code change is eventually authorized, keep branches isolated, run full tests and Windows CI, and verify the exact final tree before reporting completion.
