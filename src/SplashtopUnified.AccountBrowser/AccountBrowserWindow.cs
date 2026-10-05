using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SplashtopUnified.AccountBrowser;

internal sealed class AccountBrowserWindow : Window
{
    private readonly AccountStore _store;
    private readonly List<AccountProfile> _profiles;
    private readonly List<AccountWebViewPane> _panes = [];
    private bool _closed;

    public AccountBrowserWindow(AccountStore store, List<AccountProfile> profiles)
    {
        _store = store;
        _profiles = profiles;
        Title = "Splashtop · two live accounts · isolated logins";
        Width = 1500; Height = 950; MinWidth = 900; MinHeight = 600;
        var root = new DockPanel();
        var notice = new TextBlock {
            Text = "Sign in separately in each official console. Both lists populate live; no CSV needed. Browser logins are isolated and retained. This is a split-console prototype, not a merged native inventory.",
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(12), Background = Brushes.AliceBlue
        };
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);
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
        var pane = new AccountWebViewPane(message => { if (!_closed) status.Text = message; });
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
