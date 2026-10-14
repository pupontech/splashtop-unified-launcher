namespace SplashtopUnified.Core;

public sealed class LocalComputerMetadata
{
    private readonly IReadOnlyList<string> _tags;

    public LocalComputerMetadata(
        string accountId,
        long splashtopComputerId,
        bool isFavorite = false,
        string? alias = null,
        IEnumerable<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("An account ID is required for keyed local metadata.", nameof(accountId));
        }

        AccountId = accountId;
        SplashtopComputerId = splashtopComputerId;
        Identity = new ComputerIdentity(accountId, splashtopComputerId);
        IsFavorite = isFavorite;
        Alias = alias;
        _tags = Array.AsReadOnly((tags ?? []).ToArray());
    }

    public string AccountId { get; }

    public long SplashtopComputerId { get; }

    public ComputerIdentity Identity { get; }

    public bool IsFavorite { get; }

    public string? Alias { get; }

    public IReadOnlyList<string> Tags => _tags;
}
