namespace PgnTools.Views.Tools;

/// <summary>
/// Page hosting all Lichess-related tools.
/// </summary>
public sealed partial class LichessToolsPage : Page
{
    private readonly bool _ownsViewModel;
    public LichessToolsViewModel ViewModel { get; }

    public LichessToolsPage() : this(App.GetService<LichessToolsViewModel>(), ownsViewModel: true)
    {
    }

    public LichessToolsPage(LichessToolsViewModel viewModel, bool ownsViewModel = false)
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
