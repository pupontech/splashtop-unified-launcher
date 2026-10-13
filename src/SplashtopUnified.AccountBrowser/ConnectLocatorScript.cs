using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

internal sealed record ConnectLocatorStep
{
    public string Status { get; init; } = string.Empty;
    public double DocumentToken { get; init; }
    public string? PageToken { get; init; }
    public bool HasPager { get; init; }
    public bool NextEnabled { get; init; }
    public bool PreviousEnabled { get; init; }
    public double ScrollTop { get; init; }
    public double ScrollClient { get; init; }
    public double ScrollMax { get; init; }
    public uint Signature { get; init; }
    public int TargetCount { get; init; }
    public int RenderedRowCount { get; init; }
    public long[] TargetPositions { get; init; } = [];
    public int[] TargetControlCounts { get; init; } = [];
}

/// <summary>Small synchronous, host-commanded DOM operations for bounded Connect locating.</summary>
internal static class ConnectLocatorScript
{
    public static string CreateStep(
        string command,
        ExtractedComputerRow expectedRow,
        string expectedUrl,
        double? documentToken,
        string? pageToken,
        long? expectedPosition,
        double? desiredScrollTop,
        long deadlineEpochMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(expectedRow);
        if (command is not ("read" or "next" or "previous" or "scroll" or "click"))
            throw new ArgumentOutOfRangeException(nameof(command));

        var request = JsonSerializer.Serialize(new
        {
            command,
            expectedUrl,
            documentToken,
            pageToken,
            expectedPosition,
            desiredScrollTop,
            deadlineEpochMilliseconds
        });
        var expected = JsonSerializer.Serialize(new
        {
            name = expectedRow.Name,
            deviceName = expectedRow.DeviceName ?? string.Empty,
            group = expectedRow.Group ?? string.Empty,
            notes = expectedRow.Notes ?? string.Empty,
            status = expectedRow.Status ?? string.Empty
        });

        // The page reads payload.request as an object. Serializing this pair as an anonymous
        // object would embed the request as a JSON string and every property read in the page
        // would be undefined, which silently turns every step into a refused origin check.
        return ScriptTemplate.Replace(
            "__PAYLOAD__",
            string.Concat("{\"request\":", request, ",\"expected\":", expected, "}"),
            StringComparison.Ordinal);
    }

