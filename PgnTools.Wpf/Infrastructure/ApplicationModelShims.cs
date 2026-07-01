// WinUI-compatibility shim. TablebaseConstants probes Package.Current.InstalledLocation to
// find its bundled download list when running as a packaged (MSIX) app. The WPF build is
// always unpackaged, so Package.Current throws and the caller's try/catch falls back to
// AppContext.BaseDirectory. This shim exists only so the shared file compiles. See
// ARCHITECTURE.md §6.
namespace Windows.ApplicationModel;

public sealed class Package
{
    public static Package Current =>
        throw new InvalidOperationException(
            "PgnTools.Wpf is not a packaged app; Package.Current is unavailable.");

    public PackageInstalledLocation InstalledLocation { get; } = new();
}

public sealed class PackageInstalledLocation
{
    public string Path => string.Empty;
}
