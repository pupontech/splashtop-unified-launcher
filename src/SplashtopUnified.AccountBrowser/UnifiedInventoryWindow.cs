using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// One merged, read-only row across all accounts. Rows are shown by account and order;
/// they are never de-duplicated by name because the console list exposes no numeric
/// identity to merge on.
/// </summary>
internal sealed record UnifiedInventoryRow(
    string AccountId,
    string AccountBadge,
    int RowIndexInAccount,
    string Name,
    string Status,
    string DeviceName,
    string Group,
    string Notes,
    bool HasConnectControl,
    bool IsCurrent)
{
    public bool CanConnect => IsCurrent && HasConnectControl;

    public ExtractedComputerRow ToComputerRow() => new(
        Name,
        string.IsNullOrEmpty(DeviceName) ? null : DeviceName,
        string.IsNullOrEmpty(Group) ? null : Group,
        string.IsNullOrEmpty(Notes) ? null : Notes,
        HasConnectControl,
        string.IsNullOrEmpty(Status) ? null : Status);
}

/// <summary>
/// Native merged list over every configured account's most recent extraction.
/// Connect is routed back to the owning account's own WebView row.
/// </summary>
internal sealed class UnifiedInventoryWindow : Window
{
    private readonly TextBlock _operationText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly StackPanel _refreshPanel = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly Dictionary<string, (TextBlock Label, ProgressBar Bar)> _refreshRows = new(StringComparer.Ordinal);

    public void BeginRefresh(IEnumerable<(string Id, string Name)> accounts)
    {
        _refreshRows.Clear();
        _refreshPanel.Children.Clear();
        _refreshPanel.Children.Add(new TextBlock { Text = "Refreshing…", FontWeight = FontWeights.Bold });
        foreach (var account in accounts)
        {
            var label = new TextBlock { Text = $"{account.Name}: refreshing…" };
            var bar = new ProgressBar { Height = 8, Maximum = 100, IsIndeterminate = true, Margin = new Thickness(0, 2, 0, 6) };
            _refreshPanel.Children.Add(label);
            _refreshPanel.Children.Add(bar);
            _refreshRows[account.Id] = (label, bar);
        }
    }

    public void UpdateRefresh(string id, string name, InventoryRefreshProgress progress)
    {
        if (!_refreshRows.TryGetValue(id, out var row)) return;
        row.Bar.IsIndeterminate = progress.Percentage is null;
        row.Bar.Value = progress.Percentage ?? 0;
        row.Label.Text = progress.Percentage is { } percent
            ? $"{name}: refreshing — {percent}% ({progress.RowsRead}/{progress.Total} rows)"
            : $"{name}: refreshing — {progress.RowsRead} rows collected (total unknown)";
    }

    public void CompleteAccountRefresh(string id, string name, bool succeeded, bool reloadRequired = false)
    {
        if (!_refreshRows.TryGetValue(id, out var row)) return;
        row.Bar.IsIndeterminate = false;
        row.Bar.Value = succeeded ? 100 : row.Bar.Value;
        row.Label.Text = succeeded ? $"{name}: refresh finished — 100%"
            : reloadRequired ? $"{name}: outcome unknown — Reload required to recover safely"
            : $"{name}: refresh failed or interrupted";
    }

    public void FinishRefresh()
    {
        if (_refreshPanel.Children.Count > 0 && _refreshPanel.Children[0] is TextBlock title)
            title.Text = "Refresh finished";
    }

    private readonly InventorySnapshotStore _store;
    private readonly Func<Task> _refreshAll;
    private readonly Func<UnifiedInventoryRow, Task> _connectRow;
    private readonly Func<Task<string>> _inspectConsoles;
    private readonly Action<string> _status;
    private readonly ListView _list = new();
    private readonly Button _connectButton = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _statusText = new();
    private List<UnifiedInventoryRow> _rows = [];
    private bool _isClosed;