    private const string ScriptTemplate = """
    (function () {
      var payload = __PAYLOAD__;
      var request = payload.request;
      var expected = payload.expected;
      function result(status, extra) { return JSON.stringify(Object.assign({ status: status }, extra || {})); }
      try {
        if (window.top !== window.self || Date.now() >= request.deadlineEpochMilliseconds) return result('expired');
        var url; try { url = new URL(location.href); } catch (_) { return result('blocked-origin'); }
        var host = url.hostname.toLowerCase();
        if (url.protocol !== 'https:' || url.port !== '' || url.username !== '' || url.password !== '' ||
            (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu') || location.href !== request.expectedUrl)
          return result('blocked-origin');
        var documentToken = performance.timeOrigin;
        if (request.documentToken !== null && request.documentToken !== documentToken) return result('document-changed');
        var clean = function (value) { return String(value == null ? '' : value).replace(/\s+/g, ' ').trim(); };
        var visible = function (node) {
          if (!node || !node.isConnected) return false;
          for (var current = node; current && current.nodeType === 1; current = current.parentElement) {
            if (current.hasAttribute('hidden') || current.getAttribute('aria-hidden') === 'true') return false;
            var style = getComputedStyle(current);
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || Number(style.opacity) === 0) return false;
          }
          var rect = node.getBoundingClientRect();
          return node.getClientRects().length > 0 && rect.width > 0 && rect.height > 0;
        };
        var tables = Array.prototype.slice.call(document.querySelectorAll('table')).filter(function (table) {
          if (!visible(table)) return false;
          var head = table.querySelector('thead');
          if (!head || head.querySelectorAll('tr').length !== 1) return false;
          var headers = Array.prototype.slice.call(head.querySelector('tr').children);
          if (!headers.length || headers.some(function (cell) {
            return (cell.tagName !== 'TH' && cell.tagName !== 'TD') || Number(cell.colSpan || 1) !== 1 || Number(cell.rowSpan || 1) !== 1;
          })) return false;
          var map = { name: [], device: [], group: [], notes: [] };
          headers.forEach(function (cell, index) {
            var text = clean(cell.textContent).toLowerCase();
            if (text === 'name' || text === 'computer name') map.name.push(index);
            else if (text === 'device name') map.device.push(index);
            else if (text === 'group') map.group.push(index);
            else if (text === 'notes') map.notes.push(index);
          });
          if (map.name.length !== 1 || map.device.length !== 1 || map.group.length !== 1 || map.notes.length > 1) return false;
          table.__connectLocatorMap = { count: headers.length, name: map.name[0], device: map.device[0], group: map.group[0], notes: map.notes.length ? map.notes[0] : null };
          return true;
        });
        if (tables.length !== 1) return result(tables.length ? 'ambiguous-table' : 'no-table');
        var table = tables[0], map = table.__connectLocatorMap, body = table.querySelector('tbody');
        if (!body) return result('no-table');
        var rows = Array.prototype.filter.call(body.querySelectorAll(':scope > tr'), function (row) {
          return visible(row) && row.querySelectorAll(':scope > td').length === map.count;
        });
        var statusOf = function (row) {
          var words = /^(online|offline|available|unavailable|connected|disconnected|in use|busy|idle|unreachable|sleeping|locked)$/i;
          var nodes = Array.prototype.slice.call(row.querySelectorAll('[aria-label],[title],img[alt],svg[aria-label]'));
          for (var i = 0; i < nodes.length; i++) {
            var text = clean(nodes[i].getAttribute('aria-label') || nodes[i].getAttribute('title') || nodes[i].getAttribute('alt') || '');
            if (words.test(text)) return text;
            var match = text.match(/\b(online|offline|available|unavailable|connected|disconnected|in use|busy|idle|unreachable|sleeping|locked)\b/i);
            if (match && text.length <= 40) return match[1];
          }
          return '';
        };
        var identity = function (row) {
          var cells = row.querySelectorAll(':scope > td');
          return clean(cells[map.name].textContent) === expected.name && clean(cells[map.device].textContent) === expected.deviceName &&
            clean(cells[map.group].textContent) === expected.group && (map.notes === null ? '' : clean(cells[map.notes].textContent)) === expected.notes &&
            statusOf(row) === expected.status;
        };
        var safeAction = function (node, allowDisabled) {
          var tag = node.tagName.toLowerCase(), role = (node.getAttribute('role') || '').toLowerCase();
          if (tag !== 'button' && role !== 'button' && tag !== 'a') return false;
          if (!allowDisabled && (node.disabled || node.hasAttribute('disabled') || node.getAttribute('aria-disabled') === 'true')) return false;
          if (tag === 'a') {
            try { var target = new URL(node.href, location.href); if (target.protocol !== 'https:' || target.origin !== location.origin) return false; }
            catch (_) { return false; }
          }
          return visible(node);
        };
        var controlsFor = function (row) {
          return Array.prototype.slice.call(row.querySelectorAll('button,[role=button],a[href]')).filter(function (node) {
            var label = clean(node.getAttribute('aria-label') || node.innerText || node.textContent || '');
            return safeAction(node, false) && label.toLowerCase() === 'connect';
          });
        };
        var matches = rows.filter(identity);
        var scroller = null, documentScroller = document.scrollingElement;
        for (var ancestor = table, depth = 0; ancestor && depth++ < 64; ancestor = ancestor.parentElement) {
          if (ancestor === documentScroller) {
            var rootStyle = getComputedStyle(ancestor);
            if (rootStyle.overflowY !== 'hidden' && rootStyle.overflowY !== 'clip' && ancestor.scrollHeight > ancestor.clientHeight + 8) { scroller = ancestor; break; }
          } else if (ancestor.clientHeight > 1 && ancestor.scrollHeight > ancestor.clientHeight + 8) {
            var style = getComputedStyle(ancestor);
            if (style.overflowY === 'auto' || style.overflowY === 'scroll') { scroller = ancestor; break; }
          }
        }
        var scrollTop = scroller ? scroller.scrollTop : 0;
        var scrollClient = scroller ? scroller.clientHeight : 0;
        var scrollMax = scroller ? Math.max(0, scroller.scrollHeight - scroller.clientHeight) : 0;
        var rangeTotal = function (text) {
          var match = clean(text).match(/^\d[\d,]*\s*[-–]\s*\d[\d,]*\s+of\s+(\d[\d,]*)$/i);
          return match ? parseInt(match[1].replace(/,/g, ''), 10) : null;
        };
        var navs = Array.prototype.slice.call(document.querySelectorAll('nav[aria-label],[role=navigation][aria-label]')).filter(function (node) {
          var label = clean(node.getAttribute('aria-label')).toLowerCase();
          return visible(node) && !table.contains(node) && !node.contains(table) && (label === 'pagination' || label === 'pager');
        });
        var pager = null, pageToken = null, next = null, previous = null;
        if (navs.length > 1) return result('invalid-pager');
        if (navs.length === 1) {
          var nav = navs[0];
          var ranges = Array.prototype.slice.call(nav.querySelectorAll('*')).filter(function (node) { return visible(node) && rangeTotal(node.textContent) !== null; });
          var current = Array.prototype.filter.call(nav.querySelectorAll('[aria-current=page]'), visible);
          var marker = current.length === 1 ? clean(current[0].textContent).match(/^(?:page\s+)?(\d+)$/i) : null;
          var actions = function (names) {
            return Array.prototype.slice.call(nav.querySelectorAll('button,[role=button],a')).filter(function (node) {
              return safeAction(node, true) && names.indexOf(clean(node.getAttribute('aria-label') || node.innerText || node.textContent).toLowerCase()) !== -1;
            });
          };
          var nexts = actions(['next', 'next page', '›', '»', '>']);
          var prevs = actions(['previous', 'previous page', 'prev', 'prev page', '‹', '«', '<']);
          if ((ranges.length !== 1 && !marker) || ranges.length > 1 || nexts.length !== 1 || prevs.length > 1) return result('invalid-pager');
          pageToken = ranges.length === 1 ? 'range:' + clean(ranges[0].textContent) : 'page:' + marker[1];
          pager = nav; next = nexts[0]; previous = prevs.length === 1 ? prevs[0] : null;
        }
        var positions = [], controlCounts = [];
        matches.forEach(function (row) {
          var rect = row.getBoundingClientRect();
          var position = Math.round((rect.top + scrollTop) * 1000);
          positions.push(position);
          controlCounts.push(controlsFor(row).length);
        });
        var signatureText = rows.map(function (row) { return clean(row.textContent); }).join('\u001f');
        var signature = 2166136261;
        for (var si = 0; si < signatureText.length; si++) signature = Math.imul(signature ^ signatureText.charCodeAt(si), 16777619);
        var state = { documentToken: documentToken, pageToken: pageToken, hasPager: !!pager,
          nextEnabled: !!(next && !next.disabled && !next.hasAttribute('disabled') && next.getAttribute('aria-disabled') !== 'true'),
          previousEnabled: !!(previous && !previous.disabled && !previous.hasAttribute('disabled') && previous.getAttribute('aria-disabled') !== 'true'),
          scrollTop: scrollTop, scrollClient: scrollClient, scrollMax: scrollMax, signature: signature >>> 0,
          targetCount: matches.length, renderedRowCount: rows.length, targetPositions: positions, targetControlCounts: controlCounts };
        if (request.command === 'read') return result('ok', state);
        if (Date.now() >= request.deadlineEpochMilliseconds) return result('expired', state);
        if (request.pageToken !== null && request.pageToken !== pageToken) return result('page-changed', state);
        if (request.command === 'next' || request.command === 'previous') {
          var button = request.command === 'next' ? next : previous;
          if (!pager || !button || button.disabled || button.hasAttribute('disabled') || button.getAttribute('aria-disabled') === 'true') return result('no-control', state);
          button.click(); return result('action-started', state);
        }
        if (request.command === 'scroll') {
          if (!scroller || !Number.isFinite(request.desiredScrollTop)) return result('no-scroller', state);
          scroller.scrollTop = Math.max(0, Math.min(scrollMax, request.desiredScrollTop));
          return result('action-started', Object.assign({}, state, { scrollTop: scroller.scrollTop }));
        }
        if (request.command === 'click') {
          if (matches.length !== 1) return result(matches.length ? 'ambiguous-row' : 'no-row', state);
          if (request.expectedPosition !== null && positions[0] !== request.expectedPosition) return result('identity-mismatch', state);
          var controls = controlsFor(matches[0]);
          if (controls.length !== 1) return result(controls.length ? 'ambiguous-control' : 'no-control', state);
          if (Date.now() >= request.deadlineEpochMilliseconds) return result('expired', state);
          controls[0].click(); return result('clicked', state);
        }
        return result('invalid-command');
      } catch (_) { return result('error'); }
    })();
    """;
}
