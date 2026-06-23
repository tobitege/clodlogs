using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Clodlogs.Desktop.Services;

public sealed class AvaloniaUiService(Window window, AppSettingsService settings) : IUiService
{
    public async Task<string?> PickDirectoryAsync(string? startingFolder)
    {
        var start = await TryGetFolderAsync(GetStartingFolder(startingFolder));
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Choose Claude session folder",
            SuggestedStartLocation = start
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await settings.UpdateAsync(s => s.LastOpenedFolder = path);
        }

        return NormalizeDialogPath(path);
    }

    public async Task<string?> PickExportDirectoryAsync(string sessionFilePath)
    {
        var appSettings = await settings.ReadAsync();
        var startPath = appSettings.ExportDirectory ?? Path.GetDirectoryName(sessionFilePath);
        var start = await TryGetFolderAsync(GetStartingFolder(startPath));
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Choose export folder",
            SuggestedStartLocation = start
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await settings.UpdateAsync(s => s.ExportDirectory = path);
        }

        return NormalizeDialogPath(path);
    }

    public async Task<string?> PickHtmlExportDestinationAsync(
        string sessionFilePath,
        bool includeImages,
        bool inlineImages)
    {
        var appSettings = await settings.ReadAsync();
        var startPath = appSettings.ExportDirectory ?? Path.GetDirectoryName(sessionFilePath);
        var start = await TryGetFolderAsync(GetStartingFolder(startPath));

        if (includeImages && !inlineImages)
        {
            return await PickExportDirectoryAsync(sessionFilePath);
        }

        var suggestedName = $"{Path.GetFileNameWithoutExtension(sessionFilePath)}.html";
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save HTML export",
            SuggestedStartLocation = start,
            SuggestedFileName = suggestedName,
            DefaultExtension = "html",
            FileTypeChoices =
            [
                new FilePickerFileType("HTML")
                {
                    Patterns = ["*.html", "*.htm"],
                    MimeTypes = ["text/html"]
                }
            ],
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await settings.UpdateAsync(s => s.ExportDirectory = Path.GetDirectoryName(path));
        }

        return NormalizeDialogPath(path);
    }

    public async Task CopyTextAsync(string text)
    {
        if (window.Clipboard is not null)
        {
            await window.Clipboard.SetTextAsync(text);
        }
    }

    public Task<bool> OpenPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            var target = Directory.Exists(path) ? new Uri(Path.GetFullPath(path) + Path.DirectorySeparatorChar) : new Uri(Path.GetFullPath(path));
            return window.Launcher.LaunchUriAsync(target);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> RevealPathAsync(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var argument = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = argument,
                    UseShellExecute = true
                });
                return Task.FromResult(true);
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return string.IsNullOrWhiteSpace(directory) ? Task.FromResult(false) : OpenPathAsync(directory);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Padding = new Avalonia.Thickness(24, 8)
                    }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children[^1] is Button button)
        {
            button.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(window);
    }

    private async Task<IStorageFolder?> TryGetFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return await window.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
        }

        return await window.StorageProvider.TryGetFolderFromPathAsync(path);
    }

    private static string? GetStartingFolder(string? candidate)
        => string.IsNullOrWhiteSpace(candidate) ? Environment.CurrentDirectory : candidate;

    private static string? NormalizeDialogPath(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
