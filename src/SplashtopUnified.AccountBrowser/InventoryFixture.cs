namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Builds the local fixture page used to prove the inventory extractor at runtime.
/// The markup is invented from the column labels observed in the official Splashtop
/// support screenshot (Computer Name / Device Name / Group and optional Notes plus a per-row Connect
/// control); it is NOT captured Splashtop DOM and contains no real account data.
/// </summary>
internal static class InventoryFixture
{
    public const string VirtualHost = "my.splashtop.com";

    public static string ComputerListHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title><style>.sorted::after{content:' ▲';}</style></head><body>" +
        "<div class='toolbar'>" +
        "  <button>Add Computer</button><button>Business App</button><button aria-label='Refresh'>Refresh</button>" +
        "  <button aria-label='Filter'>Filter</button><input type='search' aria-label='Search' />" +
        "</div>" +
        "<table id='computers'>" +
        "  <thead><tr><th class='sorted'>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
        "  <tbody>" +
        Row("Fixture Desktop", "fixture-desktop", "Default Group", string.Empty, true, "Online") +
        Row("Fixture Server", "fixture-server", "Servers", "lab", true, "Offline") +
        Row("Fixture VM", "fixture-vm", "VMs", string.Empty, true, "In use") +
        Row("Fixture VM", "fixture-vm-2", "VMs", "duplicate display name", false) +
        // Device name contains a status word but the row carries no indicator, so no status
        // may be inferred from the name.
        Row("Fixture Backup", "online-backup-01", "Default Group", string.Empty, false) +
        "  </tbody>" +
        "</table></body></html>";

    /// <summary>
    /// Invented semantic variant: an unlabeled leading icon column, reordered known
    /// columns, no Notes column, and trailing unlabeled action columns. It is not captured
    /// console DOM and deliberately tests that blank columns do not shift row identity.
    /// </summary>
    public static string ReorderedNoNotesListHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Reordered computers</title><style>.sorted::after{content:' ▲';}</style></head><body>" +
        "<div style='width:420px;overflow-x:auto'><table id='computers'><thead><tr>" +
        "<th></th><th>Group</th><th>Device Name</th><th class='sorted'>Computer Name</th><th></th><th></th>" +
        "</tr></thead><tbody>" +
        "<tr><td><table aria-hidden='true'><tbody><tr><td><span>▣</span></td></tr></tbody></table></td><td>Fixture Group</td><td>fixture-device</td>" +
        "<td>Fixture Computer</td><td><button aria-label='Connect' onclick=\"window.__connectClicks=(window.__connectClicks||0)+1\">Connect</button></td>" +
        "<td><button aria-label='More actions'>...</button></td></tr>" +
        "</tbody></table></div></body></html>";

    // Invented nested markup: a decorative child row deliberately has the same cell
    // count as real inventory rows. It must not shift the second target's action index.
    public static string NestedDecorativeRowsHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Nested synthetic decoration</title></head><body>" +
        "<table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody>" +
        "<tr><td>First fixture</td><td>first-device</td><td>Synthetic</td><td></td><td><table><tbody><tr>" +
        "<td>decorative one</td><td>decorative two</td><td>decorative three</td><td>decorative four</td><td>decorative five</td>" +
        "</tr></tbody></table></td></tr>" +
        "<tr><td>Nested target</td><td>nested-target</td><td>Synthetic</td><td></td><td>" +
        "<button aria-label='Connect' onclick=\"window.__connectClicks=(window.__connectClicks||0)+1\">Connect</button></td></tr>" +
        "</tbody></table></body></html>";

