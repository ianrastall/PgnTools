namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Stockfish Normalizer tool.
/// </summary>
public sealed partial class StockfishNormalizerPage : Page
{
    private readonly bool _ownsViewModel;
    public StockfishNormalizerViewModel ViewModel { get; }

    public StockfishNormalizerPage() : this(App.GetService<StockfishNormalizerViewModel>(), ownsViewModel: true)
    {
    }

    public StockfishNormalizerPage(StockfishNormalizerViewModel viewModel, bool ownsViewModel = false)
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

