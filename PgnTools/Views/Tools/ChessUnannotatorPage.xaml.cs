namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Chess Un-Annotator tool.
/// </summary>
public sealed partial class ChessUnannotatorPage : Page
{
    private readonly bool _ownsViewModel;
    public ChessUnannotatorViewModel ViewModel { get; }

    public ChessUnannotatorPage() : this(App.GetService<ChessUnannotatorViewModel>(), ownsViewModel: true)
    {
    }

    public ChessUnannotatorPage(ChessUnannotatorViewModel viewModel, bool ownsViewModel = false)
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

