namespace SplashtopUnified.Core;

public enum AccountStatus
{
    Unknown,
    Authenticated,
    AuthenticationRequired,
    Error
}

public enum ComputerStatus
{
    Unknown,
    Online,
    Offline,
    Busy
}

public enum ComputerStatusSource
{
    Unknown,
    Live,
    Cached
}

public sealed record SplashtopAccount(
    string AccountId,
    string? DisplayName = null,
    string? Email = null,
    string? ProfileName = null,
    string? BaseUrl = null,
    string? Region = null,
    DateTimeOffset? LastSuccessfulSyncUtc = null,
    AccountStatus Status = AccountStatus.Unknown)
{
    private string _accountId = ValidateAccountId(AccountId);

    public string AccountId
    {
        get => _accountId;
        init => _accountId = ValidateAccountId(value);
    }

    private static string ValidateAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A non-blank account ID is required.", nameof(AccountId));
        }

        return accountId;
    }
}

public readonly record struct ComputerIdentity
{
    public ComputerIdentity(string accountId, long splashtopComputerId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A non-blank account ID is required for computer identity.", nameof(accountId));
        }

        AccountId = accountId;
        SplashtopComputerId = splashtopComputerId;
    }

    public string AccountId { get; }

    public long SplashtopComputerId { get; }
}

public sealed record Computer(
    string? AccountId,
    long? SplashtopComputerId,
    string? Name,
    string? Hostname = null,
    string? GroupName = null,
    string? MacAddress = null,
    ComputerStatus? Status = null,
    ComputerStatusSource? StatusSource = null,
    DateTimeOffset? LastOnlineUtc = null,
    string? LoggedInUser = null,
    string? Notes = null,
    string? OperatingSystem = null,
    string? LocalId = null,
    string? TeamName = null,
    string? OfficialLaunchUri = null,
    string? OfficialLaunchMethod = null,
    string? WebConsoleActionTarget = null,
    DateTimeOffset? LastSeenUtc = null,
    DateTimeOffset? UpdatedUtc = null)
{
    public ComputerIdentity? Identity =>
        !string.IsNullOrWhiteSpace(AccountId) && SplashtopComputerId.HasValue
            ? new ComputerIdentity(AccountId, SplashtopComputerId.Value)
            : null;
}

public sealed class InventoryItem
{
    public InventoryItem(Computer computer, LocalComputerMetadata? localMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(computer);

        if (localMetadata is not null)
        {
            if (computer.Identity is not { } computerIdentity)
            {
                throw new ArgumentException("Local metadata requires a complete computer identity.", nameof(computer));
            }

            if (computerIdentity != localMetadata.Identity)
            {
                throw new ArgumentException("Local metadata must match the computer's composite identity.", nameof(localMetadata));
            }
        }

        Computer = computer;
        LocalMetadata = localMetadata;
    }

    public Computer Computer { get; }

    public LocalComputerMetadata? LocalMetadata { get; }
}
