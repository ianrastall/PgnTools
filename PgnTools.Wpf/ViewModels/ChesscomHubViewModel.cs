namespace PgnTools.Wpf;

public sealed class ChesscomHubViewModel(
    ChesscomDownloaderViewModel userDownloader,
    ChesscomMonthlyDownloaderViewModel monthlyDownloader) : IInitializable, IDisposable
{
    private bool _disposed;

    public ChesscomDownloaderViewModel UserDownloader { get; } = userDownloader;

    public ChesscomMonthlyDownloaderViewModel MonthlyDownloader { get; } = monthlyDownloader;

    public void Initialize()
    {
        UserDownloader.Initialize();
        MonthlyDownloader.Initialize();
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
    }
}
