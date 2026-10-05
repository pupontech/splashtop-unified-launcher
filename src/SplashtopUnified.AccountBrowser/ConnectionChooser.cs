using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

/// <summary>What the console's documented connect chooser currently shows.</summary>
internal sealed record ChooserProbe(bool Present, string? Heading, bool NativeAvailable, bool WebAvailable);

/// <summary>
/// Reads and applies the console's documented "Connect to this Computer" chooser.
/// Choice labels are the exact official labels; the app never invents an option, never
/// picks the web fallback on its own, and never fabricates a user gesture.
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

    /// <summary>Reports whether the documented chooser is on screen, and which options it offers.</summary>
    public static string ProbeScript => """
    (function () {
      try {
        if (window.top !== window.self) { return '{}'; }
        var host = (location.hostname || '').toLowerCase();
        if (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu') { return '{}'; }
        var clean = function (v, l) { return String(v === null || v === undefined ? '' : v).replace(/\s+/g, ' ').trim().slice(0, l || 160); };
        var nativeLabel = 'From the Splashtop Business App';
        var webLabel = 'From the Web App in this browser';
        var nodes = Array.prototype.slice.call(document.querySelectorAll('button,[role=button],[role=menuitem],a,label,li,div'));
        var nativeAvailable = false, webAvailable = false;
        for (var i = 0; i < nodes.length; i++) {
          var text = clean(nodes[i].textContent, 200);
          if (!nativeAvailable && text.indexOf(nativeLabel) !== -1) { nativeAvailable = true; }
          if (!webAvailable && text.indexOf(webLabel) !== -1) { webAvailable = true; }
        }
        var body = clean(document.body ? document.body.textContent : '', 6000);
        var heading = body.indexOf('Connect to this Computer') !== -1 ? 'Connect to this Computer' : null;
        return JSON.stringify({
          source: 'splashtop-connection-chooser', version: 1,
          present: nativeAvailable || webAvailable,
          heading: heading, nativeAvailable: nativeAvailable, webAvailable: webAvailable
        });
      } catch (e) { return '{}'; }
    })();
    """;

    /// <summary>
    /// Activates one documented option by its exact official label. Refuses when the option
    /// is not on screen so a changed page can never cause an unintended click.
    /// </summary>
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
            "    var host = (location.hostname || '').toLowerCase();" +
            "    if (host !== 'my.splashtop.com' && host !== 'my.splashtop.eu') { return 'blocked'; }" +
            "    var clean = function (v) { return String(v === null || v === undefined ? '' : v).replace(/\\s+/g, ' ').trim(); };" +
            $"    var target = {JsonSerializer.Serialize(label)};" +
            "    var nodes = Array.prototype.slice.call(document.querySelectorAll('button,[role=button],[role=menuitem],a,label,li,div'));" +
            "    var best = null, bestLength = 1e9;" +
            "    for (var i = 0; i < nodes.length; i++) {" +
            "      var text = clean(nodes[i].textContent);" +
            "      if (text.indexOf(target) !== -1 && text.length < bestLength) { best = nodes[i]; bestLength = text.length; }" +
            "    }" +
            "    if (!best) { return 'no-option'; }" +
            "    var clickable = best.closest ? (best.closest('button,[role=button],[role=menuitem],a,label') || best) : best;" +
            "    clickable.click();" +
            "    return 'clicked';" +
            "  } catch (e) { return 'error'; }" +
            "})();";
    }

    /// <summary>Fail-closed validation of a chooser probe message.</summary>
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
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
                !string.Equals(source.GetString(), ProbeSource, StringComparison.Ordinal) ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var parsedVersion) || parsedVersion != ScriptVersion ||
                !root.TryGetProperty("present", out var present) || present.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("nativeAvailable", out var native) || native.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("webAvailable", out var web) || web.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            string? heading = null;
            if (root.TryGetProperty("heading", out var headingElement) && headingElement.ValueKind != JsonValueKind.Null)
            {
                if (headingElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = headingElement.GetString();
                if (text is null || text.Length > 160 || text.Any(ch => char.IsControl(ch)))
                {
                    return false;
                }

                heading = text.Length == 0 ? null : text;
            }

            var presentValue = present.GetBoolean();
            var nativeValue = native.GetBoolean();
            var webValue = web.GetBoolean();
            // A "present" claim with no option, or an option with no chooser, is contradictory.
            if (presentValue != (nativeValue || webValue))
            {
                return false;
            }

            probe = new ChooserProbe(presentValue, heading, nativeValue, webValue);
            return true;
        }
    }
}
