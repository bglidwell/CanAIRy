using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace CanAIRy;

internal static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Apply();
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
    }

    public static void Stop()
    {
        if (!_started) return;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        _started = false;
    }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window, IsDarkMode());
    }

    private static void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var application = System.Windows.Application.Current;
        if (application is null) return;
        application.Dispatcher.BeginInvoke(Apply);
    }

    private static void Apply()
    {
        var application = System.Windows.Application.Current;
        if (application is null) return;
        var dark = IsDarkMode();
        var palette = dark ? DarkPalette : LightPalette;
        foreach (var (key, color) in palette)
            application.Resources[key] = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        foreach (Window window in application.Windows)
            ApplyWindowChrome(window, dark);
    }

    private static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private static void ApplyWindowChrome(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["WindowBrush"] = "#F3F6FA", ["SurfaceBrush"] = "#FFFFFF", ["SurfaceAltBrush"] = "#F8FAFC",
        ["CardBorderBrush"] = "#E0E6EF", ["ControlBrush"] = "#FFFFFF", ["ControlBorderBrush"] = "#C9D3E0",
        ["HoverBrush"] = "#EDF3FA", ["SelectionBrush"] = "#E5F2FF", ["TextBrush"] = "#152033",
        ["MutedBrush"] = "#627087", ["SubtleBrush"] = "#8A98AB", ["AccentBrush"] = "#2187EE",
        ["AccentHoverBrush"] = "#1478DA", ["AccentTextBrush"] = "#FFFFFF", ["BrandYellowBrush"] = "#FFD726",
        ["DividerBrush"] = "#E4E9F0", ["SuccessBrush"] = "#12A86B", ["WarningBrush"] = "#E99116",
        ["DangerBrush"] = "#E5484D"
    };

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["WindowBrush"] = "#0B111A", ["SurfaceBrush"] = "#121B27", ["SurfaceAltBrush"] = "#172230",
        ["CardBorderBrush"] = "#263548", ["ControlBrush"] = "#0F1823", ["ControlBorderBrush"] = "#34455B",
        ["HoverBrush"] = "#1B2A3B", ["SelectionBrush"] = "#173B5C", ["TextBrush"] = "#F3F7FC",
        ["MutedBrush"] = "#A5B1C2", ["SubtleBrush"] = "#72839A", ["AccentBrush"] = "#39A0FF",
        ["AccentHoverBrush"] = "#62B4FF", ["AccentTextBrush"] = "#06121F", ["BrandYellowBrush"] = "#FFD726",
        ["DividerBrush"] = "#253446", ["SuccessBrush"] = "#27C281", ["WarningBrush"] = "#FFB454",
        ["DangerBrush"] = "#FF6369"
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
