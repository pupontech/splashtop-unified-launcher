using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.BusinessHandoff.Tests;

/// <summary>
/// Covers the bounded trusted-gesture allowance that lets the documented connection
/// chooser's own Business URI be accepted even though WebView2 reports it as not
/// user initiated. The allowance never fabricates a user gesture.
/// </summary>
public sealed class TrustedNativeArmTests
{
    private const string TrustedOrigin = "https://my.splashtop.com/computers";
    private const string TrustedUri = "st-business://com.splashtop.business?account=tech%40example.test&mac=020000000001";

    [Fact]
    public void NonUserInitiatedUriIsRejectedWithoutAnAllowance()
    {
        Assert.False(BusinessAppHandoff.IsAllowedRequest(TrustedUri, TrustedOrigin, isUserInitiated: false));
        Assert.False(BusinessAppHandoff.IsAllowedRequest(TrustedUri, TrustedOrigin, isUserInitiated: false, trustedArmActive: false));
    }

    [Fact]
    public void AllowanceAcceptsTheChooserFollowUpThatWebViewReportsAsNotUserInitiated()
    {
        Assert.True(BusinessAppHandoff.IsAllowedRequest(TrustedUri, TrustedOrigin, isUserInitiated: false, trustedArmActive: true));
    }

    [Fact]
    public void AllowanceDoesNotRelaxSchemeOriginOrShapeChecks()
    {
        Assert.False(BusinessAppHandoff.IsAllowedRequest("other-app://com.splashtop.business?x=1", TrustedOrigin, false, true));
        Assert.False(BusinessAppHandoff.IsAllowedRequest("st-business://com.splashtop.business.evil.test?x=1", TrustedOrigin, false, true));
        Assert.False(BusinessAppHandoff.IsAllowedRequest(TrustedUri, "https://evil.test/computers", false, true));
        Assert.False(BusinessAppHandoff.IsAllowedRequest(TrustedUri, "http://my.splashtop.com/computers", false, true));
        Assert.False(BusinessAppHandoff.IsAllowedRequest(TrustedUri + "\n", TrustedOrigin, false, true));
    }

    [Fact]
    public void ArmIsOneShotAndExpires()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var arm = new NativeHandoffArm(() => now, TimeSpan.FromSeconds(12));

        Assert.False(arm.IsOpen);
        Assert.False(arm.Consume());

        arm.Open();
        Assert.True(arm.IsOpen);
        Assert.True(arm.Consume());
        Assert.False(arm.IsOpen);
        Assert.False(arm.Consume());

        arm.Open();
        now = now.AddSeconds(13);
        Assert.False(arm.Consume());

        arm.Open();
        now = now.AddSeconds(11);
        Assert.True(arm.Consume());
    }

    [Fact]
    public void DispatcherReceivesTheChooserUriOnlyOnceInsideTheAllowance()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var arm = new NativeHandoffArm(() => now, TimeSpan.FromSeconds(12));
        var dispatcher = new RecordingDispatcher();
        var handoff = new BusinessAppHandoff(dispatcher);

        arm.Open();
        Assert.Equal(BusinessAppHandoffResult.Dispatched, handoff.TryDispatch(TrustedUri, TrustedOrigin, isUserInitiated: false, trustedArmActive: arm.Consume()));
        Assert.Equal(BusinessAppHandoffResult.Rejected, handoff.TryDispatch(TrustedUri, TrustedOrigin, isUserInitiated: false, trustedArmActive: arm.Consume()));
        Assert.Single(dispatcher.ReceivedUris);
        Assert.Equal(TrustedUri, dispatcher.ReceivedUris[0]);
    }

    private sealed class RecordingDispatcher : IBusinessAppUriDispatcher
    {
        private readonly List<string> _receivedUris = [];

        public IReadOnlyList<string> ReceivedUris => _receivedUris;

        public void Dispatch(string uri) => _receivedUris.Add(uri);
    }
}
