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
                var pane = await CreatePaneAsync(grid, i, environments[i], folders[i], fixture);
                panes.Add(pane);
            }
            Require(!string.Equals(environments[0].UserDataFolder, environments[1].UserDataFolder, StringComparison.OrdinalIgnoreCase), "Distinct actual browser folders");
            await panes[0].ExecuteScriptAsync("document.cookie='account=A; path=/; max-age=3600; SameSite=Lax'; localStorage.setItem('account','A'); sessionStorage.setItem('account','A');");
            await panes[1].ExecuteScriptAsync("document.cookie='account=B; path=/; max-age=3600; SameSite=Lax'; localStorage.setItem('account','B'); sessionStorage.setItem('account','B');");
            await AssertStateAsync(panes[0], "A", true); await AssertStateAsync(panes[1], "B", true);
            // Recreate controls while retaining the actual environments.
            foreach (var pane in panes) { grid.Children.Remove(pane); pane.Dispose(); }
            panes.Clear();
            for (var i = 0; i < 2; i++) panes.Add(await CreatePaneAsync(grid, i, environments[i], folders[i], fixture));
            await AssertStateAsync(panes[0], "A", false); await AssertStateAsync(panes[1], "B", false);
            // Create new environment objects bound to the same persistent folders.
            foreach (var pane in panes) { grid.Children.Remove(pane); pane.Dispose(); }
            panes.Clear();
            for (var i = 0; i < 2; i++)
            {
                environments[i] = await CoreWebView2Environment.CreateAsync(userDataFolder: folders[i]);
                panes.Add(await CreatePaneAsync(grid, i, environments[i], folders[i], fixture));
            }
            await AssertStateAsync(panes[0], "A", false); await AssertStateAsync(panes[1], "B", false);
            var result = new {
                browserVersion = environments[0].BrowserVersionString,
                userDataFolders = environments.Select(e => e.UserDataFolder).ToArray(),
                webViewInitialized = panes.All(p => p.Core is not null),
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

    private static async Task<AccountWebViewPane> CreatePaneAsync(Grid grid, int column, CoreWebView2Environment environment, string folder, string fixture)
    {
        var pane = new AccountWebViewPane(message => Console.Error.WriteLine("SMOKE_BROWSER_STATUS=" + message));
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
}
