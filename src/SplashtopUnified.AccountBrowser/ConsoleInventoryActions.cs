namespace SplashtopUnified.AccountBrowser;

/// <summary>An immutable action target captured before any user prompt can yield.</summary>
internal sealed record InventoryActionTarget(
    string AccountId,
    int RowIndex,
    string DocumentSource,
    int Generation,
    ExtractedComputerRow Row);

/// <summary>Checks that a captured row still refers to the same account/document/read.</summary>
internal static class ConsoleInventoryActionGuard
{
    public static bool IsCurrentTarget(
        InventoryActionTarget target,
        string accountId,
        int rowIndex,
        string documentSource,
        int generation,
        ExtractedComputerRow currentRow) =>
        string.Equals(target.AccountId, accountId, StringComparison.Ordinal) &&
        target.RowIndex == rowIndex &&
        string.Equals(target.DocumentSource, documentSource, StringComparison.Ordinal) &&
        target.Generation == generation &&
        target.Row == currentRow;
}

/// <summary>Builds the final synchronous page-side click with exact identity revalidation.</summary>
internal static class ConsoleInventoryActions
{
    /// <summary>Legacy smoke seam: direct script-only calls cannot authorize a host-owned walk.</summary>
    public static string ActivateConnectScript(int rowIndex, ExtractedComputerRow expectedRow)
    {
        ArgumentNullException.ThrowIfNull(expectedRow);
        if (rowIndex < 0) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        return "(function(){try{if(window.top!==window.self)return 'blocked-origin';" +
            "var u=new URL(location.href),h=u.hostname.toLowerCase();" +
            "if(u.protocol!=='https:'||u.port!==''||u.username!==''||u.password!==''||" +
            "(h!=='my.splashtop.com'&&h!=='my.splashtop.eu'))return 'blocked-origin';" +
            "return 'host-coordination-required';}catch(_){return 'blocked-origin';}})();";
    }

    public static string ActivateConnectScript(
        ExtractedComputerRow expectedRow,
        string expectedUrl,
        double documentToken,
        string? pageToken,
        long? expectedPosition,
        long deadlineEpochMilliseconds) =>
        ConnectLocatorScript.CreateStep("click", expectedRow, expectedUrl, documentToken,
            pageToken, expectedPosition, null, deadlineEpochMilliseconds);
}
