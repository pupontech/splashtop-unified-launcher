using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

/// <summary>What the console's documented connect chooser currently shows.</summary>
internal sealed record ChooserProbe(bool Present, string? Heading, bool NativeAvailable, bool WebAvailable);

/// <summary>
/// Reads and applies only exact controls inside one visible, semantically identified chooser.
/// Unsupported or ambiguous markup is left untouched.
/// </summary>
internal static class ConnectionChooser
{
    public const int ScriptVersion = 1;
    public const string ProbeSource = "splashtop-connection-chooser";
    public const string HeadingText = "Connect to this Computer";
    public const string NativeLabel = "From the Splashtop Business App";
    public const string WebLabel = "From the Web App in this browser";
    public const string NativeKey = "native";
    public const string WebKey = "web";

    /// <summary>Reports exact options from one visible dialog with the documented heading.</summary>
    public static string ProbeScript => """
    (function () {
      try {
        if (window.top !== window.self) { return '{}'; }
        var currentUrl;
        try { currentUrl = new URL(window.location.href); } catch (_) { return '{}'; }
        var host = currentUrl.hostname.toLowerCase();
        if (currentUrl.protocol !== 'https:' || currentUrl.port !== '' || currentUrl.username !== '' || currentUrl.password !== '' ||
            (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu')) { return '{}'; }
        var HEADING = 'Connect to this Computer';
        var NATIVE = 'From the Splashtop Business App';
        var WEB = 'From the Web App in this browser';
        var normalize = function (value) { return String(value === null || value === undefined ? '' : value).replace(/\s+/g, ' ').trim(); };
        var visible = function (node) {
          if (!node || !node.isConnected) { return false; }
          for (var current = node; current && current.nodeType === 1; current = current.parentElement) {
            if (current.hasAttribute('hidden') || current.getAttribute('aria-hidden') === 'true') { return false; }
            var style = window.getComputedStyle(current);
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || Number(style.opacity) === 0) { return false; }
          }
          var rect = node.getBoundingClientRect();
          return node.getClientRects().length > 0 && rect.width > 0 && rect.height > 0;
        };
        var actionName = function (node) { return normalize(node.getAttribute('aria-label') || node.innerText || node.textContent || ''); };
        var actionable = function (node) {
          var tag = node.tagName.toLowerCase();
          var role = normalize(node.getAttribute('role')).toLowerCase();
          if (tag !== 'button' && role !== 'button' && role !== 'menuitem' && tag !== 'a') { return false; }
          if (node.disabled || node.hasAttribute('disabled') || node.getAttribute('aria-disabled') === 'true' || !visible(node)) { return false; }
          if (tag === 'a') {
            try { var url = new URL(node.href, location.href); if (url.protocol !== 'https:' || url.origin !== location.origin) { return false; } }
            catch (_) { return false; }
          }
          return true;
        };
        var dialogs = Array.prototype.slice.call(document.querySelectorAll('[role=dialog],[aria-modal=true]'));
        if (dialogs.length > 16) { return '{}'; }
        var matches = [];
        for (var i = 0; i < dialogs.length; i++) {
          var dialog = dialogs[i];
          if (!visible(dialog)) { continue; }
          var headings = Array.prototype.slice.call(dialog.querySelectorAll('h1,h2,h3,h4,h5,h6,[role=heading]'))
            .filter(function (node) { return visible(node) && normalize(node.innerText || node.textContent) === HEADING; });
          if (headings.length > 1) { return '{}'; }
          if (headings.length !== 1) { continue; }
          var actions = Array.prototype.slice.call(dialog.querySelectorAll('button,[role=button],[role=menuitem],a'))
            .filter(actionable);
          var native = actions.filter(function (node) { return actionName(node) === NATIVE; });
          var web = actions.filter(function (node) { return actionName(node) === WEB; });
          if (native.length > 1 || web.length > 1) { return '{}'; }
          if (native.length || web.length) { matches.push({ native: native.length === 1, web: web.length === 1 }); }
        }
        if (matches.length !== 1) { return JSON.stringify({ source: 'splashtop-connection-chooser', version: 1, present: false, heading: null, nativeAvailable: false, webAvailable: false }); }
        return JSON.stringify({ source: 'splashtop-connection-chooser', version: 1, present: true, heading: HEADING,
          nativeAvailable: matches[0].native, webAvailable: matches[0].web });
      } catch (e) { return '{}'; }
    })();
    """;

    /// <summary>Activates one exact documented option in one verified visible chooser.</summary>
    public static string SelectScript(string optionKey)
    {
        var label = optionKey switch
        {
            NativeKey => NativeLabel,
            WebKey => WebLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(optionKey), optionKey, "Unknown chooser option.")
        };

