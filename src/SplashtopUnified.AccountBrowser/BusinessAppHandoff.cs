using System.Diagnostics;

namespace SplashtopUnified.AccountBrowser;

public enum BusinessAppHandoffResult
{
    Rejected,
    Duplicate,
    Dispatched,
    Failed
}

public interface IBusinessAppUriDispatcher
{
    void Dispatch(string uri);
}

public sealed class BusinessAppHandoff
{
    private static readonly TimeSpan DefaultDuplicateWindow = TimeSpan.FromSeconds(2);
    private readonly IBusinessAppUriDispatcher _dispatcher;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _duplicateWindow;
    private readonly Dictionary<string, DateTimeOffset> _recentUris = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public BusinessAppHandoff(
        IBusinessAppUriDispatcher dispatcher,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? duplicateWindow = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _duplicateWindow = duplicateWindow ?? DefaultDuplicateWindow;
        if (_duplicateWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duplicateWindow), "The duplicate-suppression window must be positive.");
        }
    }

    public static bool IsAllowedRequest(string? uriValue, string? initiatingOrigin, bool isUserInitiated) =>
        isUserInitiated && IsAllowedTargetAndOrigin(uriValue, initiatingOrigin);

    /// <summary>
    /// Kept for source compatibility only. A caller-supplied allowance is not proof of
    /// a trusted gesture and therefore never authorizes a non-user-initiated request.
    /// </summary>
    public static bool IsAllowedRequest(string? uriValue, string? initiatingOrigin, bool isUserInitiated, bool trustedArmActive) =>
        IsAllowedRequest(uriValue, initiatingOrigin, isUserInitiated);

    private static bool IsAllowedTargetAndOrigin(string? uriValue, string? initiatingOrigin)
    {
        if (string.IsNullOrEmpty(uriValue) || string.IsNullOrEmpty(initiatingOrigin) ||
            uriValue.Any(char.IsControl) ||
            !Uri.TryCreate(uriValue, UriKind.Absolute, out var target) ||
            !string.Equals(target.Scheme, "st-business", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Host, "com.splashtop.business", StringComparison.OrdinalIgnoreCase) ||
            target.UserInfo.Length != 0 || !target.IsDefaultPort)
        {
            return false;
        }

        return Uri.TryCreate(initiatingOrigin, UriKind.Absolute, out var origin) &&
               string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(origin.Host, "my.splashtop.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(origin.Host, "my.splashtop.eu", StringComparison.OrdinalIgnoreCase)) &&
               origin.UserInfo.Length == 0 && origin.IsDefaultPort;
    }

    /// <summary>
    /// Dispatches an official-console URI after WebView2's genuine user-gesture signal
    /// or an explicit confirmation in the native host. Page-posted messages are never
    /// authorization; the legacy trustedArmActive parameter remains for source compatibility.
    /// </summary>
    public BusinessAppHandoffResult TryDispatchWithHostConfirmation(
        string? uri,
        string? initiatingOrigin,
        bool isUserInitiated,
        Func<bool> confirmHost)
    {
        ArgumentNullException.ThrowIfNull(confirmHost);
        if (!IsAllowedTargetAndOrigin(uri, initiatingOrigin))
        {
            return BusinessAppHandoffResult.Rejected;
        }

        if (isUserInitiated)
        {
            return TryDispatch(uri, initiatingOrigin, isUserInitiated: true);
        }

        try
        {
            return confirmHost() ? DispatchValidated(uri!) : BusinessAppHandoffResult.Rejected;
        }
        catch (Exception)
        {
            return BusinessAppHandoffResult.Rejected;
        }
    }

    public BusinessAppHandoffResult TryDispatch(string? uri, string? initiatingOrigin, bool isUserInitiated, bool trustedArmActive = false)
    {
        // Keep the argument for source compatibility only. No page message or caller
        // supplied flag can establish a user gesture; unverified requests use the
        // separate explicit native-confirmation path below.
        _ = trustedArmActive;
        if (!IsAllowedRequest(uri, initiatingOrigin, isUserInitiated))
        {
            return BusinessAppHandoffResult.Rejected;
        }

        return DispatchValidated(uri!);
    }

    private BusinessAppHandoffResult DispatchValidated(string uri)
    {
        var now = _utcNow();
        lock (_gate)
        {
            foreach (var expired in _recentUris
                         .Where(entry => now - entry.Value >= _duplicateWindow)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _recentUris.Remove(expired);
            }

            if (_recentUris.TryGetValue(uri, out var previous) && now - previous < _duplicateWindow)
            {
                return BusinessAppHandoffResult.Duplicate;
            }

            _recentUris[uri] = now;
        }

        try
        {
            _dispatcher.Dispatch(uri);
            return BusinessAppHandoffResult.Dispatched;
        }
        catch (Exception)
        {
            return BusinessAppHandoffResult.Failed;
        }
    }
}

internal sealed class ShellBusinessAppUriDispatcher : IBusinessAppUriDispatcher
{
    public void Dispatch(string uri)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true
        });
    }
}
