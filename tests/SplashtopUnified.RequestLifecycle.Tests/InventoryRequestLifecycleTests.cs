using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.RequestLifecycle.Tests;

public sealed class InventoryRequestLifecycleTests
{
    [Fact]
    public async Task ManualRefreshJoinsAndAwaitsTheExactActiveAutomaticRequest()
    {
        var lifecycle = new InventoryRequestLifecycle<TaskCompletionSource<string?>>();
        var key = new InventoryRequestKey(7, "https://my.splashtop.com/console");
        var automatic = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(lifecycle.TryBeginOrJoin(key, automatic, allowJoin: false, out var startedRequest, out var started));
        Assert.True(started);
        Assert.Same(automatic, startedRequest);

        Assert.True(lifecycle.TryBeginOrJoin(key, new TaskCompletionSource<string?>(), allowJoin: true, out var joinedRequest, out started));
        Assert.False(started);
        Assert.Same(automatic, joinedRequest);

        automatic.SetResult("complete");
        Assert.Equal("complete", await joinedRequest.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.True(lifecycle.TryComplete(automatic));
        Assert.False(lifecycle.HasActiveRequest);
    }

    [Fact]
    public async Task UnknownTimeoutKeepsThePageLockAndMatchingJoinAvailableUntilNavigationInvalidatesIt()
    {
        var lifecycle = new InventoryRequestLifecycle<TaskCompletionSource<string?>>();
        var key = new InventoryRequestKey(3, "https://my.splashtop.eu/console");
        var automatic = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(lifecycle.TryBeginOrJoin(key, automatic, allowJoin: false, out _, out _));

        var timedOut = await Task.WhenAny(automatic.Task, Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken)) != automatic.Task;

        Assert.True(timedOut);
        Assert.True(lifecycle.HasActiveRequest);
        Assert.True(lifecycle.TryBeginOrJoin(key, new TaskCompletionSource<string?>(), allowJoin: true, out var joined, out var started));
        Assert.False(started);
        Assert.Same(automatic, joined);
        Assert.False(lifecycle.TryBeginOrJoin(key, new TaskCompletionSource<string?>(), allowJoin: false, out _, out _));

        Assert.Same(automatic, lifecycle.Invalidate());
        Assert.False(lifecycle.HasActiveRequest);
    }

    [Fact]
    public void RequestFromDifferentDocumentOrGenerationCannotJoinActivePageLock()
    {
        var lifecycle = new InventoryRequestLifecycle<object>();
        var request = new object();
        Assert.True(lifecycle.TryBeginOrJoin(new InventoryRequestKey(4, "https://my.splashtop.com/a"), request, false, out _, out _));

        Assert.False(lifecycle.TryBeginOrJoin(new InventoryRequestKey(5, "https://my.splashtop.com/a"), new object(), true, out _, out _));
        Assert.False(lifecycle.TryBeginOrJoin(new InventoryRequestKey(4, "https://my.splashtop.com/b"), new object(), true, out _, out _));
        Assert.Same(request, lifecycle.Invalidate());
    }

    [Fact]
    public void MatchingRefreshJoinsTheActiveWalkOnlyForTheExactGenerationAndDocument()
    {
        var lifecycle = new InventoryRequestLifecycle<TaskCompletionSource<string?>>();
        var key = new InventoryRequestKey(7, "https://my.splashtop.com/console");
        var active = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(lifecycle.TryBeginOrJoin(key, active, false, out _, out _));

        // The join the host performs before competing for the console semaphore:
        // same document generation and source joins; anything else does not, and
        // the join itself can never start a second walk.
        Assert.True(lifecycle.TryJoinActive(key, out var joined));
        Assert.Same(active, joined);
        Assert.False(lifecycle.TryJoinActive(new InventoryRequestKey(8, "https://my.splashtop.com/console"), out _));
        Assert.False(lifecycle.TryJoinActive(new InventoryRequestKey(7, "https://my.splashtop.com/other"), out _));
        Assert.True(lifecycle.HasActiveRequest);

        // With no walk in flight there is nothing to join; the caller must start
        // its own request instead of joining a completed one.
        Assert.True(lifecycle.TryComplete(active));
        Assert.False(lifecycle.HasActiveRequest);
        Assert.False(lifecycle.TryJoinActive(key, out _));
    }

    [Fact]
    public async Task LateResponseAfterHostTimeoutCompletesAndReleasesThePageLock()
    {
        var lifecycle = new InventoryRequestLifecycle<TaskCompletionSource<string?>>();
        var key = new InventoryRequestKey(3, "https://my.splashtop.eu/console");
        var request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(lifecycle.TryBeginOrJoin(key, request, false, out _, out _));

        // The host deadline elapsed, but the walk is still alive and its response
        // may still arrive.
        await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        Assert.True(lifecycle.HasActiveRequest);

        request.SetResult("complete");
        Assert.True(lifecycle.TryComplete(request));
        Assert.False(lifecycle.HasActiveRequest);
    }

    [Fact]
    public void StaleRequestCannotReleaseALaterPageLock()
    {
        var lifecycle = new InventoryRequestLifecycle<object>();
        var first = new object();
        Assert.True(lifecycle.TryBeginOrJoin(new InventoryRequestKey(4, "https://my.splashtop.com/a"), first, false, out _, out _));

        lifecycle.Invalidate();
        var second = new object();
        Assert.True(lifecycle.TryBeginOrJoin(new InventoryRequestKey(5, "https://my.splashtop.com/a"), second, false, out _, out _));

        Assert.False(lifecycle.TryComplete(first));
        Assert.True(lifecycle.HasActiveRequest);
        Assert.True(lifecycle.TryComplete(second));
        Assert.False(lifecycle.HasActiveRequest);
    }
}
