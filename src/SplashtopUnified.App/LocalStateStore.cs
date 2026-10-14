using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SplashtopUnified.Core;

namespace SplashtopUnified.App;

internal sealed class LocalStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _path;

    public LocalStateStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _path = Path.GetFullPath(filePath);
            return;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Windows did not provide a Local AppData folder for saving the prototype's local state.");
        }

        _path = Path.Combine(root, "SplashtopUnified", "Prototype", "inventory.json");
    }

    public string FilePath => _path;

    public ApplicationState Load()
    {
        if (!File.Exists(_path))
        {
            return new ApplicationState();
        }

        StateDocument document;
        try
        {
            document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException("The local state file is empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Could not read local state at '{_path}'. Keep a backup and repair or rename the file before saving new changes. {exception.Message}",
                exception);
        }

        var state = new ApplicationState();
        foreach (var account in document.Accounts)
        {
            if (!string.IsNullOrWhiteSpace(account.AccountId) && !string.IsNullOrWhiteSpace(account.Name))
            {
                state.Accounts.Add(new AccountProfile(account.AccountId, account.Name, account.Email, account.ConsoleUrl ?? ""));
            }
        }

        foreach (var entry in document.Items)
        {
            if (string.IsNullOrWhiteSpace(entry.AccountId) || string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var computer = new Computer(
                AccountId: entry.AccountId,
                SplashtopComputerId: entry.ComputerId,
                Name: entry.Name,
                Hostname: entry.Hostname,
                GroupName: entry.GroupName,
                MacAddress: entry.MacAddress,
                Status: entry.Status,
                StatusSource: entry.StatusSource,
                LastOnlineUtc: entry.LastOnlineUtc,
                LoggedInUser: entry.LoggedInUser,
                Notes: entry.Notes,
                OperatingSystem: entry.OperatingSystem,
                LocalId: entry.LocalId,
                TeamName: entry.TeamName);
            LocalComputerMetadata? metadata = entry.ComputerId is { } computerId
                ? new LocalComputerMetadata(entry.AccountId, computerId, entry.IsFavorite, entry.Alias, entry.Tags)
                : null;
            state.Items.Add(new InventoryItem(computer, metadata));
        }

        return state;
    }

    public void Save(ApplicationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = new StateDocument
        {
            Accounts = state.Accounts.Select(account => new AccountDocument
            {
                AccountId = account.AccountId,
                Name = account.Name,
                Email = account.Email,
                ConsoleUrl = account.ConsoleUrl
            }).ToList(),
            Items = state.Items.Select(item => new InventoryDocument
            {
                AccountId = item.Computer.AccountId ?? "",
                ComputerId = item.Computer.SplashtopComputerId,
                Name = item.Computer.Name,
                Hostname = item.Computer.Hostname,
                GroupName = item.Computer.GroupName,
                MacAddress = item.Computer.MacAddress,
                Status = item.Computer.Status,
                StatusSource = item.Computer.StatusSource,
                LastOnlineUtc = item.Computer.LastOnlineUtc,
                LoggedInUser = item.Computer.LoggedInUser,
                Notes = item.Computer.Notes,
                OperatingSystem = item.Computer.OperatingSystem,
                LocalId = item.Computer.LocalId,
                TeamName = item.Computer.TeamName,
                IsFavorite = item.LocalMetadata?.IsFavorite == true,
                Alias = item.LocalMetadata?.Alias,
                Tags = item.LocalMetadata?.Tags.ToList() ?? []
            }).ToList()
        };

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"inventory.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class StateDocument
    {
        public List<AccountDocument> Accounts { get; set; } = [];
        public List<InventoryDocument> Items { get; set; } = [];
    }

    private sealed class AccountDocument
    {
        public string AccountId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public string? ConsoleUrl { get; set; }
    }

    private sealed class InventoryDocument
    {
        public string AccountId { get; set; } = "";
        public long? ComputerId { get; set; }
        public string? Name { get; set; }
        public string? Hostname { get; set; }
        public string? GroupName { get; set; }
        public string? MacAddress { get; set; }
        public ComputerStatus? Status { get; set; }
        public ComputerStatusSource? StatusSource { get; set; }
        public DateTimeOffset? LastOnlineUtc { get; set; }
        public string? LoggedInUser { get; set; }
        public string? Notes { get; set; }
        public string? OperatingSystem { get; set; }
        public string? LocalId { get; set; }
        public string? TeamName { get; set; }
        public bool IsFavorite { get; set; }
        public string? Alias { get; set; }
        public List<string> Tags { get; set; } = [];
    }
}
