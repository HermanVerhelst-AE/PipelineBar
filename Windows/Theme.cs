using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PipelineBar;

/// Licht/donker volgens Windows. Apps en taakbalk hebben elk hun eigen instelling.
public static class Theme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool ReadsDark(string value, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue(value) is int light ? light == 0 : fallback;
    }

    public static bool AppsDark => ReadsDark("AppsUseLightTheme", false);
    public static bool TaskbarDark => ReadsDark("SystemUsesLightTheme", true);

    /// Kleuren voor het uitklapvenster, als DynamicResource bruikbaar.
    public static void Apply(ResourceDictionary r)
    {
        var dark = AppsDark;
        r["PB.Background"] = Brush(dark ? "#FF2C2C2C" : "#FFF9F9F9");
        r["PB.HeaderBackground"] = Brush(dark ? "#F02C2C2C" : "#F0F9F9F9");
        r["PB.Border"] = Brush(dark ? "#33FFFFFF" : "#1F000000");
        r["PB.Divider"] = Brush(dark ? "#1FFFFFFF" : "#14000000");
        r["PB.Foreground"] = Brush(dark ? "#FFFFFFFF" : "#E4000000");
        r["PB.Secondary"] = Brush(dark ? "#C5FFFFFF" : "#9E000000");
        r["PB.Tertiary"] = Brush(dark ? "#87FFFFFF" : "#72000000");
        r["PB.Hover"] = Brush(dark ? "#0FFFFFFF" : "#0A000000");
        r["PB.Pressed"] = Brush(dark ? "#0AFFFFFF" : "#06000000");
        r["PB.Critical"] = Brush(StateColor(RunState.Failed, dark));
        r["PB.Caution"] = Brush(StateColor(RunState.PartiallySucceeded, dark));
        r["PB.Accent"] = Brush(dark ? "#FF60CDFF" : "#FF005FB8");
        r["PB.Thumb"] = Brush(dark ? "#8BFFFFFF" : "#72000000");
    }

    public static string StateColor(RunState s, bool dark) => s switch
    {
        RunState.Succeeded => dark ? "#FF6CCB5F" : "#FF0F7B0F",
        RunState.Failed => dark ? "#FFFF99A4" : "#FFC42B1C",
        RunState.PartiallySucceeded or RunState.Cancelling => dark ? "#FFFCE100" : "#FF9D5D00",
        RunState.InProgress => dark ? "#FF60CDFF" : "#FF005FB8",
        RunState.Waiting => dark ? "#FFC5A3FF" : "#FF7A3FC8",
        _ => dark ? "#C5FFFFFF" : "#9E000000",
    };

    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }
}
