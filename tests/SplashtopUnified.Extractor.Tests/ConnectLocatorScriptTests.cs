using System.Text.Json;
using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

public sealed class ConnectLocatorScriptTests
{
    [Fact]
    public void ConnectScriptDoesNotTreatInventoryIndexAsRenderedDomIndex()
    {
        var row = new ExtractedComputerRow("Later host", "later-device", "Lab", "target", true, "Online");
        var script = ConsoleInventoryActions.ActivateConnectScript(
            row, "https://my.splashtop.com/computers", 1234, "range:3-4 of 8", 40000, 9999999999999);

        Assert.DoesNotContain("rowIndex", script, StringComparison.Ordinal);
        Assert.Contains("expected.name", script, StringComparison.Ordinal);
        Assert.Contains("ambiguous-row", script, StringComparison.Ordinal);
        Assert.Contains("targetCount", ConnectLocatorScript.CreateStep(
            "read", row, "https://my.splashtop.com/computers", 1234, null, null, null, 9999999999999), StringComparison.Ordinal);
    }

    [Fact]
    public void LocatorStepsAreSynchronousBoundedAndPinnedToExactDocumentAndIdentity()
    {
        var row = new ExtractedComputerRow("Later host", "later-device", "Lab", "target", true, "Online");
        var script = ConnectLocatorScript.CreateStep(
            "click", row, "https://my.splashtop.com/computers", 1234, "range:3-4 of 8", 40000, null, 9999999999999);

        Assert.DoesNotContain("async function", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Promise", script, StringComparison.Ordinal);
        Assert.Contains("location.href !== request.expectedUrl", script, StringComparison.Ordinal);
        Assert.Contains("request.documentToken !== documentToken", script, StringComparison.Ordinal);
        Assert.Contains("Date.now() >= request.deadlineEpochMilliseconds", script, StringComparison.Ordinal);
        Assert.Contains("expected.status", script, StringComparison.Ordinal);
        Assert.Contains("expectedPosition", script, StringComparison.Ordinal);
        Assert.Contains("my.splashtop.eu", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void InlinedStepPayloadKeepsTheRequestAsAnObjectThePageCanRead(string newline)
    {
        var row = new ExtractedComputerRow("Later host", "later-device", "Lab", "target", true, "Online");
        var script = ConnectLocatorScript.CreateStep(
            "click", row, "https://my.splashtop.com/computers", 1234, "range:3-4 of 8", 40000, null, 9999999999999);

        using var payload = ExtractInlinedPayload(script.ReplaceLineEndings(newline));
        Assert.Equal(JsonValueKind.Object, payload.RootElement.GetProperty("request").ValueKind);
        Assert.Equal(JsonValueKind.Object, payload.RootElement.GetProperty("expected").ValueKind);
        var request = payload.RootElement.GetProperty("request");
        Assert.Equal("click", request.GetProperty("command").GetString());
        Assert.Equal("https://my.splashtop.com/computers", request.GetProperty("expectedUrl").GetString());
        Assert.Equal(1234, request.GetProperty("documentToken").GetDouble());
        Assert.Equal("range:3-4 of 8", request.GetProperty("pageToken").GetString());
        Assert.Equal(40000, request.GetProperty("expectedPosition").GetInt64());
        Assert.Equal(9999999999999, request.GetProperty("deadlineEpochMilliseconds").GetInt64());
        Assert.Equal("Later host", payload.RootElement.GetProperty("expected").GetProperty("name").GetString());
    }

    [Fact]
    public void GeneratedStepEscapesItsJavaScriptRegexesForThePage()
    {
        var row = new ExtractedComputerRow("Later host", "later-device", "Lab", "target", true, "Online");
        var script = ConnectLocatorScript.CreateStep(
            "read", row, "https://my.splashtop.com/computers", null, null, null, null, 9999999999999);

        // The template is a raw string literal, so a doubled backslash reaches the page as a
        // literal backslash: a range label like "1-3 of 9" then stops parsing, every pager is
        // rejected, and whitespace is never collapsed.
        Assert.Contains(@"/\s+/g", script, StringComparison.Ordinal);
        Assert.Contains(@"\b(online|offline", script, StringComparison.Ordinal);
        Assert.Contains(@"^\d[\d,]*\s*[-–]\s*\d[\d,]*\s+of\s+(\d[\d,]*)$", script, StringComparison.Ordinal);
        Assert.Contains(@"(?:page\s+)?(\d+)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", script, StringComparison.Ordinal);
    }

    private static JsonDocument ExtractInlinedPayload(string script)
    {
        script = script.ReplaceLineEndings("\n");
        const string marker = "var payload = ";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the generated script must inline its payload");
        start += marker.Length;
        var end = script.IndexOf(";\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "the inlined payload must be a single statement");
        return JsonDocument.Parse(script[start..end]);
    }
}
