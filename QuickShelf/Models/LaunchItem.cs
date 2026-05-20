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
    private string? _displayName;
    private string _groupName = string.Empty;
    private string? _iconPath;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public LaunchItemKind Kind { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? DisplayName
    {
        get => _displayName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_displayName == normalized)
            {
                return;
            }

            _displayName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public string GroupName
    {
        get => _groupName;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_groupName == normalized)
            {
                return;
            }

            _groupName = normalized;
            OnPropertyChanged();
        }
    }

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
            DisplayName = DisplayName,
            Path = Path,
            Kind = Kind,
            Source = Source,
            GroupName = GroupName,
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
