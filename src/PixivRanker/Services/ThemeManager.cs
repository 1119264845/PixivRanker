using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PixivRanker.Services;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>
        {
            ["WindowBrush"] = "#121417",
            ["PanelBrush"] = "#1B1E23",
            ["PanelHoverBrush"] = "#252931",
            ["HeaderBrush"] = "#20242A",
            ["SelectedBrush"] = "#24384A",
            ["AccentBrush"] = "#0096FA",
            ["TextBrush"] = "#F4F6F8",
            ["MutedTextBrush"] = "#9AA3AD",
            ["BorderBrush"] = "#303640"
        };

    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>
        {
            ["WindowBrush"] = "#F5F7FA",
            ["PanelBrush"] = "#FFFFFF",
            ["PanelHoverBrush"] = "#E9EEF5",
            ["HeaderBrush"] = "#EEF2F7",
            ["SelectedBrush"] = "#D9ECFA",
            ["AccentBrush"] = "#0078D4",
            ["TextBrush"] = "#18202A",
            ["MutedTextBrush"] = "#66717E",
            ["BorderBrush"] = "#D4DAE2"
        };

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        CurrentTheme = theme;
        var palette = theme == AppTheme.Dark ? DarkPalette : LightPalette;
        foreach (var (key, value) in palette)
        {
            Application.Current.Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(value));
        }
    }

    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var darkMode = CurrentTheme == AppTheme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkMode,
                    ref darkMode,
                    Marshal.SizeOf<int>()) != 0)
            {
                DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref darkMode,
                    Marshal.SizeOf<int>());
            }

            var caption = ToColorRef(CurrentTheme == AppTheme.Dark ? "#121417" : "#F5F7FA");
            var border = ToColorRef(CurrentTheme == AppTheme.Dark ? "#303640" : "#D4DAE2");
            var text = ToColorRef(CurrentTheme == AppTheme.Dark ? "#F4F6F8" : "#18202A");
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, Marshal.SizeOf<int>());
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, Marshal.SizeOf<int>());
            DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, Marshal.SizeOf<int>());
        }
        catch
        {
            // Older Windows versions can ignore unsupported DWM attributes.
        }
    }

    private static int ToColorRef(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
