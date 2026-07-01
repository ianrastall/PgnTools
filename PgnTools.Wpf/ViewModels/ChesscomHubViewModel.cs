namespace PgnTools.Wpf;

public sealed class ChesscomHubViewModel(
    ChesscomDownloaderViewModel userDownloader,
    ChesscomMonthlyDownloaderViewModel monthlyDownloader,
    ChesscomEventsDownloaderViewModel eventsDownloader) : IInitializable, IDisposable
{
    private bool _disposed;

    public ChesscomDownloaderViewModel UserDownloader { get; } = userDownloader;

    public ChesscomMonthlyDownloaderViewModel MonthlyDownloader { get; } = monthlyDownloader;

    public ChesscomEventsDownloaderViewModel EventsDownloader { get; } = eventsDownloader;

    public void Initialize()
    {
        UserDownloader.Initialize();
        MonthlyDownloader.Initialize();
        EventsDownloader.Initialize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UserDownloader.Dispose();
        MonthlyDownloader.Dispose();
        EventsDownloader.Dispose();
    }
}
