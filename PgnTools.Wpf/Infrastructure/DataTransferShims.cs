// WinUI-compatibility shim. The shared CompilerViewModel copies its build log to the
// clipboard via Windows.ApplicationModel.DataTransfer. Under WPF that namespace does not
// exist, so this re-declares the minimal surface the view-model uses and routes it to the
// WPF clipboard. See ARCHITECTURE.md §6.
namespace Windows.ApplicationModel.DataTransfer;

public sealed class DataPackage
{
    public string Text { get; private set; } = string.Empty;

    public void SetText(string value) => Text = value ?? string.Empty;
}

public static class Clipboard
{
    public static void SetContent(DataPackage package)
    {
        var text = package?.Text ?? string.Empty;

        // WPF's Clipboard.SetText throws on empty input; clear instead.
        if (string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.Clear();
            return;
        }

        System.Windows.Clipboard.SetText(text);
    }
}
