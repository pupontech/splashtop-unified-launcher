namespace SplashtopUnified.AccountBrowser;

internal enum ConsoleInspectionStatus
{
    Complete,
    TimedOut,
    Busy,
    Failed,
    PageChanged
}

internal sealed record ConsoleInspectionResult(ConsoleInspectionStatus Status, string? Survey = null);

internal sealed class ConsoleInspection
{
    private readonly object _sync = new();
    private bool _readInFlight;
    private bool _readTimedOut;
    private Task<string>? _activeOperation;

    public async Task<ConsoleInspectionResult> ReadAsync(
        Func<Task<string>> read,
        Func<bool> isCurrent,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be finite and greater than zero.");
        }

        lock (_sync)
        {
            if (_readInFlight && _readTimedOut && _activeOperation?.IsCompleted == true)
            {
                _readInFlight = false;
                _readTimedOut = false;
                _activeOperation = null;
            }

            if (_readInFlight)
            {
                return new ConsoleInspectionResult(ConsoleInspectionStatus.Busy);
            }

            _readInFlight = true;
            _readTimedOut = false;
        }

        Task<string> operation;
        try
        {
            // Invoke on the caller's context: WebView2 operations must stay on the UI thread.
            operation = read();
            if (operation is null)
            {
                ReleaseRead();
                return new ConsoleInspectionResult(ConsoleInspectionStatus.Failed);
            }
        }
        catch (Exception)
        {
            ReleaseRead();
            return new ConsoleInspectionResult(ConsoleInspectionStatus.Failed);
        }

        lock (_sync)
        {
            _activeOperation = operation;
        }

        // Observe late faults, but never let a delayed completion callback release
        // a newer request. Ownership is released only by the caller, or reclaimed
        // at the next entry when its exact timed-out operation has finished.
        _ = operation.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
        var completedTask = await Task.WhenAny(operation, timeoutTask);
        timeoutCancellation.Cancel();
        if (completedTask != operation)
        {
            lock (_sync)
            {
                _readTimedOut = true;
                if (operation.IsCompleted)
                {
                    _readInFlight = false;
                    _readTimedOut = false;
                }
            }

            return new ConsoleInspectionResult(ConsoleInspectionStatus.TimedOut);
        }

        string survey;
        try
        {
            survey = await operation.ConfigureAwait(true);
        }
        catch (Exception)
        {
            ReleaseRead();
            return new ConsoleInspectionResult(ConsoleInspectionStatus.Failed);
        }

        bool current;
        try
        {
            current = isCurrent();
        }
        catch (Exception)
        {
            ReleaseRead();
            return new ConsoleInspectionResult(ConsoleInspectionStatus.Failed);
        }

        ReleaseRead();
        return current
            ? new ConsoleInspectionResult(ConsoleInspectionStatus.Complete, survey)
            : new ConsoleInspectionResult(ConsoleInspectionStatus.PageChanged);
    }

    private void ReleaseRead()
    {
        lock (_sync)
        {
            _readInFlight = false;
            _readTimedOut = false;
            _activeOperation = null;
        }
    }
}
