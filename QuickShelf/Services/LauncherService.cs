using System.Diagnostics;
using System.IO;
using QuickShelf.Models;

namespace QuickShelf.Services;

public sealed class LauncherService
{
    public void Launch(LaunchItem item)
    {
        if (item.Kind == LaunchItemKind.AppsFolder)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\" + item.Path,
                UseShellExecute = true
            });
            return;
        }

        var path = item.Path.Replace('/', '\\');
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            WorkingDirectory = ResolveWorkingDirectory(path)
        });
    }

    private static string? ResolveWorkingDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }
}
