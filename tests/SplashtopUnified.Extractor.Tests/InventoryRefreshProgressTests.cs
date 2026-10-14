using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;
public sealed class InventoryRefreshProgressTests
{
    [Theory]
    [InlineData(0, 200, 0)]
    [InlineData(50, 200, 25)]
    [InlineData(200, 200, 99)]
    public void PercentReflectsCountButReservesCompletion(int rows, int total, int expected) =>
        Assert.Equal(expected, new InventoryRefreshProgress(rows, total).Percentage);

    [Fact]
    public void UnknownOrZeroTotalIsIndeterminate()
    {
        Assert.Null(new InventoryRefreshProgress(20, null).Percentage);
        Assert.Null(new InventoryRefreshProgress(0, 0).Percentage);
    }

    [Fact]
    public void CorrelatedMessageIsAcceptedButOldRequestIsRejected()
    {
        const string json = """{"source":"splashtop-inventory-progress","version":1,"requestId":"current","rowsRead":50,"total":200}""";
        Assert.True(InventoryRefreshProgress.TryParse(json, "current", out var progress));
        Assert.Equal(25, progress!.Percentage);
        Assert.False(InventoryRefreshProgress.TryParse(json, "newer", out _));
    }

    [Fact]
    public void ZeroTotalCannotReportPositiveRows()
    {
        const string json = """{"source":"splashtop-inventory-progress","version":1,"requestId":"current","rowsRead":12,"total":0}""";
        Assert.False(InventoryRefreshProgress.TryParse(json, "current", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"source\":42}")]
    public void BadPayloadNeverUpdatesProgress(string json) =>
        Assert.False(InventoryRefreshProgress.TryParse(json, "current", out _));
}
