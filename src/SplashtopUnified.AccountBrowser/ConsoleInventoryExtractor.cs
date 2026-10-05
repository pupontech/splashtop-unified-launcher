using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

/// <summary>One parsed console inventory read, sanitised for the host.</summary>
internal sealed record ConsoleInventoryRead(
    InventoryOutcome Outcome,
    ConsolePageKind PageKind,
    ConsoleAuthentication Authentication,
    int RowCount,
    int? ReportedTotal,
    IReadOnlyList<ExtractedComputerRow> Rows,
    string? Diagnostic);

/// <summary>
/// Versioned extraction of the official console computers table (columns Name /
/// Device Name / Group / Notes, per-row Connect control) plus a fail-closed host-side
/// validator. It never carries anything the list does not render: no numeric ID, MAC,
/// status or timestamp.
/// </summary>
internal static class ConsoleInventoryExtractor
{
    public const int ScriptVersion = 1;
    public const string MessageSource = "splashtop-console-inventory";
    public const int MaxPayloadChars = 262_144;
    public const int MaxRows = 5000;
    private const int MaxFieldChars = 512;

    private static readonly string[] Outcomes = ["complete", "incomplete", "unavailable"];

    /// <summary>
    /// In-page extractor. Guarded to the official console origins and the top document,
    /// matches the computers list by visible column headers or grid roles rather than
    /// minified class names, then walks every page and every virtual-scroll window so the
    /// whole list is read rather than only the rows currently on screen. Posts one
    /// allowlisted JSON message.
    /// </summary>
    public static string ExtractScript => """
    (async function () {
      try {
        if (window.top !== window.self) { return; }
        var host = (location.hostname || '').toLowerCase();
        if (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu') { return; }
        if (!('chrome' in window) || !window.chrome.webview) { return; }

        var MAX_ROWS = 5000;
        var MAX_STEPS = 80;
        var clean = function (value, limit) {
          if (value === null || value === undefined) { return ''; }
          var text = String(value).replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '').replace(/\s+/g, ' ').trim();
          limit = limit || 512;
          return text.length > limit ? text.slice(0, limit) : text;
        };
        var sleep = function (ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); };
        var wanted = ['name', 'device name', 'group', 'notes'];

        var headerTexts = function (grid) {
          var nodes = grid.tagName === 'TABLE'
            ? grid.querySelectorAll('thead th')
            : grid.querySelectorAll('[role=columnheader],thead th,[role=row]:first-child th,[role=row]:first-child [role=gridcell]');
          return Array.prototype.map.call(nodes, function (node) { return clean(node.textContent, 64).toLowerCase(); });
        };

        var findGrid = function () {
          var candidates = Array.prototype.slice.call(document.querySelectorAll('table,[role=grid],[role=table]'));
          for (var i = 0; i < candidates.length; i++) {
            var texts = headerTexts(candidates[i]);
            var hits = wanted.filter(function (h) { return texts.indexOf(h) !== -1; }).length;
            if (hits >= 3) { return candidates[i]; }
          }
          return null;
        };

        var rowElements = function (grid) {
          if (grid.tagName === 'TABLE') {
            var body = grid.querySelector('tbody') || grid;
            return Array.prototype.filter.call(body.querySelectorAll('tr'), function (tr) { return tr.querySelectorAll('td').length >= 3; });
          }
          return Array.prototype.filter.call(grid.querySelectorAll('[role=row]'), function (row) {
            return row.querySelectorAll('[role=gridcell],[role=cell],td').length >= 3;
          });
        };

        var collect = function (grid, seen, rows) {
          var nodes = rowElements(grid);
          for (var i = 0; i < nodes.length && rows.length < MAX_ROWS; i++) {
            var cells = nodes[i].tagName === 'TABLE'
              ? nodes[i].querySelectorAll('td')
              : nodes[i].querySelectorAll('[role=gridcell],[role=cell],td');
            var name = clean(cells[0] ? cells[0].textContent : '', 512);
            if (!name) { continue; }
            var record = {
              name: name,
              deviceName: clean(cells[1] ? cells[1].textContent : '', 512),
              group: clean(cells[2] ? cells[2].textContent : '', 512),
              notes: clean(cells[3] ? cells[3].textContent : '', 512),
              hasConnectControl: false
            };
            record.hasConnectControl = (function (rowNode) {
              var controls = Array.prototype.slice.call(rowNode.querySelectorAll('button,[role=button],a[href]'));
              for (var c = 0; c < controls.length; c++) {
                var label = ((controls[c].getAttribute('aria-label') || '') + ' ' + (controls[c].getAttribute('title') || '') + ' ' + (controls[c].textContent || '')).toLowerCase();
                if (label.indexOf('connect') !== -1) { return true; }
              }
              return false;
            })(nodes[i]);
            var key = record.name + '\u0001' + record.deviceName + '\u0001' + record.group;
            if (!seen[key]) { seen[key] = 1; rows.push(record); }
          }
          return nodes.length;
        };

        var disabled = function (element) {
          if (!element) { return true; }
          if (element.disabled) { return true; }
          if (element.getAttribute && (element.getAttribute('disabled') !== null || element.getAttribute('aria-disabled') === 'true')) { return true; }
          var target = element.closest ? (element.closest('button,[role=button],a,li') || element) : element;
          if (target.getAttribute && (target.getAttribute('disabled') !== null || target.getAttribute('aria-disabled') === 'true')) { return true; }
          return target.classList ? target.classList.contains('disabled') : false;
        };

        var findByLabel = function (pattern) {
          var nodes = Array.prototype.slice.call(document.querySelectorAll('button,[role=button],a,[role=link],li[role=menuitem],input[type=button]'));
          for (var i = 0; i < nodes.length; i++) {
            var label = ((nodes[i].getAttribute('aria-label') || '') + ' ' + (nodes[i].getAttribute('title') || '') + ' ' + (nodes[i].textContent || '')).replace(/\s+/g, ' ').trim();
            if (pattern.test(label)) { return nodes[i]; }
          }
          return null;
        };

        var reportedTotal = null;
        var pageText = clean(document.body ? document.body.textContent : '', 8000);
        var totalMatch = pageText.match(/\bof\s+(\d[\d,]*)/i) || pageText.match(/(\d[\d,]*)\s+(computers?|devices?|items?|results?)/i);
        if (totalMatch) { var parsedTotal = parseInt(totalMatch[1].replace(/,/g, ''), 10); if (!isNaN(parsedTotal)) { reportedTotal = parsedTotal; } }

        var hasPassword = !!document.querySelector('input[type=password]');
        var grid = findGrid();
        if (!grid) {
          window.chrome.webview.postMessage(JSON.stringify({
            source: 'splashtop-console-inventory', version: 1, outcome: 'unavailable',
            pageKind: hasPassword ? 'login' : 'unknown', authentication: hasPassword ? 'required' : 'unknown',
            rowCount: 0, reportedTotal: null, rows: [], walkedToEnd: false, mode: 'none'
          }));
          return;
        }

        var seen = {}, rows = [];
        collect(grid, seen, rows);

        var mode = 'single';
        var walkedToEnd = false;

        // Walk pagination when a pager is present.
        var next = findByLabel(/^(next|next page|›|»|>)$/i) || findByLabel(/next\s*(page)?/i);
        var pagerPresent = !!next || /\bof\s+\d/i.test(pageText);
        if (pagerPresent) {
          mode = 'paged';
          for (var page = 0; page < MAX_STEPS && next && !disabled(next) && rows.length < MAX_ROWS; page++) {
            var before = rows.length;
            next.click();
            for (var settle = 0; settle < 24; settle++) {
              await sleep(250);
              collect(grid, seen, rows);
              if (rows.length !== before) { break; }
            }
            if (rows.length === before) { break; }
            next = findByLabel(/^(next|next page|›|»|>)$/i) || findByLabel(/next\s*(page)?/i);
          }
          walkedToEnd = !next || disabled(next);
          // Best-effort: return the console to its first page.
          var previous = findByLabel(/^(prev|previous|‹|«|<)$/i) || findByLabel(/(prev|previous)\s*(page)?/i);
          for (var back = 0; back < MAX_STEPS && previous && !disabled(previous); back++) { previous.click(); await sleep(200); previous = findByLabel(/^(prev|previous|‹|«|<)$/i) || findByLabel(/(prev|previous)\s*(page)?/i); }
        } else {
          // Walk virtual scrolling: a long list may only render the rows near the viewport.
          var scrollCandidates = Array.prototype.slice.call(document.querySelectorAll('div,[role=grid],[role=region],main,section'))
            .filter(function (node) {
              if (node === document.body || node === document.documentElement) { return false; }
              if (node.scrollHeight <= node.clientHeight + 8) { return false; }
              var style = window.getComputedStyle(node);
              return !!style && (style.overflowY === 'auto' || style.overflowY === 'scroll' || node.scrollHeight > node.clientHeight + 40);
            });
          var relevant = scrollCandidates.filter(function (node) { return node.contains(grid); });
          var scroller = (relevant.length > 0 ? relevant : scrollCandidates)
            .sort(function (a, b) { return a.scrollHeight - b.scrollHeight; })[0] || null;
          if (scroller) {
            mode = 'scrolled';
            scroller.scrollTop = 0;
            await sleep(300);
            collect(grid, seen, rows);
            var stepSize = Math.max(120, Math.floor(scroller.clientHeight * 0.8));
            for (var step = 0; step < MAX_STEPS && rows.length < MAX_ROWS; step++) {
              var previousTop = scroller.scrollTop;
              var beforeCount = rows.length;
              scroller.scrollTop = Math.min(previousTop + stepSize, scroller.scrollHeight);
              await sleep(350);
              collect(grid, seen, rows);
              var atEnd = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 8;
              if (atEnd && rows.length === beforeCount) { break; }
              if (scroller.scrollTop === previousTop && rows.length === beforeCount) { break; }
            }
            walkedToEnd = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 8;
            scroller.scrollTop = 0;
            await sleep(250);
          }
        }

        var outcome;
        if (mode === 'paged') {
          outcome = walkedToEnd && (reportedTotal === null || reportedTotal === rows.length) ? 'complete' : 'incomplete';
        } else if (mode === 'scrolled') {
          outcome = walkedToEnd && reportedTotal !== null && reportedTotal === rows.length ? 'complete' : 'incomplete';
        } else {
          outcome = 'incomplete';
        }
        if (reportedTotal !== null && reportedTotal < rows.length) { outcome = 'incomplete'; }

        window.chrome.webview.postMessage(JSON.stringify({
          source: 'splashtop-console-inventory', version: 1, outcome: outcome,
          pageKind: 'computerList', authentication: 'authenticated', rowCount: rows.length,
          reportedTotal: reportedTotal, rows: rows, walkedToEnd: walkedToEnd, mode: mode
        }));
      } catch (e) { /* fail closed: post nothing */ }
    })();
    """;

