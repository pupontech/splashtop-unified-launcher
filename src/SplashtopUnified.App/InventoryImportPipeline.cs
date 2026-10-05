using SplashtopUnified.Core;
using SplashtopUnified.Prototype;

namespace SplashtopUnified.App;

internal sealed record InventoryImportPlan(int ImportedCount, List<InventoryItem> ReplacementItems);

/// <summary>Builds a full account replacement before the caller persists or swaps state.</summary>
internal static class InventoryImportPipeline
{
    public static InventoryImportPlan Prepare(
        IReadOnlyList<InventoryItem> currentItems,
        string accountId,
        string csv)
    {
        ArgumentNullException.ThrowIfNull(currentItems);
        var imported = CsvInventoryReader.Read(csv, accountId);
        if (imported.Count == 0)
        {
            return new InventoryImportPlan(0, currentItems.ToList());
        }

        var duplicate = imported
            .Where(item => item.Computer.SplashtopComputerId.HasValue)
            .GroupBy(item => item.Computer.SplashtopComputerId!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException($"The CSV contains duplicate computer ID {duplicate.Key} for this account. Resolve the duplicate before importing.");
        }

        var accountIdentities = currentItems
            .Where(item => item.Computer.Identity is { } identity &&
                string.Equals(identity.AccountId, accountId, StringComparison.Ordinal))
            .GroupBy(item => item.Computer.Identity!.Value)
            .ToDictionary(group => group.Key, group => group.First().LocalMetadata);

        var replacement = currentItems
            .Where(item => !string.Equals(item.Computer.AccountId, accountId, StringComparison.Ordinal))
            .ToList();
        foreach (var item in imported)
        {
            LocalComputerMetadata? metadata = null;
            if (item.Computer.Identity is { } identity)
            {
                accountIdentities.TryGetValue(identity, out var previous);
                metadata = new LocalComputerMetadata(
                    identity.AccountId,
                    identity.SplashtopComputerId,
                    previous?.IsFavorite ?? false,
                    previous?.Alias,
                    previous?.Tags);
            }

            replacement.Add(new InventoryItem(item.Computer, metadata));
        }

        return new InventoryImportPlan(imported.Count, replacement);
    }
}
