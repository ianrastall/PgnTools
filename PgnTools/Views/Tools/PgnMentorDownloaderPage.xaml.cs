namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the PGN Mentor Downloader tool.
/// </summary>
public sealed partial class PgnMentorDownloaderPage : Page
{
    private readonly bool _ownsViewModel;
    public PgnMentorDownloaderViewModel ViewModel { get; }

    public PgnMentorDownloaderPage() : this(App.GetService<PgnMentorDownloaderViewModel>(), ownsViewModel: true)
    {
    }

    public PgnMentorDownloaderPage(PgnMentorDownloaderViewModel viewModel, bool ownsViewModel = false)
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

