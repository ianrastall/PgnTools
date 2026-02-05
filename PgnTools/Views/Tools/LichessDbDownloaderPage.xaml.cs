namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Lichess DB Downloader tool.
/// </summary>
public sealed partial class LichessDbDownloaderPage : Page
{
    private readonly bool _ownsViewModel;
    public LichessDbDownloaderViewModel ViewModel { get; }

    public LichessDbDownloaderPage() : this(App.GetService<LichessDbDownloaderViewModel>(), ownsViewModel: true)
    {
    }

    public LichessDbDownloaderPage(LichessDbDownloaderViewModel viewModel, bool ownsViewModel = false)
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
            else
            {
                ViewModel.Dispose();
            }
        }
    }
}