    /// <summary>
    /// Invented shape of the owner report: the table sits inside an inner scrollable
    /// region and the page root also scrolls, while the row renderer rewrites status
    /// indicators (and disables Connect) on every scroll step. Scrolling the inner
    /// region must be recognized as the owner so the full list is read once, status is
    /// an observation rather than a new row, and no extra records are fabricated.
    /// </summary>
    public static string NestedScrollWithChangingStatusHtml()
    {
        const int total = 48;
        var rows = string.Join(",", Enumerable.Range(1, total).Select(i => $"{{n:'Fixture Node {i:D2}',d:'fixture-node-{i}'}}"));
        return "<!doctype html><html><head><meta charset='utf-8'><title>Synthetic nested scroll status</title></head><body>" +
            "<div id='page' style='height:200px;overflow-y:auto'><div id='scroller' style='height:320px;overflow-y:auto'>" +
            "<div style='position:relative;height:" + (total * 40) + "px'>" +
            "<table id='computers' style='position:absolute;left:0;right:0;top:0'><thead><tr><th></th><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody id='rows'></tbody></table>" +
            "</div></div><div style='height:1920px'></div></div>" +
            "<p role='status'>" + total + " computers</p>" +
            "<script>var all=[" + rows + "];var RH=40;var scroller=document.getElementById('scroller');var table=document.getElementById('computers');var pulse=0;" +
            "function render(){var start=Math.floor(scroller.scrollTop/RH);var slice=all.slice(start,start+16);" +
            "document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style=\"height:'+RH+'px\"><td></td><td>'+r.n+'</td><td>'+r.d+'</td><td>Synthetic</td><td></td><td><button aria-label=\"Connect\">Connect</button></td></tr>';}).join('');" +
            "table.style.transform='translateY('+(start*RH)+'px)';}" +
            "function restyle(){var labels=['Online','Offline','In use'];pulse=(pulse+1)%labels.length;" +
            "Array.prototype.forEach.call(document.querySelectorAll('#rows > tr'),function(row){var cell=row.cells[1];var text=cell.textContent;var dot=document.createElement('span');dot.setAttribute('aria-label',labels[pulse]);cell.replaceChildren(dot,document.createTextNode(text));});}" +
            "scroller.addEventListener('scroll',function(){render();restyle();});render();</script></body></html>";
    }

    public static string AmbiguousComputerNameHeadersHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Ambiguous headers</title></head><body>" +
        "<table><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Computer Name</th></tr></thead>" +
        "<tbody><tr><td>Should not publish</td><td>synthetic-device</td><td>Synthetic</td><td>also ambiguous</td></tr></tbody></table>" +
        "</body></html>";

    public static string UnsupportedContentHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Unsupported synthetic content</title></head><body>" +
        "<main><h1>Unsupported synthetic content</h1><p>This is not a computer list.</p></main></body></html>";

    public static string LoginHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Sign in</title></head><body>" +
        "<form id='login'><h1>Sign in</h1>" +
        "<input type='email' name='email' autocomplete='username' />" +
        "<input type='password' name='password' autocomplete='current-password' />" +
        "<button type='submit'>Sign in</button></form></body></html>";

    /// <summary>
    /// A list that only ever renders one page at a time, the shape that made the unified
    /// list show only part of a long account. The markup is invented from the observed
    /// column labels; it is not captured console DOM.
    /// </summary>
    public static string PagedListHtml()
    {
        var rows = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            var name = i is 3 or 7 ? "Fixture VM" : $"Fixture Host {i}";
            rows.Add($"{{n:'{name}',d:'fixture-{i}',g:'{(i <= 3 ? "Default Group" : "VMs")}',t:'{(i % 2 == 0 ? "row " + i : string.Empty)}'}}");
        }

