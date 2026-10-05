namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Builds the local fixture page used to prove the inventory extractor at runtime.
/// The markup is invented from the column labels observed in the official Splashtop
/// support screenshot (Name / Device Name / Group / Notes plus a per-row Connect
/// control); it is NOT captured Splashtop DOM and contains no real account data.
/// </summary>
internal static class InventoryFixture
{
    public const string VirtualHost = "my.splashtop.com";

    public static string ComputerListHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title></head><body>" +
        "<div class='toolbar'>" +
        "  <button>Add Computer</button><button>Business App</button><button aria-label='Refresh'>Refresh</button>" +
        "  <button aria-label='Filter'>Filter</button><input type='search' aria-label='Search' />" +
        "</div>" +
        "<table id='computers'>" +
        "  <thead><tr><th>Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
        "  <tbody>" +
        Row("Fixture Desktop", "fixture-desktop", "Default Group", "", true) +
        Row("Fixture Server", "fixture-server", "Servers", "lab", true) +
        Row("Fixture VM", "fixture-vm", "VMs", "", true) +
        Row("Fixture VM", "fixture-vm-2", "VMs", "duplicate display name", false) +
        "  </tbody>" +
        "</table></body></html>";

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
            "<table id='computers'><thead><tr><th>Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
            "<tbody id='rows'></tbody></table>" +
            "<div id='pager'><button id='prev' aria-label='Previous page'>Previous</button>" +
            "<span id='range'></span><button id='next' aria-label='Next page'>Next</button></div>" +
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
            "<thead><tr><th>Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
            "<tbody id='rows'></tbody></table></div></div>" +
            $"<p>{total} computers</p>" +
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

    private static string Row(string name, string device, string group, string notes, bool connect) =>
        "<tr>" +
        $"<td><span class='os-icon' aria-hidden='true'></span>{name}</td>" +
        $"<td>{device}</td><td>{group}</td><td>{notes}</td>" +
        "<td>" + (connect ? "<button class='row-connect' aria-label='Connect' title='Connect' onclick=\"window.__connectClicks=(window.__connectClicks||0)+1\">Connect</button>" : string.Empty) + "</td>" +
        "<td><button class='row-menu' aria-label='More actions' title='More'>...</button></td>" +
        "</tr>";
}
