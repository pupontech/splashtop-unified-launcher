using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

internal enum InventoryCacheFailureReason
{
    MissingFile,
    TooLarge,
    UnsupportedVersion,
    Corrupt,
    InvalidPayload,
    IoError
}

internal sealed record InventoryCacheReadResult(
    IReadOnlyList<CachedInventorySnapshot> Snapshots,
    InventoryCacheFailureReason? FailureReason)
{
    public bool IsSuccess => FailureReason is null;

    internal static InventoryCacheReadResult Failed(InventoryCacheFailureReason reason) => new([], reason);
}

/// <summary>A validated cached read. Its outcome remains exactly the outcome originally recorded.</summary>
internal sealed record CachedInventorySnapshot(
    string AccountId,
    InventoryOutcome Outcome,
    ConsolePageKind PageKind,
    ConsoleAuthentication Authentication,
    int RowCount,
    int? ReportedTotal,
    IReadOnlyList<ExtractedComputerRow> Rows,
    DateTimeOffset CapturedAtUtc,
    string? Diagnostic,
    string? Mode,
    InventoryOutcome? LatestAttemptOutcome = null,
    string? LatestAttemptDiagnostic = null,
    DateTimeOffset? LatestAttemptAtUtc = null)
{
    public TimeSpan Age => AgeAt(DateTimeOffset.UtcNow);

    public TimeSpan AgeAt(DateTimeOffset nowUtc)
    {
        var age = nowUtc.ToUniversalTime() - CapturedAtUtc;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>At exactly the maximum age, this snapshot is still within the allowed age.</summary>
    public bool IsStale(TimeSpan maximumAge, DateTimeOffset nowUtc)
    {
        if (maximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        return AgeAt(nowUtc) > maximumAge;
    }

    /// <summary>Attaches the caller's current account label without persisting profile data in the cache.</summary>
    public AccountInventorySnapshot ToAccountInventorySnapshot(string accountName) => new(
        AccountId,
        accountName,
        Outcome,
        PageKind,
        Authentication,
        RowCount,
        ReportedTotal,
        Rows,
        CapturedAtUtc,
        Diagnostic,
        Mode: Mode,
        IsCached: true,
        LatestAttemptOutcome: LatestAttemptOutcome,
        LatestAttemptDiagnostic: LatestAttemptDiagnostic,
        LatestAttemptAtUtc: LatestAttemptAtUtc);
}

/// <summary>
/// A small allowlisted on-disk copy of account-scoped inventory reads.
/// Unknown content is rejected as a whole; concurrent writes merge by account and replace atomically.
/// Notes and diagnostics are excluded as free-form text. Names, device names, and groups remain
/// plaintext inventory metadata and may themselves be sensitive; this cache is not a privacy classifier.
/// </summary>
internal static class InventoryCache
{
    internal const int FormatVersion = 1;
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);
    private const int MaximumRowsPerAccount = 5000;
    private const int MaximumFieldLength = 512;
    private const int MaximumStatusLength = 64;
    private static readonly string[] DocumentFields = ["formatVersion", "snapshots"];
    private static readonly string[] SnapshotFields =
    [
        "accountId", "outcome", "pageKind", "authentication", "rowCount", "reportedTotal",
        "capturedAtUtc", "diagnostic", "mode", "latestAttemptOutcome", "latestAttemptDiagnostic", "latestAttemptAtUtc", "rows"
    ];
    private static readonly string[] CurrentSnapshotFields =
    [
        "accountId", "outcome", "pageKind", "authentication", "rowCount", "reportedTotal",
        "capturedAtUtc", "mode", "latestAttemptOutcome", "latestAttemptAtUtc", "rows"
    ];
    private static readonly string[] IntermediateSnapshotFields =
    [
        "accountId", "outcome", "pageKind", "authentication", "rowCount", "reportedTotal",
        "capturedAtUtc", "diagnostic", "mode", "latestAttemptOutcome", "latestAttemptDiagnostic", "rows"
    ];
    private static readonly string[] LegacySnapshotFields =
    [
        "accountId", "outcome", "pageKind", "authentication", "rowCount", "reportedTotal",
        "capturedAtUtc", "diagnostic", "mode", "rows"
    ];
    private static readonly string[] RowFields =
    [
        "name", "deviceName", "group", "notes", "hasConnectControl", "status"
    ];
    private static readonly string[] CurrentRowFields =
    [
        "name", "deviceName", "group", "hasConnectControl", "status"
    ];

    public static void Write(string path, IEnumerable<AccountInventorySnapshot> snapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshots);

        var entries = snapshots.Select(ToCachedSnapshot).ToArray();
        if (entries.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw new ArgumentException("There may be only one cached snapshot per account id.", nameof(snapshots));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        using var mutex = new Mutex(initiallyOwned: false, CreateMutexName(fullPath));
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The prior writer exited while holding the mutex; the atomic file remains readable.
        }

        try
        {
            var existing = Read(fullPath);
            if (existing.FailureReason == InventoryCacheFailureReason.UnsupportedVersion)
            {
                throw new InvalidDataException("The inventory cache uses a newer format and was not overwritten.");
            }

            var merged = existing.IsSuccess
                ? existing.Snapshots.ToDictionary(snapshot => snapshot.AccountId, StringComparer.Ordinal)
                : new Dictionary<string, CachedInventorySnapshot>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!merged.TryGetValue(entry.AccountId, out var previous) || IsAtLeastAsRecent(entry, previous))
                {
                    merged[entry.AccountId] = entry;
                }
            }

            var bytes = Serialize(merged.Values);
            if (bytes.Length > MaximumFileBytes)
            {
                throw new InvalidDataException($"The cache exceeds the {MaximumFileBytes}-byte limit.");
            }

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                // Use the OS replacement primitive for an existing cache: unlike an
                // overwrite-move, it preserves atomic replacement under Windows readers
                // opened with FileShare.Delete. The writer mutex serializes creators.
                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
                else
                    File.Move(temporaryPath, fullPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static string GetPath(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        if (!Path.IsPathFullyQualified(localAppData))
        {
            throw new ArgumentException("The LocalAppData path must be fully qualified.", nameof(localAppData));
        }

        return Path.Combine(localAppData, "SplashtopUnified", "AccountBrowser", "inventory-cache.json");
    }

    private static string CreateMutexName(string fullPath)
    {
        var identity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "SplashtopUnified.InventoryCache." + hash;
    }

    private static byte[] Serialize(IEnumerable<CachedInventorySnapshot> snapshots)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteStartArray("snapshots");
            foreach (var entry in snapshots)
            {
                WriteSnapshot(writer, entry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Loads all cached snapshots or returns no snapshots and a reason; cache failures do not escape.</summary>
    public static InventoryCacheReadResult Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.IoError);

        try
        {
            // Windows replacement can expose a transient unavailable name to a new
            // opener. Serialize cooperating readers with writers across processes.
            // Mutex ownership is recursive, so Write can safely read while holding it.
            using var mutex = new Mutex(initiallyOwned: false, CreateMutexName(Path.GetFullPath(path)));
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { /* Ownership was acquired. Validate the file normally. */ }
            try { return ReadCore(path); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.IoError);
        }
    }

    private static InventoryCacheReadResult ReadCore(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.IoError);
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes)
            {
                return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.TooLarge);
            }

            var buffer = new byte[MaximumFileBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > MaximumFileBytes || stream.ReadByte() != -1)
            {
                return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.TooLarge);
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (!TryReadDocument(document.RootElement, out var entries, out var failureReason))
            {
                return InventoryCacheReadResult.Failed(failureReason);
            }

            return new InventoryCacheReadResult(entries, null);
        }
        catch (JsonException)
        {
            return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.Corrupt);
        }
        catch (FileNotFoundException)
        {
            return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.MissingFile);
        }
        catch (DirectoryNotFoundException)
        {
            return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.MissingFile);
        }
        catch (Exception)
        {
            return InventoryCacheReadResult.Failed(InventoryCacheFailureReason.IoError);
        }
    }

    private static CachedInventorySnapshot ToCachedSnapshot(AccountInventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var cached = new CachedInventorySnapshot(
            snapshot.AccountId,
            snapshot.Outcome,
            snapshot.PageKind,
            snapshot.Authentication,
            snapshot.RowCount,
            snapshot.ReportedTotal,
            snapshot.Rows.Select(row => row with { Notes = null }).ToArray(),
            snapshot.CapturedAtUtc,
            null,
            snapshot.Mode,
            snapshot.LatestAttemptOutcome,
            null,
            snapshot.LatestAttemptAtUtc);
        if (!IsValid(cached))
        {
            throw new ArgumentException("The snapshot contains data outside the cache contract.", nameof(snapshot));
        }

        return cached;
    }

    private static bool TryReadDocument(
        JsonElement root,
        out IReadOnlyList<CachedInventorySnapshot> snapshots,
        out InventoryCacheFailureReason failureReason)
    {
        snapshots = [];
        failureReason = InventoryCacheFailureReason.InvalidPayload;
        if (root.ValueKind != JsonValueKind.Object || !HasExactlyProperties(root, DocumentFields))
        {
            return false;
        }

        if (!root.TryGetProperty("formatVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version))
        {
            return false;
        }

        if (version != FormatVersion)
        {
            failureReason = InventoryCacheFailureReason.UnsupportedVersion;
            return false;
        }

        if (!root.TryGetProperty("snapshots", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var loaded = new List<CachedInventorySnapshot>(array.GetArrayLength());
        var accountIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            if (!TryReadSnapshot(element, out var entry) || !accountIds.Add(entry.AccountId))
            {
                return false;
            }

            loaded.Add(entry);
        }

        snapshots = loaded;
        return true;
    }

    private static bool TryReadSnapshot(JsonElement element, out CachedInventorySnapshot snapshot)
    {
        snapshot = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            (!HasExactlyProperties(element, CurrentSnapshotFields) &&
             !HasExactlyProperties(element, SnapshotFields) &&
             !HasExactlyProperties(element, IntermediateSnapshotFields) &&
             !HasExactlyProperties(element, LegacySnapshotFields)) ||
            !TryString(element, "accountId", MaximumFieldLength, out var accountId) || string.IsNullOrWhiteSpace(accountId) ||
            !TryEnum(element, "outcome", out InventoryOutcome outcome) ||
            !TryEnum(element, "pageKind", out ConsolePageKind pageKind) ||
            !TryEnum(element, "authentication", out ConsoleAuthentication authentication) ||
            !TryInteger(element, "rowCount", out var rowCount) || rowCount < 0 || rowCount > MaximumRowsPerAccount ||
            !TryNullableInteger(element, "reportedTotal", out var reportedTotal) || reportedTotal < 0 ||
            !TryDateTimeOffset(element, "capturedAtUtc", out var capturedAtUtc) ||
            !TryOptionalStringOrMissing(element, "diagnostic", MaximumFieldLength, out var diagnostic) ||
            !TryOptionalString(element, "mode", MaximumFieldLength, out var mode) ||
            !TryNullableEnum(element, "latestAttemptOutcome", out InventoryOutcome? latestAttemptOutcome) ||
            !TryOptionalStringOrMissing(element, "latestAttemptDiagnostic", MaximumFieldLength, out var latestAttemptDiagnostic) ||
            !TryOptionalDateTimeOffset(element, "latestAttemptAtUtc", out var latestAttemptAtUtc) ||
            !TryRows(element, out var rows) || rowCount != rows.Count)
        {
            return false;
        }

        if (mode is not (null or "none" or "single" or "paged" or "scrolled") ||
            capturedAtUtc > DateTimeOffset.UtcNow + MaximumFutureClockSkew ||
            (latestAttemptOutcome is { } attempt && Quality(attempt) >= Quality(outcome)))
        {
            return false;
        }

        if (latestAttemptOutcome is not null && latestAttemptAtUtc is null)
        {
            // Earlier version-1 development payloads had attempt outcome but no separate timestamp.
            latestAttemptAtUtc = capturedAtUtc;
        }

        snapshot = new CachedInventorySnapshot(
            accountId,
            outcome,
            pageKind,
            authentication,
            rowCount,
            reportedTotal,
            rows,
            capturedAtUtc,
            diagnostic,
            mode,
            latestAttemptOutcome,
            latestAttemptDiagnostic,
            latestAttemptAtUtc);
        return IsValid(snapshot);
    }

    private static bool TryRows(JsonElement snapshot, out IReadOnlyList<ExtractedComputerRow> rows)
    {
        rows = [];
        if (!snapshot.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > MaximumRowsPerAccount)
        {
            return false;
        }

        var loaded = new List<ExtractedComputerRow>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                (!HasExactlyProperties(element, CurrentRowFields) && !HasExactlyProperties(element, RowFields)) ||
                !TryString(element, "name", MaximumFieldLength, out var name) || string.IsNullOrWhiteSpace(name) ||
                !TryOptionalString(element, "deviceName", MaximumFieldLength, out var deviceName) ||
                !TryOptionalString(element, "group", MaximumFieldLength, out var group) ||
                !TryOptionalStringOrMissing(element, "notes", MaximumFieldLength, out var notes) ||
                !TryBoolean(element, "hasConnectControl", out var hasConnectControl) ||
                !TryOptionalString(element, "status", MaximumStatusLength, out var status))
            {
                return false;
            }

            loaded.Add(new ExtractedComputerRow(name, deviceName, group, notes, hasConnectControl, status));
        }

        rows = loaded;
        return true;
    }

    private static bool IsValid(CachedInventorySnapshot snapshot)
    {
        if (!IsSafeCachedText(snapshot.AccountId, MaximumFieldLength) || string.IsNullOrWhiteSpace(snapshot.AccountId) ||
            !Enum.IsDefined(snapshot.Outcome) || !Enum.IsDefined(snapshot.PageKind) || !Enum.IsDefined(snapshot.Authentication) ||
            snapshot.RowCount < 0 || snapshot.RowCount > MaximumRowsPerAccount || snapshot.Rows is null ||
            snapshot.RowCount != snapshot.Rows.Count || snapshot.ReportedTotal < 0 ||
            snapshot.CapturedAtUtc > DateTimeOffset.UtcNow + MaximumFutureClockSkew ||
            (snapshot.LatestAttemptOutcome is { } attempt &&
             (!Enum.IsDefined(attempt) || Quality(attempt) >= Quality(snapshot.Outcome))) ||
            (snapshot.LatestAttemptOutcome is null) != (snapshot.LatestAttemptAtUtc is null) ||
            (snapshot.LatestAttemptAtUtc is { } attemptAt &&
             (attemptAt < snapshot.CapturedAtUtc || attemptAt > DateTimeOffset.UtcNow + MaximumFutureClockSkew)) ||
            (snapshot.LatestAttemptOutcome is null && snapshot.LatestAttemptDiagnostic is not null) ||
            !IsOptionalSafe(snapshot.Diagnostic, MaximumFieldLength) ||
            !IsOptionalSafe(snapshot.LatestAttemptDiagnostic, MaximumFieldLength) ||
            !IsOptionalClean(snapshot.Mode, MaximumFieldLength) ||
            snapshot.Mode is not (null or "none" or "single" or "paged" or "scrolled"))
        {
            return false;
        }

        return snapshot.Rows.All(row => row is not null &&
            IsSafeCachedText(row.Name, MaximumFieldLength) && !string.IsNullOrWhiteSpace(row.Name) &&
            IsOptionalSafe(row.DeviceName, MaximumFieldLength) &&
            IsOptionalSafe(row.Group, MaximumFieldLength) &&
            IsOptionalSafe(row.Notes, MaximumFieldLength) &&
            IsOptionalSafe(row.Status, MaximumStatusLength));
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, CachedInventorySnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("accountId", snapshot.AccountId);
        writer.WriteString("outcome", snapshot.Outcome.ToString());
        writer.WriteString("pageKind", snapshot.PageKind.ToString());
        writer.WriteString("authentication", snapshot.Authentication.ToString());
        writer.WriteNumber("rowCount", snapshot.RowCount);
        if (snapshot.ReportedTotal is { } reportedTotal)
        {
            writer.WriteNumber("reportedTotal", reportedTotal);
        }
        else
        {
            writer.WriteNull("reportedTotal");
        }

        writer.WriteString("capturedAtUtc", snapshot.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteNullableString(writer, "mode", snapshot.Mode);
        if (snapshot.LatestAttemptOutcome is { } latestAttemptOutcome)
        {
            writer.WriteString("latestAttemptOutcome", latestAttemptOutcome.ToString());
        }
        else
        {
            writer.WriteNull("latestAttemptOutcome");
        }

        if (snapshot.LatestAttemptAtUtc is { } latestAttemptAtUtc)
        {
            writer.WriteString("latestAttemptAtUtc", latestAttemptAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull("latestAttemptAtUtc");
        }

        writer.WriteStartArray("rows");
        foreach (var row in snapshot.Rows)
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            WriteNullableString(writer, "deviceName", row.DeviceName);
            WriteNullableString(writer, "group", row.Group);
            writer.WriteBoolean("hasConnectControl", row.HasConnectControl);
            WriteNullableString(writer, "status", row.Status);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(property);
        }
        else
        {
            writer.WriteString(property, value);
        }
    }

    private static bool HasExactlyProperties(JsonElement element, IReadOnlyCollection<string> expected)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
            {
                return false;
            }
        }

        return actual.Count == expected.Count && expected.All(actual.Contains);
    }

    private static bool TryString(JsonElement element, string property, int maximumLength, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = candidate.GetString();
        if (text is null || !IsClean(text, maximumLength))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string property, int maximumLength, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate))
        {
            return false;
        }

        if (candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = candidate.GetString();
        if (text is null || !IsClean(text, maximumLength))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryInteger(JsonElement element, string property, out int value)
    {
        value = default;
        return element.TryGetProperty(property, out var candidate) &&
            candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt32(out value);
    }

    private static bool TryNullableInteger(JsonElement element, string property, out int? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate))
        {
            return false;
        }

        if (candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind != JsonValueKind.Number || !candidate.TryGetInt32(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryBoolean(JsonElement element, string property, out bool value)
    {
        value = default;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = candidate.GetBoolean();
        return true;
    }

    private static bool TryEnum<TEnum>(JsonElement element, string property, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return element.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.String &&
            Enum.TryParse(candidate.GetString(), ignoreCase: false, out value) && Enum.IsDefined(value);
    }

    private static bool TryNullableEnum<TEnum>(JsonElement element, string property, out TEnum? value)
        where TEnum : struct, Enum
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate))
        {
            return true;
        }

        if (candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind != JsonValueKind.String ||
            !Enum.TryParse(candidate.GetString(), ignoreCase: false, out TEnum parsed) || !Enum.IsDefined(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryOptionalStringOrMissing(
        JsonElement element,
        string property,
        int maximumLength,
        out string? value)
    {
        if (!element.TryGetProperty(property, out _))
        {
            value = null;
            return true;
        }

        return TryOptionalString(element, property, maximumLength, out value);
    }

    private static bool TryOptionalDateTimeOffset(JsonElement element, string property, out DateTimeOffset? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(candidate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryDateTimeOffset(JsonElement element, string property, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(candidate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    private static int Quality(InventoryOutcome outcome) => outcome switch
    {
        InventoryOutcome.Complete => 2,
        InventoryOutcome.Incomplete => 1,
        InventoryOutcome.Unavailable => 0,
        _ => -1
    };

    private static bool IsAtLeastAsRecent(CachedInventorySnapshot candidate, CachedInventorySnapshot current)
    {
        var candidateUpdatedAt = candidate.LatestAttemptAtUtc ?? candidate.CapturedAtUtc;
        var currentUpdatedAt = current.LatestAttemptAtUtc ?? current.CapturedAtUtc;
        if (candidateUpdatedAt != currentUpdatedAt)
        {
            return candidateUpdatedAt > currentUpdatedAt;
        }

        if (candidate.LatestAttemptAtUtc is null && current.LatestAttemptAtUtc is not null)
        {
            return false;
        }

        if (candidate.LatestAttemptAtUtc is not null && current.LatestAttemptAtUtc is null)
        {
            return true;
        }

        return candidate.CapturedAtUtc >= current.CapturedAtUtc;
    }

    private static bool IsOptionalClean(string? value, int maximumLength) => value is null || IsClean(value, maximumLength);

    private static bool IsOptionalSafe(string? value, int maximumLength) => value is null || IsSafeCachedText(value, maximumLength);

    private static bool IsSafeCachedText(string value, int maximumLength) =>
        IsClean(value, maximumLength) &&
        !value.Contains('<') && !value.Contains('>') &&
        !value.Contains("&lt;", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("&gt;", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("://", StringComparison.Ordinal) &&
        !value.Contains("www.", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("data:", StringComparison.OrdinalIgnoreCase) &&
        !LooksLikeBareUrl(value) &&
        !LooksLikeCredential(value);

    private static bool LooksLikeBareUrl(string value)
    {
        var candidate = value.Trim();
        return !candidate.Contains(' ') && candidate.Contains('.') &&
            Uri.TryCreate("https://" + candidate, UriKind.Absolute, out var uri) &&
            uri.Host.Contains('.', StringComparison.Ordinal);
    }

    private static bool LooksLikeCredential(string value) =>
        value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("passwd=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("authorization:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("cookie:", StringComparison.OrdinalIgnoreCase);

    private static bool IsClean(string value, int maximumLength) =>
        value.Length <= maximumLength && !value.Any(char.IsControl);
}
