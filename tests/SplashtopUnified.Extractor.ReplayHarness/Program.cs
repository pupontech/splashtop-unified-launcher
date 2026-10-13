using System.Text.Json;
using SplashtopUnified.AccountBrowser;

if (args.Length == 2 && args[0] == "--validate")
{
    var resultPath = Path.Combine(Path.GetFullPath(args[1]), "replay-results.json");
    using var results = JsonDocument.Parse(File.ReadAllText(resultPath));
    var failures = new List<string>();
    foreach (var item in results.RootElement.EnumerateArray())
    {
        var name = item.GetProperty("fixture").GetString() ?? "unknown";
        var final = item.GetProperty("final");
        if (final.ValueKind != JsonValueKind.Object ||
            !ConsoleInventoryExtractor.TryParse(final.GetRawText(), "trusted-origin-replay", out var parsed) || parsed is null)
        {
            failures.Add($"{name}: final payload rejected by production host validator");
            continue;
        }

        var expectedOutcome = final.GetProperty("outcome").GetString();
        var expectedRows = final.GetProperty("rowCount").GetInt32();
        if (!string.Equals(parsed.Outcome.ToString(), expectedOutcome, StringComparison.OrdinalIgnoreCase) || parsed.Rows.Count != expectedRows)
        {
            failures.Add($"{name}: host validator changed outcome/count to {parsed.Outcome}/{parsed.Rows.Count}");
        }
        else
        {
            Console.WriteLine($"PASS host validator {name}: {parsed.Outcome} rows={parsed.Rows.Count}");
        }
    }
    foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
    Console.WriteLine($"{results.RootElement.GetArrayLength() - failures.Count}/{results.RootElement.GetArrayLength()} final payloads accepted by production host validator");
    return failures.Count == 0 ? 0 : 1;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ExtractorReplayHarness <output-directory> | --validate <output-directory>");
    return 2;
}

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output, "extract.js"), ConsoleInventoryExtractor.CreateExtractScript("trusted-origin-replay"));
File.WriteAllText(Path.Combine(output, "diagnostics.js"), ConsoleInventoryExtractor.DiagnosticsScript);

var fixtures = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["basic"] = InventoryFixture.ComputerListHtml(),
    ["paged"] = InventoryFixture.PagedListHtml(),
    ["paged-duplicates"] = InventoryFixture.PagedDuplicateRowsNoTotalHtml(),
    ["virtual-identical"] = InventoryFixture.VirtualisedIdenticalRowsNoTotalHtml(),
    ["virtual-18"] = InventoryFixture.VirtualisedListHtml(),
    ["virtual-240"] = InventoryFixture.LargeVirtualisedListHtml(),
    ["no-total-6014"] = InventoryFixture.NoTotalVirtualisedListHtml(136, 6014),
    ["no-total-25508"] = InventoryFixture.NoTotalVirtualisedListHtml(579, 25508),
    ["no-total-distant-duplicate"] = InventoryFixture.NoTotalVirtualisedListHtml(136, 6014, distantDuplicate: true),
    ["short-viewport"] = InventoryFixture.ShortViewportVirtualisedListHtml(),
    ["slow-repaint"] = InventoryFixture.SlowRepaintVirtualisedListHtml(),
    ["nested-changing-status"] = InventoryFixture.NestedScrollWithChangingStatusHtml(),
    ["reconciled-scrollable"] = InventoryFixture.ReconciledScrollableListHtml(),
    ["reordered"] = InventoryFixture.ReorderedNoNotesListHtml(),
    ["nested-decorative"] = InventoryFixture.NestedDecorativeRowsHtml(),
    ["ambiguous-headers"] = InventoryFixture.AmbiguousComputerNameHeadersHtml(),
    ["chooser"] = InventoryFixture.ChooserListHtml(),
    ["unsupported"] = InventoryFixture.UnsupportedContentHtml(),
    ["login"] = InventoryFixture.LoginHtml()
};

foreach (var fixture in fixtures)
{
    File.WriteAllText(Path.Combine(output, fixture.Key + ".html"), fixture.Value);
}
File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(fixtures.Keys));
Console.WriteLine($"generated {fixtures.Count} production fixtures and exact extractor script at {output}");
return 0;