    public UnifiedInventoryWindow(
        InventorySnapshotStore store,
        Func<Task> refreshAll,
        Func<UnifiedInventoryRow, Task> connectRow,
        Action<string> status,
        Func<Task<string>>? inspectConsoles = null)
    {
        _store = store;
        _refreshAll = refreshAll;
        _connectRow = connectRow;
        _status = status;
        _inspectConsoles = inspectConsoles ?? (() => Task.FromResult("No console inspection is available in this build."));

        Title = "Unified list · all accounts";
        Width = 1100;
        Height = 700;
        MinWidth = 720;
        MinHeight = 420;

        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(new TextBlock { Text = "Search", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _search.Width = 260;
        _search.Margin = new Thickness(0, 0, 10, 0);
        _search.TextChanged += (_, _) => ApplyFilter();
        toolbar.Children.Add(_search);
        var refresh = new Button { Content = "Refresh from consoles", Padding = new Thickness(10, 4, 10, 4) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                await _refreshAll();
            }
            catch (Exception exception)
            {
                if (!_isClosed) _status($"Refresh failed ({exception.GetType().Name}); retry when ready.");
            }
            finally
            {
                if (!_isClosed) refresh.IsEnabled = true;
            }
        };
        toolbar.Children.Add(refresh);
        _connectButton.Content = "Connect";
        _connectButton.Padding = new Thickness(10, 4, 10, 4);
        _connectButton.Margin = new Thickness(8, 0, 0, 0);
        _connectButton.IsEnabled = false;
        _connectButton.Click += async (_, _) => await ConnectSelectedAsync();
        toolbar.Children.Add(_connectButton);
        var inspect = new Button { Content = "Inspect consoles", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Read-only survey of each console page, used to diagnose a list that does not match the expected columns." };
        inspect.Click += async (_, _) =>
        {
            inspect.IsEnabled = false;
            try { await ShowInspectionAsync(); }
            catch (Exception exception) { if (!_isClosed) _status($"Inspection failed ({exception.GetType().Name})."); }
            finally { if (!_isClosed) inspect.IsEnabled = true; }
        };
        toolbar.Children.Add(inspect);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        DockPanel.SetDock(_operationText, Dock.Top);
        root.Children.Add(_operationText);
        DockPanel.SetDock(_refreshPanel, Dock.Top);
        root.Children.Add(_refreshPanel);
        _summary.Margin = new Thickness(0, 8, 0, 4);
        _summary.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);

        _statusText.Margin = new Thickness(0, 0, 0, 6);
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.Foreground = Brushes.DimGray;
        DockPanel.SetDock(_statusText, Dock.Top);
        root.Children.Add(_statusText);

        _list.MouseDoubleClick += async (_, args) =>
        {
            var item = ItemsControl.ContainerFromElement(_list, args.OriginalSource as DependencyObject) as ListViewItem;
            if (item?.Content is UnifiedInventoryRow clicked && ReferenceEquals(_list.SelectedItem, clicked))
                await ConnectSelectedAsync(clicked);
        };
        _list.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            args.Handled = true;
            await ConnectSelectedAsync();
        };
        _list.SelectionChanged += (_, _) => UpdateConnectEnabled();
        _list.View = BuildColumns();
        root.Children.Add(_list);

        Content = root;
        Closed += (_, _) => _isClosed = true;
    }

    private static GridView BuildColumns()
    {
        var view = new GridView();
        view.Columns.Add(Column("Account", nameof(UnifiedInventoryRow.AccountBadge), 140));
        view.Columns.Add(Column("Status", nameof(UnifiedInventoryRow.Status), 90));
        view.Columns.Add(Column("Name", nameof(UnifiedInventoryRow.Name), 240));
        view.Columns.Add(Column("Device Name", nameof(UnifiedInventoryRow.DeviceName), 190));
        view.Columns.Add(Column("Group", nameof(UnifiedInventoryRow.Group), 150));
        view.Columns.Add(Column("Notes", nameof(UnifiedInventoryRow.Notes), 200));
        return view;
    }

