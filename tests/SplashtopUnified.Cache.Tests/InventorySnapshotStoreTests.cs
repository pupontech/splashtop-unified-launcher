using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Cache.Tests;

public sealed class InventorySnapshotStoreTests
{
    [Fact]
    public void IncompleteAttemptDoesNotReplaceLastCompleteRowsOrCaptureTime()
    {
        var store = new InventorySnapshotStore();
        var captured = DateTimeOffset.UtcNow.AddMinutes(-5);
        var complete = Snapshot(InventoryOutcome.Complete, captured, [new ExtractedComputerRow("Last good", null, null, null, true)]);
        var incomplete = Snapshot(InventoryOutcome.Incomplete, DateTimeOffset.UtcNow, [new ExtractedComputerRow("Partial", null, null, null, true)]) with
        {
            AccountName = "Renamed account"
        };
        store.Apply(complete);

        store.Apply(incomplete);

        var stored = store.GetOrNotYetRead("account-1", "Renamed account");
        Assert.Equal(InventoryOutcome.Complete, stored.Outcome);
        Assert.Equal(captured, stored.CapturedAtUtc);
        Assert.Equal("Last good", Assert.Single(stored.Rows).Name);
        Assert.Equal(InventoryOutcome.Incomplete, stored.LatestAttemptOutcome);
        Assert.False(stored.IsCached);
        Assert.Equal(incomplete.CapturedAtUtc, stored.LatestAttemptAtUtc);
        Assert.Equal("Renamed account", stored.AccountName);
    }

    [Fact]
    public void UnavailableAttemptAfterCompleteIsVisibleWithoutDiscardingRows()
    {
        var store = new InventorySnapshotStore();
        var captured = DateTimeOffset.UtcNow.AddMinutes(-5);
        var complete = Snapshot(InventoryOutcome.Complete, captured, [new ExtractedComputerRow("Last good", null, null, null, true)]);
        var unavailable = Snapshot(InventoryOutcome.Unavailable, DateTimeOffset.UtcNow, []);
        store.Apply(complete);

        var changed = store.Apply(unavailable);

        var stored = store.GetOrNotYetRead("account-1", "Account label");
        Assert.True(changed);
        Assert.Equal(InventoryOutcome.Complete, stored.Outcome);
        Assert.Equal(captured, stored.CapturedAtUtc);
        Assert.Equal("Last good", Assert.Single(stored.Rows).Name);
        Assert.Equal(InventoryOutcome.Unavailable, stored.LatestAttemptOutcome);
        Assert.Equal(unavailable.CapturedAtUtc, stored.LatestAttemptAtUtc);
    }

    [Fact]
    public void EmptyCompleteSnapshotCannotBeDowngradedByUnavailableAttempt()
    {
        var store = new InventorySnapshotStore();
        var captured = DateTimeOffset.UtcNow.AddMinutes(-5);
        store.Apply(Snapshot(InventoryOutcome.Complete, captured, []));

        store.Apply(Snapshot(InventoryOutcome.Unavailable, DateTimeOffset.UtcNow, []));

        var stored = store.GetOrNotYetRead("account-1", "Account label");
        Assert.Equal(InventoryOutcome.Complete, stored.Outcome);
        Assert.Equal(captured, stored.CapturedAtUtc);
        Assert.Empty(stored.Rows);
        Assert.Equal(InventoryOutcome.Unavailable, stored.LatestAttemptOutcome);
    }

    [Fact]
    public void CachedOrFailedReadsCannotAuthorizeActionsFromStoredRowPositions()
    {
        var store = new InventorySnapshotStore();
        var captured = DateTimeOffset.UtcNow.AddSeconds(-1);
        var cached = Snapshot(InventoryOutcome.Complete, captured, [new ExtractedComputerRow("Last good", null, null, null, true)]) with
        {
            IsCached = true
        };
        store.Apply(cached);
        var stored = store.GetOrNotYetRead("account-1", "Account label");
        Assert.False(stored.CanConnectUsingCurrentRowPositions);

        store.Apply(Snapshot(InventoryOutcome.Complete, DateTimeOffset.UtcNow, [new ExtractedComputerRow("Fresh", null, null, null, true)]));
        stored = store.GetOrNotYetRead("account-1", "Account label");
        Assert.True(stored.CanConnectUsingCurrentRowPositions);

        store.Apply(Snapshot(InventoryOutcome.Incomplete, DateTimeOffset.UtcNow.AddSeconds(1), []));
        stored = store.GetOrNotYetRead("account-1", "Account label");
        Assert.False(stored.CanConnectUsingCurrentRowPositions);
    }

