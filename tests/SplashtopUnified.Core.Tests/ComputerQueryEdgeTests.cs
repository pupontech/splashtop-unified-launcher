using Xunit;

namespace SplashtopUnified.Core.Tests;

public sealed class ComputerQueryEdgeTests
{
    private static readonly DateTimeOffset AsOfUtc = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AccountFilterDoesNotMatchNullAccountIds()
    {
        var items = new[]
        {
            Item(1, "Null account", accountId: null),
            Item(2, "Matching account", accountId: "account-a"),
            Item(3, "Other account", accountId: "account-b")
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            AccountIds = new HashSet<string>(["account-a"])
        });

        Assert.Equal([2L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void GroupFilterDoesNotMatchNullGroupNames()
    {
        var items = new[]
        {
            Item(1, "Null group", groupName: null),
            Item(2, "Matching group", groupName: "Support"),
            Item(3, "Other group", groupName: "Engineering")
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            GroupNames = new HashSet<string>(["Support"])
        });

        Assert.Equal([2L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void StatusFilterDoesNotMatchNullStatuses()
    {
        var items = new[]
        {
            Item(1, "Null status", status: null),
            Item(2, "Matching status", status: ComputerStatus.Offline),
            Item(3, "Other status", status: ComputerStatus.Online)
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            Statuses = new HashSet<ComputerStatus>([ComputerStatus.Offline])
        });

        Assert.Equal([2L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void EmptyAccountGroupAndStatusSetsMatchNoItems()
    {
        var items = new[] { Item(1, "Computer") };

        Assert.Empty(ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            AccountIds = new HashSet<string>()
        }));
        Assert.Empty(ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            GroupNames = new HashSet<string>()
        }));
        Assert.Empty(ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            Statuses = new HashSet<ComputerStatus>()
        }));
    }

    [Fact]
    public void BlankSearchTextDoesNotFilterItems()
    {
        var items = new[]
        {
            Item(1, "Zulu"),
            Item(2, "alpha"),
            Item(3, "Middle")
        };
        var expectedIds = ComputerQuery.Execute(items)
            .Select(item => item.Computer.SplashtopComputerId);

        foreach (var blankSearchText in new string?[] { null, "", "   ", "\t\r\n" })
        {
            var resultIds = ComputerQuery.Execute(items, new ComputerQueryOptions
            {
                SearchText = blankSearchText
            }).Select(item => item.Computer.SplashtopComputerId);

            Assert.Equal(expectedIds, resultIds);
        }
    }

    [Fact]
    public void FixedOfflineCutoffRetainsExactCutoffAndRemovesOnlyOlderItems()
    {
        var cutoff = AsOfUtc - TimeSpan.FromDays(30);
        var items = new[]
        {
            Item(1, "Exact cutoff", status: ComputerStatus.Offline, lastOnlineUtc: cutoff),
            Item(2, "One tick older", status: ComputerStatus.Offline, lastOnlineUtc: cutoff.AddTicks(-1)),
            Item(3, "Old but online", status: ComputerStatus.Online, lastOnlineUtc: cutoff.AddDays(-1)),
            Item(4, "Unknown last online", status: ComputerStatus.Offline, lastOnlineUtc: null)
        };

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            HideOfflineOlderThan = TimeSpan.FromDays(30),
            AsOfUtc = AsOfUtc
        });

        Assert.Equal([1L, 3L, 4L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Theory]
    [InlineData(ComputerSortField.Name, false)]
    [InlineData(ComputerSortField.Name, true)]
    [InlineData(ComputerSortField.Account, false)]
    [InlineData(ComputerSortField.Account, true)]
    [InlineData(ComputerSortField.Group, false)]
    [InlineData(ComputerSortField.Group, true)]
    [InlineData(ComputerSortField.Status, false)]
    [InlineData(ComputerSortField.Status, true)]
    [InlineData(ComputerSortField.LastOnline, false)]
    [InlineData(ComputerSortField.LastOnline, true)]
    public void NullableSortKeysStayLastInBothDirections(ComputerSortField sortBy, bool descending)
    {
        var items = new[]
        {
            Item(1, "Same", accountId: "alpha", groupName: "alpha", status: ComputerStatus.Online,
                lastOnlineUtc: AsOfUtc.AddDays(-2)),
            Item(2, "Same", accountId: "Zulu", groupName: "Zulu", status: ComputerStatus.Offline,
                lastOnlineUtc: AsOfUtc.AddDays(-1)),
            Item(3, "Same", accountId: null, groupName: null, status: null, lastOnlineUtc: null)
        };
        if (sortBy == ComputerSortField.Name)
        {
            items[0] = Item(1, "alpha", accountId: "same", groupName: "same", status: ComputerStatus.Online,
                lastOnlineUtc: AsOfUtc.AddDays(-2));
            items[1] = Item(2, "Zulu", accountId: "same", groupName: "same", status: ComputerStatus.Offline,
                lastOnlineUtc: AsOfUtc.AddDays(-1));
            items[2] = Item(3, null, accountId: "same", groupName: "same", status: ComputerStatus.Busy,
                lastOnlineUtc: AsOfUtc);
        }

        var result = ComputerQuery.Execute(items, new ComputerQueryOptions
        {
            SortBy = sortBy,
            SortDescending = descending
        });

        Assert.Equal(
            descending ? new long?[] { 2, 1, 3 } : new long?[] { 1, 2, 3 },
            result.Select(item => item.Computer.SplashtopComputerId));
    }

    private static InventoryItem Item(
        long id,
        string? name,
        string? accountId = "account-a",
        string? groupName = null,
        ComputerStatus? status = null,
        DateTimeOffset? lastOnlineUtc = null) =>
        new(new Computer(accountId, id, name, GroupName: groupName, Status: status, LastOnlineUtc: lastOnlineUtc));
}
