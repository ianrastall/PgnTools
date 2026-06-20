namespace PgnTools.Wpf;

public sealed class AnalysisHubViewModel(
    PgnInfoViewModel pgnInfo,
    ChessAnalyzerViewModel chessAnalyzer,
    EleganceViewModel elegance,
    CheckmateFilterViewModel checkmateFilter) : IInitializable, IDisposable
{
    private bool _disposed;

    public PgnInfoViewModel PgnInfo { get; } = pgnInfo;

    public ChessAnalyzerViewModel ChessAnalyzer { get; } = chessAnalyzer;

    public EleganceViewModel Elegance { get; } = elegance;

    public CheckmateFilterViewModel CheckmateFilter { get; } = checkmateFilter;

    public void Initialize()
    {
        PgnInfo.Initialize();
        ChessAnalyzer.Initialize();
        Elegance.Initialize();
        CheckmateFilter.Initialize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PgnInfo.Dispose();
        ChessAnalyzer.Dispose();
        Elegance.Dispose();
        CheckmateFilter.Dispose();
    }
}
