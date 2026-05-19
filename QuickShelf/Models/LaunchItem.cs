using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace QuickShelf.Models;

public enum LaunchItemKind
{
    Shortcut,
    Executable,
    File,
    Folder,
    AppsFolder
}

public sealed class LaunchItem : INotifyPropertyChanged
{
    private string? _iconPath;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public LaunchItemKind Kind { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? IconPath
    {
        get => _iconPath;
        set
        {
            if (_iconPath == value)
            {
                return;
            }

            _iconPath = value;
            OnPropertyChanged();
        }
    }

    public string KindLabel => Kind switch
    {
        LaunchItemKind.Shortcut => "快捷方式",
        LaunchItemKind.Executable => "应用",
        LaunchItemKind.File => "文件",
        LaunchItemKind.Folder => "文件夹",
        LaunchItemKind.AppsFolder => "应用",
        _ => "项目"
    };

    public static LaunchItem Create(string name, string path, LaunchItemKind kind, string source)
    {
        var normalizedPath = NormalizePath(path);
        return new LaunchItem
        {
            Id = BuildId(kind, normalizedPath, name),
            Name = name.Trim(),
            Path = normalizedPath,
            Kind = kind,
            Source = source
        };
    }

    public LaunchItem Clone()
    {
        return new LaunchItem
        {
            Id = Id,
            Name = Name,
            Path = Path,
            Kind = Kind,
            Source = Source,
            IconPath = IconPath
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string BuildId(LaunchItemKind kind, string path, string name)
    {
        var raw = $"{kind}|{path}|{name}".ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return expanded.Replace('\\', '/');
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
