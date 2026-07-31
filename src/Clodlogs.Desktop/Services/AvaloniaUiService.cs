using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
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
        return await PickExportDirectoryFromAsync(appSettings.ExportDirectory ?? Path.GetDirectoryName(sessionFilePath));
    }

    public async Task<string?> PickExportDirectoryFromAsync(string? startingFolder)
    {
        var appSettings = await settings.ReadAsync();
        var startPath = startingFolder ?? appSettings.ExportDirectory;
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

    public async Task<string?> SaveStatisticsTextAsync(string suggestedFileName, string extension, string content)
    {
        var normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        var fileType = normalizedExtension switch
        {
            "csv" => new FilePickerFileType("CSV")
            {
                Patterns = ["*.csv"],
                MimeTypes = ["text/csv"]
            },
            "md" => new FilePickerFileType("Markdown")
            {
                Patterns = ["*.md"],
                MimeTypes = ["text/markdown"]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, "Unsupported statistics export format.")
        };
        var file = await PickStatisticsFileAsync(
            $"Save token statistics as {fileType.Name}",
            suggestedFileName,
            normalizedExtension,
            fileType);
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
        await writer.FlushAsync();
        return await RememberStatisticsExportAsync(file);
    }

    public async Task<string?> SaveStatisticsImageAsync(string suggestedFileName)
    {
        var report = window.FindControl<Control>("TokenSummaryReport");
        if (report is null || report.Bounds.Width <= 0 || report.Bounds.Height <= 0)
        {
            throw new InvalidOperationException("The token statistics report is not currently available for rendering.");
        }

        var pngType = new FilePickerFileType("PNG image")
        {
            Patterns = ["*.png"],
            MimeTypes = ["image/png"]
        };
        var file = await PickStatisticsFileAsync("Save token statistics as image", suggestedFileName, "png", pngType);
        if (file is null)
        {
            return null;
        }

        var scaling = Math.Max(1d, window.RenderScaling);
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(report.Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Ceiling(report.Bounds.Height * scaling)));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(report);
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        await stream.FlushAsync();
        return await RememberStatisticsExportAsync(file);
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
                if (Directory.Exists(path))
                {
                    var directoryProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{Path.GetFullPath(path)}\"",
                        UseShellExecute = true
                    });
                    return Task.FromResult(directoryProcess is not null);
                }
                if (!File.Exists(path))
                {
                    return Task.FromResult(false);
                }

                var fileProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{Path.GetFullPath(path)}\"",
                    UseShellExecute = true
                });
                return Task.FromResult(fileProcess is not null);
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

    private async Task<IStorageFile?> PickStatisticsFileAsync(
        string title,
        string suggestedFileName,
        string extension,
        FilePickerFileType fileType)
    {
        if (!window.StorageProvider.CanSave)
        {
            return null;
        }

        var appSettings = await settings.ReadAsync();
        var start = await TryGetFolderAsync(GetStartingFolder(appSettings.ExportDirectory));
        return await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedStartLocation = start,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices = [fileType],
            SuggestedFileType = fileType,
            ShowOverwritePrompt = true
        });
    }

    private async Task<string> RememberStatisticsExportAsync(IStorageFile file)
    {
        var path = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await settings.UpdateAsync(s => s.ExportDirectory = Path.GetDirectoryName(path));
            return path;
        }

        return file.Name;
    }

    private static string? GetStartingFolder(string? candidate)
        => string.IsNullOrWhiteSpace(candidate) ? Environment.CurrentDirectory : candidate;

    private static string? NormalizeDialogPath(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
