using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SplashtopUnified.AccountBrowser;

internal sealed class AccountWebViewPane : Grid, IDisposable
{
    private readonly WebView2 _webView = new();
    private readonly Action<string> _status;
    private readonly BusinessAppHandoff _businessAppHandoff;
    private readonly Action<string, string, bool>? _handoffEventObserver;
    private readonly List<PopupWindow> _popups = [];
    private CoreWebView2Environment? _environment;
    private bool _disposed;

    public AccountWebViewPane(
        Action<string> status,
        IBusinessAppUriDispatcher? businessAppUriDispatcher = null,
        Action<string, string, bool>? handoffEventObserver = null)
    {
        _status = status;
        _businessAppHandoff = new BusinessAppHandoff(businessAppUriDispatcher ?? new ShellBusinessAppUriDispatcher());
        _handoffEventObserver = handoffEventObserver;
        Children.Add(_webView);
    }

    public CoreWebView2 Core => _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
    public CoreWebView2Environment? Environment { get; private set; }
    public string UserDataFolder { get; private set; } = string.Empty;

    public async Task InitializeAsync(string userDataFolder, string? initialUrl)
    {
        UserDataFolder = Path.GetFullPath(userDataFolder);
        Directory.CreateDirectory(UserDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
        await InitializeWithEnvironmentAsync(environment, UserDataFolder, initialUrl);
    }

    public async Task InitializeWithEnvironmentAsync(CoreWebView2Environment environment, string expectedUserDataFolder, string? initialUrl)
    {
        UserDataFolder = Path.GetFullPath(expectedUserDataFolder);
        Environment = environment;
        if (!PathsEqual(environment.UserDataFolder, UserDataFolder))
        {
            throw new InvalidOperationException($"WebView2 selected unexpected user-data folder '{environment.UserDataFolder}'. Refusing to navigate to protect account isolation.");
        }

        if (_disposed)
        {
            return;
        }

        await _webView.EnsureCoreWebView2Async(environment);
        if (_disposed)
        {
            _webView.Dispose();
            return;
        }

        ConfigureCore(_webView.CoreWebView2);
        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            Navigate(initialUrl);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    public void Navigate(string url)
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        if (!IsWebUri(url))
        {
            _status("Blocked non-web navigation. Only HTTP/HTTPS console and sign-in pages are allowed.");
            return;
        }

        _webView.CoreWebView2.Navigate(url);
    }

    public Task<string> ExecuteScriptAsync(string script) => Core.ExecuteScriptAsync(script);

    private readonly SemaphoreSlim _navigationGate = new(1, 1);

    public async Task NavigateAndWaitAsync(string url, TimeSpan timeout)
    {
        var core = Core;
        await _navigationGate.WaitAsync();
        ulong? navigationId = null;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationStartingEventArgs> starting = (_, args) =>
        {
            if (navigationId is null && !args.IsRedirected &&
                Uri.TryCreate(args.Uri, UriKind.Absolute, out var actual) &&
                Uri.TryCreate(url, UriKind.Absolute, out var expected) && actual == expected)
                navigationId = args.NavigationId;
        };
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = (_, args) =>
        {
            if (navigationId == args.NavigationId)
                completion.TrySetResult(args.IsSuccess ? string.Empty : args.WebErrorStatus.ToString());
        };
        try
        {
            if (_disposed || !IsWebUri(url)) throw new InvalidOperationException("Cannot navigate a closed browser or unsupported URL.");
            core.NavigationStarting += starting;
            core.NavigationCompleted += completed;
            Navigate(url);
            var error = await completion.Task.WaitAsync(timeout);
            if (error.Length != 0) throw new InvalidOperationException($"WebView2 navigation failed: {error}");
        }
        finally
        {
            core.NavigationStarting -= starting;
            core.NavigationCompleted -= completed;
            _navigationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var popup in _popups.ToArray())
        {
            popup.Close();
        }

        _popups.Clear();
        if (_webView.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
        }

        _webView.Dispose();
        _environment = null;
    }

    private void ConfigureCore(CoreWebView2? core)
    {
        if (core is null)
        {
            throw new InvalidOperationException("WebView2 initialized without a CoreWebView2 instance.");
        }

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsWebUri(args.Uri))
        {
            _handoffEventObserver?.Invoke(nameof(OnNavigationStarting), args.Uri, args.IsUserInitiated);
        }

        HandleNavigationStarting(args, _webView.CoreWebView2?.Source);
    }

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        _handoffEventObserver?.Invoke(nameof(OnLaunchingExternalUriScheme), args.Uri, args.IsUserInitiated);
        args.Cancel = true;
        HandleBusinessAppUri(args.Uri, args.InitiatingOrigin, args.IsUserInitiated);
    }

    private void HandleNavigationStarting(CoreWebView2NavigationStartingEventArgs args, string? initiatingOrigin)
    {
        if (IsWebUri(args.Uri))
        {
            return;
        }

        args.Cancel = true;
        HandleBusinessAppUri(args.Uri, initiatingOrigin, args.IsUserInitiated);
    }

    private void HandleBusinessAppUri(string uri, string? initiatingOrigin, bool isUserInitiated) =>
        HandleBusinessAppUri(uri, initiatingOrigin, isUserInitiated, _status, _businessAppHandoff);

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        _handoffEventObserver?.Invoke(nameof(OnNewWindowRequested), args.Uri, args.IsUserInitiated);
        await HandlePopupRequestAsync(args, _environment, _popups, _status, _businessAppHandoff);
    }

    private static async Task HandlePopupRequestAsync(
        CoreWebView2NewWindowRequestedEventArgs args,
        CoreWebView2Environment? environment,
        List<PopupWindow> popups,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff)
    {
        if (!IsPopupUri(args.Uri))
        {
            args.Handled = true;
            HandleBusinessAppUri(args.Uri, args.OriginalSourceFrameInfo?.Source, args.IsUserInitiated, status, businessAppHandoff);
            return;
        }

        if (environment is null || popups.Count >= 4)
        {
            args.Handled = true;
            status("Popup refused: unsupported destination or the four-window limit for this account was reached. Close an existing sign-in window and try again.");
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            var popup = new PopupWindow(environment, popups, status, businessAppHandoff);
            popups.Add(popup);
            popup.Closed += (_, _) => popups.Remove(popup);
            await popup.InitializeAsync();
            args.NewWindow = popup.WebView.CoreWebView2;
            popup.Show();
        }
        catch (Exception exception)
        {
            args.Handled = true;
            status($"Could not open a sign-in popup in this account profile: {exception.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsPopupUri(string value) =>
        string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase) || IsWebUri(value);

    private static bool IsWebUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));

    private static void HandleBusinessAppUri(
        string uri,
        string? initiatingOrigin,
        bool isUserInitiated,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff)
    {
        switch (businessAppHandoff.TryDispatch(uri, initiatingOrigin, isUserInitiated))
        {
            case BusinessAppHandoffResult.Dispatched:
                status("Opening the Splashtop Business app for the remote session.");
                break;
            case BusinessAppHandoffResult.Failed:
                status("Could not open Splashtop Business. Install or repair the desktop app and confirm its st-business protocol handler is registered.");
                break;
            case BusinessAppHandoffResult.Rejected:
                status("Blocked an external or executable URI scheme. Only a user-initiated Splashtop Business link from the trusted console can be opened.");
                break;
        }
    }

    private sealed class PopupWindow : Window
    {
        private readonly CoreWebView2Environment _environment;
        private readonly List<PopupWindow> _popups;
        private readonly Action<string> _status;
        private readonly BusinessAppHandoff _businessAppHandoff;

        public PopupWindow(CoreWebView2Environment environment, List<PopupWindow> popups, Action<string> status, BusinessAppHandoff businessAppHandoff)
        {
            _environment = environment;
            _popups = popups;
            _status = status;
            _businessAppHandoff = businessAppHandoff;
            Title = "Sign-in window · isolated account profile";
            Width = 920;
            Height = 700;
            MinWidth = 520;
            MinHeight = 360;
            WebView = new WebView2();
            Content = WebView;
        }

        public WebView2 WebView { get; }

        public async Task InitializeAsync()
        {
            await WebView.EnsureCoreWebView2Async(_environment);
            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.NewWindowRequested += OnNewWindowRequested;
        }

        private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
            await HandlePopupRequestAsync(args, _environment, _popups, _status, _businessAppHandoff);

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            HandleNavigationStarting(args, WebView.CoreWebView2?.Source, _status, _businessAppHandoff);
        }

        private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        {
            args.Cancel = true;
            HandleBusinessAppUri(args.Uri, args.InitiatingOrigin, args.IsUserInitiated, _status, _businessAppHandoff);
        }
    }

    private static void HandleNavigationStarting(
        CoreWebView2NavigationStartingEventArgs args,
        string? initiatingOrigin,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff)
    {
        if (IsWebUri(args.Uri))
        {
            return;
        }

        args.Cancel = true;
        HandleBusinessAppUri(args.Uri, initiatingOrigin, args.IsUserInitiated, status, businessAppHandoff);
    }
}
