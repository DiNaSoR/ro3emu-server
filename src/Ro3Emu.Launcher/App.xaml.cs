using System.IO;
using System.Windows;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

public partial class App : Application
{
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\Ro3Emu.Launcher", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Ro3EmuLauncher.exe");
        SelfUpdate.DeleteLeftovers(exePath);
        var config = LauncherConfig.Load(Path.Combine(Path.GetDirectoryName(exePath)!, "launcher.json"));
        MainWindow = new MainWindow(config, exePath);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
