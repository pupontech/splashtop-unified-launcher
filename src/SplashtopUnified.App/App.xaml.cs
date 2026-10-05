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
                VerifyCsvImport();
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

    private static void VerifyCsvImport()
    {
        const string csv = "Computer Name,Hostname,Status,Notes\r\nSmoke fixture,smoke-host,Online,not live data";
        var imported = CsvInventoryReader.Read(csv, "smoke-test-account");
        if (imported.Count != 1 || imported[0].Computer.Name != "Smoke fixture" ||
            imported[0].Computer.Status != ComputerStatus.Online || imported[0].Computer.AccountId != "smoke-test-account")
        {
            throw new InvalidOperationException("The CSV import self-test did not return the expected local fixture.");
        }
    }
}
