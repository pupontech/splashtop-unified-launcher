using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Deterministic invented browser fixture for page-aware Connect locating. A Windows
/// smoke runner may call this after creating its isolated AccountWebViewPane.
/// </summary>
internal static class ConnectLocatorSmokeTest
{
    public static async Task<object> RunAsync(AccountWebViewPane pane, string fixtureFolder)
    {
        ArgumentNullException.ThrowIfNull(pane);
        var root = Path.GetFullPath(fixtureFolder);
        Directory.CreateDirectory(root);
        pane.Core.SetVirtualHostNameToFolderMapping("my.splashtop.com", root, CoreWebView2HostResourceAccessKind.DenyCors);

        var pagedTarget = Row("Later page target", "target-device", "Synthetic", "target note");
        var paged = new[]
        {
            Row("Fixture A", "fixture-a", "Synthetic", "a"),
            Row("Fixture B", "fixture-b", "Synthetic", "b"),
            Row("Fixture C", "fixture-c", "Synthetic", "c"),
            pagedTarget,
            Row("Fixture E", "fixture-e", "Synthetic", "e")
        };
        var pagedResult = await RunPagedAsync(pane, root, "connect-paged.html", paged, 2, pagedTarget, 3);
        Require(pagedResult == "clicked" && await ReadStringAsync(pane, "window.__connectedTarget||''") == "target-device",
            "A complete paged inventory connects the exact later-page target after the extractor restores page one.");

        var wrongRowResult = await RunPagedAsync(pane, root, "connect-wrong-row.html", paged, 2, pagedTarget, 3,
            "window.__fixtureRows[3]={n:'Changed target',d:'wrong-device',g:'Synthetic',t:'target note',s:'Online'};");
        Require(wrongRowResult == "no-row" && await ReadNumberAsync(pane, "window.__connectClicks||0") == 0,
            "A changed live identity is refused without clicking a neighboring row.");

        var duplicateTarget = Row("Duplicate target", "duplicate-device", "Synthetic", "same note");
        var duplicates = new[]
        {
            Row("Fixture A", "fixture-a", "Synthetic", "a"), duplicateTarget,
            Row("Fixture C", "fixture-c", "Synthetic", "c"), duplicateTarget
        };
        var duplicateResult = await RunPagedAsync(pane, root, "connect-duplicates.html", duplicates, 2, duplicateTarget, 1);
        Require(duplicateResult == "ambiguous-row" && await ReadNumberAsync(pane, "window.__connectClicks||0") == 0,
            "An exact identity duplicated on another page is refused without clicking.");

        var navigationResult = await RunPagedAsync(pane, root, "connect-navigation.html", paged, 2, pagedTarget, 3,
            "window.__connectNavigate=true;");
        Require(navigationResult == "row-changed" && await ReadNumberAsync(pane, "window.__connectClicks||0") == 0,
            "A document URL change during pager traversal invalidates the action before click.");

        const int virtualCount = 18;
        var virtualRows = Enumerable.Range(1, virtualCount)
            .Select(index => Row($"Virtual node {index:D2}", $"virtual-{index:D2}", "Synthetic", string.Empty))
            .ToArray();
        var virtualTarget = virtualRows[^1];
        var virtualResult = await RunVirtualAsync(pane, root, virtualRows, virtualTarget, virtualCount - 1);
        Require(virtualResult == "clicked" && await ReadStringAsync(pane, "window.__connectedTarget||''") == virtualTarget.DeviceName,
            "A complete virtual inventory connects an exact target outside the initial rendered window.");

        return new
        {
            fixture = "invented local browser rows only",
            pagedLaterTarget = pagedResult,
            changedIdentity = wrongRowResult,
            crossPageDuplicate = duplicateResult,
            navigationDuringLocate = navigationResult,
            virtualLaterTarget = virtualResult
        };
    }

    private static async Task<string> RunPagedAsync(
        AccountWebViewPane pane,
        string root,
        string fileName,
        SyntheticRow[] rows,
        int pageSize,
        SyntheticRow target,
        int targetIndex,
        string? afterRefreshScript = null)
    {
        await File.WriteAllTextAsync(Path.Combine(root, fileName), BuildPagedHtml(rows, pageSize));
        var url = "https://my.splashtop.com/" + fileName;
        await pane.NavigateAndWaitAsync(url, TimeSpan.FromSeconds(15));
        var refresh = await pane.RequestInventoryAsync();
        Require(refresh == InventoryRefreshKind.Complete, $"Paged synthetic inventory is complete for {fileName}.");
        if (afterRefreshScript is not null) await pane.ExecuteScriptAsync(afterRefreshScript);
        await pane.ExecuteScriptAsync("window.__connectClicks=0;window.__connectedTarget='';");
        Require(pane.TryCaptureConnectTarget(targetIndex, target.ToExtractedRow(), out var captured) && captured is not null,
            $"The synthetic target is captured from complete inventory for {fileName}.");
        var result = await pane.ActivateConnectAsync(captured!);
        Console.Error.WriteLine($"SMOKE_CONNECT_RESULT={fileName}:{result}");
        return result;
    }

