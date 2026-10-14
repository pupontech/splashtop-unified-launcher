namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Host-owned, one-shot allowance that lets the documented connection chooser's
/// own follow-up Business URI be accepted even though WebView2 reports it as not
/// user initiated. The allowance is opened only by a validated "armed" message
/// from the exact console origin after a real trusted Connect gesture, is bounded
/// in time, and is consumed by the first accepted handoff.
/// </summary>
internal sealed class NativeHandoffArm
{
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _lifetime;
    private readonly object _gate = new();
    private DateTimeOffset _armedUntil = DateTimeOffset.MinValue;

    public NativeHandoffArm(Func<DateTimeOffset>? utcNow = null, TimeSpan? lifetime = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _lifetime = lifetime ?? NativeConnectionPreference.ArmLifetime;
        if (_lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The native handoff allowance must be positive.");
        }
    }

    /// <summary>Opens (or extends) the allowance after a validated trusted arming message.</summary>
    public void Open() => _armedUntil = _utcNow() + _lifetime;

    /// <summary>True while an allowance is open. Read-only inspection for diagnostics.</summary>
    public bool IsOpen => _utcNow() < _armedUntil;

    /// <summary>Consumes the allowance; returns true when the caller may accept one follow-up handoff.</summary>
    public bool Consume()
    {
        lock (_gate)
        {
            if (_utcNow() >= _armedUntil)
            {
                _armedUntil = DateTimeOffset.MinValue;
                return false;
            }

            _armedUntil = DateTimeOffset.MinValue;
            return true;
        }
    }
}
