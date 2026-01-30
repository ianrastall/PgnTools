namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Sorter tool.
/// </summary>
public sealed partial class PgnSorterPage : Page
{
    private readonly bool _ownsViewModel;
    public PgnSorterViewModel ViewModel { get; }

    public PgnSorterPage() : this(App.GetService<PgnSorterViewModel>(), ownsViewModel: true)
    {
    }

    public PgnSorterPage(PgnSorterViewModel viewModel, bool ownsViewModel = false)
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