    /// <summary>
    /// Read-only structural survey of the console page: how many list containers, headers,
    /// rows and pager controls exist and where scrolling happens. Counts and labels only —
    /// never row data, cookies, tokens or storage.
    /// </summary>
    public static string DiagnosticsScript => """
    (function () {
      try {
        if (window.top !== window.self) { return '{}'; }
        var host = (location.hostname || '').toLowerCase();
        if (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu') { return '{}'; }
        var clean = function (v, l) { return String(v === null || v === undefined ? '' : v).replace(/\s+/g, ' ').trim().slice(0, l || 200); };
        var tables = Array.prototype.slice.call(document.querySelectorAll('table'));
        var roleGrids = Array.prototype.slice.call(document.querySelectorAll('[role=grid],[role=table]'));
        var scrollers = Array.prototype.slice.call(document.querySelectorAll('div,[role=region],main,section')).filter(function (n) {
          return window.getComputedStyle(n).overflowY !== 'visible' && n.scrollHeight > n.clientHeight + 8;
        }).length;
        var labels = Array.prototype.slice.call(document.querySelectorAll('button,[role=button],a[role=link]')).map(function (n) {
          return clean((n.getAttribute('aria-label') || '') + ' ' + (n.getAttribute('title') || '') + ' ' + n.textContent, 60);
        }).filter(function (t) { return t.length > 0; });
        return JSON.stringify({
          tables: tables.length,
          tableHeaders: tables.map(function (t) { return Array.prototype.map.call(t.querySelectorAll('thead th'), function (th) { return clean(th.textContent, 40); }); }),
          roleGrids: roleGrids.length,
          roleRows: document.querySelectorAll('[role=row]').length,
          gridHeaderCells: Array.prototype.map.call(document.querySelectorAll('[role=columnheader]'), function (n) { return clean(n.textContent, 40); }),
          scrollContainers: scrollers,
          hasPasswordField: !!document.querySelector('input[type=password]'),
          controlLabels: labels.slice(0, 60),
          bodyTextSample: clean(document.body ? document.body.textContent : '', 400)
        });
      } catch (e) { return '{}'; }
    })();
    """;


