namespace PgnTools.Views.Tools;

/// <summary>
/// Page hosting all Analysis tools.
/// </summary>
public sealed partial class AnalysisPage : Page
{
    private readonly bool _ownsViewModel;
    public AnalysisViewModel ViewModel { get; }

    public AnalysisPage() : this(App.GetService<AnalysisViewModel>(), ownsViewModel: true) { }

    public AnalysisPage(AnalysisViewModel viewModel, bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        _ownsViewModel = ownsViewModel;
        this.InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (!_ownsViewModel) return;

        bool anyRunning =
            ViewModel.PgnInfo.IsAnalyzing || ViewModel.ChessAnalyzer.IsRunning ||
            ViewModel.Elegance.IsRunning || ViewModel.CheckmateFilter.IsRunning;

        if (anyRunning)
        {
            if (ViewModel.PgnInfo.IsAnalyzing) ViewModel.PgnInfo.CancelAnalysisCommand.Execute(null);
            if (ViewModel.ChessAnalyzer.IsRunning) ViewModel.ChessAnalyzer.CancelCommand.Execute(null);
            if (ViewModel.Elegance.IsRunning) ViewModel.Elegance.CancelCommand.Execute(null);
            if (ViewModel.CheckmateFilter.IsRunning) ViewModel.CheckmateFilter.CancelCommand.Execute(null);
        }
        else
        {
            ViewModel.Dispose();
        }
    }
}
