using System.ComponentModel;
using System.Runtime.CompilerServices;
using SplashtopUnified.Core;

namespace SplashtopUnified.App;

internal sealed record AccountProfile(string AccountId, string Name, string? Email, string ConsoleUrl);

internal sealed class ApplicationState
{
    public List<AccountProfile> Accounts { get; } = [];
    public List<InventoryItem> Items { get; } = [];
}

internal sealed class InventoryRow : INotifyPropertyChanged
{
    private bool _isFavorite;
    private string _alias;

    public InventoryRow(InventoryItem item, string accountBadge)
    {
        Item = item;
        AccountBadge = accountBadge;
        _isFavorite = item.LocalMetadata?.IsFavorite == true;
        _alias = item.LocalMetadata?.Alias ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public InventoryItem Item { get; private set; }
    public void ReplaceItem(InventoryItem item) => Item = item;
    public string AccountBadge { get; }
    public string Name => Item.Computer.Name ?? "(unnamed)";
    public string Hostname => Item.Computer.Hostname ?? "";
    public string GroupName => Item.Computer.GroupName ?? "";
    public string Status => Item.Computer.Status?.ToString() ?? "Not provided";
    public string OperatingSystem => Item.Computer.OperatingSystem ?? "";

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    public string Alias
    {
        get => _alias;
        set
        {
            if (_alias == value)
            {
                return;
            }

            _alias = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
