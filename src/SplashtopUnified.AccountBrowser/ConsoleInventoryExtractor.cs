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
    string? Diagnostic,
    int PagesVisited = 0,
    int WalkMillis = 0,
    string? Mode = null,
    bool HasAmbiguousDuplicates = false);

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
    public const int MaxWalkMillis = 60_000;
    private const int MaxFieldChars = 512;

    private static readonly string[] Outcomes = ["complete", "incomplete", "unavailable"];

    /// <summary>
    /// In-page extractor. Guarded to the official console origins and the top document,
    /// matches the computers list by visible column headers or grid roles rather than
    /// minified class names, then walks every page and every virtual-scroll window so the
    /// whole list is read rather than only the rows currently on screen. Posts one
    /// allowlisted JSON message.
    /// </summary>
    public static string ExtractScript => CreateExtractScript("unbound-request");

    public static string CreateExtractScript(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64 || requestId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded request correlation identifier is required.", nameof(requestId));
        }

        return ExtractScriptTemplate
            .Replace("__REQUEST_ID__", JsonSerializer.Serialize(requestId), StringComparison.Ordinal)
            .Replace("__MAX_WALK_MILLIS__", MaxWalkMillis.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string ExtractScriptTemplate => """
    (async function () {
      var requestId = __REQUEST_ID__;
      if (!requestId || window.top !== window.self) { return; }
      var currentUrl;
      try { currentUrl = new URL(location.href); } catch (_) { return; }
      var host = currentUrl.hostname.toLowerCase();
      if (currentUrl.protocol !== 'https:' || currentUrl.port !== '' || currentUrl.username !== '' || currentUrl.password !== '' ||
          (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu')) { return; }
      if (!('chrome' in window) || !window.chrome.webview) { return; }
      // An overlapping invocation does not own the document lock and must never release it.
      if (window.__splashtopInventoryWalkActive) { return; }
      window.__splashtopInventoryWalkActive = requestId;
      var MAX_ROWS = 5000;
      var MAX_STEPS = 80;
      var MAX_WALK_MILLIS = __MAX_WALK_MILLIS__;
      var now = function () { return (window.performance && window.performance.now) ? window.performance.now() : Date.now(); };
      var walkStartedAt = now();
      var walkDeadline = walkStartedAt + MAX_WALK_MILLIS;
      var postRead = function (payload) {
        payload.source = 'splashtop-console-inventory';
        payload.version = 1;
        payload.requestId = requestId;
        payload.ambiguousDuplicates = payload.ambiguousDuplicates === true;
        window.chrome.webview.postMessage(JSON.stringify(payload));
      };
      try {
        var clean = function (value, limit) {
          if (value === null || value === undefined) { return ''; }
          var text = String(value).replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '').replace(/\s+/g, ' ').trim();
          limit = limit || 512;
          return text.length > limit ? text.slice(0, limit) : text;
        };
        var sleep = function (ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); };
        var wanted = ['name', 'computer name', 'device name', 'group', 'notes'];
        var normalizeHeader = function (value) { return clean(value, 64).toLowerCase(); };
        var buildHeaderMap = function (grid) {
          var nodes;
          if (grid.tagName === 'TABLE') {
            var head = grid.querySelector('thead');
            if (!head || head.querySelectorAll('tr').length !== 1) { return null; }
            nodes = Array.prototype.slice.call(head.querySelector('tr').children);
            if (nodes.some(function (node) { return node.tagName !== 'TH' && node.tagName !== 'TD'; })) { return null; }
          } else {
            nodes = Array.prototype.slice.call(grid.querySelectorAll('[role=columnheader]'));
          }
          if (!nodes.length || nodes.some(function (node) { return !isVisible(node) || Number(node.colSpan || 1) !== 1 || Number(node.rowSpan || 1) !== 1; })) { return null; }
          var result = { name: [], deviceName: [], group: [], notes: [] };
          nodes.forEach(function (node, index) {
            var text = normalizeHeader(node.textContent);
            if (text === 'name' || text === 'computer name') { result.name.push(index); }
            else if (text === 'device name') { result.deviceName.push(index); }
            else if (text === 'group') { result.group.push(index); }
            else if (text === 'notes') { result.notes.push(index); }
          });
          if (result.name.length !== 1 || result.deviceName.length !== 1 || result.group.length !== 1 || result.notes.length > 1) { return null; }
          return { nodes: nodes, name: result.name[0], deviceName: result.deviceName[0], group: result.group[0], notes: result.notes.length ? result.notes[0] : null };
        };
        var isVisible = function (node) {
          if (!node || !node.isConnected) { return false; }
          for (var current = node; current && current.nodeType === 1; current = current.parentElement) {
            if (current.hasAttribute('hidden') || current.getAttribute('aria-hidden') === 'true') { return false; }
            var style = window.getComputedStyle(current);
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || Number(style.opacity) === 0) { return false; }
          }
          var rect = node.getBoundingClientRect();
          return node.getClientRects().length > 0 && rect.width > 0 && rect.height > 0;
        };
        var accessibleName = function (node) {
          return clean(node.getAttribute('aria-label') || node.innerText || node.textContent || '', 128);
        };
        var findGrid = function () {
          var candidates = Array.prototype.slice.call(document.querySelectorAll('table,[role=grid],[role=table]'));
          var matches = candidates.map(function (candidate) {
            if (!isVisible(candidate)) { return null; }
            var map = buildHeaderMap(candidate);
            return map ? { grid: candidate, map: map } : null;
          }).filter(Boolean);
          return matches.length === 1 ? matches[0] : null;
        };

        var safeAction = function (element, allowDisabled) {
          var tag = element.tagName.toLowerCase();
          var role = (element.getAttribute('role') || '').toLowerCase();
          if (tag !== 'button' && role !== 'button' && tag !== 'a') { return false; }
          if (!allowDisabled && (element.disabled || element.hasAttribute('disabled') || element.getAttribute('aria-disabled') === 'true')) { return false; }
          if (tag === 'a') {
            try {
              var targetUrl = new URL(element.href, location.href);
              if (targetUrl.protocol !== 'https:' || targetUrl.origin !== location.origin) { return false; }
            } catch (_) { return false; }
          }
          return isVisible(element);
        };
        var rowElements = function (grid, map) {
          if (grid.tagName === 'TABLE') {
            var body = grid.querySelector('tbody');
            if (!body) { return []; }
            return Array.prototype.filter.call(body.querySelectorAll(':scope > tr'), function (tr) { return tr.querySelectorAll(':scope > td').length === map.nodes.length; });
          }
          return Array.prototype.filter.call(grid.querySelectorAll('[role=row]'), function (row) {
            return row.querySelectorAll('[role=gridcell],[role=cell],td').length === map.nodes.length;
          });
        };

        var virtualDuplicateAmbiguity = false;
        var recordKey = function (record) {
          // Presence and enabled Connect state can change during a walk. They are
          // observations, not row identity, and must not create extra computer records.
          return JSON.stringify([record.name, record.deviceName, record.group, record.notes]);
        };
        var rememberScrollPositions = function (grid, columnMap, scroller, positions) {
          var nodes = rowElements(grid, columnMap);
          var scrollerTop = scroller.getBoundingClientRect().top;
          var ambiguous = false;
          for (var i = 0; i < nodes.length; i++) {
            var cells = grid.tagName === 'TABLE'
              ? nodes[i].querySelectorAll(':scope > td')
              : nodes[i].querySelectorAll('[role=gridcell],[role=cell],td');
            var record = {
              name: clean(cells[columnMap.name] ? cells[columnMap.name].textContent : '', 512),
              deviceName: clean(cells[columnMap.deviceName] ? cells[columnMap.deviceName].textContent : '', 512),
              group: clean(cells[columnMap.group] ? cells[columnMap.group].textContent : '', 512),
              notes: columnMap.notes === null ? '' : clean(cells[columnMap.notes] ? cells[columnMap.notes].textContent : '', 512)
            };
            if (!record.name) { continue; }
            var key = recordKey(record);
            var rect = nodes[i].getBoundingClientRect();
            var absoluteTop = rect.top - scrollerTop + scroller.scrollTop;
            if (!isFinite(absoluteTop)) { ambiguous = true; continue; }
            if (Object.prototype.hasOwnProperty.call(positions, key)) {
              // Virtualized tables can have per-window layout drift relative to their spacer.
              // Allow at most half a viewport; a distant recurrence still fails closed.
              if (Math.abs(positions[key] - absoluteTop) > Math.max(32, scroller.clientHeight * 0.5)) { ambiguous = true; }
            }
            positions[key] = absoluteTop;
          }
          return ambiguous;
        };
        var collect = function (grid, columnMap, seen, rows, preserveMultiplicity) {
          var nodes = rowElements(grid, columnMap);
          var sampleCounts = Object.create(null);
          var sampleRecords = [];
          for (var i = 0; i < nodes.length && rows.length < MAX_ROWS; i++) {
            var cells = grid.tagName === 'TABLE'
              ? nodes[i].querySelectorAll(':scope > td')
              : nodes[i].querySelectorAll('[role=gridcell],[role=cell],td');
            var name = clean(cells[columnMap.name] ? cells[columnMap.name].textContent : '', 512);
            if (!name) { continue; }
            var record = {
              name: name,
              deviceName: clean(cells[columnMap.deviceName] ? cells[columnMap.deviceName].textContent : '', 512),
              group: clean(cells[columnMap.group] ? cells[columnMap.group].textContent : '', 512),
              notes: columnMap.notes === null ? '' : clean(cells[columnMap.notes] ? cells[columnMap.notes].textContent : '', 512),
              hasConnectControl: false,
              status: null
            };
            record.hasConnectControl = (function (rowNode) {
              var controls = Array.prototype.slice.call(rowNode.querySelectorAll('button,[role=button],a[href]'))
                .filter(function (control) { return safeAction(control) && accessibleName(control).toLowerCase() === 'connect'; });
              return controls.length === 1;
            })(nodes[i]);
            // Presence is read only from an explicit indicator the row itself carries: an
            // aria-label, title or image alt. A device name that merely contains such a word
            // is never treated as status.
            record.status = (function (rowNode) {
              var statusWords = /^(online|offline|available|unavailable|connected|disconnected|in use|busy|idle|unreachable|sleeping|locked)$/i;
              var indicators = Array.prototype.slice.call(rowNode.querySelectorAll('[aria-label],[title],img[alt],svg[aria-label]'));
              for (var s = 0; s < indicators.length; s++) {
                var node = indicators[s];
                var text = clean(node.getAttribute('aria-label') || node.getAttribute('title') || node.getAttribute('alt') || '', 64);
                if (!text) { continue; }
                if (statusWords.test(text)) { return text; }
                var word = text.match(/\b(online|offline|available|unavailable|connected|disconnected|in use|busy|idle|unreachable|sleeping|locked)\b/i);
                if (word && text.length <= 40) { return word[1]; }
              }
              return null;
            })(nodes[i]);
            sampleRecords.push(record);
          }
          if (preserveMultiplicity) {
            Array.prototype.push.apply(rows, sampleRecords.slice(0, Math.max(0, MAX_ROWS - rows.length)));
          } else {
            for (var sampleIndex = 0; sampleIndex < sampleRecords.length && rows.length < MAX_ROWS; sampleIndex++) {
              var record = sampleRecords[sampleIndex];
              var key = recordKey(record);
              sampleCounts[key] = (sampleCounts[key] || 0) + 1;
              if (sampleCounts[key] > 1) { virtualDuplicateAmbiguity = true; }
              if (sampleCounts[key] > (seen[key] || 0)) { rows.push(record); }
            }
            Object.keys(sampleCounts).forEach(function (key) {
              seen[key] = Math.max(seen[key] || 0, sampleCounts[key]);
            });
          }
          window.chrome.webview.postMessage(JSON.stringify({
            source: 'splashtop-inventory-progress', version: 1, requestId: requestId,
            rowsRead: rows.length, total: reportedTotal == null ? null : reportedTotal
          }));
          return nodes.length;
        };

        // A cheap fingerprint of what is currently rendered. Used to detect that a page or
        // scroll step has actually repainted, so the walk never busy-waits a fixed interval
        // per step and never re-reads the whole list to find out.
        var signatureOf = function () {
          var selector = grid.tagName === 'TABLE' ? 'tbody tr' : '[role=row]';
          var nodes = grid.querySelectorAll(selector);
          var count = nodes.length;
          var first = count ? clean(nodes[0].textContent, 48) : '';
          var last = count ? clean(nodes[count - 1].textContent, 48) : '';
          var pager = findVerifiedPager(grid);
          return count + '|' + first + '|' + last + '|' + (pager.valid ? pager.pageToken : '');
        };

        var waitForChange = function (signature, maxMillis, repaint) {
          var started = now();
          return (function poll() {
            if (now() - started >= maxMillis || now() >= walkDeadline) { return Promise.resolve(false); }
            return sleep(Math.min(40, Math.max(1, maxMillis - (now() - started)))).then(function () {
              if (signatureOf() !== signature || (repaint && repaint.changed)) { return sleep(40).then(function () { return true; }); }
              return poll();
            });
          })();
        };

        var waitForScrollPaint = async function (scroller, targetTop, maxMillis) {
          var signature = signatureOf();
          var repaint = { changed: false };
          var observer = new MutationObserver(function () { repaint.changed = true; });
          observer.observe(grid, { childList: true, subtree: true, characterData: true });
          try {
            scroller.scrollTop = targetTop;
            return await waitForChange(signature, Math.min(maxMillis, Math.max(0, walkDeadline - now())), repaint);
          } finally { observer.disconnect(); }
        };
        var hasStableScrollEnd = async function (scroller, viewport) {
          var stableHeight = scroller.scrollHeight;
          if (scroller.scrollTop + viewport < stableHeight - 8) { return false; }
          var changed = false;
          var observer = new MutationObserver(function () { changed = true; });
          observer.observe(scroller, { childList: true, subtree: true, characterData: true, attributes: true });
          try {
            // Two quiet intervals catch a list that appends another window at its first bottom.
            for (var quietCheck = 0; quietCheck < 2; quietCheck++) {
              if (now() >= walkDeadline) { return false; }
              await sleep(250);
              if (changed || scroller.scrollHeight !== stableHeight ||
                  scroller.scrollTop + viewport < scroller.scrollHeight - 8) { return false; }
            }
            return true;
          } finally { observer.disconnect(); }
        };

        var disabled = function (element) {
          return !element || !!element.disabled || element.hasAttribute('disabled') ||
            element.getAttribute('aria-disabled') === 'true' ||
            (!!element.classList && element.classList.contains('disabled'));
        };
        var parseRangeTotal = function (text) {
          var match = clean(text, 64).match(/^\d[\d,]*\s*[-–]\s*\d[\d,]*\s+of\s+(\d[\d,]*)$/i);
          if (!match) { return null; }
          var total = parseInt(match[1].replace(/,/g, ''), 10);
          return isFinite(total) && total >= 0 ? total : null;
        };
        var pagerActions = function (container, names) {
          return Array.prototype.slice.call(container.querySelectorAll('button,[role=button],a'))
            .filter(function (node) { return safeAction(node, true) && names.indexOf(accessibleName(node).toLowerCase()) !== -1; });
        };
        var findVerifiedPager = function (grid) {
          var navs = Array.prototype.slice.call(document.querySelectorAll('nav[aria-label],[role=navigation][aria-label]'))
            .filter(function (node) {
              var label = clean(node.getAttribute('aria-label'), 32).toLowerCase();
              return isVisible(node) && !grid.contains(node) && !node.contains(grid) && (label === 'pagination' || label === 'pager');
            });
          if (navs.length !== 1) { return { present: navs.length > 0, valid: false }; }
          var container = navs[0];
          var ranges = Array.prototype.slice.call(container.querySelectorAll('*')).filter(function (node) {
            return isVisible(node) && parseRangeTotal(node.textContent) !== null;
          });
          var currentPages = Array.prototype.filter.call(container.querySelectorAll('[aria-current=page]'), isVisible);
          var next = pagerActions(container, ['next', 'next page', '›', '»', '>']);
          var previous = pagerActions(container, ['previous', 'previous page', 'prev', 'prev page', '‹', '«', '<']);
          var first = pagerActions(container, ['first', 'first page', '«', '⏮']);
          var pageMarker = currentPages.length === 1
            ? clean(currentPages[0].textContent, 64).match(/^(?:page\s+)?(\d+)$/i)
            : null;
          if ((ranges.length !== 1 && !pageMarker) || ranges.length > 1 || next.length !== 1 || previous.length > 1 || first.length > 1) {
            return { present: true, valid: false };
          }
          var pageToken = ranges.length === 1
            ? 'range:' + clean(ranges[0].textContent, 64)
            : 'page:' + pageMarker[1];
          return { present: true, valid: true, container: container, range: ranges[0] || currentPages[0],
            total: ranges.length === 1 ? parseRangeTotal(ranges[0].textContent) : null, pageToken: pageToken,
            next: next[0], previous: previous.length === 1 ? previous[0] : null, first: first.length === 1 ? first[0] : null };
        };
        var findResultCount = function () {
          var nodes = Array.prototype.slice.call(document.querySelectorAll('[role=status],[aria-live=polite]'))
            .filter(function (node) {
              if (!isVisible(node)) { return false; }
              return /^\d[\d,]*\s+(computers?|devices?|items?|results?)$/i.test(clean(node.textContent, 64));
            });
          if (nodes.length !== 1) { return null; }
          var match = clean(nodes[0].textContent, 64).match(/^(\d[\d,]*)/);
          if (!match) { return null; }
          var total = parseInt(match[1].replace(/,/g, ''), 10);
          return isFinite(total) && total >= 0 ? total : null;
        };

        var hasPassword = !!document.querySelector('input[type=password]');
        var gridMatch = findGrid();
        if (!gridMatch) {
          postRead({ outcome: 'unavailable', pageKind: hasPassword ? 'login' : 'unknown',
            authentication: hasPassword ? 'required' : 'unknown', rowCount: 0, reportedTotal: null,
            rows: [], walkedToEnd: false, mode: 'none', pagesVisited: 0, walkMillis: 0 });
          return;
        }
        var grid = gridMatch.grid;
        var columnMap = gridMatch.map;

        var pagerInfo = findVerifiedPager(grid);
        var reportedTotal = pagerInfo.valid ? pagerInfo.total : (pagerInfo.present ? null : findResultCount());
        var seen = Object.create(null), rows = [];
        collect(grid, columnMap, seen, rows, true);
        var mode = 'single';
        var walkedToEnd = false;
        var pagesVisited = 1;

        // A reconciled result count needs no walk at all.
        if (reportedTotal !== null && reportedTotal === rows.length) {
          mode = pagerInfo.present ? 'paged' : 'single';
        } else if (pagerInfo.present) {
          mode = 'paged';
          if (pagerInfo.valid) {
            var pageWalkFailed = false;
            var next = pagerInfo.next;
            var visitedPages = new Set([pagerInfo.pageToken]);
            for (var page = 0; page < MAX_STEPS && rows.length < MAX_ROWS; page++) {
              if (!next || disabled(next)) { walkedToEnd = !!next && disabled(next); break; }
              if (now() >= walkDeadline) { pageWalkFailed = true; break; }
              var pageSignature = signatureOf();
              next.click();
              var changed = await waitForChange(pageSignature, Math.min(4000, Math.max(0, walkDeadline - now())));
              if (!changed) { pageWalkFailed = true; break; }
              var updatedPager = findVerifiedPager(grid);
              if (!updatedPager.valid || !updatedPager.present) { pageWalkFailed = true; break; }
              if (!updatedPager.pageToken || visitedPages.has(updatedPager.pageToken)) { pageWalkFailed = true; break; }
              pagerInfo = updatedPager;
              visitedPages.add(pagerInfo.pageToken);
              collect(grid, columnMap, seen, rows, true);
              pagesVisited++;
              next = pagerInfo.next;
              if (reportedTotal !== null && rows.length === reportedTotal) { break; }
            }
            if (!pageWalkFailed) {
              var endPager = findVerifiedPager(grid);
              walkedToEnd = endPager.valid && disabled(endPager.next);
            }

            // Restore only with one exact, visible control from the same verified pager.
            if (pagesVisited > 1) {
              var restorePager = findVerifiedPager(grid);
              if (restorePager.valid && restorePager.first && !disabled(restorePager.first)) {
                var restoreSignature = signatureOf();
                restorePager.first.click();
                await waitForChange(restoreSignature, Math.min(4000, Math.max(0, walkDeadline - now())));
              } else if (restorePager.valid && restorePager.previous) {
                for (var back = 0; back < pagesVisited - 1; back++) {
                  var previousPager = findVerifiedPager(grid);
                  if (!previousPager.valid || !previousPager.previous || disabled(previousPager.previous)) { break; }
                  var previousSignature = signatureOf();
                  previousPager.previous.click();
                  if (!await waitForChange(previousSignature, Math.min(4000, Math.max(0, walkDeadline - now())))) { break; }
                }
              }
            }
          }
        } else {
          // Probe only structural ancestors of the verified table, nearest first.
          // A nested page scroller and the document root are not arbitrary ambiguity:
          // choose the owner whose bounded scroll actually repaints the row window.
          var documentScroller = document.scrollingElement;
          var relevant = [];
          var eligibleScroller = function (node) {
            if (!node || !isVisible(node) || node.clientHeight <= 1 || node.scrollHeight <= node.clientHeight + 8) { return false; }
            var style = window.getComputedStyle(node);
            if (!style) { return false; }
            return node === documentScroller
              ? style.overflowY !== 'hidden' && style.overflowY !== 'clip'
              : style.overflowY === 'auto' || style.overflowY === 'scroll';
          };
          var ancestorCount = 0;
          for (var ancestor = grid; ancestor && ancestorCount++ < 64; ancestor = ancestor.parentElement) {
            if ((ancestor !== document.body && ancestor !== document.documentElement || ancestor === documentScroller) && eligibleScroller(ancestor)) {
              relevant.push(ancestor);
            }
          }
          if (documentScroller && relevant.indexOf(documentScroller) === -1 && documentScroller.contains(grid) && eligibleScroller(documentScroller)) {
            relevant.push(documentScroller);
          }
          if (relevant.length > 0) { mode = 'scrolled'; }
          if (relevant.length > 0) {
            rows.forEach(function (record) {
              var key = recordKey(record);
              seen[key] = (seen[key] || 0) + 1;
              if (seen[key] > 1) { virtualDuplicateAmbiguity = true; }
            });
            // Do not move any container when an explicit total already reconciles.
            if (reportedTotal === null || rows.length !== reportedTotal) {
              var scroller = null;
              var scrolledPositions = Object.create(null);
              for (var candidateIndex = 0; candidateIndex < Math.min(relevant.length, 6); candidateIndex++) {
                if (now() >= walkDeadline) { break; }
                var candidate = relevant[candidateIndex];
                var initialTop = candidate.scrollTop;
                var viewport = candidate.clientHeight;
                var stepSize = Math.min(Math.floor(viewport * 0.8), viewport - 1);
                if (stepSize <= 0) { continue; }
                try {
                  if (initialTop !== 0) { await waitForScrollPaint(candidate, 0, 1000); }
                  var candidateRows = [], candidateSeen = Object.create(null);
                  var candidatePositions = Object.create(null);
                  var candidatePositionsAmbiguous = rememberScrollPositions(grid, columnMap, candidate, candidatePositions);
                  collect(grid, columnMap, candidateSeen, candidateRows, false);
                  var probeTop = Math.min(stepSize, Math.max(0, candidate.scrollHeight - viewport));
                  if (probeTop > 0 && await waitForScrollPaint(candidate, probeTop, 1000) && candidate.scrollTop > 0) {
                    collect(grid, columnMap, candidateSeen, candidateRows, false);
                    candidatePositionsAmbiguous = rememberScrollPositions(grid, columnMap, candidate, candidatePositions) || candidatePositionsAmbiguous;
                    scroller = candidate;
                    rows = candidateRows;
                    seen = candidateSeen;
                    scrolledPositions = candidatePositions;
                    virtualDuplicateAmbiguity = virtualDuplicateAmbiguity || candidatePositionsAmbiguous;
                    break;
                  }
                } finally {
                  // A failed probe cannot leave an unrelated ancestor displaced.
                  if (scroller !== candidate && candidate.scrollTop !== initialTop) {
                    await waitForScrollPaint(candidate, initialTop, 300);
                  }
                }
              }
              if (scroller) {
                try {
                  var viewport = scroller.clientHeight;
                  var stepSize = Math.min(Math.floor(viewport * 0.8), viewport - 1);
                  for (var step = 0; step < MAX_STEPS && rows.length < MAX_ROWS; step++) {
                    if (reportedTotal !== null && rows.length === reportedTotal) { break; }
                    if (now() >= walkDeadline) { break; }
                    var previousTop = scroller.scrollTop;
                    var maxScrollTop = Math.max(0, scroller.scrollHeight - viewport);
                    var targetTop = Math.min(previousTop + stepSize, maxScrollTop);
                    if (targetTop === previousTop) {
                      if (previousTop + viewport >= scroller.scrollHeight - 8 && await hasStableScrollEnd(scroller, viewport)) {
                        walkedToEnd = true;
                        break;
                      }
                      collect(grid, columnMap, seen, rows, false);
                      virtualDuplicateAmbiguity = rememberScrollPositions(grid, columnMap, scroller, scrolledPositions) || virtualDuplicateAmbiguity;
                      continue;
                    }
                    if (!await waitForScrollPaint(scroller, targetTop, 1000)) { break; }
                    collect(grid, columnMap, seen, rows, false);
                    virtualDuplicateAmbiguity = rememberScrollPositions(grid, columnMap, scroller, scrolledPositions) || virtualDuplicateAmbiguity;
                    if (reportedTotal !== null && rows.length === reportedTotal) { break; }
                    if (scroller.scrollTop + viewport >= scroller.scrollHeight - 8) {
                      if (await hasStableScrollEnd(scroller, viewport)) { walkedToEnd = true; break; }
                      collect(grid, columnMap, seen, rows, false);
                      virtualDuplicateAmbiguity = rememberScrollPositions(grid, columnMap, scroller, scrolledPositions) || virtualDuplicateAmbiguity;
                    }
                  }
                } finally {
                  if (scroller.scrollTop !== 0) { await waitForScrollPaint(scroller, 0, 1000); }
                }
              }
            }
          }
        }

        // A complete result needs a reconciled total, a pager at disabled Next, or a
        // bounded unique-row scroll walk that remains at a stable physical bottom.
        var outcome;
        if (reportedTotal !== null && reportedTotal === rows.length) {
          outcome = 'complete';
        } else if (reportedTotal === null && walkedToEnd && (mode === 'paged' || mode === 'scrolled')) {
          outcome = 'complete';
        } else {
          outcome = 'incomplete';
        }
        if (reportedTotal !== null && reportedTotal !== rows.length) { outcome = 'incomplete'; }
        if (mode === 'scrolled' && virtualDuplicateAmbiguity) { outcome = 'incomplete'; }

        var walkMillis = Math.max(0, Math.round(now() - walkStartedAt));
        postRead({ outcome: outcome, pageKind: 'computerList', authentication: 'authenticated',
          rowCount: rows.length, reportedTotal: reportedTotal, rows: rows, walkedToEnd: walkedToEnd,
          mode: mode, pagesVisited: pagesVisited, walkMillis: walkMillis,
          ambiguousDuplicates: mode === 'scrolled' && virtualDuplicateAmbiguity });
      } catch (e) {
        try {
          postRead({ outcome: 'incomplete', pageKind: 'unknown', authentication: 'unknown', rowCount: 0,
            reportedTotal: null, rows: [], walkedToEnd: false, mode: 'none', pagesVisited: 0,
            walkMillis: Math.max(0, Math.round(now() - walkStartedAt)) });
        } catch (_) { }
      } finally {
        if (window.__splashtopInventoryWalkActive === requestId) {
          window.__splashtopInventoryWalkActive = null;
        }
      }
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
        var currentUrl;
        try { currentUrl = new URL(location.href); } catch (_) { return '{}'; }
        var host = currentUrl.hostname.toLowerCase();
        if (currentUrl.protocol !== 'https:' || currentUrl.port !== '' || currentUrl.username !== '' || currentUrl.password !== '' ||
            (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu')) { return '{}'; }
        var tableNodes = Array.prototype.slice.call(document.querySelectorAll('table'));
        var tables = tableNodes.length;
        // Only fixed semantic tokens and structural counts leave the page. Unknown
        // headings, cell contents, attributes and URLs are never returned.
        var tableSchemas = tableNodes.slice(0, 8).map(function (table) {
          var head = table.tHead;
          var headerRow = head && head.rows.length === 1 ? head.rows[0] : null;
          var cells = headerRow ? Array.prototype.slice.call(headerRow.cells) : [];
          var body = table.tBodies.length ? table.tBodies[0] : null;
          var rows = body ? Array.prototype.filter.call(body.rows, function (row) { return row.parentElement === body; }) : [];
          return {
            headerRows: head ? head.rows.length : 0,
            headerCellCount: cells.length,
            columns: cells.slice(0, 32).map(function (cell, index) {
              var label = String(cell.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
              var token = label === 'name' ? 'name' : label === 'computer name' ? 'computerName' :
                label === 'device name' ? 'deviceName' : label === 'group' ? 'group' : label === 'notes' ? 'notes' : 'other';
              return { index: index, field: token, tag: cell.tagName === 'TH' ? 'th' : 'td',
                columnSpan: cell.colSpan, rowSpan: cell.rowSpan };
            }),
            renderedRows: rows.length,
            // Bounded structural scroll ancestry only: identity-free enums and numbers,
            // no attributes, ids, classes or content.
            scrollAncestors: (function () {
              var list = [];
              var documentScroller = document.scrollingElement;
              var count = 0;
              for (var node = table; node && count++ < 64; node = node.parentElement) {
                var isDocument = node === documentScroller;
                var style = window.getComputedStyle(node);
                if (!style) { continue; }
                var scrollable = node === documentScroller
                  ? style.overflowY !== 'hidden' && style.overflowY !== 'clip'
                  : style.overflowY === 'auto' || style.overflowY === 'scroll';
                if (scrollable && node.scrollHeight > node.clientHeight + 8) {
                  list.push({ depth: list.length, isDocument: isDocument, tag: node.tagName.toLowerCase(),
                    overflowY: style.overflowY, clientHeight: node.clientHeight, scrollHeight: node.scrollHeight });
                }
                if (list.length >= 6) { break; }
              }
              return list;
            })(),
            sampleCellCounts: rows.slice(0, 3).map(function (row) { return row.cells.length; })
          };
        });
        var roleGrids = document.querySelectorAll('[role=grid],[role=table]').length;
        var scrollers = Array.prototype.filter.call(document.querySelectorAll('div,[role=region],main,section'), function (node) {
          var style = window.getComputedStyle(node);
          return style && (style.overflowY === 'auto' || style.overflowY === 'scroll') && node.scrollHeight > node.clientHeight + 8;
        }).length;
        return JSON.stringify({
          tables: tables,
          tableSchemas: tableSchemas,
          roleGrids: roleGrids,
          roleRows: document.querySelectorAll('[role=row]').length,
          scrollContainers: scrollers,
          hasPasswordField: !!document.querySelector('input[type=password]')
        });
      } catch (e) { return '{}'; }
    })();
    """;


    /// <summary>
    /// Fail-closed host-side validation. Rejects wrong source/version, bad shapes,
    /// oversized payloads, control characters, rows without a name, and row counts over
    /// the cap. Only then are records normalised.
    /// </summary>
    public static bool TryParse(string? json, out ConsoleInventoryRead? read) =>
        TryParseCore(json, expectedRequestId: null, out read);

    /// <summary>Parses only a payload correlated to the host's current request identifier.</summary>
    public static bool TryParse(string? json, string expectedRequestId, out ConsoleInventoryRead? read) =>
        TryParseCore(json, expectedRequestId, out read);

    private static bool TryParseCore(string? json, string? expectedRequestId, out ConsoleInventoryRead? read)
    {
        read = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadChars ||
            (expectedRequestId is not null && (string.IsNullOrWhiteSpace(expectedRequestId) || expectedRequestId.Length > 64 || expectedRequestId.Any(char.IsControl))))
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

            var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "source", "version", "requestId", "outcome", "pageKind", "authentication", "rowCount",
                "reportedTotal", "rows", "walkedToEnd", "mode", "pagesVisited", "walkMillis", "ambiguousDuplicates"
            };
            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!allowedProperties.Contains(property.Name) || !seenProperties.Add(property.Name))
                {
                    return false;
                }
            }

            if (!TryString(root, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64 ||
                (expectedRequestId is not null && !string.Equals(requestId, expectedRequestId, StringComparison.Ordinal)))
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
            var walkedToEnd = false;
            if (root.TryGetProperty("walkedToEnd", out var walkedElement))
            {
                if (walkedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                walkedToEnd = walkedElement.GetBoolean();
            }

            var ambiguousDuplicates = false;
            var ambiguousDuplicatesProvided = root.TryGetProperty("ambiguousDuplicates", out var ambiguousElement);
            if (ambiguousDuplicatesProvided)
            {
                if (ambiguousElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                ambiguousDuplicates = ambiguousElement.GetBoolean();
            }

            // Optional read-cost telemetry, used to spot a regression in the walk itself.
            var pagesVisited = 0;
            if (root.TryGetProperty("pagesVisited", out var pagesElement) && pagesElement.ValueKind != JsonValueKind.Null)
            {
                if (pagesElement.ValueKind != JsonValueKind.Number || !pagesElement.TryGetInt32(out pagesVisited) || pagesVisited < 0 || pagesVisited > 10_000)
                {
                    return false;
                }
            }

            var walkMillis = 0;
            if (root.TryGetProperty("walkMillis", out var millisElement) && millisElement.ValueKind != JsonValueKind.Null)
            {
                if (millisElement.ValueKind != JsonValueKind.Number || !millisElement.TryGetInt32(out walkMillis) || walkMillis < 0 || walkMillis > 600_000)
                {
                    return false;
                }
            }

            string? mode = null;
            if (root.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind != JsonValueKind.Null)
            {
                if (modeElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = modeElement.GetString();
                if (text is not ("none" or "single" or "paged" or "scrolled"))
                {
                    return false;
                }

                mode = text;
            }

            var rows = new List<ExtractedComputerRow>(rowsElement.GetArrayLength());
            foreach (var item in rowsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var rowProperties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (property.Name is not ("name" or "deviceName" or "group" or "notes" or "hasConnectControl" or "status") ||
                        !rowProperties.Add(property.Name))
                    {
                        return false;
                    }
                }

                if (!TryString(item, "name", out var name) || string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                if (!TryOptionalString(item, "deviceName", out var deviceName) ||
                    !TryOptionalString(item, "group", out var group) ||
                    !TryOptionalString(item, "notes", out var notes) ||
                    !item.TryGetProperty("hasConnectControl", out var connectElement) ||
                    connectElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !TryOptionalString(item, "status", out var status, 64))
                {
                    return false;
                }

                rows.Add(new ExtractedComputerRow(name, deviceName, group, notes, connectElement.GetBoolean(), status));
            }

            if (rowCount != rows.Count)
            {
                return false;
            }

            var outcome = Enum.Parse<InventoryOutcome>(outcomeText, ignoreCase: true);
            if (reportedTotal is { } reported && reported != rows.Count)
            {
                outcome = InventoryOutcome.Incomplete;
            }

            if (outcome == InventoryOutcome.Complete &&
                (pageKindText != "computerList" || authenticationText != "authenticated" ||
                 ambiguousDuplicates ||
                 (mode == "scrolled" && reportedTotal is null && !ambiguousDuplicatesProvided) ||
                 (reportedTotal != rows.Count && !(reportedTotal is null && (mode == "paged" || mode == "scrolled") && walkedToEnd))))
            {
                outcome = InventoryOutcome.Incomplete;
            }

            var diagnostic = outcome == InventoryOutcome.Complete
                ? null
                : outcome == InventoryOutcome.Unavailable
                    ? pageKindText == "login" && authenticationText == "required"
                        ? "Sign-in page detected; complete normal console sign-in."
                        : "Computer list layout not recognized; this does not establish a sign-in failure. Use Inspect consoles to report the structural survey."
                    : $"Console read marked {outcome.ToString().ToLowerInvariant()}; the list exposes no numeric identity or guaranteed total.";

            read = new ConsoleInventoryRead(
                outcome,
                Enum.Parse<ConsolePageKind>(pageKindText, ignoreCase: true),
                Enum.Parse<ConsoleAuthentication>(authenticationText, ignoreCase: true),
                rowCount,
                reportedTotal,
                rows,
                diagnostic,
                pagesVisited,
                walkMillis,
                mode,
                ambiguousDuplicates);
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

    private static bool TryOptionalString(JsonElement element, string property, out string? value, int maxLength = MaxFieldChars)
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
        if (text is null || !IsClean(text, maxLength))
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
