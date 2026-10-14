namespace SplashtopUnified.Core;

public static class ComputerMerge
{
    public static IReadOnlyList<InventoryItem> Compose(params IEnumerable<InventoryItem>[] accountInventories)
    {
        ArgumentNullException.ThrowIfNull(accountInventories);

        var combined = new List<InventoryItem>();
        var identities = new HashSet<ComputerIdentity>();

        foreach (var accountInventory in accountInventories)
        {
            ArgumentNullException.ThrowIfNull(accountInventory);

            foreach (var item in accountInventory)
            {
                ArgumentNullException.ThrowIfNull(item);

                if (item.Computer.Identity is { } identity && !identities.Add(identity))
                {
                    throw new InvalidOperationException("Duplicate computer identity found.");
                }

                combined.Add(item);
            }
        }

        return combined.AsReadOnly();
    }
}
