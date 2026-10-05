using System.IO;
using System.Text.Json;
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
    private readonly NativeHandoffArm _nativeArm = new();
    private readonly Action<string, string, bool>? _handoffEventObserver;
    private readonly Action<AccountInventorySnapshot>? _inventoryObserver;
    private readonly List<PopupWindow> _popups = [];
    private CoreWebView2Environment? _environment;
    private int _inventoryGeneration;
    private int _reportedGeneration = -1;
    private int _lastMessageGeneration = -1;
    private ConsolePageKind _reportedPageKind = ConsolePageKind.Unknown;
    private bool _walkInFlight;
    private DateTimeOffset _walkStartedAtUtc;
    private bool _disposed;

    public AccountWebViewPane(
        Action<string> status,
        IBusinessAppUriDispatcher? businessAppUriDispatcher = null,
        Action<string, string, bool>? handoffEventObserver = null,
        string accountId = "",
        string accountName = "",
        Action<AccountInventorySnapshot>? inventoryObserver = null)
    {
        _status = status;
        _businessAppHandoff = new BusinessAppHandoff(businessAppUriDispatcher ?? new ShellBusinessAppUriDispatcher());
        _handoffEventObserver = handoffEventObserver;
        _inventoryObserver = inventoryObserver;
        AccountId = accountId;
        AccountName = accountName;
        Children.Add(_webView);
    }

    public string AccountId { get; }

    public string AccountName { get; }

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
        EnableCredentialSaving(core);
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        InstallNativePreference(core, _nativeArm, _status);
        InstallInventoryExtraction(core);
    }

    /// <summary>
    /// Loads the read-only console inventory extractor into every official console page and
    /// accepts its message only from the trusted console origin and the exact fixed script.
    /// </summary>
    private void InstallInventoryExtraction(CoreWebView2 core)
    {
        core.NavigationCompleted += (_, _) => ReadInventoryAfterNavigation();
        core.WebMessageReceived += (_, args) => HandleInventoryMessage(args);
    }

    /// <summary>
    /// Runs the extractor after a navigation and stops as soon as the current document has
    /// reported a computer list. Retrying is bounded, conditional, and never overlaps an
    /// in-flight walk: the in-page walk is asynchronous, so a second request would fight it
    /// for the same scroll position and could publish a partial list.
    /// </summary>
    private async void ReadInventoryAfterNavigation()
    {
        var generation = ++_inventoryGeneration;
        for (var attempt = 0; attempt < 3 && !_disposed; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 350 : 700));
            if (_disposed || _webView.CoreWebView2 is null)
            {
                return;
            }

            if (HasConfidentReadFor(generation) || !await WaitForIdleWalksAsync())
            {
                return;
            }

            try
            {
                _walkInFlight = true;
                _walkStartedAtUtc = DateTimeOffset.UtcNow;
                await _webView.CoreWebView2.ExecuteScriptAsync(ConsoleInventoryExtractor.ExtractScript);
            }
            catch (Exception)
            {
                _walkInFlight = false;
                return;
            }

            // The page owns the walk from here; wait for its one message before deciding
            // whether another attempt is worth starting.
            await WaitForMessageAsync(generation, TimeSpan.FromMilliseconds(attempt == 0 ? 3000 : 20000));
        }
    }

    private bool HasConfidentReadFor(int generation) =>
        _reportedGeneration == generation && _reportedPageKind == ConsolePageKind.ComputerList;

    private bool WalkInFlight =>
        _walkInFlight && DateTimeOffset.UtcNow - _walkStartedAtUtc < TimeSpan.FromSeconds(60);

    /// <summary>Waits out any walk the page is still running. False when the pane was disposed.</summary>
    private async Task<bool> WaitForIdleWalksAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (WalkInFlight && !_disposed && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        return !_disposed;
    }

    private async Task WaitForMessageAsync(int generation, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_lastMessageGeneration < generation && !_disposed && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(80);
        }
    }

    private void HandleInventoryMessage(CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsOfficialConsoleOrigin(args.Source))
        {
            return;
        }

        string json;
        try
        {
            json = args.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        if (!ConsoleInventoryExtractor.TryParse(json, out var read) || read is null)
        {
            return;
        }

        _reportedGeneration = _inventoryGeneration;
        _lastMessageGeneration = _inventoryGeneration;
        _reportedPageKind = read.PageKind;
        _walkInFlight = false;

        var capturedAt = DateTimeOffset.Now;
        var snapshot = new AccountInventorySnapshot(
            AccountId, AccountName, read.Outcome, read.PageKind, read.Authentication,
            read.RowCount, read.ReportedTotal, read.Rows, capturedAt, read.Diagnostic,
            read.PagesVisited, read.WalkMillis);
        _inventoryObserver?.Invoke(snapshot);
        _status(read.Outcome == InventoryOutcome.Unavailable
            ? $"{AccountName}: no computer list on this page yet ({read.PageKind.ToString().ToLowerInvariant()})."
            : $"{AccountName}: read {read.Rows.Count} row(s) — {read.Outcome.ToString().ToLowerInvariant()}" +
              (read.Mode is { Length: > 0 } && read.Mode != "single" ? $", {read.Mode}" : string.Empty) +
              (read.PagesVisited > 1 ? $", {read.PagesVisited} pages" : string.Empty) +
              (read.WalkMillis > 0 ? $", {read.WalkMillis} ms" : string.Empty) + ".");
    }

    private static bool IsOfficialConsoleOrigin(string? source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(uri.Host, "my.splashtop.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "my.splashtop.eu", StringComparison.OrdinalIgnoreCase)) &&
               uri.UserInfo.Length == 0 && uri.IsDefaultPort;
    }

    /// <summary>
    /// Read-only structural survey of the owning console page, used to diagnose a list that
    /// does not match the documented columns. Returns counts and labels only.
    /// </summary>
    public async Task<string> InspectConsoleAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return "{}";
        }

        var raw = await _webView.CoreWebView2.ExecuteScriptAsync(ConsoleInventoryExtractor.DiagnosticsScript);
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    /// <summary>
    /// Reads the owning console's documented connect chooser. Returns null when the chooser
    /// is absent, so a page change can never be mistaken for a chooser.
    /// </summary>
    public async Task<ChooserProbe?> ProbeChooserAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return null;
        }

        var raw = await _webView.CoreWebView2.ExecuteScriptAsync(ConnectionChooser.ProbeScript);
        string json;
        try
        {
            json = JsonSerializer.Deserialize<string>(raw) ?? "{}";
        }
        catch (JsonException)
        {
            return null;
        }

        return ConnectionChooser.TryParseProbe(json, out var probe) ? probe : null;
    }

    /// <summary>Applies the chosen documented option inside the owning console page.</summary>
    public async Task<string> SelectChooserOptionAsync(string optionKey)
    {
        if (_webView.CoreWebView2 is null)
        {
            return "no-webview";
        }

        var raw = await _webView.CoreWebView2.ExecuteScriptAsync(ConnectionChooser.SelectScript(optionKey));
        return raw.Trim('"');
    }

    /// <summary>Asks the owning console page for a fresh read of its computer list.</summary>
    public async Task RequestInventoryAsync()
    {
        // One walk per account at a time. The in-page walk is asynchronous, so starting a
        // second one would fight the first for the same scroll position.
        if (!await WaitForIdleWalksAsync() || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            _walkInFlight = true;
            _walkStartedAtUtc = DateTimeOffset.UtcNow;
            await _webView.CoreWebView2.ExecuteScriptAsync(ConsoleInventoryExtractor.ExtractScript);
        }
        catch (Exception)
        {
            _walkInFlight = false;
            throw;
        }
    }

    /// <summary>
    /// Connects using the owning account's own console row, so the official client path is
    /// preserved and no remote-session URL is constructed here.
    /// </summary>
    public async Task<string> ActivateConnectAsync(int rowIndex)
    {
        if (_webView.CoreWebView2 is null)
        {
            return "no-webview";
        }

        var raw = await _webView.CoreWebView2.ExecuteScriptAsync(ConsoleInventoryActions.ActivateConnectScript(rowIndex));
        return raw.Trim('"');
    }

    /// <summary>
    /// Lets the WebView2 engine offer to save and reuse the console login inside
    /// this account's own browser profile. The app never reads, receives, or stores
    /// the password itself; credential storage stays in the engine's encrypted
    /// store, scoped to the account's isolated user-data folder.
    /// </summary>
    private static void EnableCredentialSaving(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
    }

    /// <summary>
    /// Installs the documented-chooser preference script and opens the bounded
    /// host allowance only for a validated "armed" message from the exact console.
    /// </summary>
    private static void InstallNativePreference(CoreWebView2 core, NativeHandoffArm arm, Action<string> status)
    {
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(NativeConnectionPreference.InstallScript)
            .ContinueWith(
                task => status("The automatic Business-app preference could not be installed on this page; the connection chooser was left untouched."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        core.WebMessageReceived += (_, args) => HandleNativePreferenceMessage(args, arm, status);
    }

    private static void HandleNativePreferenceMessage(
        CoreWebView2WebMessageReceivedEventArgs args,
        NativeHandoffArm arm,
        Action<string> status)
    {
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
            !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Host, "my.splashtop.com", StringComparison.OrdinalIgnoreCase) ||
            source.UserInfo.Length != 0 || !source.IsDefaultPort)
        {
            return;
        }

        string json;
        try
        {
            json = args.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(json) || json.Length > 2048)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("source", out var sourceProperty) ||
                sourceProperty.ValueKind != JsonValueKind.String ||
                !string.Equals(sourceProperty.GetString(), NativeConnectionPreference.MessageSource, StringComparison.Ordinal) ||
                !root.TryGetProperty("version", out var versionProperty) ||
                versionProperty.ValueKind != JsonValueKind.Number ||
                !versionProperty.TryGetInt32(out var version) ||
                version != NativeConnectionPreference.ScriptVersion ||
                !root.TryGetProperty("type", out var typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeProperty.GetString();
            switch (type)
            {
                case "armed":
                    arm.Open();
                    status("Connect was recognised. Preferring the Splashtop Business app for this connection.");
                    break;
                case "failed":
                    status("Could not prepare the Business-app preference on this page; use the connection chooser shown by Splashtop.");
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON or unrelated page messages.
        }
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
        HandleBusinessAppUri(uri, initiatingOrigin, isUserInitiated, _status, _businessAppHandoff, _nativeArm);

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        _handoffEventObserver?.Invoke(nameof(OnNewWindowRequested), args.Uri, args.IsUserInitiated);
        await HandlePopupRequestAsync(args, _environment, _popups, _status, _businessAppHandoff, _nativeArm);
    }

    private static async Task HandlePopupRequestAsync(
        CoreWebView2NewWindowRequestedEventArgs args,
        CoreWebView2Environment? environment,
        List<PopupWindow> popups,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff,
        NativeHandoffArm? nativeArm = null)
    {
        if (!IsPopupUri(args.Uri))
        {
            args.Handled = true;
            HandleBusinessAppUri(args.Uri, args.OriginalSourceFrameInfo?.Source, args.IsUserInitiated, status, businessAppHandoff, nativeArm);
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
            var popup = new PopupWindow(environment, popups, status, businessAppHandoff, nativeArm);
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
        BusinessAppHandoff businessAppHandoff,
        NativeHandoffArm? nativeArm = null)
    {
        // The documented chooser's own follow-up arrives as not user initiated.
        // Accept it only inside a bounded, host-owned allowance opened by a
        // validated trusted Connect gesture, and consume it on use.
        var trustedArmActive = !isUserInitiated && nativeArm is not null && nativeArm.Consume();
        switch (businessAppHandoff.TryDispatch(uri, initiatingOrigin, isUserInitiated, trustedArmActive))
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
        private readonly NativeHandoffArm? _nativeArm;

        public PopupWindow(CoreWebView2Environment environment, List<PopupWindow> popups, Action<string> status, BusinessAppHandoff businessAppHandoff, NativeHandoffArm? nativeArm = null)
        {
            _environment = environment;
            _popups = popups;
            _status = status;
            _businessAppHandoff = businessAppHandoff;
            _nativeArm = nativeArm;
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
            EnableCredentialSaving(core);
            core.NavigationStarting += OnNavigationStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.NewWindowRequested += OnNewWindowRequested;
            if (_nativeArm is not null)
            {
                InstallNativePreference(core, _nativeArm, _status);
            }
        }

        private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
            await HandlePopupRequestAsync(args, _environment, _popups, _status, _businessAppHandoff, _nativeArm);

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            HandleNavigationStarting(args, WebView.CoreWebView2?.Source, _status, _businessAppHandoff, _nativeArm);
        }

        private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        {
            args.Cancel = true;
            HandleBusinessAppUri(args.Uri, args.InitiatingOrigin, args.IsUserInitiated, _status, _businessAppHandoff, _nativeArm);
        }
    }

    private static void HandleNavigationStarting(
        CoreWebView2NavigationStartingEventArgs args,
        string? initiatingOrigin,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff,
        NativeHandoffArm? nativeArm = null)
    {
        if (IsWebUri(args.Uri))
        {
            return;
        }

        args.Cancel = true;
        HandleBusinessAppUri(args.Uri, initiatingOrigin, args.IsUserInitiated, status, businessAppHandoff);
    }
}
