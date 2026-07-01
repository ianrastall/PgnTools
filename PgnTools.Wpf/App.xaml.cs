using System.Windows;
using PgnTools.Wpf.Infrastructure;

namespace PgnTools.Wpf;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = ConfigureServices();

        var settings = _services.GetRequiredService<IAppSettingsService>();
        AccentColorManager.ApplySavedAccent(settings);

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        services.AddSingleton<PgnReader>();
        services.AddSingleton<PgnWriter>();

        // Dedicated HttpClient for the tablebase downloader (no timeout — files are huge).
        services.AddSingleton(CreateTablebaseHttpClient());

        services.AddSingleton<ICategoryTaggerService, CategoryTaggerService>();
        services.AddSingleton<ICheckmateFilterService, CheckmateFilterService>();
        services.AddSingleton<IChessAnalyzerService, ChessAnalyzerService>();
        services.AddSingleton<IChesscomDownloaderService, ChesscomDownloaderService>();
        services.AddSingleton<IChesscomMonthlyDownloaderService, ChesscomMonthlyDownloaderService>();
        services.AddSingleton<IChesscomEventsDownloaderService, ChesscomEventsDownloaderService>();
        services.AddSingleton<IChessUnannotatorService, ChessUnannotatorService>();
        services.AddSingleton<IEcoTaggerService, EcoTaggerService>();
        services.AddSingleton<IEloAdderService, EloAdderService>();
        services.AddSingleton<IEleganceGoldenValidationService, EleganceGoldenValidationService>();
        services.AddSingleton<IEleganceService, EleganceService>();
        services.AddSingleton<ILichessDownloaderService, LichessDownloaderService>();
        services.AddSingleton<ILichessDbDownloaderService, LichessDbDownloaderService>();
        services.AddSingleton<ILc0DownloaderService, Lc0DownloaderService>();
        services.AddSingleton<IPgnMentorDownloaderService, PgnMentorDownloaderService>();
        services.AddSingleton<IPgnFilterService, PgnFilterService>();
        services.AddSingleton<IPgnInfoService, PgnInfoService>();
        services.AddSingleton<IPgnJoinerService, PgnJoinerService>();
        services.AddSingleton<IPgnSorterService, PgnSorterService>();
        services.AddSingleton<IPgnSplitterService, PgnSplitterService>();
        services.AddSingleton<IPlycountAdderService, PlycountAdderService>();
        services.AddSingleton<IRatingDatabase, EmbeddedRatingsDatabase>();
        services.AddSingleton<IRemoveDoublesService, RemoveDoublesService>();
        services.AddSingleton<IStockfishDownloaderService, StockfishDownloaderService>();
        services.AddSingleton<IStockfishNormalizerService, StockfishNormalizerService>();
        services.AddSingleton<IStockfishCompilerService, StockfishCompilerService>();
        services.AddSingleton<IBerserkCompilerService, BerserkCompilerService>();
        services.AddSingleton<ITablebaseDownloaderService, TablebaseDownloaderService>();
        services.AddSingleton<ITourBreakerService, TourBreakerService>();
        services.AddSingleton<ITwicDownloaderService, TwicDownloaderService>();

        services.AddSingleton<CategoryTaggerViewModel>();
        services.AddSingleton<CheckmateFilterViewModel>();
        services.AddSingleton<ChessAnalyzerViewModel>();
        services.AddSingleton<ChesscomDownloaderViewModel>();
        services.AddSingleton<ChesscomMonthlyDownloaderViewModel>();
        services.AddSingleton<ChesscomEventsDownloaderViewModel>();
        services.AddSingleton<CompilerViewModel>();
        services.AddSingleton<TablebaseDownloaderViewModel>();
        services.AddSingleton<ChessUnannotatorViewModel>();
        services.AddSingleton<EcoTaggerViewModel>();
        services.AddSingleton<EloAdderViewModel>();
        services.AddSingleton<EleganceViewModel>();
        services.AddSingleton<FilterViewModel>();
        services.AddSingleton<LichessDownloaderViewModel>();
        services.AddSingleton<LichessDbDownloaderViewModel>();
        services.AddSingleton<LichessToolsViewModel>();
        services.AddSingleton<Lc0DownloaderViewModel>();
        services.AddSingleton<PgnJoinerViewModel>();
        services.AddSingleton<PgnMentorDownloaderViewModel>();
        services.AddSingleton<PgnInfoViewModel>();
        services.AddSingleton<PgnSorterViewModel>();
        services.AddSingleton<PgnSplitterViewModel>();
        services.AddSingleton<PlycountAdderViewModel>();
        services.AddSingleton<RemoveDoublesViewModel>();
        services.AddSingleton<StockfishNormalizerViewModel>();
        services.AddSingleton<TourBreakerViewModel>();
        services.AddSingleton<TwicDownloaderViewModel>();
        services.AddSingleton<AnalysisHubViewModel>();
        services.AddSingleton<ChesscomHubViewModel>();
        services.AddSingleton<EnrichmentHubViewModel>();
        services.AddSingleton<OrganizersHubViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static HttpClient CreateTablebaseHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }
}
