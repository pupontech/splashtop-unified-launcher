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
    bool HasConnectControl);

/// <summary>
/// Native merged list over every configured account's most recent extraction.
/// Connect is routed back to the owning account's own WebView row.
/// </summary>
internal sealed class UnifiedInventoryWindow : Window
{
    private readonly InventorySnapshotStore _store;
    private readonly Func<Task> _refreshAll;
    private readonly Func<string, int, Task> _connectRow;
    private readonly Func<Task<string>> _inspectConsoles;
    private readonly Action<string> _status;
    private readonly ListView _list = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _statusText = new();
    private List<UnifiedInventoryRow> _rows = [];

    public UnifiedInventoryWindow(
        InventorySnapshotStore store,
        Func<Task> refreshAll,
        Func<string, int, Task> connectRow,
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
        refresh.Click += async (_, _) => { refresh.IsEnabled = false; try { await _refreshAll(); } finally { refresh.IsEnabled = true; } };
        toolbar.Children.Add(refresh);
        var connect = new Button { Content = "Connect", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        connect.Click += async (_, _) => await ConnectSelectedAsync();
        toolbar.Children.Add(connect);
        var inspect = new Button { Content = "Inspect consoles", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Read-only survey of each console page, used to diagnose a list that does not match the expected columns." };
        inspect.Click += async (_, _) => { inspect.IsEnabled = false; try { await ShowInspectionAsync(); } finally { inspect.IsEnabled = true; } };
        toolbar.Children.Add(inspect);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        _summary.Margin = new Thickness(0, 8, 0, 4);
        _summary.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);

        _statusText.Margin = new Thickness(0, 0, 0, 6);
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.Foreground = Brushes.DimGray;
        DockPanel.SetDock(_statusText, Dock.Top);
        root.Children.Add(_statusText);

        _list.MouseDoubleClick += async (_, _) => await ConnectSelectedAsync();
        _list.View = BuildColumns();
        root.Children.Add(_list);

        Content = root;
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

    /// <summary>
    /// Presents the console's connect choice as a native prompt in this window. Returns the
    /// chosen option key, or null when the user cancels.
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
            Text = "Choose how to open this session. The console behind this window is left untouched until you choose.",
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
        _statusText.Text = message;
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
            for (var index = 0; index < snapshot.Rows.Count; index++)
            {
                var row = snapshot.Rows[index];
                merged.Add(new UnifiedInventoryRow(
                    snapshot.AccountId,
                    snapshot.AccountName,
                    index,
                    row.Name,
                    row.Status ?? string.Empty,
                    row.DeviceName ?? string.Empty,
                    row.Group ?? string.Empty,
                    row.Notes ?? string.Empty,
                    row.HasConnectControl));
            }

            var freshness = snapshot.CapturedAtUtc == DateTimeOffset.MinValue
                ? "not read yet"
                : snapshot.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");
            var statusReported = snapshot.Rows.Count(row => row.Status is { Length: > 0 });
            var online = snapshot.Rows.Count(row => row.Status is { } status && IsOnlineLike(status));
            notes.Add($"{snapshot.AccountName}: {snapshot.Outcome.ToString().ToLowerInvariant()}, {snapshot.Rows.Count} row(s)" +
                      (statusReported > 0 ? $", {online} online of {statusReported} reporting status" : ", status not reported by this list") +
                      (snapshot.ReportedTotal is { } total ? $", console reports {total}" : string.Empty) +
                      $", read {freshness}" +
                      (snapshot.Diagnostic is { Length: > 0 } diagnostic ? $" — {diagnostic}" : string.Empty));
        }

        _rows = merged;
        var allReported = merged.Count(row => row.Status.Length > 0);
        var allOnline = merged.Count(row => IsOnlineLike(row.Status));
        _summary.Text = $"Merged {merged.Count} row(s) from {_store.All.Count} account(s)." +
                        (allReported > 0
                            ? $" {allOnline} online of {allReported} row(s) reporting status."
                            : " No row reported a status, so the Status column is blank rather than guessed.") +
                        " Rows are never merged by name; the console list exposes no numeric identity.";
        _statusText.Text = string.Join(Environment.NewLine, notes);
        ApplyFilter();
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

    private async Task ConnectSelectedAsync()
    {
        if (_list.SelectedItem is not UnifiedInventoryRow selected)
        {
            _status("Select a row to connect.");
            return;
        }

        await _connectRow(selected.AccountId, selected.RowIndexInAccount);
    }
}
