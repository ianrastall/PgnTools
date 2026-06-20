namespace PgnTools.Wpf;

public sealed class EnrichmentHubViewModel(
    EcoTaggerViewModel ecoTagger,
    CategoryTaggerViewModel categoryTagger,
    EloAdderViewModel eloAdder,
    PlycountAdderViewModel plycountAdder,
    StockfishNormalizerViewModel stockfishNormalizer,
    ChessUnannotatorViewModel unannotator) : IInitializable, IDisposable
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
