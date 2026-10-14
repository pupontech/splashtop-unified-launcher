using Microsoft.Web.WebView2.Core;

namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Creates the one WebView2 environment shared by production account panes and the
/// synthetic browser smoke test, so both run with identical Chromium switches.
/// An account pane lives in a window that is routinely occluded, minimized or
/// backgrounded while another window is foreground; without these switches
/// Chromium throttles background timers and backgrounds occluded windows, which
/// clamps the page-side walk's sleeps, makes its deadline expire early, and turns
/// healthy reads into unknown outcomes. Each account still gets its own isolated
/// user-data folder; only the engine switches are shared.
/// </summary>
internal static class WebViewEnvironmentFactory
{
    /// <summary>
    /// The exact engine switches every account environment is created with.
    /// Order and spelling are load-bearing for the smoke assertion.
    /// </summary>
    public const string BrowserSwitches =
        "--disable-background-timer-throttling " +
        "--disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-features=CalculateNativeWinOcclusion";

    public static CoreWebView2EnvironmentOptions CreateOptions() => new(BrowserSwitches);

    public static Task<CoreWebView2Environment> CreateAsync(string userDataFolder) =>
        CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: CreateOptions());
}
