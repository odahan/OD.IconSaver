using System.IO;
using System.Windows;

namespace IconSaver;

public partial class App : Application
{
    private readonly DesktopIcons desktop = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? screenshotPath = null;
        string[] args = e.Args;
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--screenshot" ||
                !string.Equals(Path.GetExtension(args[1]), ".png", StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(2);
                return;
            }

            screenshotPath = args[1];
        }

        string dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconSaver");
        MainWindow = new MainWindow(new LayoutStore(dataFolder), desktop, screenshotPath);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        desktop.Dispose();
        base.OnExit(e);
    }
}
