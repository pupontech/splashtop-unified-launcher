using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

public sealed class InventoryActionGuardTests
{
    [Fact]
    public async Task ReorderedRowWhilePromptIsPendingNeverClicksTheNewRowAtTheOldIndex()
    {
        var rowA = new ExtractedComputerRow("Alpha", "device-a", "Group", "A", true, "Online");
        var rowB = new ExtractedComputerRow("Beta", "device-b", "Group", "B", true, "Online");
        var target = new InventoryActionTarget("account-a", 0, "https://my.splashtop.com/computers", 7, rowA);
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicks = 0;
        var currentRows = new[] { rowA, rowB };
        var continuation = Task.Run(async () =>
        {
            await prompt.Task;
            if (ConsoleInventoryActionGuard.IsCurrentTarget(target, "account-a", 0,
                    "https://my.splashtop.com/computers", 7, currentRows[0]))
            {
                clicks++;
            }
        });

        currentRows = new[] { rowB, rowA };
        prompt.SetResult();
        await continuation;

        Assert.Equal(0, clicks);
    }

    [Fact]
    public void TargetRequiresTheOriginalDocumentGenerationAndAllVisibleIdentityFields()
    {
        var original = new ExtractedComputerRow("Alpha", "device-a", "Group", "A", true, "Online");
        var target = new InventoryActionTarget("account-a", 2, "https://my.splashtop.com/computers", 11, original);

        Assert.True(ConsoleInventoryActionGuard.IsCurrentTarget(target, "account-a", 2,
            "https://my.splashtop.com/computers", 11, original));
        Assert.False(ConsoleInventoryActionGuard.IsCurrentTarget(target, "account-a", 2,
            "https://my.splashtop.com/computers", 12, original));
        Assert.False(ConsoleInventoryActionGuard.IsCurrentTarget(target, "account-a", 2,
            "https://my.splashtop.com/computers?filter=changed", 11, original));
        Assert.False(ConsoleInventoryActionGuard.IsCurrentTarget(target, "account-a", 2,
            "https://my.splashtop.com/computers", 11,
            original with { Notes = "different notes" }));
    }
}
