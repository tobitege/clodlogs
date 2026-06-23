using System.Text.Json;

namespace Clodlogs.Desktop.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    private AppSettings? _cache;

    public AppSettingsService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "clodlogs");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "clodlogs-settings.json");
    }

    public async Task<AppSettings> ReadAsync()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            _cache = await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings();
        }
        catch
        {
            _cache = new AppSettings();
        }

        return _cache;
    }

    public async Task UpdateAsync(Action<AppSettings> update)
    {
        var settings = await ReadAsync();
        update(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class AppSettings
{
    public string? ExportDirectory { get; set; }
    public string? LastOpenedFolder { get; set; }
    public AppWindowFrame? WindowFrame { get; set; }
}

public sealed class AppWindowFrame
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
