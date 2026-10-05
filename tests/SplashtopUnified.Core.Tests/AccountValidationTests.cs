using Xunit;

namespace SplashtopUnified.Core.Tests;

public sealed class AccountValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void ConstructorRejectsNullOrBlankAccountId(string? accountId)
    {
        var exception = Assert.Throws<ArgumentException>(() => new SplashtopAccount(accountId!));

        Assert.Contains("A non-blank account ID is required.", exception.Message);
        Assert.Equal("AccountId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void WithReplacementRejectsNullOrBlankAccountId(string? accountId)
    {
        var account = new SplashtopAccount("valid-account");

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = account with { AccountId = accountId! };
        });

        Assert.Contains("A non-blank account ID is required.", exception.Message);
        Assert.Equal("AccountId", exception.ParamName);
    }

    [Fact]
    public void ValidAccountIdAndRecordCompatibilityArePreserved()
    {
        const string accountId = "  Account-A/Region  ";
        var lastSuccessfulSync = new DateTimeOffset(2026, 10, 5, 11, 30, 0, TimeSpan.Zero);
        var account = new SplashtopAccount(
            AccountId: accountId,
            DisplayName: "Operations",
            Email: "ops@example.test",
            ProfileName: "business-profile-a",
            BaseUrl: "https://my.splashtop.example",
            Region: "us",
            LastSuccessfulSyncUtc: lastSuccessfulSync,
            Status: AccountStatus.Authenticated);

        Assert.Equal(accountId, account.AccountId);
        Assert.Equal("Operations", account.DisplayName);
        Assert.Equal("ops@example.test", account.Email);
        Assert.Equal("business-profile-a", account.ProfileName);
        Assert.Equal("https://my.splashtop.example", account.BaseUrl);
        Assert.Equal("us", account.Region);
        Assert.Equal(lastSuccessfulSync, account.LastSuccessfulSyncUtc);
        Assert.Equal(AccountStatus.Authenticated, account.Status);

        var copy = account with { DisplayName = "Renamed" };
        Assert.Equal(accountId, copy.AccountId);
        Assert.Equal("Renamed", copy.DisplayName);

        var (deconstructedAccountId, displayName, email, profileName, baseUrl, region, syncUtc, status) = account;
        Assert.Equal(accountId, deconstructedAccountId);
        Assert.Equal("Operations", displayName);
        Assert.Equal("ops@example.test", email);
        Assert.Equal("business-profile-a", profileName);
        Assert.Equal("https://my.splashtop.example", baseUrl);
        Assert.Equal("us", region);
        Assert.Equal(lastSuccessfulSync, syncUtc);
        Assert.Equal(AccountStatus.Authenticated, status);
    }
}
