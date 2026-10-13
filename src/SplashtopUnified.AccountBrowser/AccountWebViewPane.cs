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
    private readonly Func<bool> _confirmNativeHandoff;
    private readonly Action<string, string, bool>? _handoffEventObserver;
    private readonly Action<AccountInventorySnapshot>? _inventoryObserver;
    private readonly List<PopupWindow> _popups = [];
    private CoreWebView2Environment? _environment;
    private int _inventoryGeneration;
    private ConsolePageKind _reportedPageKind = ConsolePageKind.Unknown;
    private readonly object _inventorySync = new();
    private readonly InventoryRequestLifecycle<InventoryRequest> _inventoryRequestLifecycle = new();
    private readonly ConsoleInspection _consoleInspection = new();
    private readonly SemaphoreSlim _consoleWalkGate = new(1, 1);
    private static readonly JsonSerializerOptions ConnectStepJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int ConnectLocatorMaxSteps = 80;
    private static readonly TimeSpan ConnectLocatorTimeout = TimeSpan.FromSeconds(20);
    private IReadOnlyList<ExtractedComputerRow> _lastExtractedRows = [];
    private string? _lastRowsDocumentSource;
    private int _lastRowsGeneration = -1;
    private bool _disposed;

    private sealed class InventoryRequest(string requestId, int generation, string documentSource)
    {
        public string RequestId { get; } = requestId;
        public int Generation { get; } = generation;
        public string DocumentSource { get; } = documentSource;
        public TaskCompletionSource<ConsoleInventoryRead?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record InventoryRequestAttempt(ConsoleInventoryRead? Read, bool ReloadRequired);

    public AccountWebViewPane(
        Action<string> status,
        IBusinessAppUriDispatcher? businessAppUriDispatcher = null,
        Action<string, string, bool>? handoffEventObserver = null,
        string accountId = "",
        string accountName = "",
        Action<AccountInventorySnapshot>? inventoryObserver = null,
        Func<bool>? nativeHandoffConfirmation = null)
    {
        _status = status;
        _businessAppHandoff = new BusinessAppHandoff(businessAppUriDispatcher ?? new ShellBusinessAppUriDispatcher());
        _confirmNativeHandoff = nativeHandoffConfirmation ?? ConfirmNativeHandoff;
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
        InvalidateInventoryForNavigation();
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
        InstallNativePreference(core, _status);
        InstallInventoryExtraction(core);
    }

    private void InstallInventoryExtraction(CoreWebView2 core)
    {
        core.NavigationCompleted += OnInventoryNavigationCompleted;
        core.WebMessageReceived += (_, args) => HandleInventoryMessage(args);
    }

    private void OnInventoryNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess && !_disposed)
        {
            _ = ReadInventoryAfterNavigation(_inventoryGeneration);
        }
    }

    private async Task ReadInventoryAfterNavigation(int generation)
    {
        for (var attempt = 0; attempt < 3 && !_disposed; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 350 : 700));
            if (_disposed || _webView.CoreWebView2 is null || generation != _inventoryGeneration)
            {
                return;
            }

            if (HasConfidentReadFor(generation))
            {
                return;
            }

            var read = (await StartInventoryRequestAsync(generation)).Read;
            if (read is null || read.PageKind == ConsolePageKind.ComputerList)
            {
                return;
            }
        }
    }

    private bool HasConfidentReadFor(int generation)
    {
        lock (_inventorySync)
        {
            return _lastRowsGeneration == generation && _reportedPageKind == ConsolePageKind.ComputerList;
        }
    }

    private async Task<InventoryRequestAttempt> StartInventoryRequestAsync(int generation, bool joinMatchingActive = false)
    {
        if (!await _consoleWalkGate.WaitAsync(TimeSpan.FromSeconds(22)))
        {
            return new InventoryRequestAttempt(null, false);
        }

        try
        {
            return await StartInventoryRequestCoreAsync(generation, joinMatchingActive);
        }
        finally
        {
            _consoleWalkGate.Release();
        }
    }

    private async Task<InventoryRequestAttempt> StartInventoryRequestCoreAsync(int generation, bool joinMatchingActive = false)
    {
        var core = _webView.CoreWebView2;
        if (_disposed || core is null || !IsOfficialConsoleOrigin(core.Source))
        {
            return new InventoryRequestAttempt(null, false);
        }

        InventoryRequest request;
        bool started;
        lock (_inventorySync)
        {
            if (_disposed || generation != _inventoryGeneration ||
                !string.Equals(core.Source, _webView.CoreWebView2?.Source, StringComparison.Ordinal))
            {
                return new InventoryRequestAttempt(null, _inventoryRequestLifecycle.HasActiveRequest);
            }

            var proposed = new InventoryRequest(Guid.NewGuid().ToString("N"), generation, core.Source);
            if (!_inventoryRequestLifecycle.TryBeginOrJoin(
                    new InventoryRequestKey(generation, core.Source), proposed, joinMatchingActive, out request, out started))
            {
                return new InventoryRequestAttempt(null, _inventoryRequestLifecycle.HasActiveRequest);
            }
        }

        var hostDeadline = Task.Delay(TimeSpan.FromMilliseconds(ConsoleInventoryExtractor.MaxWalkMillis + 5000));
        if (started)
        {
            Task<string> execution;
            try
            {
                execution = core.ExecuteScriptAsync(ConsoleInventoryExtractor.CreateExtractScript(request.RequestId));
            }
            catch (Exception)
            {
                lock (_inventorySync)
                {
                    if (_inventoryRequestLifecycle.TryComplete(request))
                    {
                        request.Completion.TrySetResult(null);
                    }
                }

                _status("The console inventory script could not be started; no page walk is active. Retry Refresh.");
                return new InventoryRequestAttempt(null, false);
            }

            if (await Task.WhenAny(execution, hostDeadline) != execution)
            {
                _status("Inventory read timed out; outcome is unknown. Reload this account page to release the active page lock, then retry.");
                return new InventoryRequestAttempt(null, true);
            }

            try
            {
                await execution;
            }
            catch (Exception)
            {
                _status("Inventory script completion is unknown. Reload this account page to release the active page lock, then retry.");
                return new InventoryRequestAttempt(null, true);
            }
        }

        if (await Task.WhenAny(request.Completion.Task, hostDeadline) != request.Completion.Task)
        {
            // A timeout is unknown: keep the page-side lock until the exact response or navigation.
            _status("Inventory read timed out; outcome is unknown. Reload this account page to release the active page lock, then retry.");
            return new InventoryRequestAttempt(null, true);
        }

        return new InventoryRequestAttempt(await request.Completion.Task, false);
    }

    private void InvalidateInventoryForNavigation()
    {
        InventoryRequest? staleRequest;
        lock (_inventorySync)
        {
            _inventoryGeneration++;
            _reportedPageKind = ConsolePageKind.Unknown;
            _lastExtractedRows = [];
            _lastRowsDocumentSource = null;
            _lastRowsGeneration = -1;
            staleRequest = _inventoryRequestLifecycle.Invalidate();
        }

        staleRequest?.Completion.TrySetResult(null);
    }

    public event Action<InventoryRefreshProgress>? InventoryProgressChanged;

    private void HandleInventoryMessage(CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsOfficialConsoleOrigin(args.Source) || _webView.CoreWebView2 is not { } core ||
            !IsOfficialConsoleOrigin(core.Source))
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

        var progressRequest = _inventoryRequestLifecycle.ActiveRequest;
        if (progressRequest is not null && progressRequest.Generation == _inventoryGeneration &&
            string.Equals(args.Source, progressRequest.DocumentSource, StringComparison.Ordinal) &&
            string.Equals(core.Source, progressRequest.DocumentSource, StringComparison.Ordinal) &&
            InventoryRefreshProgress.TryParse(json, progressRequest.RequestId, out var progress) && progress is not null)
        {
            InventoryProgressChanged?.Invoke(progress);
            return;
        }

        InventoryRequest? request;
        ConsoleInventoryRead? read;
        lock (_inventorySync)
        {
            // Request IDs and source URLs correlate this response to current host work;
            // they are not authentication against JavaScript in a trusted-origin page.
            request = _inventoryRequestLifecycle.ActiveRequest;
            if (request is null || request.Generation != _inventoryGeneration ||
                !string.Equals(args.Source, request.DocumentSource, StringComparison.Ordinal) ||
                !string.Equals(core.Source, request.DocumentSource, StringComparison.Ordinal) ||
                !ConsoleInventoryExtractor.TryParse(json, request.RequestId, out read) || read is null)
            {
                return;
            }

            if (!_inventoryRequestLifecycle.TryComplete(request))
            {
                return;
            }
            _reportedPageKind = read.PageKind;
            _lastExtractedRows = read.Rows;
            _lastRowsDocumentSource = request.DocumentSource;
            _lastRowsGeneration = request.Generation;
            request.Completion.TrySetResult(read);
        }

        var capturedAt = DateTimeOffset.Now;
        var snapshot = new AccountInventorySnapshot(
            AccountId, AccountName, read.Outcome, read.PageKind, read.Authentication,
            read.RowCount, read.ReportedTotal, read.Rows, capturedAt, read.Diagnostic,
            read.PagesVisited, read.WalkMillis, read.Mode);
        _inventoryObserver?.Invoke(snapshot);
        _status(read.Outcome == InventoryOutcome.Unavailable
            ? $"{AccountName}: {read.Diagnostic ?? "Computer list unavailable; inspect the current page layout."}"
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

    /// <summary>Returns an aggregate structural survey with no page-derived labels or samples.</summary>
    public async Task<string> InspectConsoleAsync()
    {
        if (_disposed || _webView.CoreWebView2 is not { } core || !IsOfficialConsoleOrigin(core.Source))
        {
            return "Inspection unavailable: open this account's official console first.";
        }

        var source = core.Source;
        var generation = _inventoryGeneration;
        var result = await _consoleInspection.ReadAsync(async () =>
        {
            var raw = await core.ExecuteScriptAsync(ConsoleInventoryExtractor.DiagnosticsScript);
            return JsonSerializer.Deserialize<string>(raw) ?? throw new JsonException();
        }, () => !_disposed && generation == _inventoryGeneration &&
            string.Equals(source, core.Source, StringComparison.Ordinal) && IsOfficialConsoleOrigin(core.Source),
            TimeSpan.FromSeconds(8));

        return result.Status switch
        {
            ConsoleInspectionStatus.Complete => result.Survey ?? "{}",
            ConsoleInspectionStatus.TimedOut => "Inspection timed out. The other account can still be inspected. Wait for this read to finish or reopen the account browser before retrying.",
            ConsoleInspectionStatus.Busy => "Inspection still pending for this account; no additional read was started.",
            ConsoleInspectionStatus.PageChanged => "Inspection discarded: this account navigated or closed during the read. Inspect the current console again.",
            _ => "Inspection failed. No page or exception details were included; reopen this account's console and retry."
        };
    }

    /// <summary>
    /// Reads the owning console's documented connect chooser. Both the host and script
    /// require the current top-level document to remain on an official HTTPS origin.
    /// </summary>
    public async Task<ChooserProbe?> ProbeChooserAsync()
    {
        if (_webView.CoreWebView2 is not { } core || !IsOfficialConsoleOrigin(core.Source))
        {
            return null;
        }

        var source = core.Source;
        var raw = await core.ExecuteScriptAsync(ConnectionChooser.ProbeScript);
        if (!string.Equals(source, core.Source, StringComparison.Ordinal) || !IsOfficialConsoleOrigin(core.Source))
        {
            return null;
        }

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
        if (_webView.CoreWebView2 is not { } core)
        {
            return "no-webview";
        }

        if (!IsOfficialConsoleOrigin(core.Source))
        {
            return "blocked-origin";
        }

        var source = core.Source;
        var raw = await core.ExecuteScriptAsync(ConnectionChooser.SelectScript(optionKey));
        return string.Equals(source, core.Source, StringComparison.Ordinal) && IsOfficialConsoleOrigin(core.Source)
            ? raw.Trim('"')
            : "blocked-origin";
    }

    /// <summary>Asks the owning console page for a fresh read of its computer list.</summary>
    public async Task<InventoryRefreshKind> RequestInventoryAsync()
    {
        var generation = _inventoryGeneration;
        var attempt = await StartInventoryRequestAsync(generation, joinMatchingActive: true);
        return InventoryRefreshClassification.Classify(attempt.Read?.Outcome, attempt.Read?.PageKind, attempt.ReloadRequired);
    }

    /// <summary>Captures selected row identity plus the exact document and extraction generation.</summary>
    public bool TryCaptureConnectTarget(int rowIndex, ExtractedComputerRow selectedRow, out InventoryActionTarget? target)
    {
        ArgumentNullException.ThrowIfNull(selectedRow);
        target = null;
        if (_disposed || _webView.CoreWebView2 is not { } core || !IsOfficialConsoleOrigin(core.Source))
        {
            return false;
        }

        lock (_inventorySync)
        {
            if (_disposed || _lastRowsGeneration != _inventoryGeneration ||
                !string.Equals(_lastRowsDocumentSource, core.Source, StringComparison.Ordinal) ||
                rowIndex < 0 || rowIndex >= _lastExtractedRows.Count)
            {
                return false;
            }

            var captured = new InventoryActionTarget(AccountId, rowIndex, core.Source, _inventoryGeneration, selectedRow);
            if (!ConsoleInventoryActionGuard.IsCurrentTarget(captured, AccountId, rowIndex, core.Source,
                    _inventoryGeneration, _lastExtractedRows[rowIndex]))
            {
                return false;
            }

            target = captured;
            return true;
        }
    }

    /// <summary>
    /// Connects only after a bounded page/window walk locates one exact live identity.
    /// Every host-issued step rechecks account, document, generation and snapshot identity;
    /// the synchronous page click repeats the exact identity and control checks.
    /// </summary>
    public async Task<string> ActivateConnectAsync(InventoryActionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!await _consoleWalkGate.WaitAsync(0))
        {
            return "busy";
        }

        ConnectOperation? operation = null;
        try
        {
            if (_disposed || _webView.CoreWebView2 is not { } core)
            {
                return "no-webview";
            }

            if (!IsOfficialConsoleOrigin(core.Source))
            {
                return "blocked-origin";
            }

            if (_inventoryRequestLifecycle.HasActiveRequest)
            {
                return "busy";
            }

            int inventoryRowCount;
            lock (_inventorySync)
            {
                if (!IsCurrentConnectTarget(target, core, target.DocumentSource, target.Generation))
                {
                    return "row-changed";
                }

                inventoryRowCount = _lastExtractedRows.Count;
            }

            if (!target.Row.HasConnectControl)
            {
                return "no-control";
            }

            operation = new ConnectOperation(this, target, core, target.DocumentSource,
                target.Generation, inventoryRowCount, DateTimeOffset.UtcNow + ConnectLocatorTimeout);
            return await LocateAndActivateConnectAsync(operation);
        }
        catch (Exception)
        {
            return "connect-failed";
        }
        finally
        {
            if (operation?.PendingScript is { } pending)
            {
                _ = ReleaseConsoleWalkGateWhenCompleteAsync(pending);
            }
            else
            {
                _consoleWalkGate.Release();
            }
        }
    }

    private bool IsCurrentConnectTarget(InventoryActionTarget target, CoreWebView2 core, string source, int generation)
    {
        if (_disposed || _webView.CoreWebView2 is null || !ReferenceEquals(_webView.CoreWebView2, core) ||
            !IsOfficialConsoleOrigin(core.Source) || !string.Equals(core.Source, source, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_inventorySync)
        {
            return !_disposed && generation == _inventoryGeneration &&
                _lastRowsGeneration == generation &&
                string.Equals(_lastRowsDocumentSource, source, StringComparison.Ordinal) &&
                target.RowIndex >= 0 && target.RowIndex < _lastExtractedRows.Count &&
                ConsoleInventoryActionGuard.IsCurrentTarget(target, AccountId, target.RowIndex, source,
                    generation, _lastExtractedRows[target.RowIndex]);
        }
    }

    private async Task<string> LocateAndActivateConnectAsync(ConnectOperation operation)
    {
        var initial = await operation.StepAsync("read");
        if (initial.Step is null || initial.Step.Status != "ok") return initial.Status;

        if (initial.Step.HasPager)
        {
            return await LocateInPagerAsync(operation, initial.Step);
        }

        if (initial.Step.ScrollMax > 0 && initial.Step.RenderedRowCount < operation.InventoryRowCount)
        {
            return await LocateInVirtualListAsync(operation, initial.Step);
        }

        return await ClickUniqueCurrentRowAsync(operation, initial.Step, null, null);
    }

    private async Task<string> LocateInPagerAsync(ConnectOperation operation, ConnectLocatorStep first)
    {
        var pageTokens = new List<string>();
        string? targetPage = null;
        long? targetPosition = null;
        var targetControlCount = 0;
        var current = first;
        for (var page = 0; page < ConnectLocatorMaxSteps; page++)
        {
            if (string.IsNullOrEmpty(current.PageToken) || pageTokens.Contains(current.PageToken, StringComparer.Ordinal))
                return "locator-ambiguous";
            pageTokens.Add(current.PageToken);

            if (current.TargetCount > 0)
            {
                if (current.TargetCount != 1 || targetPage is not null) return "ambiguous-row";
                targetPage = current.PageToken;
                targetPosition = current.TargetPositions.SingleOrDefault();
                targetControlCount = current.TargetControlCounts.SingleOrDefault();
            }

            if (!current.NextEnabled) break;
            if (page == ConnectLocatorMaxSteps - 1) return "locator-limit";
            var oldToken = current.PageToken;
            var action = await operation.StepAsync("next", oldToken);
            if (action.Step is null || action.Step.Status != "action-started") return action.Status;
            var changed = await WaitForPagerChangeAsync(operation, oldToken, null);
            if (changed.Step is null) return changed.Status;
            current = changed.Step;
        }

        if (targetPage is null) return "no-row";
        if (targetControlCount != 1) return targetControlCount == 0 ? "no-control" : "ambiguous-control";
        var targetIndex = pageTokens.FindIndex(token => string.Equals(token, targetPage, StringComparison.Ordinal));
        if (targetIndex < 0) return "locator-ambiguous";

        for (var index = pageTokens.Count - 1; index > targetIndex; index--)
        {
            var action = await operation.StepAsync("previous", pageTokens[index]);
            if (action.Step is null || action.Step.Status != "action-started") return action.Status;
            var restored = await WaitForPagerChangeAsync(operation, pageTokens[index], pageTokens[index - 1]);
            if (restored.Step is null) return restored.Status;
            current = restored.Step;
        }

        var revalidated = await operation.StepAsync("read", targetPage);
        if (revalidated.Step is null || revalidated.Step.Status != "ok") return revalidated.Status;
        if (revalidated.Step.TargetCount != 1 || revalidated.Step.TargetPositions.SingleOrDefault() != targetPosition)
            return "identity-mismatch";
        if (revalidated.Step.TargetControlCounts.SingleOrDefault() != 1) return "no-control";
        return await ClickUniqueCurrentRowAsync(operation, revalidated.Step, targetPage, targetPosition);
    }

    private async Task<(ConnectLocatorStep? Step, string Status)> WaitForPagerChangeAsync(
        ConnectOperation operation,
        string oldToken,
        string? desiredToken)
    {
        while (DateTimeOffset.UtcNow < operation.Deadline)
        {
            await Task.Delay(50);
            var read = await operation.StepAsync("read");
            if (read.Step is null || read.Step.Status != "ok") return read;
            if (desiredToken is not null
                ? string.Equals(read.Step.PageToken, desiredToken, StringComparison.Ordinal)
                : !string.Equals(read.Step.PageToken, oldToken, StringComparison.Ordinal)) return read;
        }

        return (null, "timeout");
    }

    private async Task<string> LocateInVirtualListAsync(ConnectOperation operation, ConnectLocatorStep initial)
    {
        var positions = new HashSet<long>();
        var controls = new Dictionary<long, int>();
        AddVirtualMatches(initial, positions, controls);
        var current = initial;
        var stableBottomReads = 0;
        uint previousBottomSignature = 0;
        double previousBottomMax = -1;

        for (var step = 0; step < ConnectLocatorMaxSteps; step++)
        {
            if (current.ScrollTop + current.ScrollClient >= current.ScrollMax - 8)
            {
                if (stableBottomReads >= 2 && current.Signature == previousBottomSignature &&
                    Math.Abs(current.ScrollMax - previousBottomMax) < 1)
                {
                    break;
                }

                stableBottomReads = current.Signature == previousBottomSignature &&
                    Math.Abs(current.ScrollMax - previousBottomMax) < 1 ? stableBottomReads + 1 : 1;
                previousBottomSignature = current.Signature;
                previousBottomMax = current.ScrollMax;
                await Task.Delay(125);
                var bottomRead = await operation.StepAsync("read");
                if (bottomRead.Step is null || bottomRead.Step.Status != "ok") return bottomRead.Status;
                AddVirtualMatches(bottomRead.Step, positions, controls);
                current = bottomRead.Step;
                continue;
            }

            stableBottomReads = 0;
            var desired = Math.Min(current.ScrollMax, current.ScrollTop + Math.Max(1, current.ScrollClient * 0.8));
            var action = await operation.StepAsync("scroll", desiredScrollTop: desired);
            if (action.Step is null || action.Step.Status != "action-started") return action.Status;
            var priorSignature = current.Signature;
            var repaintDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.5);
            ConnectLocatorStep? painted = null;
            while (DateTimeOffset.UtcNow < repaintDeadline && DateTimeOffset.UtcNow < operation.Deadline)
            {
                await Task.Delay(50);
                var read = await operation.StepAsync("read");
                if (read.Step is null || read.Step.Status != "ok") return read.Status;
                if (read.Step.ScrollTop >= desired - 1 && read.Step.Signature != priorSignature)
                {
                    painted = read.Step;
                    break;
                }
            }

            if (painted is null) return "locator-no-progress";
            AddVirtualMatches(painted, positions, controls);
            current = painted;
        }

        if (current.ScrollTop + current.ScrollClient < current.ScrollMax - 8) return "locator-limit";
        if (positions.Count == 0) return "no-row";
        if (positions.Count != 1) return "ambiguous-row";
        var targetPosition = positions.Single();
        if (!controls.TryGetValue(targetPosition, out var controlCount) || controlCount != 1)
            return controlCount == 0 ? "no-control" : "ambiguous-control";

        if (!current.TargetPositions.Contains(targetPosition))
        {
            var desired = Math.Max(0, targetPosition / 1000d - current.ScrollClient / 2);
            var action = await operation.StepAsync("scroll", desiredScrollTop: desired);
            if (action.Step is null || action.Step.Status != "action-started") return action.Status;
            var visible = await WaitForVirtualTargetAsync(operation, targetPosition);
            if (visible.Step is null) return visible.Status;
            current = visible.Step;
        }

        return await ClickUniqueCurrentRowAsync(operation, current, null, targetPosition);
    }

    private static void AddVirtualMatches(ConnectLocatorStep step, HashSet<long> positions, Dictionary<long, int> controls)
    {
        for (var index = 0; index < step.TargetPositions.Length; index++)
        {
            var position = step.TargetPositions[index];
            positions.Add(position);
            controls[position] = step.TargetControlCounts[index];
        }
    }

    private async Task<(ConnectLocatorStep? Step, string Status)> WaitForVirtualTargetAsync(ConnectOperation operation, long targetPosition)
    {
        while (DateTimeOffset.UtcNow < operation.Deadline)
        {
            await Task.Delay(50);
            var read = await operation.StepAsync("read");
            if (read.Step is null || read.Step.Status != "ok") return read;
            if (read.Step.TargetPositions.Contains(targetPosition)) return read;
        }

        return (null, "timeout");
    }

    private async Task<string> ClickUniqueCurrentRowAsync(
        ConnectOperation operation,
        ConnectLocatorStep current,
        string? pageToken,
        long? position)
    {
        if (current.TargetCount != 1) return current.TargetCount == 0 ? "no-row" : "ambiguous-row";
        if (position is not null && current.TargetPositions.SingleOrDefault() != position) return "identity-mismatch";
        if (current.TargetControlCounts.SingleOrDefault() != 1) return "no-control";
        var click = await operation.StepAsync("click", pageToken, position);
        return click.Step?.Status == "clicked" ? "clicked" : click.Status;
    }

    private async Task ReleaseConsoleWalkGateWhenCompleteAsync(Task<string> pendingScript)
    {
        try { await pendingScript; }
        catch (Exception) { }
        finally { _consoleWalkGate.Release(); }
    }

    private sealed class ConnectOperation(
        AccountWebViewPane owner,
        InventoryActionTarget target,
        CoreWebView2 core,
        string source,
        int generation,
        int inventoryRowCount,
        DateTimeOffset deadline)
    {
        private double? _documentToken;
        public Task<string>? PendingScript { get; private set; }
        public DateTimeOffset Deadline { get; } = deadline;
        public int InventoryRowCount { get; } = inventoryRowCount;

        public async Task<(ConnectLocatorStep? Step, string Status)> StepAsync(
            string command,
            string? pageToken = null,
            long? expectedPosition = null,
            double? desiredScrollTop = null)
        {
            if (!owner.IsCurrentConnectTarget(target, core, source, generation)) return (null, "row-changed");
            var remaining = Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return (null, "timeout");
            var deadlineMilliseconds = Deadline.ToUnixTimeMilliseconds();
            if (command == "click" && _documentToken is null) return (null, "connect-failed");
            var script = command == "click"
                ? ConsoleInventoryActions.ActivateConnectScript(target.Row, source, _documentToken!.Value,
                    pageToken, expectedPosition, deadlineMilliseconds)
                : ConnectLocatorScript.CreateStep(command, target.Row, source, _documentToken,
                    pageToken, expectedPosition, desiredScrollTop, deadlineMilliseconds);
            Task<string> execution;
            try { execution = core.ExecuteScriptAsync(script); }
            catch (Exception) { return (null, "connect-failed"); }

            if (await Task.WhenAny(execution, Task.Delay(remaining)) != execution)
            {
                PendingScript = execution;
                return (null, "timeout");
            }

            string raw;
            try { raw = await execution; }
            catch (Exception) { return (null, "connect-failed"); }
            if (!owner.IsCurrentConnectTarget(target, core, source, generation)) return (null, "row-changed");

            try
            {
                var json = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
                var step = JsonSerializer.Deserialize<ConnectLocatorStep>(json, ConnectStepJsonOptions);
                if (step is null) return (null, "connect-failed");
                if (step.Status is "blocked-origin" or "document-changed" or "page-changed") return (null, "row-changed");
                if (step.Status == "expired") return (null, "timeout");
                if (_documentToken is null && step.DocumentToken > 0) _documentToken = step.DocumentToken;
                return (step, step.Status == "error" ? "connect-failed" : step.Status);
            }
            catch (JsonException)
            {
                return (null, "connect-failed");
            }
        }
    }

    /// <summary>Compatibility convenience for smoke tests that activate without a modal prompt.</summary>
    public Task<string> ActivateConnectAsync(int rowIndex)
    {
        if (_webView.CoreWebView2 is { } core && !IsOfficialConsoleOrigin(core.Source))
        {
            return Task.FromResult("blocked-origin");
        }

        if (_webView.CoreWebView2 is null || rowIndex < 0 || rowIndex >= _lastExtractedRows.Count)
        {
            return Task.FromResult("no-row");
        }

        ExtractedComputerRow selectedRow;
        lock (_inventorySync)
        {
            if (rowIndex < 0 || rowIndex >= _lastExtractedRows.Count)
            {
                return Task.FromResult("no-row");
            }

            selectedRow = _lastExtractedRows[rowIndex];
        }

        return TryCaptureConnectTarget(rowIndex, selectedRow, out var target) && target is not null
            ? ActivateConnectAsync(target)
            : Task.FromResult("no-row");
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

    private static bool ConfirmNativeHandoff() =>
        MessageBox.Show(
            "WebView2 could not verify a direct user gesture for this Splashtop Business handoff. Open the Business app only if you just selected its option in the console's connection chooser.",
            "Confirm Splashtop Business handoff",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Installs the documented-chooser preference script. Its page messages are advisory
    /// only and are never used to authorize a native protocol handoff.
    /// </summary>
    private static void InstallNativePreference(CoreWebView2 core, Action<string> status)
    {
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(NativeConnectionPreference.InstallScript)
            .ContinueWith(
                task => status("The automatic Business-app preference could not be installed on this page; the connection chooser was left untouched."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (IsWebUri(args.Uri))
        {
            InvalidateInventoryForNavigation();
            _inventoryObserver?.Invoke(new AccountInventorySnapshot(
                AccountId, AccountName, InventoryOutcome.Unavailable, ConsolePageKind.Unknown,
                ConsoleAuthentication.Unknown, 0, null, [], DateTimeOffset.UtcNow,
                "Console navigation started; retained rows are from the previous document."));
        }
        else
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
        HandleBusinessAppUri(uri, initiatingOrigin, isUserInitiated, _status, _businessAppHandoff, _confirmNativeHandoff);

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        _handoffEventObserver?.Invoke(nameof(OnNewWindowRequested), args.Uri, args.IsUserInitiated);
        await HandlePopupRequestAsync(args, _environment, _popups, _status, _businessAppHandoff, _confirmNativeHandoff);
    }

    private static async Task HandlePopupRequestAsync(
        CoreWebView2NewWindowRequestedEventArgs args,
        CoreWebView2Environment? environment,
        List<PopupWindow> popups,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff,
        Func<bool> confirmNativeHandoff)
    {
        if (!IsPopupUri(args.Uri))
        {
            args.Handled = true;
            HandleBusinessAppUri(args.Uri, args.OriginalSourceFrameInfo?.Source, args.IsUserInitiated,
                status, businessAppHandoff, confirmNativeHandoff);
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
            var popup = new PopupWindow(environment, popups, status, businessAppHandoff, confirmNativeHandoff);
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
        Func<bool> confirmNativeHandoff)
    {
        // WebView2's user-initiated flag is the only trusted gesture signal. The
        // page-side chooser script and messages are not authentication. If WebView2
        // reports a non-user-initiated follow-up, require an explicit native prompt.
        var result = businessAppHandoff.TryDispatchWithHostConfirmation(
            uri, initiatingOrigin, isUserInitiated, confirmNativeHandoff);
        switch (result)
        {
            case BusinessAppHandoffResult.Dispatched:
                status("Opening the Splashtop Business app for the remote session.");
                break;
            case BusinessAppHandoffResult.Failed:
                status("Could not open Splashtop Business. Install or repair the desktop app and confirm its st-business protocol handler is registered.");
                break;
            case BusinessAppHandoffResult.Rejected:
                status("Blocked an external or executable URI scheme. Only a user-initiated Splashtop Business link from the trusted console can be opened, or explicitly confirmed in this app.");
                break;
        }
    }

    private sealed class PopupWindow : Window
    {
        private readonly CoreWebView2Environment _environment;
        private readonly List<PopupWindow> _popups;
        private readonly Action<string> _status;
        private readonly BusinessAppHandoff _businessAppHandoff;
        private readonly Func<bool> _confirmNativeHandoff;

        public PopupWindow(CoreWebView2Environment environment, List<PopupWindow> popups, Action<string> status,
            BusinessAppHandoff businessAppHandoff, Func<bool> confirmNativeHandoff)
        {
            _environment = environment;
            _popups = popups;
            _status = status;
            _businessAppHandoff = businessAppHandoff;
            _confirmNativeHandoff = confirmNativeHandoff;
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
            InstallNativePreference(core, _status);
        }

        private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
            await HandlePopupRequestAsync(args, _environment, _popups, _status, _businessAppHandoff, _confirmNativeHandoff);

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            HandleNavigationStarting(args, WebView.CoreWebView2?.Source, _status, _businessAppHandoff, _confirmNativeHandoff);
        }

        private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        {
            args.Cancel = true;
            HandleBusinessAppUri(args.Uri, args.InitiatingOrigin, args.IsUserInitiated,
                _status, _businessAppHandoff, _confirmNativeHandoff);
        }
    }

    private static void HandleNavigationStarting(
        CoreWebView2NavigationStartingEventArgs args,
        string? initiatingOrigin,
        Action<string> status,
        BusinessAppHandoff businessAppHandoff,
        Func<bool> confirmNativeHandoff)
    {
        if (IsWebUri(args.Uri))
        {
            return;
        }

        args.Cancel = true;
        HandleBusinessAppUri(args.Uri, initiatingOrigin, args.IsUserInitiated,
            status, businessAppHandoff, confirmNativeHandoff);
    }
}
