namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel hosting Chess.com tools on a single page.
/// </summary>
public sealed partial class ChesscomToolsViewModel(
    ChesscomDownloaderViewModel userDownloader,
    ChesscomMonthlyDownloaderViewModel monthlyDownloader) : BaseViewModel, IInitializable, IDisposable
{
    private bool _disposed;

    public ChesscomDownloaderViewModel UserDownloader { get; } = userDownloader;
    public ChesscomMonthlyDownloaderViewModel MonthlyDownloader { get; } = monthlyDownloader;

    public void Initialize()
    {
        Title = "Chess.com";
        StatusSeverity = InfoBarSeverity.Informational;
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
