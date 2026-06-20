namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel hosting Analysis tools on a single page.
/// </summary>
public sealed partial class AnalysisViewModel(
    PgnInfoViewModel pgnInfo,
    ChessAnalyzerViewModel chessAnalyzer,
    EleganceViewModel elegance,
    CheckmateFilterViewModel checkmateFilter) : BaseViewModel, IInitializable, IDisposable
{
    private bool _disposed;

    public PgnInfoViewModel PgnInfo { get; } = pgnInfo;
    public ChessAnalyzerViewModel ChessAnalyzer { get; } = chessAnalyzer;
    public EleganceViewModel Elegance { get; } = elegance;
    public CheckmateFilterViewModel CheckmateFilter { get; } = checkmateFilter;

    public void Initialize()
    {
        Title = "Analysis";
        StatusSeverity = InfoBarSeverity.Informational;
        PgnInfo.Initialize();
        ChessAnalyzer.Initialize();
        Elegance.Initialize();
        CheckmateFilter.Initialize();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PgnInfo.Dispose();
        ChessAnalyzer.Dispose();
        Elegance.Dispose();
        CheckmateFilter.Dispose();
    }
}
