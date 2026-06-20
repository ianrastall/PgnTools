namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel hosting Enrichment tools (taggers, adders, normalizers) on a single page.
/// </summary>
public sealed partial class EnrichmentViewModel(
    EcoTaggerViewModel ecoTagger,
    CategoryTaggerViewModel categoryTagger,
    EloAdderViewModel eloAdder,
    PlycountAdderViewModel plycountAdder,
    StockfishNormalizerViewModel stockfishNormalizer,
    ChessUnannotatorViewModel unannotator) : BaseViewModel, IInitializable, IDisposable
{
    private bool _disposed;

    public EcoTaggerViewModel EcoTagger { get; } = ecoTagger;
    public CategoryTaggerViewModel CategoryTagger { get; } = categoryTagger;
    public EloAdderViewModel EloAdder { get; } = eloAdder;
    public PlycountAdderViewModel PlycountAdder { get; } = plycountAdder;
    public StockfishNormalizerViewModel StockfishNormalizer { get; } = stockfishNormalizer;
    public ChessUnannotatorViewModel Unannotator { get; } = unannotator;

    public void Initialize()
    {
        Title = "Enrichment";
        StatusSeverity = InfoBarSeverity.Informational;
        EcoTagger.Initialize();
        CategoryTagger.Initialize();
        EloAdder.Initialize();
        PlycountAdder.Initialize();
        StockfishNormalizer.Initialize();
        Unannotator.Initialize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EcoTagger.Dispose();
        CategoryTagger.Dispose();
        EloAdder.Dispose();
        PlycountAdder.Dispose();
        StockfishNormalizer.Dispose();
        Unannotator.Dispose();
    }
}
