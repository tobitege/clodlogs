namespace Clodlogs.Desktop.Services;

public interface IUiService
{
    Task<string?> PickDirectoryAsync(string? startingFolder);
    Task<string?> PickExportDirectoryAsync(string sessionFilePath);
    Task<string?> PickHtmlExportDestinationAsync(string sessionFilePath, bool includeImages, bool inlineImages);
    Task CopyTextAsync(string text);
    Task<bool> OpenPathAsync(string path);
    Task<bool> RevealPathAsync(string path);
    Task ShowMessageAsync(string title, string message);
}
