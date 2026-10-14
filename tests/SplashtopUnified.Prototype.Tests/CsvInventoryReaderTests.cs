using SplashtopUnified.Core;
using SplashtopUnified.Prototype;
using Xunit;

namespace SplashtopUnified.Prototype.Tests;

public sealed class CsvInventoryReaderTests
{
    [Fact]
    public void ReadsShortHeaderAliasesAndMapsCoreInventoryFields()
    {
        const string csv = "Name,ID,MAC,Group,Status\r\nWorkstation,42,00:11:22:33:44:55,Support,Online\r\n";

        var item = Assert.Single(CsvInventoryReader.Read(csv, "account-a"));

        Assert.Equal("account-a", item.Computer.AccountId);
        Assert.Equal(42L, item.Computer.SplashtopComputerId);
        Assert.Equal("Workstation", item.Computer.Name);
        Assert.Equal("00:11:22:33:44:55", item.Computer.MacAddress);
        Assert.Equal("Support", item.Computer.GroupName);
        Assert.Equal(ComputerStatus.Online, item.Computer.Status);
    }

    [Fact]
    public void ReadsLongHeaderAliasesWithoutAssumingHeaderPositionOrCase()
    {
        const string csv = "status,Computer ID,unmapped,MAC Address,Computer Name,Group\r\nOffline,7,x,aa-bb,Desk 7,Field Ops\r\n";

        var item = Assert.Single(CsvInventoryReader.Read(csv, "account-b"));

        Assert.Equal("Desk 7", item.Computer.Name);
        Assert.Equal(7L, item.Computer.SplashtopComputerId);
        Assert.Equal("aa-bb", item.Computer.MacAddress);
        Assert.Equal("Field Ops", item.Computer.GroupName);
        Assert.Equal(ComputerStatus.Offline, item.Computer.Status);
    }

    [Fact]
    public void ReadsBomAndMatchesHeadingsCaseInsensitively()
    {
        const string csv = "\uFEFFcOmPuTeR nAmE,computer id\r\nMini PC,19\r\n";

        var item = Assert.Single(CsvInventoryReader.Read(csv, "account-c"));

        Assert.Equal("Mini PC", item.Computer.Name);
        Assert.Equal(19L, item.Computer.SplashtopComputerId);
    }

    [Fact]
    public void ParsesQuotedCommasNewlinesAndEscapedQuotes()
    {
        const string csv = "Name,Group,MAC\r\n\"Node, \"\"North\"\"\r\nUnit\",\"Ops,\r\nTier 2\",\"aa,bb\"\r\n";

        var item = Assert.Single(CsvInventoryReader.Read(csv, "account-d"));

        Assert.Equal("Node, \"North\"\r\nUnit", item.Computer.Name);
        Assert.Equal("Ops,\r\nTier 2", item.Computer.GroupName);
        Assert.Equal("aa,bb", item.Computer.MacAddress);
    }

    [Fact]
    public void LeavesComputerIdNullWhenIdColumnIsAbsent()
    {
        const string csv = "Name,Group\r\nUnidentified host,Lab\r\n";

        var item = Assert.Single(CsvInventoryReader.Read(csv, "account-e"));

        Assert.Null(item.Computer.SplashtopComputerId);
        Assert.Null(item.Computer.Identity);
    }

    [Fact]
    public void LeavesComputerIdNullWhenIdCellIsEmpty()
    {
        const string csv = "Name,ID\r\nUnidentified host,\r\n";

        var item = Assert.Single(CsvInventoryReader.Read(csv, "account-f"));

        Assert.Null(item.Computer.SplashtopComputerId);
    }

    [Fact]
    public void RejectsCsvWithoutNameHeaderWithActionableMessage()
    {
        var error = Assert.Throws<FormatException>(() => CsvInventoryReader.Read("ID,Group\r\n1,Lab\r\n", "account-g"));

        Assert.Contains("Name", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Computer Name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMalformedRowWidthWithRecordNumber()
    {
        var error = Assert.Throws<FormatException>(() => CsvInventoryReader.Read("Name,ID\r\nDesk,1,extra\r\n", "account-h"));

        Assert.Contains("record 2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", error.Message);
        Assert.Contains("3", error.Message);
    }

    [Theory]
    [InlineData("Name,ID\r\nDesk,not-a-number\r\n")]
    [InlineData("Name,ID\r\nDesk,9223372036854775808\r\n")]
    public void RejectsInvalidNumericIdWithRecordNumber(string csv)
    {
        var error = Assert.Throws<FormatException>(() => CsvInventoryReader.Read(csv, "account-i"));

        Assert.Contains("ID", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("record 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Name,Group\r\nDesk,\"unfinished\r\n")]
    [InlineData("Name,Group\r\nDesk,bad\"quote\r\n")]
    [InlineData("Name,Group\r\nDesk,\"closed\"tail\r\n")]
    public void RejectsMalformedCsvQuotingWithRecordNumber(string csv)
    {
        var error = Assert.Throws<FormatException>(() => CsvInventoryReader.Read(csv, "account-j"));

        Assert.Contains("record 2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CSV", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsBlankAccountId()
    {
        Assert.Throws<ArgumentException>(() => CsvInventoryReader.Read("Name\r\nDesk\r\n", " "));
    }

    [Fact]
    public void IgnoresUnknownStatusValuesRatherThanInventingAnEnumValue()
    {
        var item = Assert.Single(CsvInventoryReader.Read("Name,Status\r\nDesk,Connected\r\n", "account-k"));

        Assert.Equal(ComputerStatus.Unknown, item.Computer.Status);
    }
}
