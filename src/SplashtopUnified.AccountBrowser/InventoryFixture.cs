namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// Builds the local fixture page used to prove the inventory extractor at runtime.
/// The markup is invented from the column labels observed in the official Splashtop
/// support screenshot (Name / Device Name / Group / Notes plus a per-row Connect
/// control); it is NOT captured Splashtop DOM and contains no real account data.
/// </summary>
internal static class InventoryFixture
{
    public const string VirtualHost = "my.splashtop.com";

    public static string ComputerListHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Computers</title></head><body>" +
        "<div class='toolbar'>" +
        "  <button>Add Computer</button><button>Business App</button><button aria-label='Refresh'>Refresh</button>" +
        "  <button aria-label='Filter'>Filter</button><input type='search' aria-label='Search' />" +
        "</div>" +
        "<table id='computers'>" +
        "  <thead><tr><th>Name</th><th>Device Name</th><th>Group</th><th>Notes</th><th></th><th></th></tr></thead>" +
        "  <tbody>" +
        Row("Fixture Desktop", "fixture-desktop", "Default Group", "", true) +
        Row("Fixture Server", "fixture-server", "Servers", "lab", true) +
        Row("Fixture VM", "fixture-vm", "VMs", "", true) +
        Row("Fixture VM", "fixture-vm-2", "VMs", "duplicate display name", false) +
        "  </tbody>" +
        "</table></body></html>";

    public static string LoginHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>Sign in</title></head><body>" +
        "<form id='login'><h1>Sign in</h1>" +
        "<input type='email' name='email' autocomplete='username' />" +
        "<input type='password' name='password' autocomplete='current-password' />" +
        "<button type='submit'>Sign in</button></form></body></html>";

    private static string Row(string name, string device, string group, string notes, bool connect) =>
        "<tr>" +
        $"<td><span class='os-icon' aria-hidden='true'></span>{name}</td>" +
        $"<td>{device}</td><td>{group}</td><td>{notes}</td>" +
        "<td>" + (connect ? "<button class='row-connect' aria-label='Connect' title='Connect' onclick=\"window.__connectClicks=(window.__connectClicks||0)+1\">Connect</button>" : string.Empty) + "</td>" +
        "<td><button class='row-menu' aria-label='More actions' title='More'>...</button></td>" +
        "</tr>";
}
