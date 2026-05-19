using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using QuickShelf.Models;

namespace QuickShelf.Services;

public sealed class AppScanner
{
    private static readonly string[] UninstallSubKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public Task<IReadOnlyList<LaunchItem>> ScanAsync()
    {
        return Task.Run<IReadOnlyList<LaunchItem>>(Scan);
    }

    private static IReadOnlyList<LaunchItem> Scan()
    {
        var items = new Dictionary<string, LaunchItem>(StringComparer.OrdinalIgnoreCase);
        AddStartMenuItems(items);
        AddRegistryItems(items);
        AddAppsFolderItems(items);

        return items.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Source, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AddStartMenuItems(IDictionary<string, LaunchItem> items)
    {
        var directories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };

        foreach (var directory in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var shortcut in EnumerateFilesSafe(directory, "*.lnk", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                AddItem(items, LaunchItem.Create(name, shortcut, LaunchItemKind.Shortcut, "开始菜单"));
            }
        }
    }

    private static void AddRegistryItems(IDictionary<string, LaunchItem> items)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                }
                catch
                {
                    continue;
                }

                using (baseKey)
                {
                    foreach (var subKeyPath in UninstallSubKeys)
                    {
                        using var uninstall = baseKey.OpenSubKey(subKeyPath);
                        if (uninstall is null)
                        {
                            continue;
                        }

                        foreach (var subKeyName in uninstall.GetSubKeyNames())
                        {
                            using var appKey = uninstall.OpenSubKey(subKeyName);
                            if (appKey is null || IsHiddenSystemEntry(appKey))
                            {
                                continue;
                            }

                            var displayName = appKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                continue;
                            }

                            var launchPath = ResolveRegistryLaunchPath(
                                appKey.GetValue("DisplayIcon") as string,
                                appKey.GetValue("InstallLocation") as string,
                                displayName);

                            if (string.IsNullOrWhiteSpace(launchPath))
                            {
                                continue;
                            }

                            var kind = string.Equals(Path.GetExtension(launchPath), ".lnk", StringComparison.OrdinalIgnoreCase)
                                ? LaunchItemKind.Shortcut
                                : LaunchItemKind.Executable;
                            AddItem(items, LaunchItem.Create(displayName, launchPath, kind, "注册表"));
                        }
                    }
                }
            }
        }
    }

    private static void AddAppsFolderItems(IDictionary<string, LaunchItem> items)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000) || string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var app in document.RootElement.EnumerateArray())
                {
                    AddAppsFolderItem(items, app);
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddAppsFolderItem(items, document.RootElement);
            }
        }
        catch
        {
            // AppsFolder 扫描是补充来源，失败时保留开始菜单和注册表结果。
        }
    }

    private static void AddAppsFolderItem(IDictionary<string, LaunchItem> items, JsonElement app)
    {
        if (!app.TryGetProperty("Name", out var nameElement) ||
            !app.TryGetProperty("AppID", out var idElement))
        {
            return;
        }

        var name = nameElement.GetString();
        var appId = idElement.GetString();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        AddItem(items, LaunchItem.Create(name, appId, LaunchItemKind.AppsFolder, "AppsFolder"));
    }

    private static bool IsHiddenSystemEntry(RegistryKey appKey)
    {
        var systemComponent = appKey.GetValue("SystemComponent");
        if (systemComponent is int value && value == 1)
        {
            return true;
        }

        var releaseType = appKey.GetValue("ReleaseType") as string;
        return !string.IsNullOrWhiteSpace(releaseType) &&
               releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveRegistryLaunchPath(string? displayIcon, string? installLocation, string displayName)
    {
        var iconPath = ExtractPathFromDisplayIcon(displayIcon);
        if (IsLaunchableFile(iconPath))
        {
            return iconPath;
        }

        var expandedInstallLocation = ExpandAndTrim(installLocation);
        if (!string.IsNullOrWhiteSpace(expandedInstallLocation) && Directory.Exists(expandedInstallLocation))
        {
            return FindBestExecutable(expandedInstallLocation, displayName);
        }

        return null;
    }

    private static string? ExtractPathFromDisplayIcon(string? displayIcon)
    {
        var value = ExpandAndTrim(displayIcon);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value[1..end];
            }
        }

        var commaIndex = value.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(value[(commaIndex + 1)..], out _))
        {
            value = value[..commaIndex];
        }

        return value.Trim();
    }

    private static string? FindBestExecutable(string directory, string displayName)
    {
        var executables = EnumerateFilesSafe(directory, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(IsLaunchableFile)
            .Select(path => new
            {
                Path = path,
                Score = ScoreExecutable(path, displayName)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .ToList();

        return executables.FirstOrDefault()?.Path;
    }

    private static int ScoreExecutable(string path, string displayName)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("helper", StringComparison.OrdinalIgnoreCase))
        {
            return -100;
        }

        var score = 0;
        foreach (var token in displayName.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length;
            }
        }

        return score;
    }

    private static bool IsLaunchableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var expanded = ExpandAndTrim(path);
        if (string.IsNullOrWhiteSpace(expanded) || !File.Exists(expanded))
        {
            return false;
        }

        var extension = Path.GetExtension(expanded);
        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory, string pattern, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, searchOption).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ExpandAndTrim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
    }

    private static void AddItem(IDictionary<string, LaunchItem> items, LaunchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        var key = $"{item.Kind}|{item.Name}|{item.Path}";
        items.TryAdd(key, item);
    }
}
