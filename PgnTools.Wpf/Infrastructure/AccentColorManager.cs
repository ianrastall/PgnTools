using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace PgnTools.Wpf.Infrastructure;

/// <summary>
/// A single selectable accent choice. <see cref="Color"/> is null for the built-in app accent.
/// </summary>
public sealed record AccentOption(string Key, string DisplayName, Color? Color);

/// <summary>
/// Applies and persists accent color overrides for the WPF shell by swapping the
/// <c>AccentBrush</c> and <c>AccentSoftBrush</c> application resources at runtime.
/// </summary>
public static class AccentColorManager
{
    /// <summary>Fallback accent if the Windows system accent cannot be read.</summary>
    private static readonly Color DefaultAccent = Color.FromRgb(0x1F, 0x6B, 0x52);

    /// <summary>Dark surface the soft accent panel is tinted toward (matches SurfaceBrush in App.xaml).</summary>
    private static readonly Color SoftTintTarget = Color.FromRgb(0x27, 0x27, 0x27);

    private static readonly IReadOnlyList<AccentOption> Options =
    [
        new AccentOption("System", "Windows accent (system)", null),
        new AccentOption("CornflowerBlue", "Cornflower Blue", Colors.CornflowerBlue),
        new AccentOption("CadetBlue", "Cadet Blue", Colors.CadetBlue),
        new AccentOption("DodgerBlue", "Dodger Blue", Colors.DodgerBlue),
        new AccentOption("PowderBlue", "Powder Blue", Colors.PowderBlue),
        new AccentOption("LightCoral", "Light Coral", Colors.LightCoral),
        new AccentOption("Tomato", "Tomato", Colors.Tomato),
        new AccentOption("SoftPink", "Soft Pink", Color.FromArgb(255, 244, 182, 194)),
        new AccentOption("Plum", "Plum", Colors.Plum),
        new AccentOption("Violet", "Violet", Colors.Violet),
        new AccentOption("BurlyWood", "Burly Wood", Colors.BurlyWood),
        new AccentOption("Tan", "Tan", Colors.Tan),
        new AccentOption("Wheat", "Wheat", Colors.Wheat),
        new AccentOption("LightGreen", "Light Green", Colors.LightGreen),
        new AccentOption("LightSeaGreen", "Light Sea Green", Colors.LightSeaGreen),
        new AccentOption("Teal", "Teal", Colors.Teal)
    ];

    public static IReadOnlyList<AccentOption> GetAccentOptions() => Options;

    /// <summary>Applies whichever accent was last persisted in settings (or the app default).</summary>
    public static void ApplySavedAccent(IAppSettingsService settings)
    {
        if (settings is null)
        {
            return;
        }

        var key = settings.GetValue(AppSettingsKeys.AccentColor, "System");
        var option = Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
        ApplyAccent(option?.Color);
    }

    /// <summary>Applies an accent color (null means follow the Windows system accent).</summary>
    public static void ApplyAccent(Color? accentColor)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources)
        {
            return;
        }

        var accent = accentColor ?? TryGetSystemAccent() ?? DefaultAccent;

        // A muted, dark-tinted version of the accent for soft background panels.
        var soft = Blend(accent, SoftTintTarget, 0.62);

        // Consumers reference these via DynamicResource, so replacing the entries updates the UI live.
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentSoftBrush"] = new SolidColorBrush(soft);
    }

    /// <summary>
    /// Reads the user's current Windows accent color from the registry (DWM\AccentColor,
    /// stored as a 0xAABBGGRR DWORD). Returns null if it cannot be read.
    /// </summary>
    public static Color? TryGetSystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb(
                    (byte)(abgr & 0xFF),
                    (byte)((abgr >> 8) & 0xFF),
                    (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch
        {
            // Fall through to the caller's fallback.
        }

        return null;
    }

    private static Color Blend(Color baseColor, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        static byte Mix(byte source, byte dest, double a) =>
            (byte)Math.Clamp(source + (dest - source) * a, 0, 255);

        return Color.FromRgb(
            Mix(baseColor.R, target.R, amount),
            Mix(baseColor.G, target.G, amount),
            Mix(baseColor.B, target.B, amount));
    }
}
