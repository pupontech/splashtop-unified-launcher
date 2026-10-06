using System.Reflection;
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
    public void ClassifiesReadOutcomeWithoutCallingPartialRowsFailed(string? outcome, string? pageKind, bool reloadRequired, string expected)
    {
        // Resolve the new pure seam so the baseline fails an assertion for the missing
        // classification capability rather than a compiler/import error.
        var type = typeof(ConsoleInventoryExtractor).Assembly.GetType("SplashtopUnified.AccountBrowser.InventoryRefreshClassification");
        Assert.NotNull(type);
        var method = type.GetMethod("Classify", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object?[]
        {
            outcome is null ? null : Enum.Parse<InventoryOutcome>(outcome),
            pageKind is null ? null : Enum.Parse<ConsolePageKind>(pageKind),
            reloadRequired
        });
        Assert.Equal(expected, result?.ToString());
    }
}
