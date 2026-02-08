namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Chess.com Downloader tool.
/// </summary>
public sealed partial class ChesscomDownloaderPage : Page
{
    private readonly bool _ownsViewModel;
    public ChesscomDownloaderViewModel ViewModel { get; }

    public ChesscomDownloaderPage() : this(App.GetService<ChesscomDownloaderViewModel>(), ownsViewModel: true)
    {
    }

    public ChesscomDownloaderPage(ChesscomDownloaderViewModel viewModel, bool ownsViewModel = false)
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
            if (ViewModel.IsRunning)
            {
                ViewModel.CancelCommand.Execute(null);
            }
            ViewModel.Dispose();
        }
    }
}

