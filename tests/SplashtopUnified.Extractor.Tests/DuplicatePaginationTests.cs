using System.Text.Json;
using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

public sealed class DuplicatePaginationTests
{
    [Fact]
    public void MissingTotalScrollWithIndistinguishableRowsCannotBeComplete()
    {
        var payload = JsonSerializer.Serialize(new
        {
            source = ConsoleInventoryExtractor.MessageSource,
            version = ConsoleInventoryExtractor.ScriptVersion,
            requestId = "fixture-request",
            outcome = "complete",
            pageKind = "computerList",
            authentication = "authenticated",
            rowCount = 1,
            reportedTotal = (int?)null,
            rows = new[] { new { name = "Identical device", deviceName = "same", group = "Group", notes = "", hasConnectControl = true, status = (string?)null } },
            mode = "scrolled",
            walkedToEnd = true,
            pagesVisited = 3,
            walkMillis = 120,
            ambiguousDuplicates = true
        });

        Assert.True(ConsoleInventoryExtractor.TryParse(payload, out var read));
        Assert.NotNull(read);
        Assert.True(read!.HasAmbiguousDuplicates);
        Assert.Equal(InventoryOutcome.Incomplete, read.Outcome);
    }

    [Fact]
    public void NoTotalScrollWalkCanCompleteOnlyAfterReachingEndWithoutAmbiguousRows()
    {
        var payload = JsonSerializer.Serialize(new
        {
            source = ConsoleInventoryExtractor.MessageSource,
            version = ConsoleInventoryExtractor.ScriptVersion,
            requestId = "fixture-request",
            outcome = "complete",
            pageKind = "computerList",
            authentication = "authenticated",
            rowCount = 1,
            reportedTotal = (int?)null,
            rows = new[] { new { name = "Unique fixture", deviceName = "fixture-unique", group = "Synthetic", notes = "", hasConnectControl = true, status = (string?)null } },
            mode = "scrolled",
            walkedToEnd = true,
            pagesVisited = 1,
            walkMillis = 120,
            ambiguousDuplicates = false
        });

        Assert.True(ConsoleInventoryExtractor.TryParse(payload, out var read));
        Assert.Equal(InventoryOutcome.Complete, read!.Outcome);
        Assert.False(read.HasAmbiguousDuplicates);

        var notAtEnd = payload.Replace("\"walkedToEnd\":true", "\"walkedToEnd\":false", StringComparison.Ordinal);
        Assert.True(ConsoleInventoryExtractor.TryParse(notAtEnd, out var incomplete));
        Assert.Equal(InventoryOutcome.Incomplete, incomplete!.Outcome);

        var noAmbiguitySignal = payload.Replace(",\"ambiguousDuplicates\":false", string.Empty, StringComparison.Ordinal);
        Assert.True(ConsoleInventoryExtractor.TryParse(noAmbiguitySignal, out var unproven));
        Assert.Equal(InventoryOutcome.Incomplete, unproven!.Outcome);
    }

    [Fact]
    public void MissingTotalPagerWalkPreservesRowsThatAreIdenticalAcrossPages()
    {
        var repeated = new { name = "Identical device", deviceName = "same", group = "Group", notes = "same", hasConnectControl = true, status = (string?)null };
        var payload = JsonSerializer.Serialize(new
        {
            source = ConsoleInventoryExtractor.MessageSource,
            version = ConsoleInventoryExtractor.ScriptVersion,
            requestId = "fixture-request",
            outcome = "complete",
            pageKind = "computerList",
            authentication = "authenticated",
            rowCount = 2,
            reportedTotal = (int?)null,
            rows = new[] { repeated, repeated },
            mode = "paged",
            walkedToEnd = true,
            pagesVisited = 2,
            walkMillis = 120,
            ambiguousDuplicates = false
        });

        Assert.True(ConsoleInventoryExtractor.TryParse(payload, out var read));
        Assert.Equal(2, read!.Rows.Count);
        Assert.Equal(InventoryOutcome.Complete, read.Outcome);
        Assert.False(read.HasAmbiguousDuplicates);
    }
}
