# Trusted-origin production extractor replay

This is a deterministic browser behavior regression for `ConsoleInventoryExtractor` and `InventoryFixture`. The .NET harness links the production C# files directly, generates actual fixture HTML and the exact request-correlated production script, and runs the 19 extraction smoke shapes in an installed Chromium using CDP.

The runner navigates only to `https://my.splashtop.com/<fixture>.html`. It intercepts every request with CDP Fetch, fulfills each exact document from the just-generated local fixture bytes, rejects all other requests, and starts Chromium with hostname resolution denied. It does not bind port 443, disable TLS validation, contact a Splashtop account, or use captured account data. `--no-sandbox` is used only for this isolated synthetic replay when running Chromium as root.

Example from the repository root (use a scratch directory, not a repo build-output directory):

```sh
OUT="$(mktemp -d -t splashtop-replay.XXXXXXXX)"
dotnet run --project tests/SplashtopUnified.Extractor.ReplayHarness/SplashtopUnified.Extractor.ReplayHarness.csproj -- "$OUT"
python3 tests/SplashtopUnified.Extractor.ReplayHarness/trusted_origin_replay.py "$OUT" "$(command -v chromium)"
dotnet run --project tests/SplashtopUnified.Extractor.ReplayHarness/SplashtopUnified.Extractor.ReplayHarness.csproj -- --validate "$OUT"
```

The browser run fails unless all fixture outcomes, counts, terminal rows, geometry, duplicate safety, local fulfillment, and absence of page/runtime errors match expectations. The .NET validator mode then parses every final Chromium payload with the production host validator. Replay success is Linux Chromium evidence only; it is not Windows WebView2/WPF, hosted CI, or live-console proof.
