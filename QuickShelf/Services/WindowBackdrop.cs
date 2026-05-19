using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickShelf.Services;

public static class WindowBackdrop
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmwcpRound = 2;

    public static void Apply(Window window, bool enabled, double opacity)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetRoundedCorners(hwnd);
        SetSystemBackdrop(hwnd, false);
    }

    private static void SetRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmwaWindowCornerPreference,
                ref preference,
                Marshal.SizeOf<int>());
        }
        catch
        {
            // 老版本 Windows 不支持该属性，WPF 圆角仍会生效。
        }
    }

    private static void SetSystemBackdrop(IntPtr hwnd, bool enabled)
    {
        try
        {
            var backdrop = enabled ? DwmsbtTransientWindow : DwmsbtNone;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmwaSystemBackdropType,
                ref backdrop,
                Marshal.SizeOf<int>());
        }
        catch
        {
            // DWM 背景材质只在支持的 Windows 版本上可用。
        }
    }

    private static void SetAcrylic(IntPtr hwnd, bool enabled, double opacity)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = enabled ? AccentState.AccentEnableAcrylicBlurBehind : AccentState.AccentDisabled,
                AccentFlags = 2,
                GradientColor = BuildGradientColor(opacity)
            };

            var accentSize = Marshal.SizeOf<AccentPolicy>();
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WcaAccentPolicy,
                    Data = accentPtr,
                    SizeOfData = accentSize
                };
                _ = SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch
        {
            // Acrylic 是增强效果，失败时保留半透明 WPF 背景。
        }
    }

    private static int BuildGradientColor(double opacity)
    {
        var alpha = (byte)(Math.Clamp(opacity, 0.4, 1.0) * 255);
        return alpha << 24 | 0xF8 << 16 | 0xFB << 8 | 0xFF;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    private enum AccentState
    {
        AccentDisabled = 0,
        AccentEnableAcrylicBlurBehind = 4
    }

    private enum WindowCompositionAttribute
    {
        WcaAccentPolicy = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
