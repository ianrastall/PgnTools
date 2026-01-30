namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Checkmate Filter tool.
/// </summary>
public sealed partial class CheckmateFilterPage : Page
{
    private readonly bool _ownsViewModel;
    public CheckmateFilterViewModel ViewModel { get; }

    public CheckmateFilterPage() : this(App.GetService<CheckmateFilterViewModel>(), ownsViewModel: true)
    {
    }

    public CheckmateFilterPage(CheckmateFilterViewModel viewModel, bool ownsViewModel = false)
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

