using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

/// <summary>An immutable action target captured before any user prompt can yield.</summary>
internal sealed record InventoryActionTarget(
    string AccountId,
    int RowIndex,
    string DocumentSource,
    int Generation,
    ExtractedComputerRow Row);

/// <summary>Checks that a captured row still refers to the same account/document/read.</summary>
internal static class ConsoleInventoryActionGuard
{
    public static bool IsCurrentTarget(
        InventoryActionTarget target,
        string accountId,
        int rowIndex,
        string documentSource,
        int generation,
        ExtractedComputerRow currentRow) =>
        string.Equals(target.AccountId, accountId, StringComparison.Ordinal) &&
        target.RowIndex == rowIndex &&
        string.Equals(target.DocumentSource, documentSource, StringComparison.Ordinal) &&
        target.Generation == generation &&
        target.Row == currentRow;
}

/// <summary>
/// Host-owned action against one row from the last accepted inventory snapshot. The live
/// row identity and unique visible Connect control are revalidated immediately before click.
/// </summary>
internal static class ConsoleInventoryActions
{
    public static string ActivateConnectScript(int rowIndex, ExtractedComputerRow expectedRow)
    {
        ArgumentNullException.ThrowIfNull(expectedRow);
        if (rowIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }

        var expected = JsonSerializer.Serialize(new
        {
            name = expectedRow.Name,
            deviceName = expectedRow.DeviceName ?? string.Empty,
            group = expectedRow.Group ?? string.Empty,
            notes = expectedRow.Notes ?? string.Empty,
            status = expectedRow.Status ?? string.Empty
        });

        return "(function () {" +
            "  try {" +
            "    if (window.top !== window.self) { return 'blocked-origin'; }" +
            "    var currentUrl; try { currentUrl = new URL(window.location.href); } catch (_) { return 'blocked-origin'; }" +
            "    var host = currentUrl.hostname.toLowerCase();" +
            "    if (currentUrl.protocol !== 'https:' || currentUrl.port !== '' || currentUrl.username !== '' || currentUrl.password !== '' ||" +
            "        (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu')) { return 'blocked-origin'; }" +
            "    var clean = function (value) { return String(value === null || value === undefined ? '' : value).replace(/\\s+/g, ' ').trim(); };" +
            "    var visible = function (node) {" +
            "      if (!node || !node.isConnected) { return false; }" +
            "      for (var current = node; current && current.nodeType === 1; current = current.parentElement) {" +
            "        if (current.hasAttribute('hidden') || current.getAttribute('aria-hidden') === 'true') { return false; }" +
            "        var style = window.getComputedStyle(current);" +
            "        if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || Number(style.opacity) === 0) { return false; }" +
            "      }" +
            "      var rect = node.getBoundingClientRect();" +
            "      return node.getClientRects().length > 0 && rect.width > 0 && rect.height > 0;" +
            "    };" +
            "    var label = function (node) { return clean(node.getAttribute('aria-label') || node.innerText || node.textContent || ''); };" +
            "    var tables = Array.prototype.slice.call(document.querySelectorAll('table')).filter(function (table) {" +
            "      if (!visible(table)) { return false; }" +
            "      var head = table.querySelector('thead'); if (!head || head.querySelectorAll('tr').length !== 1) { return false; }" +
            "      var headers = Array.prototype.slice.call(head.querySelector('tr').children);" +
            "      if (!headers.length || headers.some(function (h) { return (h.tagName !== 'TH' && h.tagName !== 'TD') || Number(h.colSpan || 1) !== 1 || Number(h.rowSpan || 1) !== 1; })) { return false; }" +
            "      var map = { name: [], deviceName: [], group: [], notes: [] };" +
            "      headers.forEach(function (h, i) { var text = clean(h.textContent).toLowerCase(); if (text === 'name' || text === 'computer name') { map.name.push(i); } else if (text === 'device name') { map.deviceName.push(i); } else if (text === 'group') { map.group.push(i); } else if (text === 'notes') { map.notes.push(i); } });" +
            "      if (map.name.length !== 1 || map.deviceName.length !== 1 || map.group.length !== 1 || map.notes.length > 1) { return false; }" +
            "      table.__inventoryHeaderMap = { count: headers.length, name: map.name[0], deviceName: map.deviceName[0], group: map.group[0], notes: map.notes.length ? map.notes[0] : null };" +
            "      return true;" +
            "    });" +
            "    if (tables.length !== 1) { return tables.length ? 'ambiguous-table' : 'no-table'; }" +
            "    var body = tables[0].querySelector('tbody'); if (!body) { return 'no-table'; }" +
            "    var columnMap = tables[0].__inventoryHeaderMap;" +
            "    var rows = Array.prototype.filter.call(body.querySelectorAll('tr'), function (row) {" +
            "      return visible(row) && row.querySelectorAll(':scope > td').length === columnMap.count;" +
            "    });" +
            $"    var rowIndex = {rowIndex};" +
            $"    var expected = {expected};" +
            "    var observedStatus = function (row) {" +
            "      var statusWords = /^(online|offline|available|unavailable|connected|disconnected|in use|busy|idle|unreachable|sleeping|locked)$/i;" +
            "      var indicators = Array.prototype.slice.call(row.querySelectorAll('[aria-label],[title],img[alt],svg[aria-label]'));" +
            "      for (var i = 0; i < indicators.length; i++) {" +
            "        var node = indicators[i]; var text = clean(node.getAttribute('aria-label') || node.getAttribute('title') || node.getAttribute('alt') || '');" +
            "        if (!text) { continue; }" +
            "        if (statusWords.test(text)) { return text; }" +
            "        var word = text.match(/\\b(online|offline|available|unavailable|connected|disconnected|in use|busy|idle|unreachable|sleeping|locked)\\b/i);" +
            "        if (word && text.length <= 40) { return word[1]; }" +
            "      }" +
            "      return '';" +
            "    };" +
            "    var matchesIdentity = function (row) {" +
            "      var cells = row.querySelectorAll(':scope > td');" +
            "      return clean(cells[columnMap.name].textContent) === expected.name && clean(cells[columnMap.deviceName].textContent) === expected.deviceName &&" +
            "        clean(cells[columnMap.group].textContent) === expected.group && (columnMap.notes === null ? '' : clean(cells[columnMap.notes].textContent)) === expected.notes &&" +
            "        observedStatus(row) === expected.status;" +
            "    };" +
            "    var row = rows[rowIndex];" +
            "    if (!row) { return 'no-row'; }" +
            "    if (!matchesIdentity(row)) { return 'identity-mismatch'; }" +
            "    if (rows.filter(matchesIdentity).length !== 1) { return 'ambiguous-row'; }" +
            "    var controls = Array.prototype.slice.call(row.querySelectorAll('button,[role=button],a[href]')).filter(function (control) {" +
            "      var tag = control.tagName.toLowerCase();" +
            "      var role = (control.getAttribute('role') || '').toLowerCase();" +
            "      if (tag !== 'button' && role !== 'button' && tag !== 'a') { return false; }" +
            "      if (control.disabled || control.hasAttribute('disabled') || control.getAttribute('aria-disabled') === 'true' || !visible(control)) { return false; }" +
            "      if (label(control).toLowerCase() !== 'connect') { return false; }" +
            "      if (tag === 'a') {" +
            "        try { var targetUrl = new URL(control.href, location.href); if (targetUrl.protocol !== 'https:' || targetUrl.origin !== location.origin) { return false; } }" +
            "        catch (_) { return false; }" +
            "      }" +
            "      return true;" +
            "    });" +
            "    if (controls.length !== 1) { return controls.length ? 'ambiguous-control' : 'no-control'; }" +
            "    controls[0].click();" +
            "    return 'clicked';" +
            "  } catch (e) { return 'error'; }" +
            "})();";
    }
}
