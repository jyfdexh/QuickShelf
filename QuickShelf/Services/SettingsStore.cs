using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickShelf.Models;

namespace QuickShelf.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SettingsPath { get; }

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickShelf");
        SettingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("配置读取失败，已使用默认配置。", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var snapshot = new AppSettings
        {
            HotKey = settings.HotKey,
            UseGlass = settings.UseGlass,
            GlassOpacity = settings.GlassOpacity,
            StackIconSize = settings.StackIconSize,
            StartWithWindows = settings.StartWithWindows,
            AccentColor = settings.AccentColor,
            ThemeMode = settings.ThemeMode,
            CompactAllApps = settings.CompactAllApps,
            HideShortcutItems = settings.HideShortcutItems,
            ShowStartMenuItems = settings.ShowStartMenuItems,
            ShowRegistryItems = settings.ShowRegistryItems,
            ShowAppsFolderItems = settings.ShowAppsFolderItems,
            FavoriteGroups = settings.FavoriteGroups
                .Select(groupName => groupName.Trim())
                .Where(groupName => !string.IsNullOrWhiteSpace(groupName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Favorites = settings.Favorites.Select(item => item.Clone()).ToList(),
            Folders = settings.Folders.Select(item => item.Clone()).ToList()
        };

        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var tempPath = SettingsPath + ".tmp";
        var backupPath = SettingsPath + ".bak";

        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(SettingsPath))
            {
                File.Replace(tempPath, SettingsPath, backupPath, true);
            }
            else
            {
                File.Move(tempPath, SettingsPath, true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("配置保存失败。", ex);
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 保留下一次保存时覆盖，避免因为清理失败影响主流程。
        }
    }
}

public static class AppLog
{
    private static readonly object SyncRoot = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickShelf",
        "logs",
        "app.log");

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Warn(string message, Exception? exception = null)
    {
        Write("WARN", message, exception);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [")
                .Append(level)
                .Append("] ")
                .AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (SyncRoot)
            {
                File.AppendAllText(LogPath, builder.ToString());
            }
        }
        catch
        {
            // 日志不能影响主流程。
        }
    }
}
