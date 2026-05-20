using QuickShelf.Models;

namespace QuickShelf.Services;

public sealed class AppSettings
{
    public string HotKey { get; set; } = "Ctrl+Alt+A";

    public bool UseGlass { get; set; } = true;

    public double GlassOpacity { get; set; } = 0.86;

    public double StackIconSize { get; set; } = 52;

    public bool StartWithWindows { get; set; }

    public string AccentColor { get; set; } = "#2F7CF6";

    public string ThemeMode { get; set; } = "System";

    public bool CompactAllApps { get; set; } = true;

    public bool HideShortcutItems { get; set; } = true;

    public bool ShowStartMenuItems { get; set; } = true;

    public bool ShowRegistryItems { get; set; } = true;

    public bool ShowAppsFolderItems { get; set; } = true;

    public List<string> FavoriteGroups { get; set; } = [];

    public List<LaunchItem> Favorites { get; set; } = [];

    public List<LaunchItem> Folders { get; set; } = [];
}