    private static async Task<string> RunVirtualAsync(
        AccountWebViewPane pane,
        string root,
        SyntheticRow[] rows,
        SyntheticRow target,
        int targetIndex)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "connect-virtual.html"), BuildVirtualHtml(rows));
        var url = "https://my.splashtop.com/connect-virtual.html";
        await pane.NavigateAndWaitAsync(url, TimeSpan.FromSeconds(15));
        var refresh = await pane.RequestInventoryAsync();
        Require(refresh == InventoryRefreshKind.Complete, "Virtual synthetic inventory is complete.");
        await pane.ExecuteScriptAsync("window.__connectClicks=0;window.__connectedTarget='';");
        Require(pane.TryCaptureConnectTarget(targetIndex, target.ToExtractedRow(), out var captured) && captured is not null,
            "The virtual target is captured from complete inventory.");
        return await pane.ActivateConnectAsync(captured!);
    }

    private static string BuildPagedHtml(SyntheticRow[] rows, int pageSize)
    {
        var data = JsonSerializer.Serialize(rows.Select(row => new { n = row.Name, d = row.DeviceName, g = row.Group, t = row.Notes, s = row.Status }));
        return "<!doctype html><meta charset='utf-8'><title>Invented paged list</title><body>" +
            "<table><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody id='rows'></tbody></table>" +
            "<nav aria-label='pagination'><button id='prev' aria-label='Previous page'>Previous</button><span id='range'></span><button id='next' aria-label='Next page'>Next</button></nav>" +
            "<script>window.__fixtureRows=" + data + ";window.__page=0;window.__connectClicks=0;window.__connectedTarget='';" +
            "function render(){var first=window.__page*" + pageSize + ";var slice=window.__fixtureRows.slice(first,first+" + pageSize + ");" +
            "document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><span aria-label=\"'+r.s+'\"></span><button aria-label=\"Connect\">Connect</button></td></tr>';}).join('');" +
            "Array.prototype.forEach.call(document.querySelectorAll('tbody button'),function(button,index){button.onclick=function(){window.__connectClicks++;window.__connectedTarget=slice[index].d;};});" +
            "document.getElementById('range').textContent=(first+1)+'-'+Math.min(window.__fixtureRows.length,first+" + pageSize + ")+' of '+window.__fixtureRows.length;" +
            "document.getElementById('prev').disabled=window.__page===0;document.getElementById('next').disabled=first+" + pageSize + ">=window.__fixtureRows.length;}" +
            "document.getElementById('prev').onclick=function(){window.__page--;render();};" +
            "document.getElementById('next').onclick=function(){window.__page++;render();if(window.__connectNavigate)setTimeout(function(){history.pushState({},'','/changed-location');},0);};render();</script></body></html>";
    }

    private static string BuildVirtualHtml(SyntheticRow[] rows)
    {
        var data = JsonSerializer.Serialize(rows.Select(row => new { n = row.Name, d = row.DeviceName, g = row.Group, t = row.Notes, s = row.Status }));
        const int rowHeight = 36;
        const int viewport = 144;
        return "<!doctype html><meta charset='utf-8'><title>Invented virtual list</title><body>" +
            "<div id='scroller' style='height:" + viewport + "px;overflow-y:auto'><div style='position:relative;height:" + (rows.Length * rowHeight) + "px'>" +
            "<table style='position:absolute;left:0;right:0;top:0'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody id='rows'></tbody></table></div></div>" +
            "<p role='status'>" + rows.Length + " computers</p><script>var all=" + data + ";var RH=" + rowHeight + ";var VH=" + viewport + ";" +
            "window.__connectClicks=0;window.__connectedTarget='';var scroller=document.getElementById('scroller');var table=document.querySelector('table');" +
            "function render(){var first=Math.floor(scroller.scrollTop/RH);var shown=all.slice(first,first+Math.ceil(VH/RH)+1);" +
            "document.getElementById('rows').innerHTML=shown.map(function(r){return '<tr style=\"height:'+RH+'px\"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><span aria-label=\"'+r.s+'\"></span><button aria-label=\"Connect\">Connect</button></td></tr>';}).join('');" +
            "Array.prototype.forEach.call(document.querySelectorAll('tbody button'),function(button,index){button.onclick=function(){window.__connectClicks++;window.__connectedTarget=shown[index].d;};});" +
            "table.style.transform='translateY('+(first*RH)+'px');}scroller.addEventListener('scroll',render);render();</script></body></html>";
    }

    private static async Task<string> ReadStringAsync(AccountWebViewPane pane, string script)
    {
        var raw = await pane.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
    }

    private static async Task<int> ReadNumberAsync(AccountWebViewPane pane, string script)
    {
        var raw = await pane.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<int>(raw);
    }

    private static SyntheticRow Row(string name, string device, string group, string notes) =>
        new(name, device, group, notes, "Online");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Connect locator smoke failed: " + message);
    }

    private sealed record SyntheticRow(string Name, string DeviceName, string Group, string Notes, string Status)
    {
        public ExtractedComputerRow ToExtractedRow() => new(Name, DeviceName, Group, Notes, true, Status);
    }
}
