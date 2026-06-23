using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Clodlogs.Desktop.Services;
using Clodlogs.Desktop.ViewModels;
using Clodlogs.Desktop.Views;

namespace Clodlogs.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppSettingsService();
            var service = new ClaudeSessionService();
            var window = new MainWindow(settings);
            var ui = new AvaloniaUiService(window, settings);
            window.DataContext = new MainWindowViewModel(service, ui, settings);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
