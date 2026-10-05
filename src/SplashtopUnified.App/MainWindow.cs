using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SplashtopUnified.Prototype;
using SplashtopUnified.Core;

namespace SplashtopUnified.App;

internal sealed class MainWindow : Window
{
    private static readonly Brush Ink = Brush("#182230");
    private static readonly Brush Muted = Brush("#627185");
    private static readonly Brush Accent = Brush("#1463D9");
    private static readonly Brush Page = Brush("#F3F6FA");
    private static readonly Brush Card = Brush("#FFFFFF");
    private static readonly Brush Line = Brush("#DCE3EC");

    private readonly ApplicationState _state;
    private readonly LocalStateStore _store;
    private readonly ObservableCollection<InventoryRow> _visibleRows = [];
    private readonly TextBox _nameInput = new();
    private readonly TextBox _emailInput = new();
    private readonly TextBox _consoleInput = new();
    private readonly ComboBox _accountScope = new();
    private readonly ComboBox _importAccount = new();
    private readonly ComboBox _statusFilter = new();
    private readonly TextBox _searchInput = new();
    private readonly CheckBox _favoritesOnly = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _resultCount = new();
    private readonly TextBlock _statusMessage = new();
    private readonly TextBlock _detailName = new();
    private readonly TextBlock _detailText = new();
    private Button _openConsoleButton = new();
    private Button _importButton = new();
    private readonly bool _smokeTest;
    private bool _persistenceHealthy = true;

