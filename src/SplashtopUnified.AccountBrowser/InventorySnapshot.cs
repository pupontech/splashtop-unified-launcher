namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// A single extracted computer row. Only fields the console list actually renders are
/// carried; nothing is fabricated. There is deliberately no assumed numeric identity,
/// MAC, status or timestamp because the list view does not expose one.
/// </summary>
internal sealed record ExtractedComputerRow(
    string Name,
    string? DeviceName,
    string? Group,
    string? Notes,
    bool HasConnectControl);

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
    string? Diagnostic)
{
    public static AccountInventorySnapshot NotYetRead(string accountId, string accountName) =>
        new(accountId, accountName, InventoryOutcome.Unavailable, ConsolePageKind.Unknown,
            ConsoleAuthentication.Unknown, 0, null, [], DateTimeOffset.MinValue, null);

    public bool HasData => Outcome != InventoryOutcome.Unavailable && Rows.Count > 0;
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

        // Never downgrade a complete/partial read to an unavailable one.
        if (existing.HasData && incoming.Outcome == InventoryOutcome.Unavailable)
        {
            return false;
        }

        _snapshots[incoming.AccountId] = incoming;
        return true;
    }
}
