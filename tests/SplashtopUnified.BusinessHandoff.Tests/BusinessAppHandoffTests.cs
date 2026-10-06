using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.BusinessHandoff.Tests;

public sealed class BusinessAppHandoffTests
{
    private const string BusinessUri = "st-business://com.splashtop.business?opaque=%2F%2f&clientPayload=a%3Db#keep-case";
    private const string GlobalOrigin = "https://my.splashtop.com/web/console";
    private const string EuropeOrigin = "https://my.splashtop.eu/computers";

    [Theory]
    [InlineData(BusinessUri, GlobalOrigin)]
    [InlineData(BusinessUri, EuropeOrigin)]
    public void Official_console_business_uri_from_trusted_origin_is_allowed(string uri, string origin)
    {
        Assert.True(BusinessAppHandoff.IsAllowedRequest(uri, origin, isUserInitiated: true));
    }

    [Theory]
    [InlineData("https://example.invalid/", GlobalOrigin)]
    [InlineData("file:///C:/Windows/system.ini", GlobalOrigin)]
    [InlineData("javascript:alert(1)", GlobalOrigin)]
    [InlineData("st-business://com.splashtop.business?x=1", "https://evil.example/")]
    [InlineData("st-business://com.splashtop.business?x=1", "https://my.splashtop.com.evil.invalid/")]
    [InlineData("st-business://com.splashtop.business?x=1", "http://my.splashtop.com/")]
    [InlineData("st-business://com.splashtop.business?x=1", "https://my.splashtop.com:444/")]
    [InlineData("st-business://com.splashtop.business?x=1", "https://user@my.splashtop.com/")]
    [InlineData("st-business://com.splashtop.business.evil.invalid?x=1", GlobalOrigin)]
    [InlineData("st-business://other.application?x=1", GlobalOrigin)]
    public void Unsupported_scheme_or_origin_is_rejected(string uri, string origin)
    {
        Assert.False(BusinessAppHandoff.IsAllowedRequest(uri, origin, isUserInitiated: true));
    }

    [Fact]
    public void User_gesture_is_required()
    {
        Assert.False(BusinessAppHandoff.IsAllowedRequest(BusinessUri, GlobalOrigin, isUserInitiated: false));
    }

    [Fact]
    public void Dispatch_preserves_the_received_uri_exactly()
    {
        var dispatcher = new RecordingDispatcher();
        var handoff = new BusinessAppHandoff(dispatcher, () => DateTimeOffset.UnixEpoch);

        var result = handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);

        Assert.Equal(BusinessAppHandoffResult.Dispatched, result);
        Assert.Equal(new[] { BusinessUri }, dispatcher.ReceivedUris);
    }

    [Fact]
    public void NonUserInitiatedHandoffRequiresExplicitHostConfirmation()
    {
        var dispatcher = new RecordingDispatcher();
        var handoff = new BusinessAppHandoff(dispatcher, () => DateTimeOffset.UnixEpoch);
        var prompts = 0;

        var rejected = handoff.TryDispatchWithHostConfirmation(
            BusinessUri, GlobalOrigin, isUserInitiated: false, confirmHost: () => { prompts++; return false; });
        var accepted = handoff.TryDispatchWithHostConfirmation(
            BusinessUri, GlobalOrigin, isUserInitiated: false, confirmHost: () => { prompts++; return true; });

        Assert.Equal(BusinessAppHandoffResult.Rejected, rejected);
        Assert.Equal(BusinessAppHandoffResult.Dispatched, accepted);
        Assert.Equal(2, prompts);
        Assert.Equal(new[] { BusinessUri }, dispatcher.ReceivedUris);
    }

    [Fact]
    public void InvalidNonUserInitiatedHandoffDoesNotPromptForConfirmation()
    {
        var handoff = new BusinessAppHandoff(new RecordingDispatcher());
        var prompted = false;

        var result = handoff.TryDispatchWithHostConfirmation(
            "javascript:alert(1)", GlobalOrigin, isUserInitiated: false, confirmHost: () => { prompted = true; return true; });

        Assert.Equal(BusinessAppHandoffResult.Rejected, result);
        Assert.False(prompted);
    }

    [Fact]
    public void Repeated_event_paths_for_same_uri_dispatch_only_once()
    {
        var dispatcher = new RecordingDispatcher();
        var handoff = new BusinessAppHandoff(dispatcher, () => DateTimeOffset.UnixEpoch);

        var first = handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);
        var duplicate = handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);

        Assert.Equal(BusinessAppHandoffResult.Dispatched, first);
        Assert.Equal(BusinessAppHandoffResult.Duplicate, duplicate);
        Assert.Single(dispatcher.ReceivedUris);
    }

    [Fact]
    public void Different_uris_are_not_collapsed_by_duplicate_suppression()
    {
        var dispatcher = new RecordingDispatcher();
        var handoff = new BusinessAppHandoff(dispatcher, () => DateTimeOffset.UnixEpoch);

        handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);
        handoff.TryDispatch("st-business://com.splashtop.business?opaque=second", GlobalOrigin, isUserInitiated: true);

        Assert.Equal(2, dispatcher.ReceivedUris.Count);
    }

    [Fact]
    public void Duplicate_suppression_expires_for_a_later_explicit_connect()
    {
        var dispatcher = new RecordingDispatcher();
        var now = DateTimeOffset.UnixEpoch;
        var handoff = new BusinessAppHandoff(dispatcher, () => now, TimeSpan.FromSeconds(2));

        handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);
        now = now.AddSeconds(3);
        var result = handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);

        Assert.Equal(BusinessAppHandoffResult.Dispatched, result);
        Assert.Equal(2, dispatcher.ReceivedUris.Count);
    }

    [Fact]
    public void Missing_protocol_handler_returns_failure_without_exposing_uri()
    {
        var dispatcher = new ThrowingDispatcher();
        var handoff = new BusinessAppHandoff(dispatcher, () => DateTimeOffset.UnixEpoch);

        var result = handoff.TryDispatch(BusinessUri, GlobalOrigin, isUserInitiated: true);

        Assert.Equal(BusinessAppHandoffResult.Failed, result);
        Assert.DoesNotContain(BusinessUri, dispatcher.ExceptionMessage, StringComparison.Ordinal);
    }

    private sealed class RecordingDispatcher : IBusinessAppUriDispatcher
    {
        public List<string> ReceivedUris { get; } = [];
        public void Dispatch(string uri) => ReceivedUris.Add(uri);
    }

    private sealed class ThrowingDispatcher : IBusinessAppUriDispatcher
    {
        public string ExceptionMessage { get; } = "No URI protocol handler is registered.";
        public void Dispatch(string uri) => throw new InvalidOperationException(ExceptionMessage);
    }
}
