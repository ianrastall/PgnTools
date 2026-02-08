using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PgnTools.Services;
using Windows.UI;

namespace PgnTools.Helpers;

/// <summary>
/// Applies and persists accent color overrides.
/// </summary>
public static class AccentColorManager
{
    private static readonly IReadOnlyList<AccentColorOption> AccentOptions =
    [
        new AccentColorOption("System", "System default", null),
        new AccentColorOption("CornflowerBlue", "CornflowerBlue", Colors.CornflowerBlue),
        new AccentColorOption("CadetBlue", "CadetBlue", Colors.CadetBlue),
        new AccentColorOption("DodgerBlue", "DodgerBlue", Colors.DodgerBlue),
        new AccentColorOption("PowderBlue", "PowderBlue", Colors.PowderBlue),
        new AccentColorOption("LightCoral", "LightCoral", Colors.LightCoral),
        new AccentColorOption("Tomato", "Tomato", Colors.Tomato),
        new AccentColorOption("SoftPink", "SoftPink", Color.FromArgb(255, 244, 182, 194)),
        new AccentColorOption("Plum", "Plum", Colors.Plum),
        new AccentColorOption("Violet", "Violet", Colors.Violet),
        new AccentColorOption("BurlyWood", "BurlyWood", Colors.BurlyWood),
        new AccentColorOption("Tan", "Tan", Colors.Tan),
        new AccentColorOption("Wheat", "Wheat", Colors.Wheat),
        new AccentColorOption("LightGreen", "LightGreen", Colors.LightGreen),
        new AccentColorOption("LightSeaGreen", "LightSeaGreen", Colors.LightSeaGreen),
        new AccentColorOption("Teal", "Teal", Colors.Teal)
    ];

    private static bool _systemSnapshotCaptured;
    private static Color _systemAccent;
    private static Color _systemDark;
    private static Color _systemLight;

    public static IReadOnlyList<AccentColorOption> GetAccentOptions() => AccentOptions;

    public static IReadOnlyDictionary<string, Color?> GetAccentMap() =>
        AccentOptions.ToDictionary(option => option.Key, option => option.Color, StringComparer.OrdinalIgnoreCase);

    public static void ApplySavedAccent(IAppSettingsService settings)
    {
        if (settings == null)
        {
            return;
        }

        var key = settings.GetValue(AppSettingsKeys.AccentColor, "System");
        var map = GetAccentMap();
        if (!map.TryGetValue(key, out var color))
        {
            color = null;
        }

        ApplyAccent(color);
    }

    public static void ApplyAccent(Color? accentColor)
    {
        if (accentColor.HasValue)
        {
            var accent = accentColor.Value;
            var dark = Darken(accent, 0.25);
            var light = Lighten(accent, 0.35);
            UpdateAccentBrushes(accent, dark, light);
            return;
        }

        if (TryGetSystemSnapshot(out var systemAccent, out var systemDark, out var systemLight))
        {
            UpdateAccentBrushes(systemAccent, systemDark, systemLight);
        }
    }

    private static void UpdateAccentBrushes(Color accent, Color dark, Color light)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources)
        {
            return;
        }

        // Update the root dictionary and the themed dictionary (if present)
        SetAccentResources(resources, accent, dark, light);

        var themed = FindDefaultThemeDictionary(resources);
        if (themed != null)
        {
            SetAccentResources(themed, accent, dark, light);
        }
    }

    private static void SetAccentResources(ResourceDictionary dictionary, Color accent, Color dark, Color light)
    {
        try
        {
            SetBrush(dictionary, "AccentBrush", accent);
            SetBrush(dictionary, "PrimaryBrush", accent);
            SetBrush(dictionary, "AccentDarkBrush", dark);
            SetBrush(dictionary, "AccentLightBrush", light);
            SetBrush(dictionary, "AccentLightBrushLow", light, 0.12);
            SetBrush(dictionary, "NavigationViewSelectionIndicatorForeground", accent);
        }
        catch
        {
            // Ignore read-only or unsupported dictionaries.
        }
    }

    private static ResourceDictionary? FindDefaultThemeDictionary(ResourceDictionary root)
    {
        foreach (var merged in root.MergedDictionaries)
        {
            if (merged?.ThemeDictionaries == null)
            {
                continue;
            }

            if (merged.ThemeDictionaries.TryGetValue("Default", out var theme) && theme is ResourceDictionary dict)
            {
                // Prefer the dictionary that defines our accent resources.
                if (dict.ContainsKey("AccentBrush") || dict.ContainsKey("PrimaryBrush"))
                {
                    return dict;
                }
            }
        }

        return null;
    }

    private static bool TryGetSystemSnapshot(out Color accent, out Color dark, out Color light)
    {
        if (_systemSnapshotCaptured)
        {
            accent = _systemAccent;
            dark = _systemDark;
            light = _systemLight;
            return true;
        }

        accent = default;
        dark = default;
        light = default;

        if (Application.Current?.Resources is not ResourceDictionary resources)
        {
            return false;
        }

        var theme = FindDefaultThemeDictionary(resources) ?? resources;

        if (!TryGetBrushColor(theme, "AccentBrush", out accent))
        {
            return false;
        }

        if (!TryGetBrushColor(theme, "AccentDarkBrush", out dark))
        {
            dark = Darken(accent, 0.25);
        }

        if (!TryGetBrushColor(theme, "AccentLightBrush", out light))
        {
            light = Lighten(accent, 0.35);
        }

        _systemAccent = accent;
        _systemDark = dark;
        _systemLight = light;
        _systemSnapshotCaptured = true;
        return true;
    }

    private static bool TryGetBrushColor(ResourceDictionary dictionary, string key, out Color color)
    {
        color = default;
        if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        return false;
    }

    private static void SetBrush(ResourceDictionary dictionary, string key, Color color, double? opacity = null)
    {
        if (dictionary.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
            if (opacity.HasValue)
            {
                brush.Opacity = opacity.Value;
            }
            return;
        }

        var created = new SolidColorBrush(color);
        if (opacity.HasValue)
        {
            created.Opacity = opacity.Value;
        }

        dictionary[key] = created;
    }

    private static Color Lighten(Color color, double amount)
    {
        return Blend(color, Colors.White, amount);
    }

    private static Color Darken(Color color, double amount)
    {
        return Blend(color, Colors.Black, amount);
    }

    private static Color Blend(Color baseColor, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte BlendChannel(byte source, byte dest)
        {
            return (byte)Math.Clamp(source + (dest - source) * amount, 0, 255);
        }

        return Color.FromArgb(
            baseColor.A,
            BlendChannel(baseColor.R, target.R),
            BlendChannel(baseColor.G, target.G),
            BlendChannel(baseColor.B, target.B));
    }
}