        return "(function () {" +
            "  try {" +
            "    if (window.top !== window.self) { return 'blocked'; }" +
            "    var currentUrl; try { currentUrl = new URL(window.location.href); } catch (_) { return 'blocked'; }" +
            "    var host = currentUrl.hostname.toLowerCase();" +
            "    if (currentUrl.protocol !== 'https:' || currentUrl.port !== '' || currentUrl.username !== '' || currentUrl.password !== '' ||" +
            "        (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu')) { return 'blocked'; }" +
            "    var headingText = 'Connect to this Computer';" +
            $"    var target = {JsonSerializer.Serialize(label)};" +
            "    var normalize = function (value) { return String(value === null || value === undefined ? '' : value).replace(/\\s+/g, ' ').trim(); };" +
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
            "    var name = function (node) { return normalize(node.getAttribute('aria-label') || node.innerText || node.textContent || ''); };" +
            "    var actionable = function (node) {" +
            "      var tag = node.tagName.toLowerCase(); var role = normalize(node.getAttribute('role')).toLowerCase();" +
            "      if (tag !== 'button' && role !== 'button' && role !== 'menuitem' && tag !== 'a') { return false; }" +
            "      if (node.disabled || node.hasAttribute('disabled') || node.getAttribute('aria-disabled') === 'true' || !visible(node)) { return false; }" +
            "      if (tag === 'a') { try { var url = new URL(node.href, location.href); if (url.protocol !== 'https:' || url.origin !== location.origin) { return false; } } catch (_) { return false; } }" +
            "      return true;" +
            "    };" +
            "    var dialogs = Array.prototype.slice.call(document.querySelectorAll('[role=dialog],[aria-modal=true]'));" +
            "    if (dialogs.length > 16) { return 'ambiguous-chooser'; }" +
            "    var verified = [];" +
            "    for (var i = 0; i < dialogs.length; i++) {" +
            "      var dialog = dialogs[i]; if (!visible(dialog)) { continue; }" +
            "      var headings = Array.prototype.slice.call(dialog.querySelectorAll('h1,h2,h3,h4,h5,h6,[role=heading]')).filter(function (node) {" +
            "        return visible(node) && normalize(node.innerText || node.textContent) === headingText;" +
            "      });" +
            "      if (headings.length > 1) { return 'ambiguous-chooser'; }" +
            "      if (headings.length === 1) { verified.push(dialog); }" +
            "    }" +
            "    if (verified.length !== 1) { return verified.length ? 'ambiguous-chooser' : 'no-chooser'; }" +
            "    var options = Array.prototype.slice.call(verified[0].querySelectorAll('button,[role=button],[role=menuitem],a')).filter(function (node) {" +
            "      return actionable(node) && name(node) === target;" +
            "    });" +
            "    if (options.length !== 1) { return options.length ? 'ambiguous-option' : 'no-option'; }" +
            "    options[0].click();" +
            "    return 'clicked';" +
            "  } catch (e) { return 'error'; }" +
            "})();";
    }

    /// <summary>Fail-closed validation of the probe result returned by the owning document.</summary>
    public static bool TryParseProbe(string? json, out ChooserProbe? probe)
    {
        probe = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
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

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "source", "version", "present", "heading", "nativeAvailable", "webAvailable" };
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!allowed.Contains(property.Name) || !properties.Add(property.Name)) { return false; }
            }

            if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
                !string.Equals(source.GetString(), ProbeSource, StringComparison.Ordinal) ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var parsedVersion) || parsedVersion != ScriptVersion ||
                !root.TryGetProperty("present", out var present) || present.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("nativeAvailable", out var native) || native.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("webAvailable", out var web) || web.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            if (!root.TryGetProperty("heading", out var headingElement)) { return false; }
            string? heading = null;
            if (headingElement.ValueKind != JsonValueKind.Null)
            {
                if (headingElement.ValueKind != JsonValueKind.String) { return false; }
                var text = headingElement.GetString();
                if (!string.Equals(text, HeadingText, StringComparison.Ordinal)) { return false; }
                heading = text;
            }

            var presentValue = present.GetBoolean();
            var nativeValue = native.GetBoolean();
            var webValue = web.GetBoolean();
            if (presentValue != (nativeValue || webValue) ||
                (presentValue && heading != HeadingText) || (!presentValue && heading is not null))
            {
                return false;
            }

            probe = new ChooserProbe(presentValue, heading, nativeValue, webValue);
            return true;
        }
    }
}
