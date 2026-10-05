using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Extractor.Tests;

/// <summary>
/// Validator coverage for the console connect chooser. Option labels are the official
/// labels; the payloads here are synthetic.
/// </summary>
public sealed class ConnectionChooserTests
{
    [Fact]
    public void AcceptsAChooserThatOffersBothOptions()
    {
        var json = Probe(present: true, native: true, web: true, heading: ConnectionChooser.HeadingText);

        Assert.True(ConnectionChooser.TryParseProbe(json, out var probe));
        Assert.NotNull(probe);
        Assert.True(probe!.Present);
        Assert.True(probe.NativeAvailable);
        Assert.True(probe.WebAvailable);
        Assert.Equal(ConnectionChooser.HeadingText, probe.Heading);
    }

    [Fact]
    public void AcceptsAPageWithNoChooser()
    {
        var json = Probe(present: false, native: false, web: false, heading: null);

        Assert.True(ConnectionChooser.TryParseProbe(json, out var probe));
        Assert.False(probe!.Present);
        Assert.Null(probe.Heading);
    }

    [Fact]
    public void ContradictoryClaimsAreRejected()
    {
        Assert.False(ConnectionChooser.TryParseProbe(Probe(present: true, native: false, web: false, heading: null), out _));
        Assert.False(ConnectionChooser.TryParseProbe(Probe(present: false, native: true, web: false, heading: null), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void MalformedPayloadsAreRejected(string? json)
    {
        Assert.False(ConnectionChooser.TryParseProbe(json, out var probe));
        Assert.Null(probe);
    }

    [Fact]
    public void WrongSourceOrVersionIsRejected()
    {
        Assert.False(ConnectionChooser.TryParseProbe(
            Probe(present: true, native: true, web: false, heading: null).Replace(ConnectionChooser.ProbeSource, "chooser-evil", StringComparison.Ordinal), out _));
        Assert.False(ConnectionChooser.TryParseProbe(
            Probe(present: true, native: true, web: false, heading: null).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), out _));
    }

    [Fact]
    public void NonBooleanFlagsAndOversizedHeadingsAreRejected()
    {
        Assert.False(ConnectionChooser.TryParseProbe(
            Probe(present: true, native: true, web: false, heading: null).Replace("\"present\":true", "\"present\":\"true\"", StringComparison.Ordinal), out _));
        Assert.False(ConnectionChooser.TryParseProbe(
            Probe(present: true, native: true, web: false, heading: new string('x', 400)), out _));
        Assert.False(ConnectionChooser.TryParseProbe(
            Probe(present: true, native: true, web: false, heading: "bad\u0007heading"), out _));
    }

    [Fact]
    public void OnlyDocumentedOptionsCanBeApplied()
    {
        Assert.Contains(ConnectionChooser.NativeLabel, ConnectionChooser.SelectScript(ConnectionChooser.NativeKey), StringComparison.Ordinal);
        Assert.Contains(ConnectionChooser.WebLabel, ConnectionChooser.SelectScript(ConnectionChooser.WebKey), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionChooser.SelectScript("invented"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionChooser.SelectScript(string.Empty));
    }

    [Fact]
    public void TheApplyScriptsRefuseToRunOutsideTheOfficialConsole()
    {
        foreach (var key in new[] { ConnectionChooser.NativeKey, ConnectionChooser.WebKey })
        {
            var script = ConnectionChooser.SelectScript(key);
            Assert.Contains("my.splashtop.com", script, StringComparison.Ordinal);
            Assert.Contains("my.splashtop.eu", script, StringComparison.Ordinal);
            Assert.Contains("'blocked'", script, StringComparison.Ordinal);
        }
    }

    private static string Probe(bool present, bool native, bool web, string? heading)
    {
        var headingJson = heading is null ? "null" : "\"" + heading.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return $"{{\"source\":\"{ConnectionChooser.ProbeSource}\",\"version\":{ConnectionChooser.ScriptVersion}," +
               $"\"present\":{present.ToString().ToLowerInvariant()},\"heading\":{headingJson}," +
               $"\"nativeAvailable\":{native.ToString().ToLowerInvariant()},\"webAvailable\":{web.ToString().ToLowerInvariant()}}}";
    }
}
