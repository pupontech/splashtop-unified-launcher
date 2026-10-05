using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

/// <summary>
/// Validator coverage for the console inventory extractor. Payloads here are synthetic
/// and shaped from the column labels observed in the official Splashtop support
/// screenshot (Name / Device Name / Group / Notes); they are not captured console data.
/// </summary>
public sealed class ConsoleInventoryExtractorTests
{
    [Fact]
    public void AcceptsAWellFormedComputerListRead()
    {
        var json = Message("incomplete", "computerList", "authenticated", 2, null,
            """{"name":"Fixture Desktop","deviceName":"fixture-desktop","group":"Default Group","notes":"","hasConnectControl":true}""",
            """{"name":"Fixture Server","deviceName":"fixture-server","group":"Servers","notes":"lab","hasConnectControl":false}""");

        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.NotNull(read);
        Assert.Equal(InventoryOutcome.Incomplete, read!.Outcome);
        Assert.Equal(ConsolePageKind.ComputerList, read.PageKind);
        Assert.Equal(ConsoleAuthentication.Authenticated, read.Authentication);
        Assert.Equal(2, read.RowCount);
        Assert.Equal(2, read.Rows.Count);
        Assert.True(read.Rows[0].HasConnectControl);
        Assert.False(read.Rows[1].HasConnectControl);
        Assert.Equal("Fixture Server", read.Rows[1].Name);
        Assert.Contains("no numeric identity", read.Diagnostic);
    }

    [Fact]
    public void CompleteOutcomeIsPreservedWhenTheConsoleTotalReconciles()
    {
        var json = Message("complete", "computerList", "authenticated", 1, 1,
            """{"name":"Only Fixture","deviceName":"only","group":"","notes":"","hasConnectControl":true}""");

        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(InventoryOutcome.Complete, read!.Outcome);
        Assert.Null(read.Diagnostic);
        Assert.Equal(1, read.ReportedTotal);
    }

    [Fact]
    public void AConsoleTotalBelowTheRenderedRowsIsNeverComplete()
    {
        var json = Message("complete", "computerList", "authenticated", 2, 1,
            """{"name":"Fixture A","deviceName":"a","group":"","notes":"","hasConnectControl":true}""",
            """{"name":"Fixture B","deviceName":"b","group":"","notes":"","hasConnectControl":true}""");

        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(InventoryOutcome.Incomplete, read!.Outcome);
        Assert.Equal(2, read.Rows.Count);
    }

    [Fact]
    public void DuplicateDisplayNamesAreKeptAndNeverDeduplicated()
    {
        var json = Message("incomplete", "computerList", "authenticated", 2, null,
            """{"name":"Fixture VM","deviceName":"fixture-vm","group":"VMs","notes":"","hasConnectControl":true}""",
            """{"name":"Fixture VM","deviceName":"fixture-vm-2","group":"VMs","notes":"second","hasConnectControl":true}""");

        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(2, read!.Rows.Count);
        Assert.Equal(2, read.Rows.Count(row => row.Name == "Fixture VM"));
    }

    [Fact]
    public void LoginPageIsNeverAnEmptyCompleteList()
    {
        var json = Message("unavailable", "login", "required", 0, null);

        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(InventoryOutcome.Unavailable, read!.Outcome);
        Assert.Equal(ConsolePageKind.Login, read.PageKind);
        Assert.Equal(ConsoleAuthentication.Required, read.Authentication);
        Assert.Empty(read.Rows);
    }

