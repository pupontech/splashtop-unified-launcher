using System.Text.Json;
using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

public sealed class InventoryDiagnosticTests
{
    [Fact]
    public void UnrecognizedListDoesNotTellAuthenticatedUsersToSignInAgain()
    {
        var json = Payload("unknown", "unknown");
        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(InventoryOutcome.Unavailable, read!.Outcome);
        Assert.Contains("layout not recognized", read.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not establish a sign-in failure", read.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecognizedLoginPageGivesNormalSignInGuidance()
    {
        var json = Payload("login", "required");
        Assert.True(ConsoleInventoryExtractor.TryParse(json, out var read));
        Assert.Equal(ConsoleAuthentication.Required, read!.Authentication);
        Assert.Contains("Sign-in page detected", read.Diagnostic, StringComparison.Ordinal);
    }

    private static string Payload(string pageKind, string authentication) => JsonSerializer.Serialize(new
    {
        source = ConsoleInventoryExtractor.MessageSource,
        version = ConsoleInventoryExtractor.ScriptVersion,
        requestId = "diagnostic-fixture",
        outcome = "unavailable",
        pageKind,
        authentication,
        rowCount = 0,
        reportedTotal = (int?)null,
        rows = Array.Empty<object>(),
        mode = "none",
        walkedToEnd = false,
        pagesVisited = 0,
        walkMillis = 0
    });
}
