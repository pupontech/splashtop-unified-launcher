namespace SplashtopUnified.AccountBrowser;

internal enum InventoryRefreshKind
{
    Complete,
    Partial,
    Unavailable,
    Failed,
    ReloadRequired
}

/// <summary>Partial recognized inventory is useful data, not a failed request.</summary>
internal static class InventoryRefreshClassification
{
    public static InventoryRefreshKind Classify(InventoryOutcome? outcome, ConsolePageKind? pageKind, bool reloadRequired)
    {
        if (reloadRequired) return InventoryRefreshKind.ReloadRequired;
        if (outcome is null) return InventoryRefreshKind.Failed;
        if (pageKind != ConsolePageKind.ComputerList || outcome == InventoryOutcome.Unavailable)
            return InventoryRefreshKind.Unavailable;
        return outcome switch
        {
            InventoryOutcome.Complete => InventoryRefreshKind.Complete,
            InventoryOutcome.Incomplete => InventoryRefreshKind.Partial,
            _ => InventoryRefreshKind.Failed
        };
    }
}
