using QuickShelf.Models;

namespace QuickShelf.Services;

public sealed class AppSettings
{
    public string HotKey { get; set; } = "Ctrl+Alt+A";

    public bool UseGlass { get; set; } = true;

    public double GlassOpacity { get; set; } = 0.86;

    public bool StartWithWindows { get; set; }

    public string AccentColor { get; set; } = "#2F7CF6";

    public bool CompactAllApps { get; set; } = true;

    public bool ShowStartMenuItems { get; set; } = true;

    public bool ShowRegistryItems { get; set; } = true;

    public bool ShowAppsFolderItems { get; set; } = true;

    public List<LaunchItem> Favorites { get; set; } = [];
}
