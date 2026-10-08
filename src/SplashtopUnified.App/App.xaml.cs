using System.IO;
using System.Windows;
using SplashtopUnified.Core;
using SplashtopUnified.Prototype;

namespace SplashtopUnified.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (smokeTest)
            {
                VerifyImportAndPersistencePipeline();
            }

            var window = new MainWindow(smokeTest);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            MessageBox.Show(
                $"Splashtop Unified could not start.\n\n{exception.Message}\n\nDetails: {exception.GetType().Name}",
                "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void VerifyImportAndPersistencePipeline()
    {
        // Synthetic fixtures used only by --smoke-test; they are never loaded in normal app state.
        const string accountId = "smoke-test-account";
        var state = new ApplicationState();
        state.Accounts.Add(new AccountProfile(accountId, "Synthetic smoke fixture", null, ""));
        state.Items.Add(new InventoryItem(
            new Computer(accountId, 401, "Old synthetic device"),
            new LocalComputerMetadata(accountId, 401, isFavorite: true, alias: "Keep this alias", tags: ["keep-tag"])));
        state.Items.Add(new InventoryItem(
            new Computer("other-account", 401, "Same ID, other account"),
            new LocalComputerMetadata("other-account", 401, isFavorite: true, alias: "Other account alias")));
        state.Items.Add(new InventoryItem(
            new Computer("SMOKE-TEST-account", 401, "Case-distinct account"),
            new LocalComputerMetadata("SMOKE-TEST-account", 401, isFavorite: true, alias: "Case-distinct alias")));

        const string withId = "Name,ID,Status\r\nUpdated synthetic device,401,Offline";
        var matchingPlan = InventoryImportPipeline.Prepare(state.Items, accountId, withId);
        var updated = matchingPlan.ReplacementItems.Single(item => item.Computer.AccountId == accountId);
        if (matchingPlan.ImportedCount != 1 || matchingPlan.ReplacementItems.Count != 3 ||
            updated.LocalMetadata?.IsFavorite != true || updated.LocalMetadata.Alias != "Keep this alias" ||
            !updated.LocalMetadata.Tags.Contains("keep-tag") ||
            !matchingPlan.ReplacementItems.Any(item => item.Computer.AccountId == "other-account") ||
            !matchingPlan.ReplacementItems.Any(item => item.Computer.AccountId == "SMOKE-TEST-account"))
        {
            throw new InvalidOperationException("The account-scoped import did not preserve full composite-key metadata and other account rows.");
        }

        // Failed prepares must not mutate the caller's existing inventory list.
        var beforeFailure = matchingPlan.ReplacementItems;
        AssertImportFailsWithoutMutation(beforeFailure, accountId, "Name,ID\r\nMalformed,not-a-number");
        AssertImportFailsWithoutMutation(beforeFailure, accountId, "Name,ID\r\nDuplicate one,402\r\nDuplicate two,402");

        const string noIdCsv = "Name,Status\r\nRepeated name,Offline\r\nRepeated name,Unknown";
        var noIdPlan = InventoryImportPipeline.Prepare(beforeFailure, accountId, noIdCsv);
        var idlessRows = noIdPlan.ReplacementItems.Where(item => item.Computer.AccountId == accountId).ToArray();
        if (noIdPlan.ImportedCount != 2 || idlessRows.Length != 2 ||
            idlessRows.Any(item => item.Computer.SplashtopComputerId.HasValue || item.LocalMetadata is not null) ||
            idlessRows.Select(item => item.Computer.Name).Distinct().Count() != 1 ||
            noIdPlan.ReplacementItems.Count != 4)
        {
            throw new InvalidOperationException("The no-ID import must retain duplicate names without inventing IDs or metadata keys.");
        }

        var emptyPlan = InventoryImportPipeline.Prepare(noIdPlan.ReplacementItems, accountId, "Name,ID\r\n");
        if (emptyPlan.ImportedCount != 0 || !emptyPlan.ReplacementItems.SequenceEqual(noIdPlan.ReplacementItems))
        {
            throw new InvalidOperationException("An empty import must leave the current inventory unchanged.");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            throw new InvalidOperationException("Local AppData was unavailable for the JSON persistence smoke test.");
        }

        var smokePath = Path.Combine(appData, "SplashtopUnified", "Prototype", $"smoke-{Guid.NewGuid():N}.json");
        try
        {
            state.Items = matchingPlan.ReplacementItems;
            var store = new LocalStateStore(smokePath);
            store.Save(state);
            var reloaded = store.Load();
            var persisted = reloaded.Items.Single(item => item.Computer.AccountId == accountId);
            if (reloaded.Accounts.Count != 1 || reloaded.Items.Count != 3 ||
                persisted.LocalMetadata?.IsFavorite != true || persisted.LocalMetadata.Alias != "Keep this alias" ||
                !persisted.LocalMetadata.Tags.Contains("keep-tag"))
            {
                throw new InvalidOperationException("The local JSON state did not round-trip accounts, favorites, aliases, and tags.");
            }

            var json = File.ReadAllText(smokePath);
            if (json.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("cookie", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The local JSON smoke state unexpectedly contains a secret-like field.");
            }
        }
        finally
        {
            if (File.Exists(smokePath))
            {
                File.Delete(smokePath);
            }
        }
    }

    private static void AssertImportFailsWithoutMutation(List<InventoryItem> currentItems, string accountId, string csv)
    {
        var countBefore = currentItems.Count;
        var namesBefore = currentItems.Select(item => item.Computer.Name).ToArray();
        var failed = false;
        try
        {
            _ = InventoryImportPipeline.Prepare(currentItems, accountId, csv);
        }
        catch (FormatException)
        {
            failed = true;
        }

        if (!failed || currentItems.Count != countBefore ||
            !currentItems.Select(item => item.Computer.Name).SequenceEqual(namesBefore))
        {
            throw new InvalidOperationException("A malformed or duplicate CSV import changed the previous inventory.");
        }
    }
}
