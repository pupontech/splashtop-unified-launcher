using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

internal sealed record InventoryRefreshProgress(int RowsRead, int? Total)
{
    public int? Percentage => Total is > 0
        ? (int)Math.Min(99L, (long)RowsRead * 100 / Total.Value)
        : null;

    public static bool TryParse(string json, string requestId, out InventoryRefreshProgress? progress)
    {
        progress = null;
        if (json.Length > 2048) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var allowed = new HashSet<string> { "source", "version", "requestId", "rowsRead", "total" };
            var seen = new HashSet<string>();
            foreach (var field in root.EnumerateObject())
                if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) return false;
            if (root.GetProperty("source").GetString() != "splashtop-inventory-progress" ||
                root.GetProperty("version").GetInt32() != 1 ||
                root.GetProperty("requestId").GetString() != requestId) return false;
            var rows = root.GetProperty("rowsRead").GetInt32();
            var totalElement = root.GetProperty("total");
            int? total = totalElement.ValueKind == JsonValueKind.Null ? null : totalElement.GetInt32();
            if (rows < 0 || rows > 5000 || total < 0 || (total is { } declaredTotal && rows > declaredTotal)) return false;
            progress = new(rows, total);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
