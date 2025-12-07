using System;
using System.IO;
using System.Text.Json;

namespace NAIGallery;

/// <summary>
/// Represents application settings that can be persisted to disk.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Thumbnail cache capacity in bytes.
    /// </summary>
    public int? ThumbCacheCapacity { get; set; }

    private static string GetSettingsPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(root, "NAIGallery");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "NAIGallery.settings.json");
        }
    }

    /// <summary>
    /// Loads settings from the default settings file location.
    /// Returns a new instance with defaults if loading fails.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        
        return new AppSettings();
    }

    /// <summary>
    /// Saves the settings to the default settings file location.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = GetSettingsPath();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
