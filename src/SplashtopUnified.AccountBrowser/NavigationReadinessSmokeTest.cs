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
        const string file = "navigation-readiness-delayed.html";
        const string url = "https://my.splashtop.com/" + file;
        var inventoryHtml = InventoryFixture.ComputerListHtml()
            .Replace("fixture-desktop", "synthetic-readiness-device", StringComparison.Ordinal)
            .Replace("</body>", "<p role='status'>5 computers</p></body>", StringComparison.Ordinal);
        var payload = JsonSerializer.Serialize(inventoryHtml);
        var html = "<!doctype html><html><head><meta charset='utf-8'></head><body>" +
            "<main>Invented console awaiting hydration; not a live account.</main>" +
            "<script>setTimeout(function(){var parsed=new DOMParser().parseFromString(" + payload +
            ", 'text/html');document.body.innerHTML=parsed.body.innerHTML;window.__syntheticReadinessHydrated=true;},3000);</script></body></html>";
        await File.WriteAllTextAsync(Path.Combine(fixture, file), html);
        pane.Core.SetVirtualHostNameToFolderMapping("my.splashtop.com", fixture, CoreWebView2HostResourceAccessKind.DenyCors);
        while (snapshots.TryDequeue(out _)) { }
        await pane.NavigateAndWaitAsync(url, TimeSpan.FromSeconds(20));
        var started = Stopwatch.StartNew();
        AccountInventorySnapshot? observed = null;
        while (started.Elapsed < TimeSpan.FromSeconds(10))
        {
            observed = snapshots.LastOrDefault(snapshot => snapshot.AccountId == pane.AccountId &&
                snapshot.PageKind == ConsolePageKind.ComputerList && snapshot.Outcome == InventoryOutcome.Complete &&
                snapshot.RowCount == 5 && snapshot.ReportedTotal == 5 &&
                snapshot.Rows.Any(row => row.DeviceName == "synthetic-readiness-device"));
            if (observed is not null) break;
            await Task.Delay(25);
        }
        // Distinguish missing product discovery from a broken/nonhydrated fixture.
        var hydrated = await pane.ExecuteScriptAsync("window.__syntheticReadinessHydrated===true && document.querySelectorAll('#computers > tbody > tr').length===5");
        if (hydrated != "true") throw new InvalidOperationException("Navigation readiness fixture did not hydrate five synthetic rows.");
        if (observed is null) throw new InvalidOperationException("Navigation readiness: a delayed hydrated inventory was not automatically discovered; no manual Refresh was invoked.");
        return new { delayedHydrationDiscovered = true, manualRefreshInvoked = false,
            rows = observed.RowCount, reportedTotal = observed.ReportedTotal, elapsedMillis = started.ElapsedMilliseconds };
    }
}
