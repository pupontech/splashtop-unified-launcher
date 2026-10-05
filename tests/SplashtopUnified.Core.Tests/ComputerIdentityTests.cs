using Xunit;

namespace SplashtopUnified.Core.Tests;

public sealed class ComputerIdentityTests
{
    [Fact]
    public void Identity_UsesAccountAndNumericConsoleId_NotNameOrMac()
    {
        var first = new Computer("account-a", 42, "Shared name", MacAddress: "AA:BB:CC:DD:EE:01");
        var renamed = new Computer("account-a", 42, "Renamed", MacAddress: "AA:BB:CC:DD:EE:02");

        Assert.Equal(first.Identity, renamed.Identity);
    }

    [Fact]
    public void Identity_DistinguishesAccountAndNumericConsoleId()
    {
        var accountB = new Computer("account-b", 42, "same name");
        var idB = new Computer("account-a", 43, "same name");

        Assert.NotEqual(new Computer("account-a", 42, "same name").Identity, accountB.Identity);
        Assert.NotEqual(new Computer("account-a", 42, "same name").Identity, idB.Identity);
    }

    [Fact]
    public void MissingIdentityComponents_DoNotFallBackToNameOrMac()
    {
        var missingAccount = new Computer(null, 42, "same", MacAddress: "AA:BB:CC:DD:EE:FF");
        var missingConsoleId = new Computer("account-a", null, "same", MacAddress: "AA:BB:CC:DD:EE:FF");

        Assert.Null(missingAccount.Identity);
        Assert.Null(missingConsoleId.Identity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankAccountId_DoesNotFormRemoteIdentity(string accountId)
    {
        var blank = new Computer(accountId, 42, "same", MacAddress: "AA:BB:CC:DD:EE:FF");

        Assert.Null(blank.Identity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ComputerIdentityRejectsBlankAccountIds(string? accountId)
    {
        Assert.Throws<ArgumentException>(() => new ComputerIdentity(accountId!, 42));
    }

    [Fact]
    public void SameMacWithDifferentConsoleIds_RemainsDistinct()
    {
        var first = new InventoryItem(
            new Computer("account-a", 100, "Machine", MacAddress: "AA:BB:CC:DD:EE:FF"));
        var second = new InventoryItem(
            new Computer("account-a", 101, "Machine", MacAddress: "AA:BB:CC:DD:EE:FF"));

        var results = ComputerQuery.Execute([first, second]);

        Assert.Equal([100L, 101L], results.Select(item => item.Computer.SplashtopComputerId));
        Assert.NotEqual(results[0].Computer.Identity, results[1].Computer.Identity);
    }

    [Fact]
    public void NullableComputerFieldsRemainNull()
    {
        var computer = new Computer("account-a", 1, null);

        Assert.Null(computer.Name);
        Assert.Null(computer.Hostname);
        Assert.Null(computer.GroupName);
        Assert.Null(computer.MacAddress);
        Assert.Null(computer.LastOnlineUtc);
        Assert.Null(computer.LoggedInUser);
        Assert.Null(computer.Notes);
        Assert.Null(computer.OperatingSystem);
    }

    [Fact]
    public void ComputerCarriesNullableNormalizedFieldsAndBusyStatusAsData()
    {
        var lastOnline = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var lastSeen = new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero);
        var updated = new DateTimeOffset(2026, 10, 5, 11, 5, 0, TimeSpan.Zero);
        var computer = new Computer(
            "account-a",
            42,
            "Desk",
            Hostname: "desk-host",
            GroupName: "Support",
            MacAddress: "AA:BB:CC:DD:EE:FF",
            Status: ComputerStatus.Busy,
            StatusSource: ComputerStatusSource.Live,
            LastOnlineUtc: lastOnline,
            LoggedInUser: "tech",
            Notes: "Synthetic fixture",
            OperatingSystem: "Windows 11",
            LocalId: "local-42",
            TeamName: "North",
            OfficialLaunchUri: "st-business://synthetic-launch-target",
            OfficialLaunchMethod: "direct",
            WebConsoleActionTarget: "/synthetic/computer/42",
            LastSeenUtc: lastSeen,
            UpdatedUtc: updated);
        var sparse = new Computer("account-a", 43, null);

        Assert.Equal(ComputerStatus.Busy, computer.Status);
        Assert.Equal("local-42", computer.LocalId);
        Assert.Equal("North", computer.TeamName);
        Assert.Equal("Support", computer.GroupName);
        Assert.Equal("st-business://synthetic-launch-target", computer.OfficialLaunchUri);
        Assert.Equal("direct", computer.OfficialLaunchMethod);
        Assert.Equal("/synthetic/computer/42", computer.WebConsoleActionTarget);
        Assert.Equal(lastOnline, computer.LastOnlineUtc);
        Assert.Equal(lastSeen, computer.LastSeenUtc);
        Assert.Equal(updated, computer.UpdatedUtc);
        Assert.Null(sparse.Status);
        Assert.Null(sparse.StatusSource);
        Assert.Null(sparse.LocalId);
        Assert.Null(sparse.TeamName);
        Assert.Null(sparse.OfficialLaunchUri);
        Assert.Null(sparse.OfficialLaunchMethod);
        Assert.Null(sparse.WebConsoleActionTarget);
        Assert.Null(sparse.LastSeenUtc);
        Assert.Null(sparse.UpdatedUtc);
    }

    [Fact]
    public void SplashtopAccountCarriesConfiguredIdentityAndProfileFields()
    {
        var lastSuccessfulSync = new DateTimeOffset(2026, 10, 5, 11, 30, 0, TimeSpan.Zero);
        var account = new SplashtopAccount(
            "account-a",
            "Operations",
            "ops@example.test",
            "business-profile-a",
            "https://my.splashtop.example",
            "us",
            lastSuccessfulSync,
            AccountStatus.AuthenticationRequired);

        Assert.Equal("account-a", account.AccountId);
        Assert.Equal("Operations", account.DisplayName);
        Assert.Equal("ops@example.test", account.Email);
        Assert.Equal("business-profile-a", account.ProfileName);
        Assert.Equal("https://my.splashtop.example", account.BaseUrl);
        Assert.Equal("us", account.Region);
        Assert.Equal(lastSuccessfulSync, account.LastSuccessfulSyncUtc);
        Assert.Equal(AccountStatus.AuthenticationRequired, account.Status);
        Assert.Equal(
            [AccountStatus.Unknown, AccountStatus.Authenticated, AccountStatus.AuthenticationRequired, AccountStatus.Error],
            Enum.GetValues<AccountStatus>());
    }

    [Fact]
    public void LocalComputerMetadataIsKeyedSeparateAndCopiesItsTagsAsReadOnlyInput()
    {
        var suppliedTags = new List<string> { "priority" };
        var metadata = new LocalComputerMetadata("account-a", 1, isFavorite: true, alias: "Front desk", tags: suppliedTags);
        var item = new InventoryItem(new Computer("account-a", 1, "PC"), metadata);

        suppliedTags.Add("mutated after construction");

        Assert.Same(metadata, item.LocalMetadata);
        var attachedMetadata = Assert.IsType<LocalComputerMetadata>(item.LocalMetadata);
        Assert.True(attachedMetadata.IsFavorite);
        Assert.Equal("Front desk", attachedMetadata.Alias);
        Assert.Equal(["priority"], attachedMetadata.Tags);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(attachedMetadata.Tags);
        Assert.Equal(new ComputerIdentity("account-a", 1), attachedMetadata.Identity);
        Assert.Throws<NotSupportedException>(() =>
        {
            ((IList<string>)attachedMetadata.Tags).Add("mutate");
        });
    }

    [Fact]
    public void InventoryItemRejectsMetadataForAnotherCompositeIdentity()
    {
        var computer = new Computer("account-a", 1, "Same name", MacAddress: "AA:BB:CC:DD:EE:FF");
        var otherAccountMetadata = new LocalComputerMetadata("account-b", 1, isFavorite: true);
        var otherComputerMetadata = new LocalComputerMetadata("account-a", 2, isFavorite: true);

        Assert.Throws<ArgumentException>(() => new InventoryItem(computer, otherAccountMetadata));
        Assert.Throws<ArgumentException>(() => new InventoryItem(computer, otherComputerMetadata));
    }

    [Fact]
    public void IncompleteRemoteIdentityDoesNotRequireInventedLocalMetadataKey()
    {
        var item = new InventoryItem(new Computer(null, null, "Unidentified synthetic record"));

        Assert.Null(item.Computer.Identity);
        Assert.Null(item.LocalMetadata);
    }
}