    [Fact]
    public void UnknownPageIsUnavailable()
    {
        var json = Message("unavailable", "unknown", "unknown", 0, null);

        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(InventoryOutcome.Unavailable, read!.Outcome);
        Assert.Equal(ConsolePageKind.Unknown, read.PageKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    public void MalformedOrEmptyPayloadsAreRejected(string? json)
    {
        Assert.False(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Null(read);
    }

    [Fact]
    public void WrongSourceIsRejected()
    {
        var json = Message("incomplete", "computerList", "authenticated", 0, null).Replace(
            ConsoleInventoryExtractor.MessageSource, "splashtop-console-inventory-evil", StringComparison.Ordinal);

        Assert.False(ConsoleInventoryExtractor.TryParse(json, out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public void WrongVersionIsRejected(string version)
    {
        var json = Message("incomplete", "computerList", "authenticated", 0, null).Replace("\"version\":1", $"\"version\":{version}", StringComparison.Ordinal);

        Assert.False(ConsoleInventoryExtractor.TryParse(json, out _));
    }

    [Fact]
    public void UnknownOutcomeOrPageKindIsRejected()
    {
        Assert.False(ConsoleInventoryExtractor.TryParse(Message("probably", "computerList", "authenticated", 0, null), out _));
        Assert.False(ConsoleInventoryExtractor.TryParse(Message("incomplete", "settings", "authenticated", 0, null), out _));
        Assert.False(ConsoleInventoryExtractor.TryParse(Message("incomplete", "computerList", "maybe", 0, null), out _));
    }

    [Fact]
    public void ARowWithoutANameIsRejected()
    {
        var json = Message("incomplete", "computerList", "authenticated", 1, null,
            """{"name":"","deviceName":"device","group":"","notes":"","hasConnectControl":true}""");

        Assert.False(ConsoleInventoryExtractor.TryParse(json, out _));

        var missing = Message("incomplete", "computerList", "authenticated", 1, null,
            """{"deviceName":"device","group":"","notes":"","hasConnectControl":true}""");
        Assert.False(ConsoleInventoryExtractor.TryParse(missing, out _));
    }

    [Fact]
    public void ControlCharactersAreRejected()
    {
        var json = Message("incomplete", "computerList", "authenticated", 1, null,
            """{"name":"Fixture\u0007Bell","deviceName":"d","group":"","notes":"","hasConnectControl":true}""");

        Assert.False(ConsoleInventoryExtractor.TryParse(json, out _));
    }

    [Fact]
    public void OversizedPayloadIsRejected()
    {
        var huge = Message("incomplete", "computerList", "authenticated", 1, null,
            "{\"name\":\"" + new string('x', ConsoleInventoryExtractor.MaxPayloadChars) + "\",\"deviceName\":\"d\",\"group\":\"\",\"notes\":\"\",\"hasConnectControl\":true}");

        Assert.True(huge.Length > ConsoleInventoryExtractor.MaxPayloadChars);
        Assert.False(ConsoleInventoryExtractor.TryParse(huge, out _));
    }

    [Fact]
    public void NegativeOrNonNumericCountsAreRejected()
    {
        Assert.False(ConsoleInventoryExtractor.TryParse(Message("incomplete", "computerList", "authenticated", -1, null), out _));
        Assert.False(ConsoleInventoryExtractor.TryParse(Message("incomplete", "computerList", "authenticated", 0, -5), out _));
    }

    [Fact]
    public void RowsArrayLongerThanTheDeclaredArrayIsRejectedAsShapeMismatch()
    {
        var json = Message("incomplete", "computerList", "authenticated", 0, null,
            """{"name":"Fixture","deviceName":"d","group":"","notes":"","hasConnectControl":true}""");

        // rowCount 0 with one row is a shape mismatch the validator must refuse.
        Assert.False(ConsoleInventoryExtractor.TryParse(json, out _));
    }

    [Fact]
    public void AWalkedClaimMustBeARealBoolean()
    {
        var valid = Message("complete", "computerList", "authenticated", 1, 1,
            """{"name":"Only Fixture","deviceName":"only","group":"","notes":"","hasConnectControl":true}""")
            .Replace("\"rows\":[", "\"walkedToEnd\":true,\"rows\":[", StringComparison.Ordinal);
        Assert.True(ConsoleInventoryExtractor.TryParse(valid, out var read));
        Assert.Equal(InventoryOutcome.Complete, read!.Outcome);

        var malformed = valid.Replace("\"walkedToEnd\":true", "\"walkedToEnd\":\"true\"", StringComparison.Ordinal);
        Assert.False(ConsoleInventoryExtractor.TryParse(malformed, out _));
    }

    private static string Message(string outcome, string pageKind, string authentication, int rowCount, int? reportedTotal, params string[] rows)
    {
        var total = reportedTotal is null ? "null" : reportedTotal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = string.Join(",", rows);
        return $"{{\"source\":\"{ConsoleInventoryExtractor.MessageSource}\",\"version\":{ConsoleInventoryExtractor.ScriptVersion}," +
               $"\"outcome\":\"{outcome}\",\"pageKind\":\"{pageKind}\",\"authentication\":\"{authentication}\"," +
               $"\"rowCount\":{rowCount},\"reportedTotal\":{total},\"rows\":[{body}]}}";
    }
}
