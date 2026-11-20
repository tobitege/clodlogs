using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Clodlogs.Desktop.Cli;

namespace Clodlogs.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => ClodlogsCli.IsCommand(args)
            ? CliHost.Run(args)
            : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
