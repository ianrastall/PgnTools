namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel hosting Lichess-related tools on a single page.
/// </summary>
public sealed partial class LichessToolsViewModel(
    LichessDownloaderViewModel userDownloader,
    LichessDbDownloaderViewModel databaseDownloader) : BaseViewModel, IInitializable, IDisposable
{
    private bool _disposed;

    public LichessDownloaderViewModel UserDownloader { get; } = userDownloader;
    public LichessDbDownloaderViewModel DatabaseDownloader { get; } = databaseDownloader;

    public void Initialize()
    {
        Title = "Lichess";
        StatusSeverity = InfoBarSeverity.Informational;
        UserDownloader.Initialize();
        DatabaseDownloader.Initialize();
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





