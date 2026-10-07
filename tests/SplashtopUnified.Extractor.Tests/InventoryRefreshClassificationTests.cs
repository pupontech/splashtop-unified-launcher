using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

public sealed class InventoryRefreshClassificationTests
{
    [Theory]
    [InlineData("Incomplete", "ComputerList", false, "Partial")]
    [InlineData("Complete", "ComputerList", false, "Complete")]
    [InlineData("Unavailable", "Login", false, "Unavailable")]
    [InlineData(null, null, false, "Failed")]
    [InlineData("Incomplete", "ComputerList", true, "ReloadRequired")]
    [InlineData("Complete", "Unknown", false, "Unavailable")]
    public void ClassifiesReadOutcomeWithoutCallingPartialRowsFailed(
        string? outcome, string? pageKind, bool reloadRequired, string expected)
    {
        var result = InventoryRefreshClassification.Classify(
            outcome is null ? null : Enum.Parse<InventoryOutcome>(outcome),
            pageKind is null ? null : Enum.Parse<ConsolePageKind>(pageKind), reloadRequired);
        Assert.Equal(expected, result.ToString());
    }
}
