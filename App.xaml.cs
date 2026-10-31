using System.IO;
using System.Text.Json;
using System.Windows;

namespace Chisto;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--self-test")
        {
            var reportFile = e.Args.ElementAtOrDefault(1) ?? Path.Combine(AppContext.BaseDirectory, "chisto-tests.json");
            var report = SelfTests.Run(Path.GetDirectoryName(Path.GetFullPath(reportFile))!);
            File.WriteAllText(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(report.All(x => x.Passed) ? 0 : 1);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.FirstOrDefault() == "--scan" && e.Args.Length > 1)
            window.Loaded += async (_, _) => await window.StartScan(e.Args[1]);
    }
}
