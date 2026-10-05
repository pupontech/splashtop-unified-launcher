namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Host-owned page actions against the console computers list. These scripts run only in
/// the owning account's own WebView, so connecting still uses Splashtop's own client path
/// and the app never constructs a remote-session URL or identifier itself.
/// </summary>
internal static class ConsoleInventoryActions
{
    /// <summary>
    /// Activates the Connect control of one row, located by the row's position inside the
    /// table whose semantic headers match the observed console list (Name / Device Name /
    /// Group / Notes). Refuses silently when the row or control cannot be found, so a
    /// changed page can never cause an unintended click.
    /// </summary>
    public static string ActivateConnectScript(int rowIndex) =>
        "(function () {" +
        "  try {" +
        "    var headers = ['name','device name','group','notes'];" +
        "    var tables = Array.prototype.slice.call(document.querySelectorAll('table'));" +
        "    var grid = null;" +
        "    for (var t = 0; t < tables.length; t++) {" +
        "      var ths = Array.prototype.map.call(tables[t].querySelectorAll('thead th'), function (th) {" +
        "        return (th.textContent || '').replace(/\\s+/g, ' ').trim().toLowerCase();" +
        "      });" +
        "      var hits = headers.filter(function (h) { return ths.indexOf(h) !== -1; }).length;" +
        "      if (hits >= 3) { grid = tables[t]; break; }" +
        "    }" +
        "    if (!grid) { return 'no-table'; }" +
        "    var body = grid.querySelector('tbody') || grid;" +
        "    var rows = Array.prototype.filter.call(body.querySelectorAll('tr'), function (tr) {" +
        "      return tr.querySelectorAll('td').length >= 3;" +
        "    });" +
        $"    var row = rows[{rowIndex}];" +
        "    if (!row) { return 'no-row'; }" +
        "    var candidates = Array.prototype.slice.call(row.querySelectorAll('button,[role=button],a[href],svg,img'));" +
        "    var connect = null;" +
        "    for (var i = 0; i < candidates.length; i++) {" +
        "      var el = candidates[i];" +
        "      var label = ((el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('title') || '') + ' ' + (el.textContent || '')).toLowerCase();" +
        "      if (label.indexOf('connect') !== -1) { connect = el; break; }" +
        "    }" +
        "    if (!connect) {" +
        "      var clickable = Array.prototype.slice.call(row.querySelectorAll('button,[role=button]'));" +
        "      connect = clickable.length ? clickable[clickable.length - 1] : null;" +
        "    }" +
        "    if (!connect) { return 'no-control'; }" +
        "    var target = connect.closest ? (connect.closest('button,[role=button],a') || connect) : connect;" +
        "    target.click();" +
        "    return 'clicked';" +
        "  } catch (e) { return 'error'; }" +
        "})();";
}
