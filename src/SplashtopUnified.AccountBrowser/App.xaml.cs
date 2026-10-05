using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SplashtopUnified.AccountBrowser;

public partial class App : Application
{
    internal const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Any(argument => string.Equals(argument, "--smoke-test", StringComparison.Ordinal));
        if (smokeTest)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = await BrowserSmokeTest.RunAsync();
            Shutdown(exitCode);
            return;
        }

        try
        {
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(runtimeVersion))
            {
                throw new InvalidOperationException("The Evergreen Runtime did not report a version.");
            }
        }
        catch (Exception exception)
        {
            var answer = MessageBox.Show(
                $"Microsoft Edge WebView2 Evergreen Runtime is missing or unavailable. Install it from Microsoft's official download page, then restart this app.\n\nDetails: {exception.Message}\n\nOpen the official WebView2 download page now?",
                "WebView2 Runtime Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true });
                }
                catch (Exception launchError)
                {
                    MessageBox.Show($"Could not open the official download page. Open this URL manually:\n{RuntimeDownloadUrl}\n\n{launchError.Message}",
                        "Open WebView2 Download", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }

            Shutdown(2);
            return;
        }

        try
        {
            var store = new AccountStore();
            var profiles = store.LoadOrCreate();
            var window = new AccountBrowserWindow(store, profiles);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"The account browser could not start safely. No account configuration was overwritten.\n\n{exception.Message}",
                "Account Browser Startup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
