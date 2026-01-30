namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel hosting Lichess-related tools on a single page.
/// </summary>
public sealed partial class LichessToolsViewModel : BaseViewModel, IDisposable
{
    private bool _disposed;

    public LichessDownloaderViewModel UserDownloader { get; }
    public LichessDbDownloaderViewModel DatabaseDownloader { get; }

    public LichessToolsViewModel(
        LichessDownloaderViewModel userDownloader,
        LichessDbDownloaderViewModel databaseDownloader)
    {
        UserDownloader = userDownloader;
        DatabaseDownloader = databaseDownloader;
        Title = "Lichess";
        StatusSeverity = InfoBarSeverity.Informational;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UserDownloader.Dispose();
        DatabaseDownloader.Dispose();
    }
}
