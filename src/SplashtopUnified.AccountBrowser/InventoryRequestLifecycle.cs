namespace SplashtopUnified.AccountBrowser;

internal readonly record struct InventoryRequestKey(int Generation, string DocumentSource);

/// <summary>
/// Tracks the one page-side inventory walk whose completion is correlated to the current
/// document generation. A host timeout deliberately does not complete or release it.
/// Callers serialize compound lifecycle operations with their inventory-state lock.
/// </summary>
internal sealed class InventoryRequestLifecycle<TRequest> where TRequest : class
{
    private readonly object _sync = new();
    private InventoryRequestKey? _activeKey;
    private TRequest? _activeRequest;

    public bool HasActiveRequest
    {
        get
        {
            lock (_sync)
            {
                return _activeRequest is not null;
            }
        }
    }

    public TRequest? ActiveRequest
    {
        get
        {
            lock (_sync)
            {
                return _activeRequest;
            }
        }
    }

    public bool TryBeginOrJoin(
        InventoryRequestKey key,
        TRequest proposedRequest,
        bool allowJoin,
        out TRequest request,
        out bool started)
    {
        ArgumentNullException.ThrowIfNull(proposedRequest);
        lock (_sync)
        {
            if (_activeRequest is null)
            {
                _activeKey = key;
                _activeRequest = proposedRequest;
                request = proposedRequest;
                started = true;
                return true;
            }

            if (allowJoin && _activeKey == key)
            {
                request = _activeRequest;
                started = false;
                return true;
            }

            request = null!;
            started = false;
            return false;
        }
    }

    /// <summary>Releases only the matching completed request, never a later request.</summary>
    public bool TryComplete(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (!ReferenceEquals(_activeRequest, request))
            {
                return false;
            }

            _activeRequest = null;
            _activeKey = null;
            return true;
        }
    }

    /// <summary>Navigation invalidates the old page lock; late responses cannot release a new one.</summary>
    public TRequest? Invalidate()
    {
        lock (_sync)
        {
            var request = _activeRequest;
            _activeRequest = null;
            _activeKey = null;
            return request;
        }
    }
}
