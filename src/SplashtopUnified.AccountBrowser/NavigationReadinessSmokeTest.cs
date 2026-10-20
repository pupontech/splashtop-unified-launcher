using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SplashtopUnified.AccountBrowser;

/// <summary>Invented local DOM hydration, never a captured or live account page.</summary>
internal static class NavigationReadinessSmokeTest
{
    public static async Task<object> RunAsync(AccountWebViewPane pane, string fixture,
        ConcurrentQueue<AccountInventorySnapshot> snapshots)
    {
        var delayedGrid = await RunCaseAsync(pane, fixture, snapshots, headerOnly: false);
        var delayedRows = await RunCaseAsync(pane, fixture, snapshots, headerOnly: true);
        return new { delayedGrid, delayedRows };
    }

    private static async Task<object> RunCaseAsync(AccountWebViewPane pane, string fixture,
        ConcurrentQueue<AccountInventorySnapshot> snapshots, bool headerOnly)
    {
        var file = headerOnly ? "navigation-readiness-rows.html" : "navigation-readiness-delayed.html";
        var url = "https://my.splashtop.com/" + file;
        var deviceName = headerOnly ? "synthetic-readiness-rows" : "synthetic-readiness-device";
        var inventoryHtml = InventoryFixture.ComputerListHtml()
            .Replace("fixture-desktop", deviceName, StringComparison.Ordinal)
            .Replace("</body>", "<p role='status'>5 computers</p></body>", StringComparison.Ordinal);
        var payload = JsonSerializer.Serialize(inventoryHtml);
        var initialBody = "<main>Invented console awaiting hydration; not a live account.</main>";
        if (headerOnly)
        {
            var bodyStart = inventoryHtml.IndexOf("<body>", StringComparison.Ordinal) + "<body>".Length;
            var bodyEnd = inventoryHtml.IndexOf("</body>", StringComparison.Ordinal);
            initialBody = inventoryHtml[bodyStart..bodyEnd];
            var rowsStart = initialBody.IndexOf("<tbody>", StringComparison.Ordinal) + "<tbody>".Length;
            var rowsEnd = initialBody.IndexOf("</tbody>", StringComparison.Ordinal);
            initialBody = initialBody[..rowsStart] + initialBody[rowsEnd..];
        }
        var html = "<!doctype html><html><head><meta charset='utf-8'></head><body>" + initialBody +
            "<script>window.__syntheticHydrate=function(){var parsed=new DOMParser().parseFromString(" + payload +
            ", 'text/html');document.body.innerHTML=parsed.body.innerHTML;window.__syntheticReadinessHydrated=true;};</script></body></html>";
        await File.WriteAllTextAsync(Path.Combine(fixture, file), html);
        pane.Core.SetVirtualHostNameToFolderMapping("my.splashtop.com", fixture, CoreWebView2HostResourceAccessKind.DenyCors);
        while (snapshots.TryDequeue(out _)) { }
        var started = Stopwatch.StartNew();
        await pane.NavigateAndWaitAsync(url, TimeSpan.FromSeconds(20));
        var initiallyUnsupported = await pane.ExecuteScriptAsync("location.href===" + JsonSerializer.Serialize(url) +
            " && !window.__syntheticReadinessHydrated && document.querySelectorAll('table,[role=grid],[role=table]').length===" + (headerOnly ? "1" : "0") +
            " && document.querySelectorAll('#computers > tbody > tr').length===0 && typeof window.__syntheticHydrate==='function'");
        if (initiallyUnsupported != "true") throw new InvalidOperationException("Navigation readiness fixture was not observed before hydration.");
        // Arm hydration only AFTER proving the real page has no inventory. A slow
        // navigation can never turn this into an already-hydrated-page false positive.
        await pane.ExecuteScriptAsync("setTimeout(window.__syntheticHydrate,3000);void 0;");
        AccountInventorySnapshot? observed = null;
        while (started.Elapsed < TimeSpan.FromSeconds(10))
        {
            observed = snapshots.LastOrDefault(snapshot => snapshot.AccountId == pane.AccountId &&
                snapshot.PageKind == ConsolePageKind.ComputerList && snapshot.Outcome == InventoryOutcome.Complete &&
                snapshot.RowCount == 5 && snapshot.ReportedTotal == 5 &&
                snapshot.Rows.Any(row => row.DeviceName == deviceName));
            if (observed is not null) break;
            await Task.Delay(25);
        }
        // Distinguish missing product discovery from a broken/nonhydrated fixture.
        var hydrated = await pane.ExecuteScriptAsync("window.__syntheticReadinessHydrated===true && document.querySelectorAll('#computers > tbody > tr').length===5");
        if (hydrated != "true") throw new InvalidOperationException("Navigation readiness fixture did not hydrate five synthetic rows.");
        if (observed is null) throw new InvalidOperationException("Navigation readiness: a delayed hydrated inventory was not automatically discovered (headerOnly=" + headerOnly + "); no manual Refresh was invoked.");
        return new { delayedHydrationDiscovered = true, manualRefreshInvoked = false,
            headerOnly, rows = observed.RowCount, reportedTotal = observed.ReportedTotal, elapsedMillis = started.ElapsedMilliseconds };
    }
}