    [Fact]
    public void ExpiredCompleteReadCannotAuthorizeActions()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var expired = Snapshot(InventoryOutcome.Complete, now - TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1),
            [new ExtractedComputerRow("Expired", null, null, null, true)]);

        Assert.False(expired.CanConnectUsingCurrentRowPositionsAt(now));
    }

    [Fact]
    public void FutureCompleteReadCannotAuthorizeActions()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var future = Snapshot(InventoryOutcome.Complete, now + TimeSpan.FromTicks(1),
            [new ExtractedComputerRow("Future", null, null, null, true)]);

        Assert.False(future.CanConnectUsingCurrentRowPositionsAt(now));
    }

    [Fact]
    public void CompleteReadAtFreshnessBoundaryIsEligible()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var atBoundary = Snapshot(InventoryOutcome.Complete, now - TimeSpan.FromMinutes(5),
            [new ExtractedComputerRow("Boundary", null, null, null, true)]);

        Assert.True(atBoundary.CanConnectUsingCurrentRowPositionsAt(now));
    }

    [Fact]
    public void CompleteReadCapturedExactlyNowIsEligible()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var current = Snapshot(InventoryOutcome.Complete, now,
            [new ExtractedComputerRow("Current", null, null, null, true)]);

        Assert.True(current.CanConnectUsingCurrentRowPositionsAt(now));
    }

    [Fact]
    public void ActionEligibilityRequiresAuthenticatedComputerListAndNoFailedAttempt()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var current = Snapshot(InventoryOutcome.Complete, now,
            [new ExtractedComputerRow("Current", null, null, null, true)]);

        Assert.True(current.CanConnectUsingCurrentRowPositionsAt(now));
        Assert.False((current with { Authentication = ConsoleAuthentication.Required })
            .CanConnectUsingCurrentRowPositionsAt(now));
        Assert.False((current with { PageKind = ConsolePageKind.Login })
            .CanConnectUsingCurrentRowPositionsAt(now));
        Assert.False((current with
        {
            LatestAttemptOutcome = InventoryOutcome.Unavailable,
            LatestAttemptAtUtc = now
        }).CanConnectUsingCurrentRowPositionsAt(now));
    }

    [Fact]
    public void OlderSourceResultCannotReplaceANewerAccountSnapshot()
    {
        var store = new InventorySnapshotStore();
        var captured = DateTimeOffset.UtcNow;
        store.Apply(Snapshot(InventoryOutcome.Complete, captured, [new ExtractedComputerRow("Newer", null, null, null, true)]));

        store.Apply(Snapshot(InventoryOutcome.Complete, captured - TimeSpan.FromMinutes(1), [new ExtractedComputerRow("Older", null, null, null, true)]));

        var stored = store.GetOrNotYetRead("account-1", "Account label");
        Assert.Equal(captured, stored.CapturedAtUtc);
        Assert.Equal("Newer", Assert.Single(stored.Rows).Name);
    }

    private static AccountInventorySnapshot Snapshot(
        InventoryOutcome outcome,
        DateTimeOffset captured,
        IReadOnlyList<ExtractedComputerRow> rows) => new(
            "account-1",
            "Account label",
            outcome,
            outcome == InventoryOutcome.Unavailable ? ConsolePageKind.Unknown : ConsolePageKind.ComputerList,
            ConsoleAuthentication.Authenticated,
            rows.Count,
            outcome == InventoryOutcome.Unavailable ? null : rows.Count,
            rows,
            captured,
            outcome == InventoryOutcome.Unavailable ? "page unavailable" : null);
}
