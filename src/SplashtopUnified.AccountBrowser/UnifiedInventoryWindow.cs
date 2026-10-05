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
        Action<string> status)
    {
        _store = store;
        _refreshAll = refreshAll;
        _connectRow = connectRow;
        _status = status;

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
        refresh.Click += async (_, _) => { refresh.IsEnabled = false; try { await _refreshAll(); Rebuild(); } finally { refresh.IsEnabled = true; } };
        toolbar.Children.Add(refresh);
        var connect = new Button { Content = "Connect", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        connect.Click += async (_, _) => await ConnectSelectedAsync();
        toolbar.Children.Add(connect);
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
        view.Columns.Add(Column("Account", nameof(UnifiedInventoryRow.AccountBadge), 150));
        view.Columns.Add(Column("Name", nameof(UnifiedInventoryRow.Name), 250));
        view.Columns.Add(Column("Device Name", nameof(UnifiedInventoryRow.DeviceName), 200));
        view.Columns.Add(Column("Group", nameof(UnifiedInventoryRow.Group), 150));
        view.Columns.Add(Column("Notes", nameof(UnifiedInventoryRow.Notes), 220));
        return view;
    }

    private static GridViewColumn Column(string header, string path, double width)
    {
        var binding = new System.Windows.Data.Binding(path);
        var column = new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = binding };
        return column;
    }

    /// <summary>Shows a short status line, e.g. the result of a connect request.</summary>
    public void Status(string message)
    {
        _statusText.Text = message;
    }

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
                    row.DeviceName ?? string.Empty,
                    row.Group ?? string.Empty,
                    row.Notes ?? string.Empty,
                    row.HasConnectControl));
            }

            var freshness = snapshot.CapturedAtUtc == DateTimeOffset.MinValue
                ? "not read yet"
                : snapshot.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");
            notes.Add($"{snapshot.AccountName}: {snapshot.Outcome.ToString().ToLowerInvariant()}, {snapshot.Rows.Count} row(s)" +
                      (snapshot.ReportedTotal is { } total ? $", console reports {total}" : string.Empty) +
                      $", read {freshness}" +
                      (snapshot.Diagnostic is { Length: > 0 } diagnostic ? $" — {diagnostic}" : string.Empty));
        }

        _rows = merged;
        _summary.Text = $"Merged {merged.Count} row(s) from {_store.All.Count} account(s). " +
                        "Rows are never merged by name; the console list exposes no numeric identity.";
        _statusText.Text = string.Join(Environment.NewLine, notes);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = _search.Text.Trim();
        _list.ItemsSource = string.IsNullOrEmpty(search)
            ? _rows
            : _rows.Where(row => Contains(row.Name, search) || Contains(row.DeviceName, search) ||
                                 Contains(row.Group, search) || Contains(row.Notes, search) ||
                                 Contains(row.AccountBadge, search)).ToList();
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
