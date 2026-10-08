using System.Text;
using System.Text.Json.Nodes;
using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.Cache.Tests;

public sealed class InventoryCacheTests
{
    [Fact]
    public void RoundTripsAllowlistedFieldsWithoutPersistingAccountProfileDataOrUpgradingOutcome()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var captured = new DateTimeOffset(2026, 10, 5, 10, 30, 0, TimeSpan.Zero);
        var snapshot = Snapshot(captured);

        InventoryCache.Write(path, [snapshot]);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("Private Profile Label", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pagesVisited", json, StringComparison.Ordinal);
        Assert.DoesNotContain("walkMillis", json, StringComparison.Ordinal);
        var result = InventoryCache.Read(path);
        Assert.True(result.IsSuccess);
        var cached = Assert.Single(result.Snapshots);
        Assert.Equal("account-1", cached.AccountId);
        Assert.Equal(InventoryOutcome.Incomplete, cached.Outcome);
        Assert.Equal(ConsolePageKind.ComputerList, cached.PageKind);
        Assert.Equal(ConsoleAuthentication.Authenticated, cached.Authentication);
        Assert.Equal(1, cached.RowCount);
        Assert.Equal(4, cached.ReportedTotal);
        Assert.Equal(captured, cached.CapturedAtUtc);
        Assert.Null(cached.Diagnostic);
        Assert.Equal("paged", cached.Mode);
        Assert.Equal(snapshot.Rows.Select(row => row with { Notes = null }), cached.Rows);
    }

    [Fact]
    public void AtomicWriteLeavesNoTemporaryFileBehind()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");

        InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow)]);

        Assert.True(File.Exists(path));
        Assert.Equal(new[] { "inventory.json" }, Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void CorruptJsonFailsClosedWithMachineReadableReason()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Write("{ not json");

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.Corrupt, result.FailureReason);
    }

    [Fact]
    public void UnknownVersionIsRefused()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteThenMutate(directory, json => json.Replace("\"formatVersion\":1", "\"formatVersion\":999", StringComparison.Ordinal));

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.UnsupportedVersion, result.FailureReason);
    }

    [Fact]
    public void WritingNeverOverwritesAnUnsupportedFutureVersion()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteThenMutate(directory, json => json.Replace("\"formatVersion\":1", "\"formatVersion\":999", StringComparison.Ordinal));
        var before = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow)]));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void LastFailedAttemptAndCachedMarkerSurviveRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var snapshot = Snapshot(DateTimeOffset.UtcNow) with
        {
            Outcome = InventoryOutcome.Complete,
            ReportedTotal = 1,
            LatestAttemptOutcome = InventoryOutcome.Unavailable,
            LatestAttemptDiagnostic = "refresh timed out",
            LatestAttemptAtUtc = DateTimeOffset.UtcNow
        };

        InventoryCache.Write(path, [snapshot]);

        var cached = Assert.Single(InventoryCache.Read(path).Snapshots);
        var accountSnapshot = cached.ToAccountInventorySnapshot("Current display label");
        Assert.Equal(InventoryOutcome.Complete, accountSnapshot.Outcome);
        Assert.Equal(InventoryOutcome.Unavailable, accountSnapshot.LatestAttemptOutcome);
        Assert.Null(accountSnapshot.LatestAttemptDiagnostic);
        Assert.Equal(snapshot.LatestAttemptAtUtc, accountSnapshot.LatestAttemptAtUtc);
        Assert.True(accountSnapshot.IsCached);
        Assert.Equal("Current display label", accountSnapshot.AccountName);
    }

    [Fact]
    public void PreviousVersionOnePayloadWithoutAttemptFieldsRemainsReadable()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteThenMutate(directory, json =>
        {
            var document = JsonNode.Parse(json)!.AsObject();
            var snapshot = document["snapshots"]![0]!.AsObject();
            snapshot["diagnostic"] = "legacy inventory read";
            snapshot.Remove("latestAttemptOutcome");
            snapshot.Remove("latestAttemptAtUtc");
            snapshot["rows"]![0]!["notes"] = "legacy row notes";
            return document.ToJsonString();
        });

        var result = InventoryCache.Read(path);

        Assert.True(result.IsSuccess);
        var legacy = Assert.Single(result.Snapshots);
        Assert.Null(legacy.LatestAttemptOutcome);
        Assert.Equal("legacy inventory read", legacy.Diagnostic);
        Assert.Equal("legacy row notes", Assert.Single(legacy.Rows).Notes);
    }

    [Fact]
    public void OrdinaryFreeFormNotesAndDiagnosticsAreOmittedFromNewWrites()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var captured = DateTimeOffset.UtcNow;
        const string notes = "Mira plans to work from home next Thursday; use extension 4821.";
        const string diagnostic = "Customer requested that the second-floor workstation stay hidden until Monday.";
        const string latestAttemptDiagnostic = "Operator confirmed that the account owner is traveling this week.";
        var snapshot = Snapshot(captured) with
        {
            Outcome = InventoryOutcome.Complete,
            ReportedTotal = 1,
            Diagnostic = diagnostic,
            LatestAttemptOutcome = InventoryOutcome.Unavailable,
            LatestAttemptDiagnostic = latestAttemptDiagnostic,
            LatestAttemptAtUtc = captured.AddSeconds(10),
            Rows = [new ExtractedComputerRow("Computer", "device-01", "Lab", notes, true, "online")]
        };

        InventoryCache.Write(path, [snapshot]);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain(notes, json, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, json, StringComparison.Ordinal);
        Assert.DoesNotContain(latestAttemptDiagnostic, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"notes\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"diagnostic\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"latestAttemptDiagnostic\"", json, StringComparison.Ordinal);

        var cached = Assert.Single(InventoryCache.Read(path).Snapshots);
        Assert.Null(cached.Diagnostic);
        Assert.Null(cached.LatestAttemptDiagnostic);
        var row = Assert.Single(cached.Rows);
        Assert.Null(row.Notes);
        Assert.Equal("Computer", row.Name);
        Assert.Equal("device-01", row.DeviceName);
        Assert.Equal("Lab", row.Group);
        Assert.True(row.HasConnectControl);
        Assert.Equal("online", row.Status);
        Assert.Equal(InventoryOutcome.Unavailable, cached.LatestAttemptOutcome);
        Assert.Equal(snapshot.LatestAttemptAtUtc, cached.LatestAttemptAtUtc);
    }

    [Fact]
    public void RewritingLegacyVersionOneCacheDropsPriorFreeTextButPreservesOutcomeAndTime()
    {
        using var directory = new TemporaryDirectory();
        const string legacyNote = "Mira plans to work from home next Thursday; use extension 4821.";
        const string legacyDiagnostic = "Customer requested that the workstation stay hidden until Monday.";
        const string legacyAttemptDiagnostic = "Operator confirmed that the account owner is traveling this week.";
        var captured = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(captured) with
        {
            Outcome = InventoryOutcome.Complete,
            ReportedTotal = 1,
            Diagnostic = legacyDiagnostic,
            LatestAttemptOutcome = InventoryOutcome.Unavailable,
            LatestAttemptDiagnostic = legacyAttemptDiagnostic,
            LatestAttemptAtUtc = captured.AddSeconds(10),
            Rows = [new ExtractedComputerRow("Computer", "device-01", "Lab", legacyNote, true, "online")]
        };
        var path = Path.Combine(directory.Path, "inventory.json");
        InventoryCache.Write(path, [snapshot]);

        // Restore the old format-v1 free-text fields to model a cache written by an earlier build.
        var priorVersionOne = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var priorSnapshot = priorVersionOne["snapshots"]![0]!.AsObject();
        priorSnapshot["diagnostic"] = legacyDiagnostic;
        priorSnapshot["latestAttemptDiagnostic"] = legacyAttemptDiagnostic;
        priorSnapshot["rows"]![0]!["notes"] = legacyNote;
        File.WriteAllText(path, priorVersionOne.ToJsonString(), new UTF8Encoding(false));

        Assert.True(InventoryCache.Read(path).IsSuccess);
        InventoryCache.Write(path, []);

        var rewrittenJson = File.ReadAllText(path);
        Assert.DoesNotContain(legacyNote, rewrittenJson, StringComparison.Ordinal);
        Assert.DoesNotContain(legacyDiagnostic, rewrittenJson, StringComparison.Ordinal);
        Assert.DoesNotContain(legacyAttemptDiagnostic, rewrittenJson, StringComparison.Ordinal);
        var rewritten = Assert.Single(InventoryCache.Read(path).Snapshots);
        Assert.Null(rewritten.Rows[0].Notes);
        Assert.Null(rewritten.Diagnostic);
        Assert.Null(rewritten.LatestAttemptDiagnostic);
        Assert.Equal(InventoryOutcome.Complete, rewritten.Outcome);
        Assert.Equal(InventoryOutcome.Unavailable, rewritten.LatestAttemptOutcome);
        Assert.Equal(captured, rewritten.CapturedAtUtc);
        Assert.Equal(snapshot.LatestAttemptAtUtc, rewritten.LatestAttemptAtUtc);
    }

    [Fact]
    public void OversizedFileFailsClosedBeforeLoadingData()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        File.WriteAllBytes(path, new byte[InventoryCache.MaximumFileBytes + 1]);

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.TooLarge, result.FailureReason);
    }

    [Fact]
    public void RowCountMismatchFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteThenMutate(directory, json => json.Replace("\"rowCount\":1", "\"rowCount\":2", StringComparison.Ordinal));

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.InvalidPayload, result.FailureReason);
    }

    [Fact]
    public void ControlCharacterInRowFieldFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteThenMutate(directory, json => json.Replace("\"name\":\"Computer\"", "\"name\":\"Bad\\u0001Name\"", StringComparison.Ordinal));

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.InvalidPayload, result.FailureReason);
    }

    [Fact]
    public void MissingRowNameFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteThenMutate(directory, json => json.Replace("\"name\":\"Computer\",", string.Empty, StringComparison.Ordinal));

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.InvalidPayload, result.FailureReason);
    }

    [Fact]
    public void StalenessIsComputedAndExactlyAtLimitIsNotStale()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        InventoryCache.Write(path, [Snapshot(now - TimeSpan.FromMinutes(10))]);
        var cached = Assert.Single(InventoryCache.Read(path).Snapshots);

        Assert.Equal(TimeSpan.FromMinutes(10), cached.AgeAt(now));
        Assert.False(cached.IsStale(TimeSpan.FromMinutes(10), now));
        Assert.True(cached.IsStale(TimeSpan.FromMinutes(9), now));
    }

    [Fact]
    public void FutureCaptureTimestampFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var captured = DateTimeOffset.UtcNow;
        var path = Path.Combine(directory.Path, "inventory.json");
        InventoryCache.Write(path, [Snapshot(captured)]);
        var json = File.ReadAllText(path);
        var marker = "\"capturedAtUtc\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = json.IndexOf('"', start);
        json = json[..start] + "2099-01-01T00:00:00.0000000+00:00" + json[end..];
        File.WriteAllText(path, json, new UTF8Encoding(false));

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.InvalidPayload, result.FailureReason);
    }

    [Fact]
    public void FutureFailedAttemptTimestampFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);
        var snapshot = Snapshot(captured) with
        {
            Outcome = InventoryOutcome.Complete,
            ReportedTotal = 1,
            LatestAttemptOutcome = InventoryOutcome.Unavailable,
            LatestAttemptDiagnostic = "refresh failed",
            LatestAttemptAtUtc = DateTimeOffset.UtcNow
        };
        var path = Path.Combine(directory.Path, "inventory.json");
        InventoryCache.Write(path, [snapshot]);
        var json = File.ReadAllText(path);
        var marker = "\"latestAttemptAtUtc\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = json.IndexOf('"', start);
        json = json[..start] + "2099-01-01T00:00:00.0000000+00:00" + json[end..];
        File.WriteAllText(path, json, new UTF8Encoding(false));

        var result = InventoryCache.Read(path);

        Assert.Empty(result.Snapshots);
        Assert.Equal(InventoryCacheFailureReason.InvalidPayload, result.FailureReason);
    }

    [Fact]
    public async Task ConcurrentWritersPreserveDistinctAccountSnapshots()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        using var start = new Barrier(3);
        var first = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 8; i++)
            {
                InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow) with { AccountId = "account-1" }]);
            }
        });
        var second = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 8; i++)
            {
                InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow) with { AccountId = "account-2" }]);
            }
        });
        start.SignalAndWait();
        await Task.WhenAll(first, second);

        var result = InventoryCache.Read(path);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "account-1", "account-2" }, result.Snapshots.Select(snapshot => snapshot.AccountId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OlderWriteCannotReplaceANewerSnapshotForTheSameAccount()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var now = DateTimeOffset.UtcNow;
        var newer = Snapshot(now) with
        {
            Rows = [new ExtractedComputerRow("Newer", null, null, null, true)]
        };
        var older = newer with
        {
            CapturedAtUtc = now - TimeSpan.FromMinutes(1),
            Rows = [new ExtractedComputerRow("Older", null, null, null, true)]
        };

        InventoryCache.Write(path, [newer]);
        InventoryCache.Write(path, [older]);

        Assert.Equal("Newer", Assert.Single(InventoryCache.Read(path).Snapshots).Rows[0].Name);
    }

    [Fact]
    public void StaleWriterCannotEraseANewerFailedAttemptForTheSameLastGoodSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var lastGood = Snapshot(DateTimeOffset.UtcNow.AddMinutes(-1)) with
        {
            Outcome = InventoryOutcome.Complete,
            ReportedTotal = 1
        };
        var failedAttempt = lastGood with
        {
            LatestAttemptOutcome = InventoryOutcome.Unavailable,
            LatestAttemptDiagnostic = "console unavailable",
            LatestAttemptAtUtc = DateTimeOffset.UtcNow
        };

        InventoryCache.Write(path, [failedAttempt]);
        InventoryCache.Write(path, [lastGood]);

        var loaded = Assert.Single(InventoryCache.Read(path).Snapshots);
        Assert.Equal(InventoryOutcome.Unavailable, loaded.LatestAttemptOutcome);
        Assert.Null(loaded.LatestAttemptDiagnostic);
    }

    [Fact]
    public void CachePathIsUnderTheProvidedPerUserLocalAppDataDirectory()
    {
        using var directory = new TemporaryDirectory();
        var localAppData = Path.Combine(directory.Path, "AppData", "Local");
        var path = InventoryCache.GetPath(localAppData);

        Assert.Equal(Path.Combine(localAppData, "SplashtopUnified", "AccountBrowser", "inventory-cache.json"), path);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("https://example.invalid/private")]
    [InlineData("example.invalid/private")]
    [InlineData("password=hunter2")]
    [InlineData("Authorization: Bearer token-value")]
    public void UnsafeLookingFreeFormNotesAreOmittedWithoutPersistingTheirContents(string unsafeText)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        var snapshot = Snapshot(DateTimeOffset.UtcNow) with
        {
            Rows = [new ExtractedComputerRow("Computer", null, null, unsafeText, true, null)]
        };

        InventoryCache.Write(path, [snapshot]);

        Assert.DoesNotContain(unsafeText, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(Assert.Single(InventoryCache.Read(path).Snapshots).Rows[0].Notes);
    }

    [Fact]
    public async Task ConcurrentReadersNeverObserveTornDataDuringAtomicReplacement()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inventory.json");
        InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow)]);
        var failures = new List<string>();
        var readCount = 0;
        var writeTask = Task.Run(() =>
        {
            for (var i = 0; i < 80; i++)
            {
                var rows = Enumerable.Range(0, 400)
                    .Select(n => new ExtractedComputerRow($"Computer {i}-{n}", $"device-{n}", "group", null, n % 2 == 0, "online"))
                    .ToArray();
                InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow, rows)]);
            }
        });
        var readTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            do
            {
                var read = InventoryCache.Read(path);
                Interlocked.Increment(ref readCount);
                if (!read.IsSuccess || read.Snapshots.Count != 1 || read.Snapshots[0].RowCount != read.Snapshots[0].Rows.Count)
                {
                    lock (failures)
                    {
                        failures.Add(read.FailureReason?.ToString() ?? "inconsistent row count");
                    }
                }
            }
            while (!writeTask.IsCompleted);
        })).ToArray();

        await Task.WhenAll(readTasks.Append(writeTask));

        Assert.Empty(failures);
        Assert.True(readCount > 0);
        Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));
    }

    private static string WriteThenMutate(TemporaryDirectory directory, Func<string, string> mutation)
    {
        var path = Path.Combine(directory.Path, "inventory.json");
        InventoryCache.Write(path, [Snapshot(DateTimeOffset.UtcNow)]);
        File.WriteAllText(path, mutation(File.ReadAllText(path)), new UTF8Encoding(false));
        return path;
    }

    private static AccountInventorySnapshot Snapshot(
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<ExtractedComputerRow>? rows = null) => new(
            "account-1",
            "Private Profile Label",
            InventoryOutcome.Incomplete,
            ConsolePageKind.ComputerList,
            ConsoleAuthentication.Authenticated,
            rows?.Count ?? 1,
            4,
            rows ?? [new ExtractedComputerRow("Computer", "device-01", "Lab", "desk", true, "online")],
            capturedAtUtc,
            "read was partial",
            PagesVisited: 3,
            WalkMillis: 90,
            Mode: "paged");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"splashtop-cache-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string text)
        {
            var path = System.IO.Path.Combine(Path, "inventory.json");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
