namespace PgnTools.Views.Tools;

/// <summary>
/// Page hosting all Enrichment tools (taggers, adders, normalizers).
/// </summary>
public sealed partial class EnrichmentPage : Page
{
    private readonly bool _ownsViewModel;
    public EnrichmentViewModel ViewModel { get; }

    public EnrichmentPage() : this(App.GetService<EnrichmentViewModel>(), ownsViewModel: true)
    {
    }

    public EnrichmentPage(EnrichmentViewModel viewModel, bool ownsViewModel = false)
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
            // Cancel any running tools before disposing
            CancelIfRunning(ViewModel.EcoTagger);
            CancelIfRunning(ViewModel.CategoryTagger);
            CancelIfRunning(ViewModel.EloAdder);
            CancelIfRunning(ViewModel.PlycountAdder);
            CancelIfRunning(ViewModel.StockfishNormalizer);
            CancelIfRunning(ViewModel.Unannotator);

            if (!ViewModel.EcoTagger.IsRunning &&
                !ViewModel.CategoryTagger.IsRunning &&
                !ViewModel.EloAdder.IsRunning &&
                !ViewModel.PlycountAdder.IsRunning &&
                !ViewModel.StockfishNormalizer.IsRunning &&
                !ViewModel.Unannotator.IsRunning)
            {
                ViewModel.Dispose();
            }
        }
    }

    private static void CancelIfRunning(dynamic vm)
    {
        if (vm.IsRunning)
        {
            vm.CancelCommand.Execute(null);
        }
    }
}
