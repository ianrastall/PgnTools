using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PgnTools.Helpers;

/// <summary>
/// Represents an accent color option for the UI.
/// </summary>
public sealed record AccentColorOption(string Key, string DisplayName, Color? Color)
{
    public bool IsSystemDefault => Color is null;

    public SolidColorBrush PreviewBrush =>
        new(Color ?? Colors.Transparent);
}
