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
            await File.WriteAllTextAsync(Path.Combine(fixture, "computers.html"), InventoryFixture.ComputerListHtml());
            await File.WriteAllTextAsync(Path.Combine(fixture, "login.html"), InventoryFixture.LoginHtml());
            await File.WriteAllTextAsync(Path.Combine(fixture, "paged.html"), InventoryFixture.PagedListHtml());
            await File.WriteAllTextAsync(Path.Combine(fixture, "virtualised.html"), InventoryFixture.VirtualisedListHtml());
            await File.WriteAllTextAsync(Path.Combine(fixture, "chooser.html"), InventoryFixture.ChooserListHtml());
            await File.WriteAllTextAsync(Path.Combine(fixture, "large-virtualised.html"), InventoryFixture.LargeVirtualisedListHtml());
            grid.Children.Remove(panes[1]);
            panes[1].Dispose();
            var snapshots = new ConcurrentQueue<AccountInventorySnapshot>();
            panes[1] = await CreatePaneAsync(grid, 1, environments[1], folders[1], fixture, null, null, statusMessages, snapshots.Enqueue);
            var inventory = await RunInventorySmokeAsync(panes[1], fixture, snapshots);
            var credentialSaving = panes.All(p => p.Core.Settings.IsPasswordAutosaveEnabled && p.Core.Settings.IsGeneralAutofillEnabled);
            Require(credentialSaving, "Both isolated accounts allow the engine-managed password store");
            var result = new
            {
                browserVersion = environments[0].BrowserVersionString,
                userDataFolders = environments.Select(e => e.UserDataFolder).ToArray(),
                webViewInitialized = panes.All(p => p.Core is not null),
                nativeHandoff,
                inventory,
                credentialSaving = new { passwordAutosaveEnabled = panes[0].Core.Settings.IsPasswordAutosaveEnabled, generalAutofillEnabled = panes[0].Core.Settings.IsGeneralAutofillEnabled },
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
        ConcurrentQueue<string> statusMessages,
        Action<AccountInventorySnapshot>? inventoryObserver = null)
    {
        Action<string, string, bool>? observer = handoffEvents is null
            ? null
            : (eventName, uri, isUserInitiated) => handoffEvents.Enqueue(new HandoffEvent(eventName, uri, isUserInitiated));
        var pane = new AccountWebViewPane(message =>
        {
            Console.Error.WriteLine("SMOKE_BROWSER_STATUS=" + message);
            statusMessages.Enqueue(message);
        }, dispatcher, observer, accountId: column == 0 ? "smoke-a" : "smoke-b", accountName: column == 0 ? "Smoke A" : "Smoke B", inventoryObserver: inventoryObserver);
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
 <a id="unrelated" href="unrelated-smoke://invalid.test/blocked">Unrelated scheme link</a>
 <a id="untrusted" href="st-business://com.splashtop.business?source=untrusted-origin">Untrusted-origin link</a>
 <button id="delayed" type="button" onclick="setTimeout(() => { location.href = 'st-business://com.splashtop.business?source=delayed-redirect'; }, 300)">Connect: script-triggered native chooser follow-up</button>
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
        const string unrelatedUri = "unrelated-smoke://invalid.test/blocked";
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
            scriptTriggeredChooserFollowUp = new
            {
                uri = delayedUri,
                events = delayedEvents.Select(item => new { eventName = item.EventName, isUserInitiated = item.IsUserInitiated }).ToArray(),
                dispatched = delayedWasDispatched
            }
        };
    }

    /// <summary>
    /// Proves the inventory extractor against a fixture that reproduces only the column
    /// labels observed in the official Splashtop support screenshot, and proves that
    /// Connect is routed back into the owning page's own row control.
    /// </summary>
    private static async Task<object> RunInventorySmokeAsync(
        AccountWebViewPane pane,
        string fixture,
        ConcurrentQueue<AccountInventorySnapshot> snapshots)
    {
        const string trustedOrigin = "https://my.splashtop.com";
        pane.Core.SetVirtualHostNameToFolderMapping("my.splashtop.com", fixture, CoreWebView2HostResourceAccessKind.DenyCors);
        await pane.NavigateAndWaitAsync(trustedOrigin + "/computers.html", TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => snapshots.Any(item => item.PageKind == ConsolePageKind.ComputerList), "the console fixture to be extracted as a computer list");

        var read = snapshots.Last(item => item.PageKind == ConsolePageKind.ComputerList);
        Require(read.Authentication == ConsoleAuthentication.Authenticated, "The fixture list is recognised as an authenticated console");
        Require(read.Outcome == InventoryOutcome.Incomplete, "A list without a console-reported total is reported as incomplete, never complete");
        Require(read.Rows.Count == 5, "All five fixture rows are extracted");
        Require(read.Rows.Count(row => row.Name == "Fixture VM") == 2, "Duplicate display names are retained, never merged");
        Require(read.Rows.Count(row => row.HasConnectControl) == 3, "Rows that expose a Connect control are marked as such");
        Require(read.Rows[1].Group == "Servers", "The Group column is read from the fixture row");
        Require(read.Rows[1].Notes == "lab", "The Notes column is read from the fixture row");
        Require(read.Rows.Count(row => row.Status is { Length: > 0 }) == 3, "Rows that carry a presence indicator report a status");
        Require(read.Rows[0].Status == "Online", "The online indicator is read from the row's own indicator");
        Require(read.Rows[1].Status == "Offline", "An offline indicator is reported as offline");
        Require(read.Rows[2].Status == "In use", "A non-online/offline indicator is reported verbatim");
        Require(read.Rows.Single(row => row.Name == "Fixture Backup").Status is null,
            "A device name that contains a status word is never treated as a status");

        var firstConnect = await pane.ActivateConnectAsync(0);
        Require(firstConnect == "clicked", "Connect activates the owning page's own row control");
        var clickCount = await pane.ExecuteScriptAsync("window.__connectClicks||0");
        Require(clickCount.Trim() == "1", "The page observed exactly one Connect activation from the unified list");

        var missingRow = await pane.ActivateConnectAsync(99);
        Require(missingRow == "no-row", "A stale row index is refused without clicking anything");

        await pane.NavigateAndWaitAsync(trustedOrigin + "/login.html", TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => snapshots.Any(item => item.PageKind == ConsolePageKind.Login), "the sign-in fixture to be recognised");
        var signIn = snapshots.Last(item => item.PageKind == ConsolePageKind.Login);
        Require(signIn.Outcome == InventoryOutcome.Unavailable, "A sign-in page is never an empty complete list");
        Require(signIn.Authentication == ConsoleAuthentication.Required, "A sign-in page is reported as authentication required");

        // A single navigation must produce exactly one extraction: the old behaviour walked
        // the whole console five times per page load, which is what made refresh feel slow.
        await Task.Delay(1500);
        var extractionsForOneNavigation = snapshots.Count(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 5);
        Require(extractionsForOneNavigation == 1, "One page load triggers exactly one console walk, not repeated walks");

        // A list that only renders one page at a time must still be read in full, quickly.
        var pagedStartedAt = DateTimeOffset.Now;
        await pane.NavigateAndWaitAsync(trustedOrigin + "/paged.html", TimeSpan.FromSeconds(20));
        await WaitUntilAsync(
            () => snapshots.Any(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 9),
            "the paged fixture to be walked past its first page");
        var pagedWallMillis = (int)(DateTimeOffset.Now - pagedStartedAt).TotalMilliseconds;
        var paged = snapshots.Last(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 9);
        Require(paged.Rows.Count == 9, "All nine paged rows are captured, not only the first page");
        Require(paged.Outcome == InventoryOutcome.Complete, "Walking every page and matching the console total is reported as complete");
        Require(paged.ReportedTotal == 9, "The console-stated total is read from the pager");
        Require(paged.Rows.Count(row => row.Name == "Fixture VM") == 2, "Paged duplicate display names are retained");
        Require(paged.PagesVisited == 3, "The pager walk reports the three pages it visited");
        Require(paged.WalkMillis > 0, "The pager walk reports its own duration");
        Require(pagedWallMillis < 8000, "Walking a three-page console finishes well inside the refresh budget");
        Require(paged.WalkMillis < 6000, "The three-page walk itself stays inside its budget");

        // A virtualised list that only renders the rows near the viewport must still be read in full.
        var virtualisedStartedAt = DateTimeOffset.Now;
        await pane.NavigateAndWaitAsync(trustedOrigin + "/virtualised.html", TimeSpan.FromSeconds(20));
        await WaitUntilAsync(
            () => snapshots.Any(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 18),
            "the virtualised fixture to be walked past its first window");
        var virtualisedWallMillis = (int)(DateTimeOffset.Now - virtualisedStartedAt).TotalMilliseconds;
        var virtualised = snapshots.Last(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 18);
        Require(virtualised.Rows.Count == 18, "All eighteen virtualised rows are captured, not only the rendered window");
        Require(virtualised.Outcome == InventoryOutcome.Complete, "Scrolling to the end and matching the console total is reported as complete");
        Require(virtualised.Rows.Any(row => row.Name == "Fixture Node 18"), "A row that is only rendered after scrolling is captured");

        // A long account: 240 rows rendered a window at a time. This is the case that used to
        // take tens of seconds because every scroll step waited a fixed interval.
        var largeStartedAt = DateTimeOffset.Now;
        await pane.NavigateAndWaitAsync(trustedOrigin + "/large-virtualised.html", TimeSpan.FromSeconds(20));
        await WaitUntilAsync(
            () => snapshots.Any(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 240),
            "the 240-row virtualised fixture to be walked to the end",
            TimeSpan.FromSeconds(30));
        var largeWallMillis = (int)(DateTimeOffset.Now - largeStartedAt).TotalMilliseconds;
        var large = snapshots.Last(item => item.PageKind == ConsolePageKind.ComputerList && item.Rows.Count == 240);
        Require(large.Rows.Count == 240, "All 240 rows of a long account are captured");
        Require(large.Outcome == InventoryOutcome.Complete, "Walking a long virtualised list to the end is reported as complete");
        Require(large.Rows.Any(row => row.Name == "Fixture Node 240"), "The last row of a long account is captured");
        Require(largeWallMillis < 15000, "Reading a 240-row virtualised account stays inside the refresh budget");
        Require(large.WalkMillis < 12000, "The long-list walk itself stays inside its budget");

        // The console's connect chooser is presented and applied from the unified list.
        var absent = await pane.ProbeChooserAsync();
        Require(absent is null || !absent.Present, "A list page without a chooser is never reported as showing one");
        await pane.NavigateAndWaitAsync(trustedOrigin + "/chooser.html", TimeSpan.FromSeconds(20));
        var chooser = await WaitForChooserAsync(pane);
        Require(chooser is { Present: true, NativeAvailable: true, WebAvailable: true }, "The documented connect chooser and both of its options are detected");
        Require(chooser!.Heading == ConnectionChooser.HeadingText, "The documented chooser heading is read");

        var nativeApplied = await pane.SelectChooserOptionAsync(ConnectionChooser.NativeKey);
        Require(nativeApplied == "clicked", "The Business-app option is applied inside the console");
        Require((await pane.ExecuteScriptAsync("window.__chooserChoice||''")).Contains("native", StringComparison.Ordinal), "The console observed the Business-app choice");
        var webApplied = await pane.SelectChooserOptionAsync(ConnectionChooser.WebKey);
        Require(webApplied == "clicked", "The web-app option is applied inside the console");
        Require((await pane.ExecuteScriptAsync("window.__chooserChoice||''")).Contains("web", StringComparison.Ordinal), "The console observed the web-app choice");
        var inventedRefused = false;
        try
        {
            await pane.SelectChooserOptionAsync("invented-option");
        }
        catch (ArgumentOutOfRangeException)
        {
            inventedRefused = true;
        }

        Require(inventedRefused, "An option that is not one of the two documented labels is refused");

        return new
        {
            capturedRows = read.Rows.Count,
            duplicateDisplayNamesRetained = read.Rows.Count(row => row.Name == "Fixture VM"),
            rowsWithConnectControl = read.Rows.Count(row => row.HasConnectControl),
            rowsWithStatus = read.Rows.Count(row => row.Status is { Length: > 0 }),
            onlineRows = read.Rows.Count(row => string.Equals(row.Status, "Online", StringComparison.OrdinalIgnoreCase)),
            offlineRows = read.Rows.Count(row => string.Equals(row.Status, "Offline", StringComparison.OrdinalIgnoreCase)),
            statusNotInferredFromDeviceName = read.Rows.Single(row => row.Name == "Fixture Backup").Status is null,
            outcome = read.Outcome.ToString(),
            connectActivatedOnce = clickCount.Trim() == "1",
            staleRowRefused = missingRow == "no-row",
            signInOutcome = signIn.Outcome.ToString(),
            pagedRowsCaptured = paged.Rows.Count,
            pagedOutcome = paged.Outcome.ToString(),
            pagedReportedTotal = paged.ReportedTotal,
            virtualisedRowsCaptured = virtualised.Rows.Count,
            virtualisedOutcome = virtualised.Outcome.ToString(),
            extractionsForOneNavigation,
            pagedPagesVisited = paged.PagesVisited,
            pagedWalkMillis = paged.WalkMillis,
            pagedWallMillis,
            virtualisedWallMillis,
            largeRowsCaptured = large.Rows.Count,
            largeOutcome = large.Outcome.ToString(),
            largeWalkMillis = large.WalkMillis,
            largeWallMillis,
            chooserDetected = chooser.Present,
            chooserNativeApplied = nativeApplied == "clicked",
            chooserWebApplied = webApplied == "clicked",
            inventedChooserOptionRefused = true
        };
    }

    private static async Task<ChooserProbe?> WaitForChooserAsync(AccountWebViewPane pane)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = await pane.ProbeChooserAsync();
            if (probe is { Present: true })
            {
                return probe;
            }

            await Task.Delay(200);
        }

        return null;
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

    private static async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
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
