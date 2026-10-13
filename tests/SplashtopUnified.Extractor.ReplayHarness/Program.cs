using System.Text.Json;
using SplashtopUnified.AccountBrowser;

if (args.Length == 2 && args[0] == "--validate")
{
    var outputRoot = Path.GetFullPath(args[1]);
    var resultPath = Path.Combine(outputRoot, "replay-results.json");
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

    var connectPath = Path.Combine(outputRoot, "connect-results.json");
    if (File.Exists(connectPath))
    {
        using var connect = JsonDocument.Parse(File.ReadAllText(connectPath));
        foreach (var item in connect.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("target").GetString() ?? "unknown";
            var expected = item.GetProperty("expected").GetString();
            var actual = item.GetProperty("status").GetString();
            var clicks = item.GetProperty("clicks").GetInt32();
            var wantedClicks = expected == "clicked" ? 1 : 0;
            if (!string.Equals(expected, actual, StringComparison.Ordinal) || clicks != wantedClicks)
            {
                failures.Add($"{name}: expected {expected} with {wantedClicks} click(s), observed {actual} with {clicks}");
            }
            else
            {
                Console.WriteLine($"PASS connect locator {name}: {actual}, {clicks} click(s)");
            }
        }
    }

    foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
    if (failures.Count > 0)
    {
        Console.WriteLine($"{failures.Count} failure(s)");
        return 1;
    }

    Console.WriteLine($"{results.RootElement.GetArrayLength()} extraction payloads and connect targets accepted");
    return 0;
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

// Connect locating targets: one extracted identity per fixture, exactly as the production
// extractor reports that row, restricted to rows the initial document renders. The host's
// page/window traversal is covered by the Windows smoke; this replay pins the single
// read/click step contract, which is the whole path for a list that fits one window.
const long deadline = 9_999_999_999_999;
var connectTargets = new[]
{
    new ConnectTarget("basic", "Fixture Desktop", "fixture-desktop", "Default Group", string.Empty, "Online", "clicked"),
    new ConnectTarget("reconciled-scrollable", "Reconciled 1", "fixture-1", "Synthetic", string.Empty, string.Empty, "clicked"),
    new ConnectTarget("nested-decorative", "Nested target", "nested-target", "Synthetic", string.Empty, string.Empty, "clicked"),
    new ConnectTarget("reordered", "Fixture Computer", "fixture-device", "Fixture Group", string.Empty, string.Empty, "clicked"),
    new ConnectTarget("virtual-18", "Fixture Node 01", "fixture-node-1", "Servers", string.Empty, string.Empty, "clicked"),
    new ConnectTarget("no-total-6014", "Fixture Node 001", "fixture-node-1", "Synthetic", string.Empty, string.Empty, "clicked"),
    new ConnectTarget("paged", "Fixture Host 1", "fixture-1", "Default Group", string.Empty, string.Empty, "clicked"),
    new ConnectTarget("basic", "Fixture Backup", "online-backup-01", "Default Group", string.Empty, string.Empty, "no-control"),
    new ConnectTarget("virtual-18", "Fixture Node 01", "changed-device", "Servers", string.Empty, string.Empty, "no-row")
};

var connectManifest = new List<object>();
for (var index = 0; index < connectTargets.Length; index++)
{
    var target = connectTargets[index];
    var url = $"https://my.splashtop.com/{target.Fixture}.html";
    var row = new ExtractedComputerRow(target.Name, target.DeviceName, target.Group, target.Notes,
        true, target.Status.Length == 0 ? null : target.Status);
    // One file per target, never per fixture name: a fixture can contribute a clickable
    // target and a refusal target, and the later file must not overwrite the earlier one.
    var stem = $"connect-{index:D2}-{target.Fixture}";
    File.WriteAllText(Path.Combine(output, $"{stem}.read.js"),
        ConnectLocatorScript.CreateStep("read", row, url, null, null, null, null, deadline));
    File.WriteAllText(Path.Combine(output, $"{stem}.click.js"),
        ConsoleInventoryActions.ActivateConnectScript(row, url, -1, null, null, deadline));
    connectManifest.Add(new
    {
        target = $"{target.Fixture}#{index:D2}",
        fixture = target.Fixture,
        device = target.DeviceName,
        url,
        expected = target.Expected,
        readScript = $"{stem}.read.js",
        clickScript = $"{stem}.click.js"
    });
}

File.WriteAllText(Path.Combine(output, "connect-manifest.json"),
    JsonSerializer.Serialize(connectManifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"generated {fixtures.Count} production fixtures, {connectTargets.Length} connect targets and the exact extractor script at {output}");
return 0;

internal sealed record ConnectTarget(
    string Fixture,
    string Name,
    string DeviceName,
    string Group,
    string Notes,
    string Status,
    string Expected);
