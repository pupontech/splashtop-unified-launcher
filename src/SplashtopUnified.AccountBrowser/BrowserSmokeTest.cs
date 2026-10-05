using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace SplashtopUnified.AccountBrowser;

internal static class BrowserSmokeTest
{
    public static async Task<int> RunAsync()
    {
        Window? window = null;
        var panes = new List<AccountWebViewPane>();
        var handoffDispatcher = new RecordingBusinessAppUriDispatcher();
        var handoffEvents = new ConcurrentQueue<HandoffEvent>();
        var statusMessages = new ConcurrentQueue<string>();
        try
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--smoke-test-root");
            if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Smoke test requires an isolated --smoke-test-root.");
            var root = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(root);
            var fixture = Path.Combine(root, "fixture");
            Directory.CreateDirectory(fixture);
            await File.WriteAllTextAsync(Path.Combine(fixture, "index.html"), "<!doctype html><html><title>Synthetic isolation fixture</title><body>Not a live account</body></html>");
            await File.WriteAllTextAsync(Path.Combine(fixture, "handoff.html"), CreateHandoffFixtureHtml());
            var folders = new[] { Path.Combine(root, "account-a"), Path.Combine(root, "account-b") };
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER"))) throw new InvalidOperationException("Global browser folder override would invalidate isolation.");
            var available = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(available)) throw new InvalidOperationException("WebView2 Runtime unavailable.");
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            window = new Window { Title = "Synthetic account-isolation test", Width = 900, Height = 600, Content = grid };
            window.Show();
            var environments = new CoreWebView2Environment[2];
            for (var i = 0; i < 2; i++)
            {
                environments[i] = await CoreWebView2Environment.CreateAsync(userDataFolder: folders[i]);
                var pane = await CreatePaneAsync(grid, i, environments[i], folders[i], fixture, i == 0 ? handoffDispatcher : null, i == 0 ? handoffEvents : null, statusMessages);
                panes.Add(pane);
            }
            Require(!string.Equals(environments[0].UserDataFolder, environments[1].UserDataFolder, StringComparison.OrdinalIgnoreCase), "Distinct actual browser folders");
            await panes[0].ExecuteScriptAsync("document.cookie='account=A; path=/; max-age=3600; SameSite=Lax'; localStorage.setItem('account','A'); sessionStorage.setItem('account','A');");
            await panes[1].ExecuteScriptAsync("document.cookie='account=B; path=/; max-age=3600; SameSite=Lax'; localStorage.setItem('account','B'); sessionStorage.setItem('account','B');");
            await AssertStateAsync(panes[0], "A", true); await AssertStateAsync(panes[1], "B", true);
            // Recreate controls while retaining the actual environments.
            foreach (var pane in panes) { grid.Children.Remove(pane); pane.Dispose(); }
            panes.Clear();
            for (var i = 0; i < 2; i++) panes.Add(await CreatePaneAsync(grid, i, environments[i], folders[i], fixture, i == 0 ? handoffDispatcher : null, i == 0 ? handoffEvents : null, statusMessages));
            await AssertStateAsync(panes[0], "A", false); await AssertStateAsync(panes[1], "B", false);
            // Create new environment objects bound to the same persistent folders.
            foreach (var pane in panes) { grid.Children.Remove(pane); pane.Dispose(); }
            panes.Clear();
            for (var i = 0; i < 2; i++)
            {
                environments[i] = await CoreWebView2Environment.CreateAsync(userDataFolder: folders[i]);
                panes.Add(await CreatePaneAsync(grid, i, environments[i], folders[i], fixture, i == 0 ? handoffDispatcher : null, i == 0 ? handoffEvents : null, statusMessages));
            }
            await AssertStateAsync(panes[0], "A", false); await AssertStateAsync(panes[1], "B", false);
            var nativeHandoff = await RunNativeHandoffSmokeAsync(panes[0], fixture, handoffDispatcher, handoffEvents, statusMessages);
            var result = new
            {
                browserVersion = environments[0].BrowserVersionString,
                userDataFolders = environments.Select(e => e.UserDataFolder).ToArray(),
                webViewInitialized = panes.All(p => p.Core is not null),
                nativeHandoff,
                isolation = new { sameProcessTwoAccounts = true, cookiesIsolated = true, localStorageIsolated = true, sessionStorageIsolated = true, persistenceAfterControlRecreation = true, persistenceAfterEnvironmentRecreation = true }
            };
            Console.WriteLine("ACCOUNT_BROWSER_SMOKE_RESULT=" + JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ACCOUNT_BROWSER_SMOKE_FAILED=" + ex);
            return 1;
        }
        finally
        {
            foreach (var pane in panes) pane.Dispose();
            window?.Close();
        }
    }

    private static async Task<AccountWebViewPane> CreatePaneAsync(
        Grid grid,
        int column,
        CoreWebView2Environment environment,
        string folder,
        string fixture,
        IBusinessAppUriDispatcher? dispatcher,
        ConcurrentQueue<HandoffEvent>? handoffEvents,
        ConcurrentQueue<string> statusMessages)
    {
        Action<string, string, bool>? observer = handoffEvents is null
            ? null
            : (eventName, uri, isUserInitiated) => handoffEvents.Enqueue(new HandoffEvent(eventName, uri, isUserInitiated));
        var pane = new AccountWebViewPane(message =>
        {
            Console.Error.WriteLine("SMOKE_BROWSER_STATUS=" + message);
            statusMessages.Enqueue(message);
        }, dispatcher, observer);
        Grid.SetColumn(pane, column); grid.Children.Add(pane);
        try
        {
            await pane.InitializeWithEnvironmentAsync(environment, folder, null);
            pane.Core.SetVirtualHostNameToFolderMapping("isolation.test", fixture, CoreWebView2HostResourceAccessKind.DenyCors);
            await pane.NavigateAndWaitAsync("https://isolation.test/index.html", TimeSpan.FromSeconds(20));
            return pane;
        }
        catch { grid.Children.Remove(pane); pane.Dispose(); throw; }
    }

    private static string CreateHandoffFixtureHtml() => """
 <!doctype html>
 <html><head><meta charset="utf-8"><title>Synthetic native handoff fixture</title>
 <style>body{font:20px sans-serif;margin:24px}a,button{display:block;margin:18px 0;padding:12px}</style>
 </head><body>
 <a id="direct" href="st-business://com.splashtop.business?source=main-click">Main-page native link</a>
 <a id="popup" target="_blank" href="st-business://com.splashtop.business?source=popup-click">Popup native link</a>
 <a id="unrelated" href="file:///__splashtop_native_handoff_smoke_missing__.txt">Unrelated scheme link</a>
 <a id="untrusted" href="st-business://com.splashtop.business?source=untrusted-origin">Untrusted-origin link</a>
 <button id="delayed" type="button" onclick="setTimeout(() => { location.href = 'st-business://com.splashtop.business?source=delayed-redirect'; }, 300)">Delayed redirect link</button>
 </body></html>
 """;

    private static async Task<object> RunNativeHandoffSmokeAsync(
        AccountWebViewPane pane,
        string fixture,
        RecordingBusinessAppUriDispatcher dispatcher,
        ConcurrentQueue<HandoffEvent> handoffEvents,
        ConcurrentQueue<string> statusMessages)
    {
        const string trustedUrl = "https://my.splashtop.com/handoff.html";
        const string untrustedUrl = "https://untrusted.test/handoff.html";
        const string directUri = "st-business://com.splashtop.business?source=main-click";
        const string popupUri = "st-business://com.splashtop.business?source=popup-click";
        const string unrelatedUri = "file:///__splashtop_native_handoff_smoke_missing__.txt";
        const string untrustedUri = "st-business://com.splashtop.business?source=untrusted-origin";
        const string delayedUri = "st-business://com.splashtop.business?source=delayed-redirect";

        pane.Core.SetVirtualHostNameToFolderMapping("my.splashtop.com", fixture, CoreWebView2HostResourceAccessKind.DenyCors);
        pane.Core.SetVirtualHostNameToFolderMapping("untrusted.test", fixture, CoreWebView2HostResourceAccessKind.DenyCors);
        pane.Core.Settings.AreDevToolsEnabled = true;
        await pane.NavigateAndWaitAsync(trustedUrl, TimeSpan.FromSeconds(20));

        await ClickElementAsync(pane, "direct");
        await WaitUntilAsync(
            () => dispatcher.ReceivedUris.Contains(directUri) || CountBlockedHandoffs(statusMessages) > 0,
            "the direct native link to reach a handoff event");
        var directEvents = handoffEvents.Where(item => item.Uri == directUri).ToArray();
        Require(directEvents.Any(item => item.IsUserInitiated), "A real direct-link click is reported as user initiated by WebView2");
        Require(dispatcher.ReceivedUris.SequenceEqual(new[] { directUri }), "Exactly one exact URI dispatch from the main-page direct link");

        await ClickElementAsync(pane, "popup");
        await WaitUntilAsync(
            () => dispatcher.ReceivedUris.Contains(popupUri) || CountBlockedHandoffs(statusMessages) > 0,
            "the popup native link to reach a handoff event");
        var popupEvents = handoffEvents.Where(item => item.Uri == popupUri).ToArray();
        Require(popupEvents.Any(item => item.EventName == "OnNewWindowRequested"), "The popup link raises WebView2 NewWindowRequested");
        Require(popupEvents.Any(item => item.IsUserInitiated), "A real popup-link click is reported as user initiated by WebView2");
        Require(dispatcher.ReceivedUris.SequenceEqual(new[] { directUri, popupUri }), "Exactly one exact URI dispatch each from the direct and popup links");

        var blockedBeforeUnrelated = CountBlockedHandoffs(statusMessages);
        await ClickElementAsync(pane, "unrelated");
        await WaitUntilAsync(
            () => CountBlockedHandoffs(statusMessages) > blockedBeforeUnrelated,
            "the unrelated-scheme link to be explicitly rejected");
        Require(handoffEvents.Any(item => item.Uri == unrelatedUri), "The unrelated-scheme click reaches a WebView2 handoff event");
        Require(dispatcher.ReceivedUris.SequenceEqual(new[] { directUri, popupUri }), "The unrelated scheme is rejected without dispatch");

        await pane.NavigateAndWaitAsync(untrustedUrl, TimeSpan.FromSeconds(20));
        var blockedBeforeUntrusted = CountBlockedHandoffs(statusMessages);
        await ClickElementAsync(pane, "untrusted");
        await WaitUntilAsync(
            () => CountBlockedHandoffs(statusMessages) > blockedBeforeUntrusted,
            "the untrusted-origin native link to be explicitly rejected");
        Require(handoffEvents.Any(item => item.Uri == untrustedUri), "The untrusted-origin click reaches a WebView2 handoff event");
        Require(dispatcher.ReceivedUris.SequenceEqual(new[] { directUri, popupUri }), "The untrusted origin is rejected without dispatch");

        await pane.NavigateAndWaitAsync(trustedUrl, TimeSpan.FromSeconds(20));
        var blockedBeforeDelayed = CountBlockedHandoffs(statusMessages);
        await ClickElementAsync(pane, "delayed");
        await WaitUntilAsync(
            () => handoffEvents.Any(item => item.Uri == delayedUri) || CountBlockedHandoffs(statusMessages) > blockedBeforeDelayed,
            "the delayed redirect click to reach a handoff event");
        var delayedEvents = handoffEvents.Where(item => item.Uri == delayedUri).ToArray();
        Require(delayedEvents.Length > 0, "The delayed redirect reaches a real WebView2 external-scheme or navigation event");
        var delayedWasUserInitiated = delayedEvents.Any(item => item.IsUserInitiated);
        var delayedWasDispatched = dispatcher.ReceivedUris.Contains(delayedUri);
        Require(delayedWasDispatched == delayedWasUserInitiated, "Delayed redirect dispatch agrees with WebView2's observed IsUserInitiated value");
        var expectedDispatches = delayedWasDispatched
            ? new[] { directUri, popupUri, delayedUri }
            : new[] { directUri, popupUri };
        Require(dispatcher.ReceivedUris.SequenceEqual(expectedDispatches), "No duplicate or rejected-URI dispatch occurred during the handoff smoke test");

        return new
        {
            syntheticTrustedOrigin = trustedUrl,
            dispatchUris = dispatcher.ReceivedUris,
            directLinkDispatchCount = dispatcher.ReceivedUris.Count(uri => uri == directUri),
            popupLinkDispatchCount = dispatcher.ReceivedUris.Count(uri => uri == popupUri),
            popupNewWindowRequested = popupEvents.Any(item => item.EventName == "OnNewWindowRequested"),
            unrelatedSchemeRejected = true,
            untrustedOriginRejected = true,
            delayedRedirect = new
            {
                uri = delayedUri,
                observedIsUserInitiatedValues = delayedEvents.Select(item => item.IsUserInitiated).Distinct().ToArray(),
                dispatched = delayedWasDispatched
            }
        };
    }

    private static async Task ClickElementAsync(AccountWebViewPane pane, string elementId)
    {
        var script = $"(() => {{ const e=document.getElementById('{elementId}'); if(!e) throw new Error('Missing smoke link: {elementId}'); e.scrollIntoView({{block:'center'}}); const r=e.getBoundingClientRect(); return JSON.stringify({{x:r.left+r.width/2,y:r.top+r.height/2}}); }})()";
        var serializedPosition = await pane.ExecuteScriptAsync(script);
        var positionJson = JsonSerializer.Deserialize<string>(serializedPosition)
            ?? throw new InvalidOperationException("WebView2 did not return a click position for the synthetic fixture.");
        using var position = JsonDocument.Parse(positionJson);
        var x = position.RootElement.GetProperty("x").GetDouble();
        var y = position.RootElement.GetProperty("y").GetDouble();

        await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
    }

    private static int CountBlockedHandoffs(ConcurrentQueue<string> statusMessages) =>
        statusMessages.Count(message => message.StartsWith("Blocked an external or executable URI scheme", StringComparison.Ordinal));

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for WebView2 smoke assertion: " + description);
    }

    private static async Task AssertStateAsync(AccountWebViewPane pane, string expected, bool checkSession)
    {
        var text = await pane.ExecuteScriptAsync("JSON.stringify({cookie:document.cookie,local:localStorage.getItem('account'),session:sessionStorage.getItem('account')})");
        var inner = JsonSerializer.Deserialize<string>(text) ?? throw new InvalidOperationException("Missing script result.");
        using var state = JsonDocument.Parse(inner);
        Require(state.RootElement.GetProperty("cookie").GetString() == "account=" + expected, "Cookie account isolation/persistence");
        Require(state.RootElement.GetProperty("local").GetString() == expected, "Local storage account isolation/persistence");
        if (checkSession) Require(state.RootElement.GetProperty("session").GetString() == expected, "Session storage account isolation");
    }
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Failed real WebView2 assertion: " + name);
    }

    private sealed record HandoffEvent(string EventName, string Uri, bool IsUserInitiated);

    private sealed class RecordingBusinessAppUriDispatcher : IBusinessAppUriDispatcher
    {
        private readonly ConcurrentQueue<string> _receivedUris = new();
        public string[] ReceivedUris => _receivedUris.ToArray();
        public void Dispatch(string uri) => _receivedUris.Enqueue(uri);
    }
}
