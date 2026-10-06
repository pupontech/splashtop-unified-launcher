using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SplashtopUnified.AccountBrowser;

internal sealed class AccountBrowserWindow : Window
{
    private readonly AccountStore _store;
    private readonly List<AccountProfile> _profiles;
    private readonly List<AccountWebViewPane> _panes = [];
    private readonly InventorySnapshotStore _inventory = new();
    private readonly string _inventoryCachePath;
    private UnifiedInventoryWindow? _unifiedWindow;
    private string? _inventoryCacheNotice;
    private bool _rebuildQueued;
    private bool _connectInFlight;
    private bool _closed;

    public AccountBrowserWindow(AccountStore store, List<AccountProfile> profiles)
    {
        _store = store;
        _profiles = profiles;
        _inventoryCachePath = InventoryCache.GetPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        LoadCachedInventory();
        Title = "Splashtop · two live accounts · isolated logins";
        Width = 1500; Height = 950; MinWidth = 900; MinHeight = 600;
        var root = new DockPanel();
        var notice = new TextBlock {
            Text = "Sign in separately in each official console. Both lists populate live, and the Unified list view merges every account's rows read-only from the official console tables. Rows are never merged by name because the console list exposes no numeric identity. This remains a read-only prototype.",
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(12), Background = Brushes.AliceBlue
        };
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);
        var viewBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
        var openUnified = new Button { Content = "Open unified list", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4) };
        openUnified.Click += (_, _) => OpenUnifiedList();
        viewBar.Children.Add(openUnified);
        DockPanel.SetDock(viewBar, Dock.Top); root.Children.Add(viewBar);
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        split.ColumnDefinitions.Add(new ColumnDefinition());
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.LightGray };
        Grid.SetColumn(splitter, 1); split.Children.Add(splitter);
        for (var slot = 0; slot < 2; slot++) {
            var panel = BuildSlot(slot);
            Grid.SetColumn(panel, slot == 0 ? 0 : 2); split.Children.Add(panel);
        }
        root.Children.Add(split); Content = root;
        Closed += (_, _) => { _closed = true; foreach(var pane in _panes) pane.Dispose(); };
    }

    private FrameworkElement BuildSlot(int slot)
    {
        var profile = _profiles[slot];
        var panel = new DockPanel { Margin = new Thickness(4) };
        var toolbar = new StackPanel();
        var title = new TextBlock { Text = profile.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4) };
        toolbar.Children.Add(title);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var reload = new Button { Content = "Reload", Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4), IsEnabled = false };
        var edit = new Button { Content = "Account settings", Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        controls.Children.Add(reload); controls.Children.Add(edit); toolbar.Children.Add(controls);
        var status = new TextBlock { Text = "Starting isolated browser…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        toolbar.Children.Add(status); DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        var pane = new AccountWebViewPane(message => { if (!_closed) status.Text = message; },
            accountId: profile.Id, accountName: profile.Name,
            inventoryObserver: snapshot => MainThread(() => {
                if (_closed || !_inventory.Apply(snapshot)) return;
                PersistInventoryCache();
                ScheduleRebuild();
            }), nativeHandoffConfirmation: () => MessageBox.Show(
                _unifiedWindow is { IsVisible: true } unified ? unified : this,
                "Open this session in the Splashtop Business app?",
                "Confirm connection", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes);
        _panes.Add(pane); panel.Children.Add(pane);
        reload.Click += (_, _) => { if (!_closed) pane.Navigate(_profiles[slot].ConsoleUrl); };
        edit.Click += (_, _) => {
            var updated = EditProfile(_profiles[slot]);
            if (updated is null) return;
            var next = _profiles.ToList(); next[slot] = updated;
            try { _store.Save(next); _profiles[slot] = updated; title.Text = updated.Name; if (reload.IsEnabled) pane.Navigate(updated.ConsoleUrl); }
            catch(Exception ex) { status.Text = "Settings not saved: " + ex.Message; }
        };
        var initializationStarted = false;
        panel.Loaded += async (_, _) => {
            if (initializationStarted || _closed) return;
            initializationStarted = true;
            try {
                await pane.InitializeAsync(_store.GetUserDataFolder(_profiles[slot]), _profiles[slot].ConsoleUrl);
                if (_closed) return;
                reload.IsEnabled = true;
                status.Text = "Independent account session · sign in normally below";
            } catch(Exception ex) { if (!_closed) status.Text = "Browser could not start: " + ex.Message; }
        };
        return panel;
    }

    private void OpenUnifiedList()
    {
        if (_unifiedWindow is null || !_unifiedWindow.IsLoaded)
        {
            UnifiedInventoryWindow? created = null;
            created = new UnifiedInventoryWindow(
                _inventory,
                RefreshInventoryAsync,
                ConnectByAccountRowAsync,
                message =>
                {
                    if (!_closed && ReferenceEquals(created, _unifiedWindow) && created is { IsLoaded: true })
                        created.Status(message);
                },
                InspectConsolesAsync);
            _unifiedWindow = created;
            created.Closed += (_, _) => { if (ReferenceEquals(created, _unifiedWindow)) _unifiedWindow = null; };
            created.Show();
        }

        _unifiedWindow.Activate();
        _unifiedWindow.Rebuild();
        if (_inventoryCacheNotice is not null)
        {
            _unifiedWindow.Status(_inventoryCacheNotice);
        }
    }

    /// <summary>
    /// Asks every account's own console for a fresh read. Accounts are read in parallel
    /// because each has its own page and its own lock, so the slowest account sets the time
    /// instead of the sum of all of them.
    /// </summary>
    private async Task RefreshInventoryAsync()
    {
        var panes = _panes.ToList();
        var targetWindow = _unifiedWindow;
        targetWindow?.BeginRefresh(panes.Select(p => (p.AccountId, p.AccountName)));
        targetWindow?.Status($"Reading {panes.Count} console(s)…");
        var started = DateTimeOffset.Now;
        var failures = new List<string>();
        await Task.WhenAll(panes.Select(async pane =>
        {
            void OnProgress(InventoryRefreshProgress progress)
            {
                if (!_closed && ReferenceEquals(targetWindow, _unifiedWindow))
                    targetWindow?.UpdateRefresh(pane.AccountId, pane.AccountName, progress);
            }
            pane.InventoryProgressChanged += OnProgress;
            var succeeded = false;
            var reloadRequired = false;
            try
            {
                var result = await pane.RequestInventoryAsync();
                succeeded = result == AccountWebViewPane.InventoryRequestStatus.Complete;
                reloadRequired = result == AccountWebViewPane.InventoryRequestStatus.ReloadRequired;
                if (!succeeded)
                {
                    failures.Add(reloadRequired
                        ? $"{pane.AccountName}: outcome unknown; Reload required to recover safely"
                        : $"{pane.AccountName}: incomplete, unavailable or interrupted");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{pane.AccountName}: {ex.Message}");
            }
            finally
            {
                pane.InventoryProgressChanged -= OnProgress;
                if (!_closed && ReferenceEquals(targetWindow, _unifiedWindow))
                    targetWindow?.CompleteAccountRefresh(pane.AccountId, pane.AccountName, succeeded, reloadRequired);
            }
        }));

        ScheduleRebuild();
        var elapsed = (int)(DateTimeOffset.Now - started).TotalMilliseconds;
        if (!_closed && ReferenceEquals(targetWindow, _unifiedWindow))
        {
            targetWindow?.FinishRefresh();
            targetWindow?.Status(failures.Count == 0
                ? $"Read {panes.Count} console(s) in {elapsed} ms."
                : $"Read {panes.Count} console(s) in {elapsed} ms; {string.Join("; ", failures)}");
        }
    }

    private async Task ConnectByAccountRowAsync(UnifiedInventoryRow selectedRow)
    {
        if (_connectInFlight || _closed) return;
        _connectInFlight = true;
        var connectionWindow = _unifiedWindow;
        try { await ConnectByAccountRowCoreAsync(selectedRow, connectionWindow); }
        catch (Exception)
        {
            if (IsCurrentConnectionWindow(connectionWindow))
                connectionWindow!.Status("The connection could not be completed. Its outcome is unconfirmed; refresh the account before retrying.");
        }
        finally { _connectInFlight = false; }
    }

    private bool IsCurrentConnectionWindow(UnifiedInventoryWindow? window) =>
        !_closed && window is { IsLoaded: true } && ReferenceEquals(window, _unifiedWindow);

    private async Task ConnectByAccountRowCoreAsync(UnifiedInventoryRow selectedRow, UnifiedInventoryWindow? connectionWindow)
    {
        var pane = _panes.FirstOrDefault(candidate => string.Equals(candidate.AccountId, selectedRow.AccountId, StringComparison.Ordinal));
        if (pane is null)
        {
            if (IsCurrentConnectionWindow(connectionWindow)) connectionWindow!.Status("That account is no longer open.");
            return;
        }

        if (!IsCurrentConnectionWindow(connectionWindow)) return;
        if (!pane.TryCaptureConnectTarget(selectedRow.RowIndexInAccount, selectedRow.ToComputerRow(), out var target) || target is null)
        {
            connectionWindow!.Status("The selected row changed before the request began. Refresh the list and select it again.");
            return;
        }

        // This is host intent only. No vendor chooser option is claimed before observing it.
        var proceed = await connectionWindow!.AskConnectionIntentAsync(pane.AccountName);
        if (!IsCurrentConnectionWindow(connectionWindow)) return;
        if (!proceed)
        {
            connectionWindow.Status("Connect cancelled. No console action was performed.");
            return;
        }

        connectionWindow.Status($"{pane.AccountName}: preparing the connection…");
        var outcome = await pane.ActivateConnectAsync(target);
        if (!IsCurrentConnectionWindow(connectionWindow)) return;
        if (outcome != "clicked")
        {
            connectionWindow.Status(outcome switch
            {
                "no-table" => $"{pane.AccountName} is not showing the computer list right now; the console was left untouched.",
                "no-row" or "row-changed" => $"{pane.AccountName}'s selected row or document changed; refresh and select it again.",
                "no-control" => $"{pane.AccountName}'s row has no Connect control; the console was left untouched.",
                _ => $"{pane.AccountName} could not be asked to connect ({outcome})."
            });
            return;
        }

        // The chooser prompt and selection are based only on the actual post-Connect probe.
        var probe = await WaitForChooserAsync(pane);
        if (!IsCurrentConnectionWindow(connectionWindow)) return;
        if (probe is not { Present: true })
        {
            connectionWindow.Status($"{pane.AccountName}: the console did not expose a supported connection chooser. Connection outcome is unconfirmed; no background-pane fallback was requested.");
            return;
        }

        var choice = await connectionWindow.AskConnectChoiceAsync(pane.AccountName, probe);
        if (!IsCurrentConnectionWindow(connectionWindow)) return;
        if (choice is null)
        {
            connectionWindow.Status("Connection chooser cancelled. No option was applied; the console may still show its chooser.");
            return;
        }

        var applied = await pane.SelectChooserOptionAsync(choice);
        if (!IsCurrentConnectionWindow(connectionWindow)) return;
        connectionWindow.Status(applied switch
        {
            "clicked" when choice == ConnectionChooser.NativeKey => $"Asked {pane.AccountName} to open this session in the Splashtop Business app.",
            "clicked" => $"Asked {pane.AccountName} to open this session in the web app in that account's browser.",
            "no-option" => "The console changed before the choice could be applied; try Connect again.",
            "blocked" => "Refused to act outside the official console page.",
            _ => $"The choice could not be applied ({applied})."
        });
    }

    /// <summary>
    /// Waits a bounded time for the console's documented chooser to render after a Connect.
    /// Returns null when it never appears, so a direct connection is not mistaken for a
    /// chooser and no option is ever clicked blindly.
    /// </summary>
    private static async Task<ChooserProbe?> WaitForChooserAsync(AccountWebViewPane pane)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(attempt == 0 ? 500 : 600);
            ChooserProbe? probe;
            try
            {
                probe = await pane.ProbeChooserAsync();
            }
            catch (Exception)
            {
                return null;
            }

            if (probe is { Present: true })
            {
                return probe;
            }
        }

        return null;
    }

    /// <summary>Read-only structural survey of each console, for diagnosing a partial list.</summary>
    private async Task<string> InspectConsolesAsync()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Read-only console survey. Counts and labels only; no row data, cookies or tokens.");
        builder.AppendLine("Send this back if the merged list is missing computers.");
        builder.AppendLine();
        foreach (var pane in _panes.ToList())
        {
            builder.AppendLine("=== " + (string.IsNullOrEmpty(pane.AccountName) ? "(unnamed account)" : pane.AccountName) + " ===");
            try
            {
                builder.AppendLine(await pane.InspectConsoleAsync());
            }
            catch (Exception ex)
            {
                builder.AppendLine("inspection failed: " + ex.Message);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rebuilds the merged view once per burst instead of once per message. Reading several
    /// accounts posts several snapshots in quick succession, and rebuilding per message
    /// re-creates every row repeatedly for no visible benefit.
    /// </summary>
    private void ScheduleRebuild()
    {
        if (_rebuildQueued || _closed)
        {
            return;
        }

        _rebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _rebuildQueued = false;
            if (_closed)
            {
                return;
            }

            _unifiedWindow?.Rebuild();
            if (_inventoryCacheNotice is not null)
            {
                _unifiedWindow?.Status(_inventoryCacheNotice);
            }
        }), DispatcherPriority.Background);
    }

    private void MainThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Invoke(action);
        }
    }

    private void LoadCachedInventory()
    {
        var cached = InventoryCache.Read(_inventoryCachePath);
        if (!cached.IsSuccess)
        {
            if (cached.FailureReason != InventoryCacheFailureReason.MissingFile)
            {
                _inventoryCacheNotice = $"Local cache was not used ({cached.FailureReason}); the list will use current console reads.";
            }

            return;
        }

        var configuredProfiles = _profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (var snapshot in cached.Snapshots)
        {
            if (configuredProfiles.TryGetValue(snapshot.AccountId, out var profile))
            {
                _inventory.Apply(snapshot.ToAccountInventorySnapshot(profile.Name));
            }
        }
    }

    private void PersistInventoryCache()
    {
        try
        {
            InventoryCache.Write(_inventoryCachePath, _inventory.All);
            _inventoryCacheNotice = null;
        }
        catch (Exception exception)
        {
            // Do not surface filesystem paths or exception details that may contain profile data.
            _inventoryCacheNotice = $"Local cache write failed ({exception.GetType().Name}); current rows remain in memory.";
            _unifiedWindow?.Status(_inventoryCacheNotice);
        }
    }

    private AccountProfile? EditProfile(AccountProfile profile)
    {
        var dialog = new Window { Title = "Account settings", Owner = this, Width = 500, Height = 250, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(16) };
        var name = new TextBox { Text = profile.Name, Margin = new Thickness(0, 4, 0, 12) };
        var url = new TextBox { Text = profile.ConsoleUrl, Margin = new Thickness(0, 4, 0, 12) };
        body.Children.Add(new TextBlock { Text = "Display name" }); body.Children.Add(name);
        body.Children.Add(new TextBlock { Text = "Official console URL (Global or EU; both accounts may use the same region)" }); body.Children.Add(url);
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(error);
        var save = new Button { Content = "Save", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Right };
        body.Children.Add(save); dialog.Content = body;
        AccountProfile? result = null;
        save.Click += (_, _) => {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Trim().Length > 80) { error.Text = "Enter a name of 1–80 characters."; return; }
            if (!AccountStore.TryNormalizeConsoleUrl(url.Text, out var normalized, out var why)) { error.Text = why; return; }
            result = profile with { Name = name.Text.Trim(), ConsoleUrl = normalized };
            dialog.DialogResult = true;
        };
        dialog.ShowDialog(); return result;
    }
}
