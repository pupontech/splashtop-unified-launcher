using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.RequestLifecycle.Tests;

public sealed class ConsoleInspectionTests
{
    [Fact]
    public async Task UnsupportedTimeoutIsRejectedBeforeStartingAnUnderlyingRead()
    {
        var inspector = new ConsoleInspection();
        var started = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => inspector.ReadAsync(
            () => { started++; return Task.FromResult("survey"); }, () => true, TimeSpan.MaxValue));
        Assert.Equal(0, started);
        Assert.Equal(ConsoleInspectionStatus.Complete,
            (await inspector.ReadAsync(() => Task.FromResult("fresh"), () => true, TimeSpan.FromSeconds(1))).Status);
    }

    [Fact]
    public async Task IndependentInspectorCompletesWhileAnotherReadIsHung()
    {
        var stuck = new ConsoleInspection();
        var healthy = new ConsoleInspection();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = await stuck.ReadAsync(() => pending.Task, () => true, TimeSpan.FromMilliseconds(25));
        var result = await healthy.ReadAsync(() => Task.FromResult("ok"), () => true, TimeSpan.FromSeconds(1));

        Assert.Equal(ConsoleInspectionStatus.TimedOut, timedOut.Status);
        Assert.Equal(new ConsoleInspectionResult(ConsoleInspectionStatus.Complete, "ok"), result);
        pending.SetResult("late");
    }

    [Fact]
    public async Task LateFaultIsObservedAndNextReadCanComplete()
    {
        var inspector = new ConsoleInspection();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(ConsoleInspectionStatus.TimedOut,
            (await inspector.ReadAsync(() => pending.Task, () => true, TimeSpan.FromMilliseconds(25))).Status);

        pending.SetException(new InvalidOperationException("private detail"));
        var next = await inspector.ReadAsync(() => Task.FromResult("ok"), () => true, TimeSpan.FromSeconds(1));

        Assert.Equal(new ConsoleInspectionResult(ConsoleInspectionStatus.Complete, "ok"), next);
    }

    [Fact]
    public async Task ChangedPageAndReadFailuresAreReportedWithoutExceptionDetails()
    {
        var inspector = new ConsoleInspection();
        var changed = await inspector.ReadAsync(() => Task.FromResult("survey"), () => false, TimeSpan.FromSeconds(1));
        var failed = await inspector.ReadAsync(() => throw new InvalidOperationException("secret"), () => true, TimeSpan.FromSeconds(1));

        Assert.Equal(ConsoleInspectionStatus.PageChanged, changed.Status);
        Assert.Null(changed.Survey);
        Assert.Equal(ConsoleInspectionStatus.Failed, failed.Status);
        Assert.Null(failed.Survey);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => inspector.ReadAsync(() => Task.FromResult("x"), () => true, TimeSpan.Zero));
    }

    [Fact]
    public async Task ConcurrentCallsStartOnlyOneUnderlyingRead()
    {
        var inspector = new ConsoleInspection();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var first = inspector.ReadAsync(() => { Interlocked.Increment(ref started); return pending.Task; }, () => true, TimeSpan.FromSeconds(1));
        var second = inspector.ReadAsync(() => { Interlocked.Increment(ref started); return Task.FromResult("second"); }, () => true, TimeSpan.FromSeconds(1));

        Assert.Equal(ConsoleInspectionStatus.Busy, (await second).Status);
        Assert.Equal(1, started);
        pending.SetResult("first");
        Assert.Equal(ConsoleInspectionStatus.Complete, (await first).Status);
    }

    [Fact]
    public async Task TimedOutReadKeepsSameInspectorBusyUntilUnderlyingReadFinishes()
    {
        var inspector = new ConsoleInspection();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = inspector.ReadAsync(() => pending.Task, () => true, TimeSpan.FromMilliseconds(30));

        Assert.Equal(ConsoleInspectionStatus.TimedOut,
            (await first.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)).Status);
        var starts = 0;
        var busy = await inspector.ReadAsync(
            () => { starts++; return Task.FromResult("unexpected"); },
            () => true,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ConsoleInspectionStatus.Busy, busy.Status);
        Assert.Equal(0, starts);

        pending.SetResult("late");
        var recovered = await inspector.ReadAsync(() => Task.FromResult("fresh"), () => true,
            TimeSpan.FromSeconds(1));
        Assert.Equal(ConsoleInspectionStatus.Complete, recovered.Status);
        Assert.Equal("fresh", recovered.Survey);
    }

    [Fact]
    public async Task HungReadReturnsTimedOutWithinTheConfiguredBound()
    {
        var inspector = new ConsoleInspection();
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await inspector.ReadAsync(
                () => never.Task,
                () => true,
                TimeSpan.FromMilliseconds(40))
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ConsoleInspectionStatus.TimedOut, result.Status);
    }
}