    /// <summary>
    /// Fail-closed host-side validation. Rejects wrong source/version, bad shapes,
    /// oversized payloads, control characters, rows without a name, and row counts over
    /// the cap. Only then are records normalised.
    /// </summary>
    public static bool TryParse(string? json, out ConsoleInventoryRead? read)
    {
        read = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadChars)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryString(root, "source", out var source) || !string.Equals(source, MessageSource, StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var version) || version != ScriptVersion)
            {
                return false;
            }

            if (!TryString(root, "outcome", out var outcomeText) ||
                !Outcomes.Contains(outcomeText, StringComparer.Ordinal))
            {
                return false;
            }

            if (!TryString(root, "pageKind", out var pageKindText) ||
                pageKindText is not ("computerList" or "login" or "unknown"))
            {
                return false;
            }

            if (!TryString(root, "authentication", out var authenticationText) ||
                authenticationText is not ("authenticated" or "required" or "unknown"))
            {
                return false;
            }

            if (!root.TryGetProperty("rowCount", out var rowCountElement) || rowCountElement.ValueKind != JsonValueKind.Number ||
                !rowCountElement.TryGetInt32(out var rowCount) || rowCount < 0 || rowCount > MaxRows)
            {
                return false;
            }

            int? reportedTotal = null;
            if (root.TryGetProperty("reportedTotal", out var totalElement) && totalElement.ValueKind != JsonValueKind.Null)
            {
                if (totalElement.ValueKind != JsonValueKind.Number || !totalElement.TryGetInt32(out var total) || total < 0)
                {
                    return false;
                }

                reportedTotal = total;
            }

            if (!root.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array ||
                rowsElement.GetArrayLength() > MaxRows)
            {
                return false;
            }

            // Optional: the page walked every page or scroll window. Must be a real boolean
            // when present so a malformed claim can never be read as complete.
            if (root.TryGetProperty("walkedToEnd", out var walkedElement) &&
                walkedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            var rows = new List<ExtractedComputerRow>(rowsElement.GetArrayLength());
            foreach (var item in rowsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryString(item, "name", out var name) || string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                if (!TryOptionalString(item, "deviceName", out var deviceName) ||
                    !TryOptionalString(item, "group", out var group) ||
                    !TryOptionalString(item, "notes", out var notes) ||
                    !item.TryGetProperty("hasConnectControl", out var connectElement) ||
                    connectElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                rows.Add(new ExtractedComputerRow(name, deviceName, group, notes, connectElement.GetBoolean()));
            }

            if (rowCount != rows.Count)
            {
                return false;
            }

            var outcome = Enum.Parse<InventoryOutcome>(outcomeText, ignoreCase: true);
            // A console that reports fewer rows than it rendered is never a complete read.
            if (reportedTotal is { } reported && reported < rows.Count)
            {
                outcome = InventoryOutcome.Incomplete;
            }

            var diagnostic = outcome == InventoryOutcome.Complete
                ? null
                : $"Console read marked {outcome.ToString().ToLowerInvariant()}; the list exposes no numeric identity or guaranteed total.";

            read = new ConsoleInventoryRead(
                outcome,
                Enum.Parse<ConsolePageKind>(pageKindText, ignoreCase: true),
                Enum.Parse<ConsoleAuthentication>(authenticationText, ignoreCase: true),
                rowCount,
                reportedTotal,
                rows,
                diagnostic);
            return true;
        }
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = candidate.GetString();
        if (text is null || !IsClean(text, MaxFieldChars))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = candidate.GetString();
        if (text is null || !IsClean(text, MaxFieldChars))
        {
            return false;
        }

        value = text.Length == 0 ? null : text;
        return true;
    }

    private static bool IsClean(string text, int maxLength)
    {
        if (text.Length > maxLength)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r'))
            {
                return false;
            }
        }

        return !text.Contains('\u0001', StringComparison.Ordinal);
    }
}