        return "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title></head><body>" +
            "<table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
            "<tbody id='rows'></tbody></table>" +
            "<nav id='pager' aria-label='pagination'><button id='prev' aria-label='Previous page'>Previous</button>" +
            "<span id='range'></span><button id='next' aria-label='Next page'>Next</button></nav>" +
            "<script>" +
            "var all=[" + string.Join(",", rows) + "];var size=3;var page=0;" +
            "function render(){var slice=all.slice(page*size,page*size+size);" +
            "document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label=\"Connect\">Connect</button></td><td><button aria-label=\"More actions\">...</button></td></tr>';}).join('');" +
            "document.getElementById('range').textContent=(page*size+1)+'-'+Math.min(all.length,(page+1)*size)+' of '+all.length;" +
            "document.getElementById('next').disabled=(page+1)*size>=all.length;document.getElementById('prev').disabled=page===0;}" +
            "document.getElementById('next').onclick=function(){page++;render();};" +
            "document.getElementById('prev').onclick=function(){page--;render();};render();" +
            "</script></body></html>";
    }

    public static string PagedDuplicateRowsNoTotalHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Duplicate rows without a total</title></head><body>" +
        "<table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody id='rows'></tbody></table>" +
        "<nav aria-label='pagination'><button id='prev' aria-label='Previous page'>Previous</button>" +
        "<span id='page' aria-current='page'></span><button id='next' aria-label='Next page'>Next</button></nav>" +
        "<script>var pages=[[" +
        "{n:'Repeated device',d:'shared-device',g:'Synthetic',t:'notes A'}," +
        "{n:'Exact duplicate',d:'same-device',g:'Synthetic',t:'same notes'}],[" +
        "{n:'Repeated device',d:'shared-device',g:'Synthetic',t:'notes B'}," +
        "{n:'Exact duplicate',d:'same-device',g:'Synthetic',t:'same notes'}]];var page=0;" +
        "function render(){document.getElementById('rows').innerHTML=pages[page].map(function(r){return '<tr><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label=\"Connect\">Connect</button></td></tr>';}).join('');" +
        "document.getElementById('page').textContent='Page '+(page+1);document.getElementById('prev').disabled=page===0;document.getElementById('next').disabled=page===pages.length-1;}" +
        "document.getElementById('next').onclick=function(){page++;render();};document.getElementById('prev').onclick=function(){page--;render();};render();</script></body></html>";

    public static string VirtualisedIdenticalRowsNoTotalHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Indistinguishable virtual rows</title></head><body>" +
        "<div id='scroller' style='height:80px;overflow-y:auto'><div style='position:relative;height:240px'>" +
        "<table id='computers' style='position:absolute;left:0;right:0;top:0'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody id='rows'></tbody></table></div></div>" +
        "<script>var all=[{n:'Exact duplicate',d:'same-device',g:'Synthetic',t:'same notes'},{n:'Exact duplicate',d:'same-device',g:'Synthetic',t:'same notes'}," +
        "{n:'Virtual 3',d:'virtual-3',g:'Synthetic',t:''},{n:'Virtual 4',d:'virtual-4',g:'Synthetic',t:''},{n:'Virtual 5',d:'virtual-5',g:'Synthetic',t:''}];" +
        "var scroller=document.getElementById('scroller');var table=document.getElementById('computers');function render(){var start=Math.floor(scroller.scrollTop/40);" +
        "var slice=all.slice(start,start+3);document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style=\"height:40px\"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label=\"Connect\">Connect</button></td></tr>';}).join('');" +
        "table.style.transform='translateY('+(start*40)+'px)';}scroller.addEventListener('scroll',render);render();</script></body></html>";

    /// <summary>
    /// A virtualised list that only renders the rows near the viewport at any moment, the
    /// other shape that truncates a long account. Invented markup, not captured DOM.
    /// </summary>
    public static string VirtualisedListHtml()
    {
        const int total = 18;
        const int rowHeight = 36;
        const int viewport = 180;
        var rows = new List<string>();
        for (var i = 1; i <= total; i++)
        {
            rows.Add($"{{n:'Fixture Node {i:D2}',d:'fixture-node-{i}',g:'Servers',t:''}}");
        }

        return "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title></head><body>" +
            $"<div id='scroller' style='height:{viewport}px;overflow-y:auto'>" +
            $"<div id='canvas' style='position:relative;height:{total * rowHeight}px'>" +
            "<table id='computers' style='position:absolute;left:0;right:0;top:0'>" +
            "<thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
            "<tbody id='rows'></tbody></table></div></div>" +
            $"<p role='status'>{total} computers</p>" +
            "<script>" +
            "var all=[" + string.Join(",", rows) + "];var RH=" + rowHeight + ";var VH=" + viewport + ";" +
            "var scroller=document.getElementById('scroller');var table=document.getElementById('computers');" +
            "function render(){var start=Math.floor(scroller.scrollTop/RH);var count=Math.ceil(VH/RH)+1;" +
            "var slice=all.slice(start,start+count);" +
            "document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style=\"height:'+RH+'px\"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label=\"Connect\">Connect</button></td><td><button aria-label=\"More actions\">...</button></td></tr>';}).join('');" +
            "table.style.transform='translateY('+(start*RH)+'px)';}" +
            "scroller.addEventListener('scroll',render);render();" +
            "</script></body></html>";
    }

    /// <summary>
    /// A larger virtualised list (240 rows, one short window rendered at a time) used to
    /// bound the cost of walking a long account. Invented markup, not captured DOM.
    /// </summary>
    public static string LargeVirtualisedListHtml()
    {
        const int total = 240;
        const int rowHeight = 12;
        const int viewport = 180;
        var rows = new List<string>();
        for (var i = 1; i <= total; i++)
        {
            rows.Add($"{{n:'Fixture Node {i:D3}',d:'fixture-node-{i}',g:'Servers',t:''}}");
        }

        return "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title></head><body>" +
            $"<div id='scroller' style='height:{viewport}px;overflow-y:auto'>" +
            $"<div id='canvas' style='position:relative;height:{total * rowHeight}px'>" +
            "<table id='computers' style='position:absolute;left:0;right:0;top:0'>" +
            "<thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
            "<tbody id='rows'></tbody></table></div></div>" +
            $"<p role='status'>{total} computers</p>" +
            "<script>" +
            "var all=[" + string.Join(",", rows) + "];var RH=" + rowHeight + ";var VH=" + viewport + ";" +
            "var scroller=document.getElementById('scroller');var table=document.getElementById('computers');" +
            "function render(){var start=Math.floor(scroller.scrollTop/RH);var count=Math.ceil(VH/RH)+1;" +
            "var slice=all.slice(start,start+count);" +
            "document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style=\"height:'+RH+'px\"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label=\"Connect\">Connect</button></td><td><button aria-label=\"More actions\">...</button></td></tr>';}).join('');" +
            "table.style.transform='translateY('+(start*RH)+'px)';}" +
            "scroller.addEventListener('scroll',render);render();" +
            "</script></body></html>";
    }

    /// <summary>
    /// A list page that also shows the documented connect chooser, used to prove the choice
    /// is read and applied without the user touching the split view. Option labels are the
    /// exact official labels; the surrounding markup is invented, not captured DOM.
    /// </summary>
    public static string ChooserListHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title></head><body>" +
        "<table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead><tbody>" +
        Row("Fixture Desktop", "fixture-desktop", "Default Group", string.Empty, true) +
        "</tbody></table>" +
        "<div role='dialog' aria-label='Connect to this Computer'>" +
        "<h2>Connect to this Computer</h2>" +
        "<button id='opt-native' onclick=\"window.__chooserChoice='native'\">From the Splashtop Business App</button>" +
        "<p>Provides the fullest Splashtop experience; Installation required.</p>" +
        "<button id='opt-web' onclick=\"window.__chooserChoice='web'\">From the Web App in this browser</button>" +
        "</div></body></html>";

    /// <summary>
    /// Synthetic no-total virtual scroll matching the owner's structural survey: a
    /// five-column table, 16 rendered rows, one 646px scroll owner, and a separate
    /// unrelated scroll container. No row text represents live account data.
    /// </summary>
    public static string NoTotalVirtualisedListHtml(int totalRows, int scrollHeight, bool distantDuplicate = false)
    {
        if (totalRows < 16) throw new ArgumentOutOfRangeException(nameof(totalRows));
        if (scrollHeight <= 646) throw new ArgumentOutOfRangeException(nameof(scrollHeight));
        var rows = Enumerable.Range(1, totalRows)
            .Select(i => $"{{n:'Fixture Node {i:D3}',d:'fixture-node-{i}',g:'Synthetic',t:''}}")
            .ToArray();
        if (distantDuplicate) rows[^1] = rows[0];

        return """
            <!doctype html><html><head><meta charset='utf-8'><title>Synthetic no-total virtual list</title>
            <style>html,body{height:100%;margin:0;overflow:hidden}</style></head><body>
            <div id='unrelated-scroller' style='height:80px;overflow-y:auto'><div style='height:160px'></div></div>
            <div id='scroller' style='height:646px;overflow-y:auto'><div id='canvas' style='position:relative;height:__SCROLL_HEIGHT__px'>
            <table id='computers' style='position:absolute;left:0;right:0;top:0'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th>Other</th></tr></thead><tbody id='rows'></tbody></table>
            </div></div>
            <script>var all=[__ROWS__];var RH=44;var VH=646;var scroller=document.getElementById('scroller');var table=document.getElementById('computers');
            function render(){var start=Math.floor(scroller.scrollTop/RH);var count=Math.ceil(VH/RH)+1;var slice=all.slice(start,start+count);document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style="height:'+RH+'px"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label="Connect">Connect</button></td></tr>';}).join('');table.style.transform='translateY('+(start*RH)+'px');}
            scroller.addEventListener('scroll',render);render();</script></body></html>
            """.Replace("__SCROLL_HEIGHT__", scrollHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__ROWS__", string.Join(",", rows), StringComparison.Ordinal);
    }

    public static string ShortViewportVirtualisedListHtml() => VirtualisedListHtml(
        title: "Short viewport computers", total: 24, rowHeight: 40, viewport: 48, repaintDelayMillis: 0);

    public static string SlowRepaintVirtualisedListHtml() => VirtualisedListHtml(
        title: "Slow repaint computers", total: 24, rowHeight: 40, viewport: 48, repaintDelayMillis: 1100);

    private static string VirtualisedListHtml(string title, int total, int rowHeight, int viewport, int repaintDelayMillis)
    {
        var rows = Enumerable.Range(1, total)
            .Select(i => $"{{n:'Fixture Node {i:D2}',d:'fixture-node-{i}',g:'Synthetic',t:''}}")
            .ToArray();
        var render = "function render(){var start=Math.floor(scroller.scrollTop/RH);var count=Math.ceil(VH/RH)+1;" +
                     "var slice=all.slice(start,start+count);var apply=function(){document.getElementById('rows').innerHTML=slice.map(function(r){return '<tr style=\"height:'+RH+'px\"><td>'+r.n+'</td><td>'+r.d+'</td><td>'+r.g+'</td><td>'+r.t+'</td><td><button aria-label=\"Connect\">Connect</button></td></tr>';}).join('');table.style.transform='translateY('+(start*RH)+'px)';};" +
                     // Delay scroll repaints, not initial hydration: the regression isolates
                     // a renderer exceeding the per-step budget after a populated first window.
                     (repaintDelayMillis == 0 ? "apply();}" :
                         "if(initialRender){initialRender=false;apply();}else{setTimeout(apply," + repaintDelayMillis + ");}}");

        return "<!doctype html><html><head><meta charset='utf-8'><title>" + title + "</title>" +
            "<style>tr{height:" + rowHeight + "px}</style></head><body>" +
            "<div id='scroller' style='height:" + viewport + "px;overflow-y:auto'>" +
            "<div style='position:relative;height:" + (total * rowHeight) + "px'>" +
            "<table id='computers' style='position:absolute;left:0;right:0;top:0'>" +
            "<thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead>" +
            "<tbody id='rows'></tbody></table></div></div>" +
            "<p role='status'>" + total + " computers</p>" +
            "<script>var initialRender=true;var all=[" + string.Join(",", rows) + "];var RH=" + rowHeight + ";var VH=" + viewport + ";" +
            "var scroller=document.getElementById('scroller');var table=document.getElementById('computers');" +
            render + "scroller.addEventListener('scroll',render);render();</script></body></html>";
    }

    public static string ReconciledScrollableListHtml()
    {
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"<tr style='height:50px'><td>Reconciled {i}</td><td>fixture-{i}</td><td>Synthetic</td><td></td><td><button aria-label='Connect'>Connect</button></td></tr>"));
        return "<!doctype html><html><head><meta charset='utf-8'><title>Reconciled list</title></head><body>" +
            "<div id='scroller' style='height:80px;overflow-y:auto' onscroll=\"window.__reconcileScrolls=(window.__reconcileScrolls||0)+1\">" +
            "<table id='computers'><thead><tr><th>Computer Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th></tr></thead><tbody>" + rows +
            "</tbody></table></div><p role='status'>5 computers</p></body></html>";
    }

    private static string Row(string name, string device, string group, string notes, bool connect, string? status = null) =>
        "<tr>" +
        "<td><span class='os-icon' aria-hidden='true'></span>" +
        (status is null ? string.Empty : $"<span class='status-dot' aria-label='{status}' title='{status}'></span>") +
        $"{name}</td>" +
        $"<td>{device}</td><td>{group}</td><td>{notes}</td>" +
        "<td>" + (connect ? "<button class='row-connect' aria-label='Connect' title='Connect' onclick=\"window.__connectClicks=(window.__connectClicks||0)+1\">Connect</button>" : string.Empty) + "</td>" +
        "<td><button class='row-menu' aria-label='More actions' title='More'>...</button></td>" +
        "</tr>";
}
