using Xunit;

namespace SplashtopUnified.Core.Tests;

public sealed class ComputerMergeTests
{
    [Fact]
    public void Compose_FlattensAnyNumberOfInventoriesInInputOrderAndPreservesMetadata()
    {
        var firstMetadata = new LocalComputerMetadata("account-a", 1, isFavorite: true, alias: "Front desk", tags: ["priority"]);
        var first = new InventoryItem(new Computer("account-a", 1, "First"), firstMetadata);
        var second = new InventoryItem(new Computer("account-a", 2, "Second"));
        var thirdMetadata = new LocalComputerMetadata("account-c", 3, alias: "Third alias");
        var third = new InventoryItem(new Computer("account-c", 3, "Third"), thirdMetadata);
        var fourth = new InventoryItem(new Computer("account-d", 4, "Fourth"));

        var result = ComputerMerge.Compose(
            [first, second],
            [],
            [third],
            [fourth]);

        Assert.Equal(4, result.Count);
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
        Assert.Same(third, result[2]);
        Assert.Same(fourth, result[3]);
        var attachedFirstMetadata = Assert.IsType<LocalComputerMetadata>(result[0].LocalMetadata);
        Assert.Same(firstMetadata, attachedFirstMetadata);
        Assert.True(attachedFirstMetadata.IsFavorite);
        Assert.Equal("Front desk", attachedFirstMetadata.Alias);
        Assert.Equal(["priority"], attachedFirstMetadata.Tags);
        var attachedThirdMetadata = Assert.IsType<LocalComputerMetadata>(result[2].LocalMetadata);
        Assert.Same(thirdMetadata, attachedThirdMetadata);
        Assert.Equal("Third alias", attachedThirdMetadata.Alias);
    }

    [Fact]
    public void Compose_PreservesSameNamesWithinAndAcrossAccounts()
    {
        var first = new InventoryItem(new Computer("account-a", 1, "Shared name"));
        var second = new InventoryItem(new Computer("account-a", 2, "Shared name"));
        var third = new InventoryItem(new Computer("account-b", 1, "Shared name"));

        var result = ComputerMerge.Compose([first, second], [third]);

        Assert.Equal(3, result.Count);
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
    public void Compose_DoesNotCollapseSameMacAddressWithDifferentComputerIds()
    {
        const string mac = "AA:BB:CC:DD:EE:FF";
        var first = new InventoryItem(new Computer("account-a", 10, "Machine", MacAddress: mac));
        var second = new InventoryItem(new Computer("account-a", 11, "Machine", MacAddress: mac));

        var result = ComputerMerge.Compose([first], [second]);

        Assert.Equal(2, result.Count);
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
        Assert.Equal([10L, 11L], result.Select(item => item.Computer.SplashtopComputerId));
    }

    [Fact]
    public void Compose_DistinguishesSameNumericIdInDifferentAccounts()
    {
        var first = new InventoryItem(new Computer("account-a", 42, "First"));
        var second = new InventoryItem(new Computer("account-b", 42, "Second"));

        var result = ComputerMerge.Compose([first], [second]);

        Assert.Equal(2, result.Count);
        Assert.Equal(
            new ComputerIdentity?[] { new("account-a", 42), new("account-b", 42) },
            result.Select(item => item.Computer.Identity));
    }

    [Fact]
    public void Compose_KeepsRecordsWithUnavailableRemoteIdentityIndividuallyVisible()
    {
        var missingAccount = new InventoryItem(new Computer(null, 42, "Unidentified", MacAddress: "AA:BB:CC:DD:EE:FF"));
        var missingComputerId = new InventoryItem(new Computer("account-a", null, "Unidentified", MacAddress: "AA:BB:CC:DD:EE:FF"));
        var blankAccount = new InventoryItem(new Computer("  ", 42, "Unidentified", MacAddress: "AA:BB:CC:DD:EE:FF"));

        var result = ComputerMerge.Compose([missingAccount, missingComputerId], [blankAccount]);

        Assert.Equal(3, result.Count);
        Assert.Same(missingAccount, result[0]);
        Assert.Same(missingComputerId, result[1]);
        Assert.Same(blankAccount, result[2]);
        Assert.All(result, item => Assert.Null(item.Computer.Identity));
        Assert.All(result, item => Assert.Null(item.LocalMetadata));
    }

    [Fact]
    public void Compose_RejectsRepeatedCompleteIdentityWithoutExposingIdentifiers()
    {
        var first = new InventoryItem(new Computer("private-account-id", 987654321, "Private computer name"));
        var second = new InventoryItem(new Computer("private-account-id", 987654321, "A conflicting record"));

        var exception = Assert.Throws<InvalidOperationException>(() => ComputerMerge.Compose([first], [second]));

        Assert.Equal("Duplicate computer identity found.", exception.Message);
        Assert.DoesNotContain("private-account-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private computer name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WithNoInventoriesReturnsAnEmptyCollection()
    {
        var result = ComputerMerge.Compose();

        Assert.Empty(result);
    }
}
