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
}
