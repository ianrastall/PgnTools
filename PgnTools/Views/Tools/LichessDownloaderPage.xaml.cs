namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Lichess Downloader tool.
/// </summary>
public sealed partial class LichessDownloaderPage : Page
{
    private readonly bool _ownsViewModel;
    public LichessDownloaderViewModel ViewModel { get; }

    public LichessDownloaderPage() : this(App.GetService<LichessDownloaderViewModel>(), ownsViewModel: true)
    {
    }

    public LichessDownloaderPage(LichessDownloaderViewModel viewModel, bool ownsViewModel = false)
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
            ViewModel.Dispose();
        }
    }
}

