using Avalonia;
using System;
using Moist.Core.Diagnostics;

namespace Moist.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort capture: a crash anywhere — UI thread, worker, fire-and-forget task —
        // lands in %AppData%/moist/logs/crash_*.log instead of vanishing with the window.
        CrashLog.InstallGlobalHandlers("gui");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("gui", "Main (fatal)", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
