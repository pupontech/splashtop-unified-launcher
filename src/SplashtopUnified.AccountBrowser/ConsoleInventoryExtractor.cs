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
    /// matches the computers table by visible column headers rather than minified class
    /// names, and posts one allowlisted JSON message.
    /// </summary>
    public static string ExtractScript => """
    (function () {
      try {
        if (window.top !== window.self) { return; }
        var host = (location.hostname || '').toLowerCase();
        if (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu') { return; }
        if (!('chrome' in window) || !window.chrome.webview) { return; }

        var clean = function (value, limit) {
          if (value === null || value === undefined) { return ''; }
          var text = String(value).replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '').replace(/\s+/g, ' ').trim();
          limit = limit || 512;
          return text.length > limit ? text.slice(0, limit) : text;
        };

        var wanted = ['name', 'device name', 'group', 'notes'];
        var tables = Array.prototype.slice.call(document.querySelectorAll('table'));
        var grid = null, headerCells = [];
        for (var t = 0; t < tables.length; t++) {
          var ths = Array.prototype.map.call(tables[t].querySelectorAll('thead th'), function (th) { return clean(th.textContent, 64).toLowerCase(); });
          var hits = wanted.filter(function (h) { return ths.indexOf(h) !== -1; }).length;
          if (hits >= 3) { grid = tables[t]; headerCells = ths; break; }
        }

        var body = grid ? (grid.querySelector('tbody') || grid) : null;
        var rows = [];
        if (body) {
          var trs = Array.prototype.filter.call(body.querySelectorAll('tr'), function (tr) { return tr.querySelectorAll('td').length >= 3; });
          for (var r = 0; r < trs.length; r++) {
            var tds = trs[r].querySelectorAll('td');
            var name = clean(tds[0] ? tds[0].textContent : '', 512);
            if (!name) { continue; }
            var hasConnect = false;
            var controls = Array.prototype.slice.call(trs[r].querySelectorAll('button,[role=button],a[href]'));
            for (var c = 0; c < controls.length; c++) {
              var label = ((controls[c].getAttribute('aria-label') || '') + ' ' + (controls[c].getAttribute('title') || '') + ' ' + (controls[c].textContent || '')).toLowerCase();
              if (label.indexOf('connect') !== -1) { hasConnect = true; break; }
            }
            rows.push({ name: name, deviceName: clean(tds[1] ? tds[1].textContent : '', 512), group: clean(tds[2] ? tds[2].textContent : '', 512), notes: clean(tds[3] ? tds[3].textContent : '', 512), hasConnectControl: hasConnect });
            if (rows.length >= 5000) { break; }
          }
        }

        var hasPassword = !!document.querySelector('input[type=password]');
        var pageKind = grid ? 'computerList' : (hasPassword ? 'login' : 'unknown');
        var auth = grid ? 'authenticated' : (hasPassword ? 'required' : 'unknown');

        var reportedTotal = null, outcome;
        if (grid) {
          var text = clean(document.body ? document.body.textContent : '', 4000);
          var match = text.match(/(\d[\d,]*)\s+(computers?|devices?|items?|results?)/i);
          if (match) { var n = parseInt(match[1].replace(/,/g, ''), 10); if (!isNaN(n)) { reportedTotal = n; } }
          var unique = {}; var uniqueCount = 0;
          for (var u = 0; u < rows.length; u++) { var key = rows[u].name + '\u0001' + rows[u].deviceName; if (!unique[key]) { unique[key] = 1; uniqueCount++; } }
          outcome = (reportedTotal !== null && reportedTotal === uniqueCount) ? 'complete' : 'incomplete';
          if (reportedTotal !== null && reportedTotal < uniqueCount) { outcome = 'incomplete'; }
        } else { outcome = 'unavailable'; }

        window.chrome.webview.postMessage(JSON.stringify({
          source: 'splashtop-console-inventory', version: 1, outcome: outcome,
          pageKind: pageKind, authentication: auth, rowCount: rows.length,
          reportedTotal: reportedTotal, rows: rows
        }));
      } catch (e) { /* fail closed: post nothing */ }
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
