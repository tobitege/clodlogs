using System.Text.Json;

namespace Clodlogs.Desktop.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    private readonly string[] _legacySettingsPaths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _cache;
    private bool _persistenceBlocked;

    public AppSettingsService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "clodlogs");
        _settingsPath = Path.Combine(root, "clodlogs-settings.json");
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dev.tobitege.clodlogs");
        _legacySettingsPaths =
        [
            Path.Combine(legacyRoot, "stable", "clodlogs-settings.json"),
            Path.Combine(legacyRoot, "dev", "clodlogs-settings.json")
        ];
    }

    public AppSettingsService(string settingsPath, params string[] legacySettingsPaths)
    {
        _settingsPath = settingsPath;
        _legacySettingsPaths = legacySettingsPaths;
    }

    public async Task<AppSettings> ReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Action<AppSettings> update)
    {
        await _gate.WaitAsync();
        try
        {
            var settings = await ReadCoreAsync();
            update(settings);
            if (!_persistenceBlocked)
            {
                await TryWriteAsync(settings);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> ReadCoreAsync()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            _cache = await JsonSerializer.DeserializeAsync<AppSettings>(stream)
                ?? throw new JsonException("Settings file did not contain an object.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _cache = new AppSettings();
        }
        catch
        {
            // Keep corrupt or unreadable current settings untouched.
            _persistenceBlocked = true;
            return _cache = new AppSettings();
        }

        if (!_cache.LegacySettingsMigrated)
        {
            if (await MergeLegacySettingsAsync(_cache))
            {
                _cache.LegacySettingsMigrated = true;
                await TryWriteAsync(_cache);
            }
        }

        return _cache;
    }

    private async Task<bool> MergeLegacySettingsAsync(AppSettings settings)
    {
        var succeeded = true;
        foreach (var legacySettingsPath in _legacySettingsPaths)
        {
            try
            {
                await using var stream = File.OpenRead(legacySettingsPath);
                var legacy = await JsonSerializer.DeserializeAsync<AppSettings>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (legacy is null)
                {
                    succeeded = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(settings.LastOpenedFolder) && !string.IsNullOrWhiteSpace(legacy.LastOpenedFolder))
                {
                    settings.LastOpenedFolder = legacy.LastOpenedFolder;
                }
                if (string.IsNullOrWhiteSpace(settings.ExportDirectory) && !string.IsNullOrWhiteSpace(legacy.ExportDirectory))
                {
                    settings.ExportDirectory = legacy.ExportDirectory;
                }
                if (settings.WindowFrame is null && legacy.WindowFrame is not null)
                {
                    settings.WindowFrame = legacy.WindowFrame;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            catch
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private async Task<bool> TryWriteAsync(AppSettings settings)
    {
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                await stream.FlushAsync();
            }

            File.Move(temporaryPath, _settingsPath, true);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }

            return false;
        }
    }
}

public sealed class AppSettings
{
    public string? ExportDirectory { get; set; }
    public string? LastOpenedFolder { get; set; }
    public AppWindowFrame? WindowFrame { get; set; }
    public bool LegacySettingsMigrated { get; set; }
}

public sealed class AppWindowFrame
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
