using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using QuickShelf.Models;

namespace QuickShelf.Services;

public sealed class IconCache
{
    private readonly string _cacheDirectory;

    public IconCache()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickShelf",
            "IconCache");
    }

    public void PopulateIcons(IEnumerable<LaunchItem> items)
    {
        Directory.CreateDirectory(_cacheDirectory);

        foreach (var item in items)
        {
            item.IconPath = TryGetIconPath(item);
        }
    }

    public string? TryGetIconPath(LaunchItem item)
    {
        try
        {
            if (item.Kind == LaunchItemKind.AppsFolder)
            {
                return null;
            }

            if (item.Kind == LaunchItemKind.Folder)
            {
                return EnsureFolderIconPath();
            }

            var path = item.Path.Replace('/', '\\');
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return null;
            }

            Directory.CreateDirectory(_cacheDirectory);
            var cachePath = Path.Combine(_cacheDirectory, Hash(item.Path) + ".png");
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            using var icon = File.Exists(path)
                ? Icon.ExtractAssociatedIcon(path)
                : SystemIcons.WinLogo;

            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            bitmap.Save(cachePath, ImageFormat.Png);
            return cachePath;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"图标缓存失败：{item.Name}", ex);
            return null;
        }
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            return Convert.ToHexString(bytes)[..24];
    }

    private string EnsureFolderIconPath()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, "folder-v2.png");
        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        using var bitmap = new Bitmap(256, 256, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        using (var shadow = CreateRoundedRectanglePath(new RectangleF(32, 88, 198, 126), 30))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(34, 25, 93, 190)))
        {
            using var matrix = new Matrix();
            matrix.Translate(0, 10);
            shadow.Transform(matrix);
            graphics.FillPath(shadowBrush, shadow);
        }

        using (var tab = CreateRoundedRectanglePath(new RectangleF(36, 50, 90, 52), 18))
        using (var tabBrush = new LinearGradientBrush(
                   new RectangleF(36, 50, 90, 52),
                   Color.FromArgb(255, 74, 144, 255),
                   Color.FromArgb(255, 46, 112, 232),
                   LinearGradientMode.Vertical))
        {
            graphics.FillPath(tabBrush, tab);
        }

        using (var back = CreateRoundedRectanglePath(new RectangleF(26, 72, 204, 138), 28))
        using (var backBrush = new LinearGradientBrush(
                   new RectangleF(26, 72, 204, 138),
                   Color.FromArgb(255, 91, 164, 255),
                   Color.FromArgb(255, 42, 118, 239),
                   LinearGradientMode.Vertical))
        {
            graphics.FillPath(backBrush, back);
        }

        using (var front = CreateRoundedRectanglePath(new RectangleF(20, 94, 216, 116), 30))
        using (var frontBrush = new LinearGradientBrush(
                   new RectangleF(20, 94, 216, 116),
                   Color.FromArgb(255, 84, 176, 255),
                   Color.FromArgb(255, 32, 118, 245),
                   LinearGradientMode.Vertical))
        using (var outlinePen = new Pen(Color.FromArgb(80, 255, 255, 255), 3))
        {
            graphics.FillPath(frontBrush, front);
            graphics.DrawPath(outlinePen, front);
        }

        using (var shine = CreateRoundedRectanglePath(new RectangleF(48, 112, 150, 28), 14))
        using (var shineBrush = new LinearGradientBrush(
                   new RectangleF(48, 112, 150, 28),
                   Color.FromArgb(82, 255, 255, 255),
                   Color.FromArgb(16, 255, 255, 255),
                   LinearGradientMode.Horizontal))
        {
            graphics.FillPath(shineBrush, shine);
        }

        bitmap.Save(cachePath, ImageFormat.Png);
        return cachePath;
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
