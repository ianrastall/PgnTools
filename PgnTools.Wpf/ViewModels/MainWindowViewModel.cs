namespace PgnTools.Wpf;

public sealed class MainWindowViewModel(
    ChesscomHubViewModel chesscom,
    LichessToolsViewModel lichess,
    AnalysisHubViewModel analysis,
    EnrichmentHubViewModel enrichment,
    OrganizersHubViewModel organizers,
    Lc0DownloaderViewModel lc0,
    PgnMentorDownloaderViewModel pgnMentor,
    TwicDownloaderViewModel twic,
    SettingsViewModel settings) : IInitializable, IDisposable
{
    private bool _disposed;

    public string Title => "PGN Tools";

    public ChesscomHubViewModel Chesscom { get; } = chesscom;

    public LichessToolsViewModel Lichess { get; } = lichess;

    public AnalysisHubViewModel Analysis { get; } = analysis;

    public EnrichmentHubViewModel Enrichment { get; } = enrichment;

    public OrganizersHubViewModel Organizers { get; } = organizers;

    public Lc0DownloaderViewModel Lc0 { get; } = lc0;

    public PgnMentorDownloaderViewModel PgnMentor { get; } = pgnMentor;

    public TwicDownloaderViewModel Twic { get; } = twic;

    public SettingsViewModel Settings { get; } = settings;

    public void Initialize()
    {
        Chesscom.Initialize();
        Lichess.Initialize();
        Analysis.Initialize();
        Enrichment.Initialize();
        Organizers.Initialize();
        Lc0.Initialize();
        PgnMentor.Initialize();
        Twic.Initialize();
        Settings.Initialize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Chesscom.Dispose();
        Lichess.Dispose();
        Analysis.Dispose();
        Enrichment.Dispose();
        Organizers.Dispose();
        Lc0.Dispose();
        PgnMentor.Dispose();
        Twic.Dispose();
        Settings.Dispose();
    }
}
