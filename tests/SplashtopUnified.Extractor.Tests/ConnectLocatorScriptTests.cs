using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

public sealed class ConnectLocatorScriptTests
{
    [Fact]
    public void ConnectScriptDoesNotTreatInventoryIndexAsRenderedDomIndex()
    {
        var row = new ExtractedComputerRow("Later host", "later-device", "Lab", "target", true, "Online");
        var script = ConsoleInventoryActions.ActivateConnectScript(
            row, "https://my.splashtop.com/computers", 1234, "range:3-4 of 8", 40000, 9999999999999);

        Assert.DoesNotContain("rowIndex", script, StringComparison.Ordinal);
        Assert.Contains("expected.name", script, StringComparison.Ordinal);
        Assert.Contains("ambiguous-row", script, StringComparison.Ordinal);
        Assert.Contains("targetCount", ConnectLocatorScript.CreateStep(
            "read", row, "https://my.splashtop.com/computers", 1234, null, null, null, 9999999999999), StringComparison.Ordinal);
    }

    [Fact]
    public void LocatorStepsAreSynchronousBoundedAndPinnedToExactDocumentAndIdentity()
    {
        var row = new ExtractedComputerRow("Later host", "later-device", "Lab", "target", true, "Online");
        var script = ConnectLocatorScript.CreateStep(
            "click", row, "https://my.splashtop.com/computers", 1234, "range:3-4 of 8", 40000, null, 9999999999999);

        Assert.DoesNotContain("async function", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Promise", script, StringComparison.Ordinal);
        Assert.Contains("location.href !== request.expectedUrl", script, StringComparison.Ordinal);
        Assert.Contains("request.documentToken !== documentToken", script, StringComparison.Ordinal);
        Assert.Contains("Date.now() >= request.deadlineEpochMilliseconds", script, StringComparison.Ordinal);
        Assert.Contains("expected.status", script, StringComparison.Ordinal);
        Assert.Contains("expectedPosition", script, StringComparison.Ordinal);
        Assert.Contains("my.splashtop.eu", script, StringComparison.Ordinal);
    }
}