    public MainWindow(bool smokeTest)
    {
        _smokeTest = smokeTest;
        _store = new LocalStateStore();
        try
        {
            _state = _store.Load();
        }
        catch (Exception exception)
        {
            _state = new ApplicationState();
            _persistenceHealthy = false;
            SetStatus($"Local state error: {exception.Message}", isError: true);
        }

        Title = "Splashtop Unified · Prototype";
        Width = 1200;
        Height = 790;
        MinWidth = 940;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Page;
        FontFamily = new FontFamily("Segoe UI");
        BuildInterface();
        RefreshAccountChoices();
        RefreshRows();
        UpdateSelectionDetails();
        if (!_persistenceHealthy)
        {
            SetStatus("Local state could not be loaded. No changes will overwrite the existing file. Rename or repair it, then restart.", isError: true);
        }

        if (_smokeTest)
        {
            Loaded += (_, _) =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Close();
                };
                timer.Start();
            };
        }
    }

    private void BuildInterface()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var accountCard = BuildAccountCard();
        Grid.SetRow(accountCard, 1);
        root.Children.Add(accountCard);

        var content = BuildInventoryArea();
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        _statusMessage.Margin = new Thickness(2, 10, 2, 0);
        _statusMessage.FontSize = 12;
        _statusMessage.TextWrapping = TextWrapping.Wrap;
        _statusMessage.Foreground = Muted;
        Grid.SetRow(_statusMessage, 3);
        root.Children.Add(_statusMessage);
        Content = root;
    }

    private UIElement BuildHeader()
    {
        var panel = new DockPanel { Margin = new Thickness(2, 0, 2, 14), LastChildFill = true };
        var title = new TextBlock
        {
            Text = "Unified inventory",
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new Border
        {
            Background = Brush("#E9F1FF"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 3, 0, 0),
            Child = new TextBlock { Text = "PROTOTYPE", Foreground = Accent, FontSize = 10, FontWeight = FontWeights.Bold }
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        left.Children.Add(label);
        panel.Children.Add(left);
        panel.Children.Add(new TextBlock
        {
            Text = "CSV/manual snapshots · local-only favorites · no automatic sync",
            Foreground = Muted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        return panel;
    }

    private UIElement BuildAccountCard()
    {
        var card = new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 13)
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = "Add a customer account",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(intro, 0);
        Grid.SetColumnSpan(intro, 4);
        panel.Children.Add(intro);

        ConfigureInput(_nameInput, "Account name");
        ConfigureInput(_emailInput, "Email (optional)");
        ConfigureInput(_consoleInput, "Official HTTPS console URL");
        AddLabeledInput(panel, "Name", _nameInput, 1, 0);
        AddLabeledInput(panel, "Email", _emailInput, 1, 1);
        AddLabeledInput(panel, "Console URL", _consoleInput, 1, 2);
        var addButton = MakeButton("Add account", isPrimary: true);
        addButton.Margin = new Thickness(10, 22, 0, 0);
        addButton.Click += (_, _) => AddAccount();
        Grid.SetRow(addButton, 1);
        Grid.SetColumn(addButton, 3);
        panel.Children.Add(addButton);

        _accountScope.MinWidth = 190;
        _importAccount.MinWidth = 190;
        _statusFilter.MinWidth = 135;
        ConfigureCombo(_accountScope);
        ConfigureCombo(_importAccount);
        ConfigureCombo(_statusFilter);
        var bottom = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(new TextBlock
        {
            Text = "Import into",
            Foreground = Muted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _importAccount.Width = 190;
        _importAccount.Margin = new Thickness(0, 0, 10, 0);
        bottom.Children.Add(_importAccount);

        _importButton = MakeButton("Import CSV…", isPrimary: false);
        _importButton.Margin = new Thickness(0, 0, 14, 0);
        _importButton.Click += (_, _) => ImportCsv();
        bottom.Children.Add(_importButton);
        bottom.Children.Add(new TextBlock
        {
            Text = "Use a CSV exported by you. No credentials are requested or stored.",
            Foreground = Muted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(bottom, 2);
        Grid.SetColumnSpan(bottom, 4);
        panel.Children.Add(bottom);

        card.Child = panel;
        return card;
    }

    private UIElement BuildInventoryArea()
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.3, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        layout.ColumnDefinitions[1].MinWidth = 275;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var caption = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        var title = new TextBlock { Text = "Computers", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink };
        caption.Children.Add(title);
        _resultCount.Foreground = Muted;
        _resultCount.FontSize = 12;
        _resultCount.HorizontalAlignment = HorizontalAlignment.Right;
        caption.Children.Add(_resultCount);
        Grid.SetRow(caption, 0);
        left.Children.Add(caption);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 9), VerticalAlignment = VerticalAlignment.Center };
        _searchInput.Width = 230;
        _searchInput.Height = 32;
        _searchInput.Margin = new Thickness(0, 0, 8, 0);
        _searchInput.Padding = new Thickness(9, 5, 9, 4);
        _searchInput.ToolTip = "Search name, alias, host, group, notes, or account";
        _searchInput.TextChanged += (_, _) => RefreshRows();
        filters.Children.Add(_searchInput);

        _accountScope.Margin = new Thickness(0, 0, 8, 0);
        _accountScope.SelectionChanged += (_, _) => RefreshRows();
        filters.Children.Add(_accountScope);

        _statusFilter.ItemsSource = new[]
        {
            new StatusChoice("Any status", null),
            new StatusChoice("Online", ComputerStatus.Online),
            new StatusChoice("Offline", ComputerStatus.Offline),
            new StatusChoice("Busy", ComputerStatus.Busy),
            new StatusChoice("Unknown", ComputerStatus.Unknown)
        };
        _statusFilter.DisplayMemberPath = nameof(StatusChoice.Label);
        _statusFilter.SelectedIndex = 0;
        _statusFilter.SelectionChanged += (_, _) => RefreshRows();
        filters.Children.Add(_statusFilter);

        _favoritesOnly.Content = "Favorites only";
        _favoritesOnly.Margin = new Thickness(12, 6, 0, 0);
        _favoritesOnly.Foreground = Ink;
        _favoritesOnly.Checked += (_, _) => RefreshRows();
        _favoritesOnly.Unchecked += (_, _) => RefreshRows();
        filters.Children.Add(_favoritesOnly);
        Grid.SetRow(filters, 1);
        left.Children.Add(filters);

        _grid.ItemsSource = _visibleRows;
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HorizontalGridLinesBrush = Line;
        _grid.BorderBrush = Line;
        _grid.BorderThickness = new Thickness(1);
        _grid.Background = Card;
        _grid.RowBackground = Card;
        _grid.AlternatingRowBackground = Brush("#F8FAFC");
        _grid.SelectionChanged += (_, _) => UpdateSelectionDetails();
        var favoriteCellStyle = MetadataCellStyle();
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "★",
            Binding = new Binding(nameof(InventoryRow.IsFavorite)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            CellStyle = favoriteCellStyle,
            Width = 42
        });
        _grid.Columns.Add(TextColumn("Device", nameof(InventoryRow.Name), 1.2, isReadOnly: true));
        var aliasColumn = TextColumn("Alias", nameof(InventoryRow.Alias), 1.0, isReadOnly: false);
        aliasColumn.CellStyle = MetadataCellStyle();
        _grid.Columns.Add(aliasColumn);
        _grid.Columns.Add(TextColumn("Account", nameof(InventoryRow.AccountBadge), 1.0, isReadOnly: true));
        _grid.Columns.Add(TextColumn("Status", nameof(InventoryRow.Status), 0.75, isReadOnly: true));
        _grid.Columns.Add(TextColumn("Group", nameof(InventoryRow.GroupName), 0.9, isReadOnly: true));
        _grid.Columns.Add(TextColumn("Hostname", nameof(InventoryRow.Hostname), 1.0, isReadOnly: true));
        _grid.CellEditEnding += (_, args) =>
        {
            if (args.Column.Header?.ToString() == "Alias")
            {
                Dispatcher.BeginInvoke(RefreshRows, DispatcherPriority.Background);
            }
        };
        Grid.SetRow(_grid, 2);
        left.Children.Add(_grid);

        var spacer = new Border { Width = 14, Background = Brushes.Transparent };
        Grid.SetColumn(spacer, 2);
        layout.Children.Add(spacer);
        Grid.SetColumn(left, 0);
        layout.Children.Add(left);

        var detail = BuildDetailsPanel();
        Grid.SetColumn(detail, 1);
        layout.Children.Add(detail);
        return layout;
    }

    private UIElement BuildDetailsPanel()
    {
        var border = new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(16)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Selected computer",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _detailName.Text = "Select a computer";
        _detailName.TextWrapping = TextWrapping.Wrap;
        _detailName.FontSize = 20;
        _detailName.FontWeight = FontWeights.SemiBold;
        _detailName.Foreground = Ink;
        panel.Children.Add(_detailName);
        _detailText.Margin = new Thickness(0, 12, 0, 16);
        _detailText.Foreground = Muted;
        _detailText.FontSize = 12;
        _detailText.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_detailText);
        _openConsoleButton = MakeButton("Open account console", isPrimary: true);
        _openConsoleButton.IsEnabled = false;
        _openConsoleButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _openConsoleButton.Click += (_, _) => OpenSelectedConsole();
        panel.Children.Add(_openConsoleButton);
        panel.Children.Add(new TextBlock
        {
            Text = "Opens the configured Splashtop web console for this account. It does not target or connect to the selected computer.",
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        border.Child = panel;
        return border;
    }

    private void AddAccount()
    {
        var name = _nameInput.Text.Trim();
        var email = _emailInput.Text.Trim();
        var url = _consoleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Enter an account name.", isError: true);
            return;
        }

        if (!TryAllowedConsoleUrl(url, out var normalizedUrl, out var error))
        {
            SetStatus($"Enter an HTTPS URL on a Splashtop domain: {error}", isError: true);
            return;
        }

        if (!CanPersist())
        {
            return;
        }

        var account = new AccountProfile($"local-{Guid.NewGuid():N}", name, string.IsNullOrWhiteSpace(email) ? null : email, normalizedUrl);
        _state.Accounts.Add(account);
        _nameInput.Clear();
        _emailInput.Clear();
        _consoleInput.Clear();
        RefreshAccountChoices(account.AccountId);
        Persist();
        SetStatus($"Added account '{account.Name}'. Import a CSV snapshot to populate its inventory.");
    }

    private void ImportCsv()
    {
        if (_importAccount.SelectedItem is not AccountChoice { AccountId: not null } selected)
        {
            SetStatus("Add or select an account before importing a CSV.", isError: true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import a user-provided Splashtop inventory CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var plan = InventoryImportPipeline.Prepare(_state.Items, selected.AccountId, File.ReadAllText(dialog.FileName));
            if (plan.ImportedCount == 0)
            {
                SetStatus("The CSV contained no computer rows; existing inventory was left unchanged.", isError: true);
                return;
            }

            var nextState = new ApplicationState();
            nextState.Accounts.AddRange(_state.Accounts);
            nextState.Items = plan.ReplacementItems;
            if (!TryPersist(nextState))
            {
                return;
            }

            _state.Items = plan.ReplacementItems;
            RefreshRows();
            SetStatus($"Imported {plan.ImportedCount} CSV row(s) into '{selected.Label}'. This is a local snapshot; no live sync ran.");
        }
        catch (Exception exception)
        {
            SetStatus($"CSV import failed: {exception.Message}", isError: true);
        }
    }

    private void RefreshAccountChoices(string? selectedAccountId = null)
    {
        var filterSelection = _accountScope.SelectedItem as AccountChoice;
        var importSelection = _importAccount.SelectedItem as AccountChoice;
        var filterChoices = new List<AccountChoice> { new(null, "All accounts") };
        filterChoices.AddRange(_state.Accounts.Select(account => new AccountChoice(account.AccountId, account.Name)));
        _accountScope.ItemsSource = filterChoices;
        _accountScope.DisplayMemberPath = nameof(AccountChoice.Label);
        _accountScope.SelectedItem = filterChoices.FirstOrDefault(choice =>
            string.Equals(choice.AccountId, filterSelection?.AccountId, StringComparison.OrdinalIgnoreCase)) ?? filterChoices[0];

        var importChoices = _state.Accounts.Select(account => new AccountChoice(account.AccountId, account.Name)).ToList();
        _importAccount.ItemsSource = importChoices;
        _importAccount.DisplayMemberPath = nameof(AccountChoice.Label);
        var preferredId = selectedAccountId ?? importSelection?.AccountId;
        _importAccount.SelectedItem = importChoices.FirstOrDefault(choice =>
            string.Equals(choice.AccountId, preferredId, StringComparison.OrdinalIgnoreCase)) ?? importChoices.FirstOrDefault();
        _importButton.IsEnabled = importChoices.Count > 0;
    }

    private void RefreshRows()
    {
        if (_grid.ItemsSource is null)
        {
            return;
        }

        var selectedAccount = _accountScope.SelectedItem as AccountChoice;
        var statusChoice = _statusFilter.SelectedItem as StatusChoice;
        var baseQuery = ComputerQuery.Execute(_state.Items, new ComputerQueryOptions
        {
            AccountIds = selectedAccount?.AccountId is { } accountId
                ? new HashSet<string>([accountId], StringComparer.OrdinalIgnoreCase)
                : null,
            Statuses = statusChoice?.Status is { } status
                ? new HashSet<ComputerStatus> { status }
                : null,
            FavoritesOnly = _favoritesOnly.IsChecked == true
        });
        var search = _searchInput.Text.Trim();
        var matches = baseQuery.Where(item => MatchesSearch(item, search)).ToArray();
        _visibleRows.Clear();
        foreach (var item in matches)
        {
            var account = _state.Accounts.FirstOrDefault(profile =>
                string.Equals(profile.AccountId, item.Computer.AccountId, StringComparison.OrdinalIgnoreCase));
            var badge = account?.Name ?? "Unknown account";
            var row = new InventoryRow(item, badge);
            row.PropertyChanged += InventoryRowChanged;
            _visibleRows.Add(row);
        }

        _resultCount.Text = $"{matches.Length} shown · {_state.Items.Count} total";
        if (_grid.SelectedItem is not InventoryRow current || !_visibleRows.Contains(current))
        {
            _grid.SelectedItem = null;
            UpdateSelectionDetails();
        }
    }

    private bool MatchesSearch(InventoryItem item, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        var computer = item.Computer;
        var account = _state.Accounts.FirstOrDefault(profile =>
            string.Equals(profile.AccountId, computer.AccountId, StringComparison.OrdinalIgnoreCase));
        var haystack = new[]
        {
            computer.Name,
            item.LocalMetadata?.Alias,
            computer.Hostname,
            computer.GroupName,
            computer.MacAddress,
            computer.LoggedInUser,
            computer.Notes,
            computer.OperatingSystem,
            computer.TeamName,
            account?.Name,
            account?.Email
        };
        return haystack.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void InventoryRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not InventoryRow row || e.PropertyName is not (nameof(InventoryRow.IsFavorite) or nameof(InventoryRow.Alias)) ||
            row.Item.Computer.Identity is not { } identity)
        {
            return;
        }

        var index = _state.Items.FindIndex(item => item.Computer.Identity == identity);
        if (index < 0)
        {
            return;
        }

        var metadata = new LocalComputerMetadata(identity.AccountId, identity.SplashtopComputerId, row.IsFavorite,
            string.IsNullOrWhiteSpace(row.Alias) ? null : row.Alias.Trim(), row.Item.LocalMetadata?.Tags);
        var updated = new InventoryItem(row.Item.Computer, metadata);
        _state.Items[index] = updated;
        row.ReplaceItem(updated);
        Persist();
        if (_favoritesOnly.IsChecked == true && !row.IsFavorite)
        {
            Dispatcher.BeginInvoke(RefreshRows, DispatcherPriority.Background);
        }

        UpdateSelectionDetails();
    }

    private void UpdateSelectionDetails()
    {
        if (_grid.SelectedItem is not InventoryRow row)
        {
            _detailName.Text = "Select a computer";
            _detailText.Text = "Imported CSV data is kept as a local snapshot. Add aliases or mark favorites in the table.";
            _openConsoleButton.IsEnabled = false;
            return;
        }

        var computer = row.Item.Computer;
        var account = _state.Accounts.FirstOrDefault(profile =>
            string.Equals(profile.AccountId, computer.AccountId, StringComparison.OrdinalIgnoreCase));
        _detailName.Text = string.IsNullOrWhiteSpace(row.Alias) ? row.Name : row.Alias;
        _detailText.Text =
            $"Account\n{account?.Name ?? row.AccountBadge}\n\n" +
            $"Device\n{row.Name}\n\n" +
            $"Status\n{row.Status} (CSV snapshot)\n\n" +
            $"Hostname\n{(string.IsNullOrWhiteSpace(computer.Hostname) ? "Not provided" : computer.Hostname)}\n\n" +
            $"Group\n{(string.IsNullOrWhiteSpace(computer.GroupName) ? "Not provided" : computer.GroupName)}\n\n" +
            $"Operating system\n{(string.IsNullOrWhiteSpace(computer.OperatingSystem) ? "Not provided" : computer.OperatingSystem)}\n\n" +
            $"Notes\n{(string.IsNullOrWhiteSpace(computer.Notes) ? "None" : computer.Notes)}" +
            (computer.Identity is null
                ? "\n\nLocal favorite/alias\nUnavailable: this CSV row has no numeric computer ID; the app will not invent one."
                : "");
        _openConsoleButton.IsEnabled = account is not null;
        _openConsoleButton.Tag = account;
    }

    private void OpenSelectedConsole()
    {
        if (_openConsoleButton.Tag is not AccountProfile account)
        {
            SetStatus("Select a computer associated with a configured account first.", isError: true);
            return;
        }

        if (!TryAllowedConsoleUrl(account.ConsoleUrl, out var normalizedUrl, out var error))
        {
            SetStatus($"Blocked console URL for '{account.Name}': {error}", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = normalizedUrl, UseShellExecute = true });
            SetStatus($"Opened the configured web console for '{account.Name}'. No device-specific launch was attempted.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open the browser: {exception.Message}", isError: true);
        }
    }

    private void Persist() => _ = TryPersist(_state);

    private bool TryPersist(ApplicationState state)
    {
        if (!_persistenceHealthy)
        {
            SetStatus("Changes are disabled because the existing local state could not be read. Rename or repair it, then restart.", isError: true);
            return false;
        }

        try
        {
            _store.Save(state);
            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save local state at '{_store.FilePath}': {exception.Message}", isError: true);
            return false;
        }
    }

    private bool CanPersist()
    {
        if (_persistenceHealthy)
        {
            return true;
        }

        SetStatus("Changes are disabled because the existing local state could not be read. Rename or repair it, then restart.", isError: true);
        return false;
    }

    private void SetStatus(string message, bool isError = false)
    {
        _statusMessage.Text = message;
        _statusMessage.Foreground = isError ? Brush("#B42318") : Muted;
    }

    private static bool TryAllowedConsoleUrl(string value, out string normalizedUrl, out string error)
    {
        normalizedUrl = "";
        error = "URL must use HTTPS on a Splashtop-owned domain (splashtop.com or a subdomain), with no custom port or embedded credentials.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (!string.Equals(host, "splashtop.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".splashtop.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        error = "";
        return true;
    }

    private static Style MetadataCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        var trigger = new DataTrigger
        {
            Binding = new Binding(nameof(InventoryRow.SupportsLocalMetadata)),
            Value = false
        };
        trigger.Setters.Add(new Setter(IsEnabledProperty, false));
        style.Triggers.Add(trigger);
        return style;
    }

    private static DataGridTextColumn TextColumn(string header, string binding, double width, bool isReadOnly)
    {
        var column = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(binding) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            IsReadOnly = isReadOnly,
            Width = new DataGridLength(width, DataGridLengthUnitType.Star)
        };
        if (!isReadOnly)
        {
            column.EditingElementStyle = new Style(typeof(TextBox));
        }

        return column;
    }

    private static void ConfigureInput(TextBox box, string placeholder)
    {
        box.Height = 32;
        box.Padding = new Thickness(9, 5, 9, 4);
        box.Margin = new Thickness(0, 0, 12, 0);
        box.ToolTip = placeholder;
        box.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static void AddLabeledInput(Grid grid, string label, TextBox input, int row, int column)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3)
        });
        stack.Children.Add(input);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void ConfigureCombo(ComboBox combo)
    {
        combo.Height = 32;
        combo.Padding = new Thickness(6, 3, 6, 3);
        combo.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static Button MakeButton(string text, bool isPrimary)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(13, 7, 13, 7),
            MinHeight = 32,
            BorderThickness = new Thickness(1),
            BorderBrush = isPrimary ? Accent : Line,
            Background = isPrimary ? Accent : Card,
            Foreground = isPrimary ? Brushes.White : Ink,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        return button;
    }

    private static Brush Brush(string color) => new BrushConverter().ConvertFromString(color) as Brush ?? Brushes.Transparent;

    private sealed record AccountChoice(string? AccountId, string Label);
    private sealed record StatusChoice(string Label, ComputerStatus? Status);
}
