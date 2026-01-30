namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Chess Analyzer tool.
/// </summary>
public sealed partial class ChessAnalyzerPage : Page
{
    private readonly bool _ownsViewModel;
    public ChessAnalyzerViewModel ViewModel { get; }

    public ChessAnalyzerPage() : this(App.GetService<ChessAnalyzerViewModel>(), ownsViewModel: true)
    {
    }

    public ChessAnalyzerPage(ChessAnalyzerViewModel viewModel, bool ownsViewModel = false)
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


