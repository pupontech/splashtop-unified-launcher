namespace SplashtopUnified.Core;

public enum ComputerSortField
{
    Name,
    Account,
    Group,
    Status,
    LastOnline
}

public sealed class ComputerQueryOptions
{
    public string? SearchText { get; init; }

    public IReadOnlySet<string>? AccountIds { get; init; }

    public IReadOnlySet<string>? GroupNames { get; init; }

    public IReadOnlySet<ComputerStatus>? Statuses { get; init; }

    public bool FavoritesOnly { get; init; }

    public TimeSpan? HideOfflineOlderThan { get; init; }

    public DateTimeOffset? AsOfUtc { get; init; }

    public ComputerSortField SortBy { get; init; } = ComputerSortField.Name;

    public bool SortDescending { get; init; }
}

public static class ComputerQuery
{
    public static IReadOnlyList<InventoryItem> Execute(
        IEnumerable<InventoryItem> items,
        ComputerQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        options ??= new ComputerQueryOptions();
        if (options.HideOfflineOlderThan is { } ageThreshold && ageThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.HideOfflineOlderThan), ageThreshold, "The offline age threshold must be non-negative.");
        }

        IEnumerable<InventoryItem> query = items;

        if (!string.IsNullOrWhiteSpace(options.SearchText))
        {
            var searchText = options.SearchText.Trim();
            query = query.Where(item => MatchesSearch(item, searchText));
        }

        if (options.AccountIds is not null)
        {
            query = query.Where(item => ContainsIgnoreCase(options.AccountIds, item.Computer.AccountId));
        }

        if (options.GroupNames is not null)
        {
            query = query.Where(item => ContainsIgnoreCase(options.GroupNames, item.Computer.GroupName));
        }

        if (options.Statuses is not null)
        {
            query = query.Where(item => item.Computer.Status is { } status && options.Statuses.Contains(status));
        }

        if (options.FavoritesOnly)
        {
            query = query.Where(item => item.LocalMetadata?.IsFavorite == true);
        }

        if (options.HideOfflineOlderThan is { } age)
        {
            var cutoff = (options.AsOfUtc ?? DateTimeOffset.UtcNow) - age;
            query = query.Where(item => !IsOldOffline(item.Computer, cutoff));
        }

        return Array.AsReadOnly(Sort(query, options).ToArray());
    }

    private static bool MatchesSearch(InventoryItem item, string searchText)
    {
        if (Contains(item.Computer.Name, searchText) || Contains(item.LocalMetadata?.Alias, searchText))
        {
            return true;
        }

        return item.LocalMetadata?.Tags.Any(tag => Contains(tag, searchText)) == true;
    }

    private static bool Contains(string? value, string searchText) =>
        value?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsIgnoreCase(IReadOnlySet<string> values, string? candidate) =>
        candidate is not null && values.Any(value => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsOldOffline(Computer computer, DateTimeOffset cutoff) =>
        computer.Status == ComputerStatus.Offline &&
        computer.LastOnlineUtc is { } lastOnlineUtc &&
        lastOnlineUtc < cutoff;

    private static IOrderedEnumerable<InventoryItem> Sort(
        IEnumerable<InventoryItem> items,
        ComputerQueryOptions options)
    {
        return options.SortBy switch
        {
            ComputerSortField.Account => SortNullableString(items, item => item.Computer.AccountId, options.SortDescending),
            ComputerSortField.Group => SortNullableString(items, item => item.Computer.GroupName, options.SortDescending),
            ComputerSortField.Status => SortNullableStatus(items, options.SortDescending),
            ComputerSortField.LastOnline => SortNullableDate(items, item => item.Computer.LastOnlineUtc, options.SortDescending),
            _ => SortNullableString(items, item => item.Computer.Name, options.SortDescending)
        };
    }

    private static IOrderedEnumerable<InventoryItem> SortNullableString(
        IEnumerable<InventoryItem> items,
        Func<InventoryItem, string?> keySelector,
        bool descending)
    {
        var nullsLast = items.OrderBy(item => keySelector(item) is null);
        return descending
            ? nullsLast.ThenByDescending(keySelector, StringComparer.OrdinalIgnoreCase)
            : nullsLast.ThenBy(keySelector, StringComparer.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<InventoryItem> SortNullableDate(
        IEnumerable<InventoryItem> items,
        Func<InventoryItem, DateTimeOffset?> keySelector,
        bool descending)
    {
        var nullsLast = items.OrderBy(item => keySelector(item) is null);
        return descending
            ? nullsLast.ThenByDescending(keySelector)
            : nullsLast.ThenBy(keySelector);
    }

    private static IOrderedEnumerable<InventoryItem> SortNullableStatus(
        IEnumerable<InventoryItem> items,
        bool descending)
    {
        var nullsLast = items.OrderBy(item => item.Computer.Status is null);
        return descending
            ? nullsLast.ThenByDescending(item => item.Computer.Status)
            : nullsLast.ThenBy(item => item.Computer.Status);
    }
}
