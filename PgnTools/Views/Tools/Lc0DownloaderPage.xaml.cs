namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Lc0 Downloader tool.
/// </summary>
public sealed partial class Lc0DownloaderPage : Page
{
    private readonly bool _ownsViewModel;
    public Lc0DownloaderViewModel ViewModel { get; }

    public Lc0DownloaderPage() : this(App.GetService<Lc0DownloaderViewModel>(), ownsViewModel: true)
    {
    }

    public Lc0DownloaderPage(Lc0DownloaderViewModel viewModel, bool ownsViewModel = false)
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