    private static GridViewColumn Column(string header, string path, double width)
    {
        var binding = new System.Windows.Data.Binding(path);
        var column = new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = binding };
        return column;
    }

    /// <summary>Confirms host intent before asking the console to open its connection flow.</summary>
    public Task<bool> AskConnectionIntentAsync(string accountName)
    {
        var dialog = new Window
        {
            Title = "Request connection",
            Owner = this,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = $"Account: {accountName}",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        });
        body.Children.Add(new TextBlock
        {
            Text = "Request connection in Business app. Availability will be checked after the console opens its connection chooser.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var request = new Button { Content = "Request connection in Business app", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        request.Click += (_, _) => dialog.DialogResult = true;
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(request);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);
        dialog.Content = body;
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    /// <summary>
    /// Presents only the options in the console's observed chooser. Returns the chosen
    /// option key, or null when the user cancels.
    /// </summary>
    public Task<string?> AskConnectChoiceAsync(string accountName, ChooserProbe probe)
    {
        var dialog = new Window
        {
            Title = "Connect to this computer",
            Owner = this,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = probe.Heading ?? ConnectionChooser.HeadingText,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        body.Children.Add(new TextBlock
        {
            Text = $"Account: {accountName}",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(new TextBlock
        {
            Text = "These are the options exposed by the console's current connection chooser.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (probe.NativeAvailable)
        {
            var native = new Button { Content = ConnectionChooser.NativeLabel, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
            native.Click += (_, _) => { result = ConnectionChooser.NativeKey; dialog.DialogResult = true; };
            buttons.Children.Add(native);
        }

        if (probe.WebAvailable)
        {
            var web = new Button { Content = ConnectionChooser.WebLabel, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
            web.Click += (_, _) => { result = ConnectionChooser.WebKey; dialog.DialogResult = true; };
            buttons.Children.Add(web);
        }

        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => { result = null; dialog.DialogResult = false; };
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);
        dialog.Content = body;
        dialog.ShowDialog();
        return Task.FromResult(result);
    }

    /// <summary>Shows the read-only console survey so a mismatch can be reported precisely.</summary>
    private async Task ShowInspectionAsync()
    {
        var report = await _inspectConsoles();
        var dialog = new Window
        {
            Title = "Console inspection (read-only)",
            Owner = this,
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var box = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(8)
        };
        dialog.Content = box;
        dialog.ShowDialog();
    }

    /// <summary>Shows a short status line, e.g. the result of a connect request.</summary>
    public void Status(string message)
    {
        _operationText.Text = message;
    }

    private static bool IsOnlineLike(string status) =>
        status.Equals("Online", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Connected", StringComparison.OrdinalIgnoreCase);

    public void Rebuild()
    {
        var merged = new List<UnifiedInventoryRow>();
        var notes = new List<string>();
        foreach (var snapshot in _store.All.OrderBy(snapshot => snapshot.AccountName, StringComparer.OrdinalIgnoreCase))
        {
            var current = snapshot.CanConnectUsingCurrentRowPositions;
            for (var index = 0; index < snapshot.Rows.Count; index++)
            {
                var row = snapshot.Rows[index];
                var rowStatus = row.Status ?? string.Empty;
                if (!current && rowStatus.Length > 0)
                {
                    var prefix = snapshot.IsCached
                        ? "Cached"
                        : snapshot.LatestAttemptOutcome is not null
                            ? "Last read"
                            : snapshot.Outcome == InventoryOutcome.Incomplete
                                ? "Partial read"
                                : "Historical";
                    rowStatus = $"{prefix}: {rowStatus}";
                }

                merged.Add(new UnifiedInventoryRow(
                    snapshot.AccountId,
                    snapshot.AccountName,
                    index,
                    row.Name,
                    rowStatus,
                    row.DeviceName ?? string.Empty,
                    row.Group ?? string.Empty,
                    row.Notes ?? string.Empty,
                    row.HasConnectControl,
                    current));
            }

            var freshness = snapshot.CapturedAtUtc == DateTimeOffset.MinValue
                ? "not read yet"
                : snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var statusReported = current ? snapshot.Rows.Count(row => row.Status is { Length: > 0 }) : 0;
            var online = current ? snapshot.Rows.Count(row => row.Status is { } status && IsOnlineLike(status)) : 0;
            var outcomeText = snapshot.Outcome.ToString().ToLowerInvariant();
            var freshnessText = snapshot.IsCached
                ? snapshot.LatestAttemptOutcome is { } cachedFailure
                    ? $"cached (not live), latest attempt {cachedFailure.ToString().ToLowerInvariant()}, retained {outcomeText} snapshot"
                    : $"cached (not live), retained {outcomeText} snapshot"
                : snapshot.LatestAttemptOutcome is { } failedAttempt
                    ? $"latest attempt {failedAttempt.ToString().ToLowerInvariant()}, retaining {outcomeText} snapshot"
                    : snapshot.Outcome == InventoryOutcome.Incomplete
                        ? "partial live read"
                        : outcomeText;
            notes.Add($"{snapshot.AccountName}: {freshnessText}, {snapshot.Rows.Count} row(s)" +
                      (current
                          ? statusReported > 0 ? $", {online} online of {statusReported} reporting status" : ", status not reported by this list"
                          : ", row status is cached/last-read and not current") +
                      (snapshot.ReportedTotal is { } total ? $", console reports {total}" : string.Empty) +
                      $", read {freshness}" +
                      (snapshot.LatestAttemptAtUtc is { } attemptAt
                          ? $", last attempt {attemptAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                          : string.Empty) +
                      ((snapshot.LatestAttemptDiagnostic ?? snapshot.Diagnostic) is { Length: > 0 } diagnostic ? $" — {diagnostic}" : string.Empty));
        }

        _rows = merged;
        var currentRows = merged.Where(row => row.IsCurrent).ToList();
        var allReported = currentRows.Count(row => row.Status.Length > 0);
        var allOnline = currentRows.Count(row => IsOnlineLike(row.Status));
        _summary.Text = $"Merged {merged.Count} row(s) from {_store.All.Count} account(s)." +
                        (allReported > 0
                            ? $" {allOnline} online of {allReported} row(s) reporting status in current complete reads."
                            : " No current complete read reported row status; cached/partial values are marked as historical.") +
                        " Rows are never merged by name; the console list exposes no numeric identity.";
        _statusText.Text = string.Join(Environment.NewLine, notes);
        ApplyFilter();
        UpdateConnectEnabled();
    }

    private void ApplyFilter()
    {
        var search = _search.Text.Trim();
        _list.ItemsSource = string.IsNullOrEmpty(search)
            ? _rows
            : _rows.Where(row => Contains(row.Name, search) || Contains(row.Status, search) ||
                                 Contains(row.DeviceName, search) || Contains(row.Group, search) ||
                                 Contains(row.Notes, search) || Contains(row.AccountBadge, search)).ToList();
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private async Task ConnectSelectedAsync(UnifiedInventoryRow? clickedRow = null)
    {
        if (_isClosed) return;
        var selected = clickedRow ?? _list.SelectedItem as UnifiedInventoryRow;
        if (selected is null)
        {
            ReportStatus("Select a row to connect.");
            return;
        }

        if (clickedRow is not null && !ReferenceEquals(_list.SelectedItem, clickedRow))
        {
            return;
        }

        if (!selected.CanConnect)
        {
            ReportStatus("Refresh this account's console before connecting; cached, partial, or stale row positions cannot be used.");
            return;
        }

        if (!IsCurrentRowMatch(selected))
        {
            UpdateConnectEnabled();
            ReportStatus("The current complete console read no longer matches this row. Refresh the list before connecting.");
            return;
        }

        try
        {
            await _connectRow(selected);
        }
        catch (Exception exception)
        {
            if (!_isClosed) ReportStatus($"Connect failed ({exception.GetType().Name}); refresh before retrying.");
        }
    }

    private void ReportStatus(string message)
    {
        if (_isClosed) return;
        try { _status(message); }
        catch (Exception) { }
    }

    private void UpdateConnectEnabled() =>
        _connectButton.IsEnabled = _list.SelectedItem is UnifiedInventoryRow selected &&
            selected.CanConnect && IsCurrentRowMatch(selected);

    private bool IsCurrentRowMatch(UnifiedInventoryRow row)
    {
        var current = _store.GetOrNotYetRead(row.AccountId, row.AccountBadge);
        return current.CanConnectUsingCurrentRowPositions &&
            row.RowIndexInAccount >= 0 && row.RowIndexInAccount < current.Rows.Count &&
            Matches(current.Rows[row.RowIndexInAccount], row);
    }

    private static bool Matches(ExtractedComputerRow source, UnifiedInventoryRow row) =>
        string.Equals(source.Name, row.Name, StringComparison.Ordinal) &&
        string.Equals(source.Status ?? string.Empty, row.Status, StringComparison.Ordinal) &&
        string.Equals(source.DeviceName ?? string.Empty, row.DeviceName, StringComparison.Ordinal) &&
        string.Equals(source.Group ?? string.Empty, row.Group, StringComparison.Ordinal) &&
        string.Equals(source.Notes ?? string.Empty, row.Notes, StringComparison.Ordinal) &&
        source.HasConnectControl == row.HasConnectControl;
}
