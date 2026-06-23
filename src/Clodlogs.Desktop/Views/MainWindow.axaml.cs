using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Clodlogs.Desktop.Services;

namespace Clodlogs.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppSettingsService _settings;

    public MainWindow()
        : this(new AppSettingsService())
    {
    }

    public MainWindow(AppSettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        Opened += async (_, _) => await RestoreWindowFrameAsync();
        Closing += (_, _) => _ = SaveWindowFrameAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async Task RestoreWindowFrameAsync()
    {
        var frame = (await _settings.ReadAsync()).WindowFrame;
        if (frame is null || frame.Width < MinWidth || frame.Height < MinHeight)
        {
            return;
        }

        var bounds = new PixelRect(frame.X, frame.Y, frame.Width, frame.Height);
        if (!Screens.All.Any(screen => Intersects(screen.Bounds, bounds)))
        {
            return;
        }

        Width = frame.Width;
        Height = frame.Height;
        Position = new PixelPoint(frame.X, frame.Y);
    }

    private async Task SaveWindowFrameAsync()
    {
        if (WindowState is not WindowState.Normal)
        {
            return;
        }

        var position = Position;
        var width = (int)Math.Round(Width);
        var height = (int)Math.Round(Height);
        if (width < MinWidth || height < MinHeight)
        {
            return;
        }

        await _settings.UpdateAsync(settings =>
        {
            settings.WindowFrame = new AppWindowFrame
            {
                X = position.X,
                Y = position.Y,
                Width = width,
                Height = height
            };
        });
    }

    private static bool Intersects(PixelRect left, PixelRect right)
        => left.X < right.X + right.Width
           && left.X + left.Width > right.X
           && left.Y < right.Y + right.Height
           && left.Y + left.Height > right.Y;
}
