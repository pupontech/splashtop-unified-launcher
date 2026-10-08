using Xunit;

namespace SplashtopUnified.Core.Tests;

public sealed class ComputerMergeEdgeTests
{
    [Fact]
    public void Compose_PreservesSameMacAndComputerIdAcrossAccountsInInputOrder()
    {
        const string macAddress = "AA:BB:CC:DD:EE:FF";
        var first = new InventoryItem(new Computer("account-a", 42, "First", MacAddress: macAddress));
        var second = new InventoryItem(new Computer("account-b", 42, "Second", MacAddress: macAddress));

        var result = ComputerMerge.Compose([first], [second]);

        Assert.Equal(2, result.Count);
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
        Assert.Equal(
            new ComputerIdentity?[] { new("account-a", 42), new("account-b", 42) },
            result.Select(item => item.Computer.Identity));
    }

    [Fact]
    public void Compose_RejectsNullInventoryArray()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ComputerMerge.Compose((IEnumerable<InventoryItem>[])null!));
    }

    [Fact]
    public void Compose_RejectsNullAccountInventory()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ComputerMerge.Compose((IEnumerable<InventoryItem>)null!));
    }

    [Fact]
    public void Compose_RejectsNullInventoryItem()
    {
        IEnumerable<InventoryItem> inventory = new InventoryItem[] { null! };

        Assert.Throws<ArgumentNullException>(() => ComputerMerge.Compose(inventory));
    }

    [Fact]
    public void Compose_RejectsDuplicateCompositeIdentityWithinOneInventory()
    {
        var first = new InventoryItem(new Computer("account-a", 42, "First"));
        var second = new InventoryItem(new Computer("account-a", 42, "Second"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ComputerMerge.Compose([first, second]));

        Assert.Equal("Duplicate computer identity found.", exception.Message);
    }
}
