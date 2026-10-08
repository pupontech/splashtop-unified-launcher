using Xunit;

namespace SplashtopUnified.Core.Tests;

public sealed class ComputerQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SearchesNameAliasAndTagsWithoutCaseSensitivity()
    {
        var items = new[]
        {
            Item(1, name: "North Server"),
            Item(2, name: "Workshop PC", alias: "Billing station"),
            Item(3, name: "Spare", tags: ["Critical", "Finance"]),
            Item(4, name: "Unrelated")
        };

        Assert.Single(ComputerQuery.Execute(items, new ComputerQueryOptions { SearchText = "SERVER" }));
        Assert.Equal(2, ComputerQuery.Execute(items, new ComputerQueryOptions { SearchText = "billing" }).Single().Computer.SplashtopComputerId);
        Assert.Equal(3, ComputerQuery.Execute(items, new ComputerQueryOptions { SearchText = "finance" }).Single().Computer.SplashtopComputerId);
    }

    [Fact]
    public void AppliesAccountGroupStatusAndFavoriteFiltersTogether()
    {
        var items = new[]
        {
            Item(1, "Support PC", accountId: "account-a", groupName: "Support", status: ComputerStatus.Online, favorite: true),
            Item(2, "Support PC", accountId: "account-b", groupName: "Support", status: ComputerStatus.Online, favorite: true),
            Item(3, "Support PC", accountId: "account-a", groupName: "Engineering", status: ComputerStatus.Online, favorite: true),
            Item(4, "Support PC", accountId: "account-a", groupName: "Support", status: ComputerStatus.Offline, favorite: true),
            Item(5, "Support PC", accountId: "account-a", groupName: "Support", status: ComputerStatus.Online, favorite: false)
        };
        var options = new ComputerQueryOptions
        {
            AccountIds = new HashSet<string>(["account-a"]),
            GroupNames = new HashSet<string>(["support"]),
            Statuses = new HashSet<ComputerStatus>([ComputerStatus.Online]),
            FavoritesOnly = true
        };

        var result = ComputerQuery.Execute(items, options);

        Assert.Equal([1L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void SortsByNameAndLeavesUnknownNamesLastByDefault()
    {
        var items = new[]
        {
            Item(1, "Zulu"),
            Item(2, null),
            Item(3, "alpha")
        };

        var result = ComputerQuery.Execute(items);

        Assert.Equal([3L, 1L, 2L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void CanSortByLastOnlineDescending()
    {
        var items = new[]
        {
            Item(1, "Older", lastOnlineUtc: Now.AddDays(-5)),
            Item(2, "Newer", lastOnlineUtc: Now.AddDays(-1)),
            Item(3, "Unknown", lastOnlineUtc: null)
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            SortBy = ComputerSortField.LastOnline,
            SortDescending = true
        });

        Assert.Equal([2L, 1L, 3L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void SortsByNameDescendingWithNullsLastAndStableTies()
    {
        var items = new[]
        {
            Item(1, "alpha"),
            Item(2, null),
            Item(3, "Zulu"),
            Item(4, "ALPHA")
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions { SortDescending = true });

        Assert.Equal([3L, 1L, 4L, 2L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void SortsByAccountBothDirectionsWithNullsLastAndStableTies()
    {
        var items = new[]
        {
            Item(1, "One", accountId: "account-b"),
            Item(2, "Two", accountId: "account-a"),
            Item(3, "Three", accountId: "ACCOUNT-A"),
            new InventoryItem(new Computer(null, 4, "No account"))
        };

        var ascending = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.Account });
        var descending = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.Account, SortDescending = true });

        Assert.Equal([2L, 3L, 1L, 4L], ascending.Select(item => item.Computer.SplashtopComputerId));
        Assert.Equal([1L, 2L, 3L, 4L], descending.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void SortsByGroupBothDirectionsWithNullsLastAndStableTies()
    {
        var items = new[]
        {
            Item(1, "One", groupName: "Zulu"),
            Item(2, "Two", groupName: "alpha"),
            Item(3, "Three", groupName: "ALPHA"),
            Item(4, "No group")
        };

        var ascending = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.Group });
        var descending = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.Group, SortDescending = true });

        Assert.Equal([2L, 3L, 1L, 4L], ascending.Select(item => item.Computer.SplashtopComputerId));
        Assert.Equal([1L, 2L, 3L, 4L], descending.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void SortsByStatusBothDirectionsWithNullsLastAndStableTies()
    {
        var items = new[]
        {
            Item(1, "Busy", status: ComputerStatus.Busy),
            Item(2, "Unknown", status: null),
            Item(3, "Offline one", status: ComputerStatus.Offline),
            Item(4, "Online", status: ComputerStatus.Online),
            Item(5, "Offline two", status: ComputerStatus.Offline)
        };

        var ascending = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.Status });
        var descending = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.Status, SortDescending = true });

        Assert.Equal([4L, 3L, 5L, 1L, 2L], ascending.Select(item => item.Computer.SplashtopComputerId));
        Assert.Equal([1L, 3L, 5L, 4L, 2L], descending.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void SortsByLastOnlineAscendingWithNullsLastAndStableTies()
    {
        var tied = Now.AddDays(-2);
        var items = new[]
        {
            Item(1, "Later", lastOnlineUtc: Now.AddDays(-1)),
            Item(2, "Unknown", lastOnlineUtc: null),
            Item(3, "Tied one", lastOnlineUtc: tied),
            Item(4, "Tied two", lastOnlineUtc: tied)
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions { SortBy = ComputerSortField.LastOnline });

        Assert.Equal([3L, 4L, 1L, 2L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void PreservesDuplicateNamesWithinAndAcrossAccounts()
    {
        var items = new[]
        {
            Item(1, "Duplicate", accountId: "account-a"),
            Item(2, "Duplicate", accountId: "account-a"),
            Item(1, "Duplicate", accountId: "account-b")
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions { SearchText = "duplicate" });

        Assert.Equal(
            new ComputerIdentity?[]
            {
                new("account-a", 1),
                new("account-a", 2),
                new("account-b", 1)
            },
            result.Select(item => item.Computer.Identity));
    }

    [Fact]
    public void OldOfflineFilterIsOptInAndKeepsRecentUnknownAndOnlineRecords()
    {
        var old = Item(1, "Old offline", status: ComputerStatus.Offline, lastOnlineUtc: Now.AddDays(-100));
        var recent = Item(2, "Recent offline", status: ComputerStatus.Offline, lastOnlineUtc: Now.AddDays(-4));
        var unknown = Item(3, "Unknown last seen", status: ComputerStatus.Offline, lastOnlineUtc: null);
        var online = Item(4, "Online", status: ComputerStatus.Online, lastOnlineUtc: Now.AddDays(-100));
        var items = new[] { old, recent, unknown, online };

        Assert.Equal([1L, 4L, 2L, 3L], ComputerQuery.Execute(items).Select(item => item.Computer.SplashtopComputerId));

        var filtered = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            HideOfflineOlderThan = TimeSpan.FromDays(30),
            AsOfUtc = Now
        });

        Assert.Equal([4L, 2L, 3L], filtered.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void NegativeOfflineAgeThreshold_ThrowsArgumentOutOfRangeException()
    {
        var options = new ComputerQueryOptions { HideOfflineOlderThan = TimeSpan.FromDays(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => ComputerQuery.Execute([], options));
    }

    private static InventoryItem Item(
        long id,
        string? name,
        string accountId = "account-a",
        string? groupName = null,
        ComputerStatus? status = null,
        DateTimeOffset? lastOnlineUtc = null,
        bool favorite = false,
        string? alias = null,
        IEnumerable<string>? tags = null) =>
        new(
            new Computer(accountId, id, name, GroupName: groupName, Status: status, LastOnlineUtc: lastOnlineUtc),
            new LocalComputerMetadata(accountId, id, favorite, alias, tags));
}
