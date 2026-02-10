namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Chess.com Downloader tool.
/// </summary>
public sealed partial class ChesscomDownloaderPage : Page
{
    private readonly bool _ownsViewModel;
    public ChesscomToolsViewModel ViewModel { get; }

    public ChesscomDownloaderPage() : this(App.GetService<ChesscomToolsViewModel>(), ownsViewModel: true)
    {
    }

    public ChesscomDownloaderPage(ChesscomToolsViewModel viewModel, bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        _ownsViewModel = ownsViewModel;
        this.InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_ownsViewModel)
        {
            if (ViewModel.UserDownloader.IsRunning || ViewModel.MonthlyDownloader.IsRunning)
            {
                if (ViewModel.UserDownloader.IsRunning)
                {
                    ViewModel.UserDownloader.CancelCommand.Execute(null);
                }

                if (ViewModel.MonthlyDownloader.IsRunning)
                {
                    ViewModel.MonthlyDownloader.CancelCommand.Execute(null);
                }
            }
            else
            {
                ViewModel.Dispose();
            }
        }
    }
}

