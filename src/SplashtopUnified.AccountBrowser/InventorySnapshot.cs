namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// A single extracted computer row. Only fields the console list actually renders are
/// carried; nothing is fabricated. There is deliberately no assumed numeric identity and
/// no invented timestamp because the list view does not expose one. <see cref="Status"/>
/// is null whenever the row shows no presence indicator, and is never inferred from a
/// device name.
/// </summary>
internal sealed record ExtractedComputerRow(
    string Name,
    string? DeviceName,
    string? Group,
    string? Notes,
    bool HasConnectControl,
    string? Status = null);

internal enum InventoryOutcome
{
    Unavailable,
    Incomplete,
    Complete
}

internal enum ConsolePageKind
{
    Unknown,
    ComputerList,
    Login
}

internal enum ConsoleAuthentication
{
    Unknown,
    Authenticated,
    Required
}

/// <summary>One account's most recent extraction result plus its freshness.</summary>
internal sealed record AccountInventorySnapshot(
    string AccountId,
    string AccountName,
    InventoryOutcome Outcome,
    ConsolePageKind PageKind,
    ConsoleAuthentication Authentication,
    int RowCount,
    int? ReportedTotal,
    IReadOnlyList<ExtractedComputerRow> Rows,
    DateTimeOffset CapturedAtUtc,
    string? Diagnostic,
    int PagesVisited = 0,
    int WalkMillis = 0,
    string? Mode = null,
    // Additive local cache/UI metadata; the console pane may keep publishing the existing fields.
    bool IsCached = false,
    InventoryOutcome? LatestAttemptOutcome = null,
    string? LatestAttemptDiagnostic = null,
    DateTimeOffset? LatestAttemptAtUtc = null)
{
    private static readonly TimeSpan DefaultActionFreshnessWindow = TimeSpan.FromMinutes(5);

    public static AccountInventorySnapshot NotYetRead(string accountId, string accountName) =>
        new(accountId, accountName, InventoryOutcome.Unavailable, ConsolePageKind.Unknown,
            ConsoleAuthentication.Unknown, 0, null, [], DateTimeOffset.MinValue, null);

    public bool HasData => Outcome != InventoryOutcome.Unavailable && Rows.Count > 0;

    /// <summary>Stored row indexes may drive an action only after a fresh complete source read.</summary>
    public bool CanConnectUsingCurrentRowPositions =>
        CanConnectUsingCurrentRowPositionsAt(DateTimeOffset.UtcNow);

    /// <summary>
    /// Tests whether the snapshot is eligible to authorize actions at an explicit time.
    /// The default freshness window is five minutes; the exact age boundary is eligible.
    /// </summary>
    public bool CanConnectUsingCurrentRowPositionsAt(DateTimeOffset nowUtc, TimeSpan? maximumAge = null)
    {
        var freshnessWindow = maximumAge ?? DefaultActionFreshnessWindow;
        if (freshnessWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        nowUtc = nowUtc.ToUniversalTime();
        return !IsCached && LatestAttemptOutcome is null &&
            Outcome == InventoryOutcome.Complete &&
            PageKind == ConsolePageKind.ComputerList &&
            Authentication == ConsoleAuthentication.Authenticated &&
            CapturedAtUtc <= nowUtc &&
            nowUtc - CapturedAtUtc <= freshnessWindow;
    }
}

/// <summary>
/// Holds the latest per-account snapshot. A snapshot is only replaced when the new read
/// is at least as trustworthy as the old one, so a transient failure never erases a
/// previously working list.
/// </summary>
internal sealed class InventorySnapshotStore
{
    private readonly Dictionary<string, AccountInventorySnapshot> _snapshots = new(StringComparer.Ordinal);

    public IReadOnlyCollection<AccountInventorySnapshot> All => _snapshots.Values;

    public AccountInventorySnapshot GetOrNotYetRead(string accountId, string accountName) =>
        _snapshots.TryGetValue(accountId, out var snapshot) ? snapshot : AccountInventorySnapshot.NotYetRead(accountId, accountName);

    /// <summary>Returns true when the stored snapshot changed.</summary>
    public bool Apply(AccountInventorySnapshot incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (!_snapshots.TryGetValue(incoming.AccountId, out var existing))
        {
            _snapshots[incoming.AccountId] = incoming;
            return true;
        }

        var latestStoredAt = existing.LatestAttemptAtUtc ?? existing.CapturedAtUtc;
        if (incoming.CapturedAtUtc < latestStoredAt)
        {
            return false;
        }

        // Keep the strongest rows and original capture time when a later attempt is worse.
        // The attempt metadata changes so the UI can report the failure without presenting
        // the retained status values as a fresh read.
        if (Quality(incoming.Outcome) < Quality(existing.Outcome))
        {
            var retained = existing with
            {
                AccountName = incoming.AccountName,
                LatestAttemptOutcome = incoming.Outcome,
                LatestAttemptDiagnostic = incoming.Diagnostic,
                LatestAttemptAtUtc = incoming.CapturedAtUtc
            };
            if (retained == existing)
            {
                return false;
            }

            _snapshots[incoming.AccountId] = retained;
            return true;
        }

        _snapshots[incoming.AccountId] = incoming with
        {
            IsCached = false,
            LatestAttemptOutcome = null,
            LatestAttemptDiagnostic = null,
            LatestAttemptAtUtc = null
        };
        return true;
    }

    private static int Quality(InventoryOutcome outcome) => outcome switch
    {
        InventoryOutcome.Complete => 2,
        InventoryOutcome.Incomplete => 1,
        InventoryOutcome.Unavailable => 0,
        _ => -1
    };
}
