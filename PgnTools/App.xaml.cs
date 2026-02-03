using System.Net.Http;
using Microsoft.Extensions.Hosting;
using PgnTools.Models;
using PgnTools.ViewModels;

namespace PgnTools;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Gets the current App instance.
    /// </summary>
    public static new App Current => (App)Application.Current;
    
    /// <summary>
    /// Gets the DI service provider.
    /// </summary>
    public IServiceProvider Services { get; }
    
    /// <summary>
    /// Gets the main window instance.
    /// </summary>
    public Window? MainWindow => _window;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        Services = ConfigureServices();
        this.InitializeComponent();
    }

    /// <summary>
    /// Configures the services for the application.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core Services
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        // PGN IO
        services.AddSingleton<PgnReader>();
        services.AddSingleton<PgnWriter>();
        
        // Tool Services
        services.AddSingleton(CreateTablebaseHttpClient());
        services.AddSingleton<IStockfishDownloaderService, StockfishDownloaderService>();
        services.AddSingleton<IRatingDatabase, EmbeddedRatingsDatabase>();

        RegisterToolServices(services);
        RegisterViewModels(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _window = new MainWindow();

        // Initialize window service with the main window
        var windowService = Services.GetRequiredService<IWindowService>();
        windowService.SetMainWindow(_window);

        // Set up navigation service
        var navigationService = Services.GetRequiredService<INavigationService>();
        RegisterPages(navigationService);

        _window.Activate();
    }

    /// <summary>
    /// Registers all pages with the navigation service.
    /// </summary>
    private static void RegisterPages(INavigationService navigationService)
    {
        if (navigationService is NavigationService navService)
        {
            foreach (var tool in ToolRegistry.Tools)
            {
                navService.RegisterPage(tool.Key, tool.PageType);
            }
        }
    }

    /// <summary>
    /// Gets a service from the DI container.
    /// </summary>
    public static T GetService<T>() where T : class
    {
        if (Application.Current is not App app)
        {
            throw new InvalidOperationException("Application.Current is not available or is not of type App.");
        }

        var service = app.Services.GetRequiredService<T>();
        if (service is IInitializable initializable)
        {
            initializable.Initialize();
        }
        return service;
    }

    private static void RegisterToolServices(IServiceCollection services)
    {
        services.AddTransient<ICategoryTaggerService, CategoryTaggerService>();
        services.AddTransient<ICheckmateFilterService, CheckmateFilterService>();
        services.AddTransient<IChessAnalyzerService, ChessAnalyzerService>();
        services.AddTransient<IChesscomDownloaderService, ChesscomDownloaderService>();
        services.AddTransient<IChessUnannotatorService, ChessUnannotatorService>();
        services.AddTransient<IEcoTaggerService, EcoTaggerService>();
        services.AddTransient<IEleganceGoldenValidationService, EleganceGoldenValidationService>();
        services.AddTransient<IEleganceService, EleganceService>();
        services.AddTransient<IEloAdderService, EloAdderService>();
        services.AddTransient<ILc0DownloaderService, Lc0DownloaderService>();
        services.AddTransient<ILichessDbDownloaderService, LichessDbDownloaderService>();
        services.AddTransient<ILichessDownloaderService, LichessDownloaderService>();
        services.AddTransient<IPgnFilterService, PgnFilterService>();
        services.AddTransient<IPgnInfoService, PgnInfoService>();
        services.AddTransient<IPgnJoinerService, PgnJoinerService>();
        services.AddTransient<IPgnMentorDownloaderService, PgnMentorDownloaderService>();
        services.AddTransient<IPgnSorterService, PgnSorterService>();
        services.AddTransient<IPgnSplitterService, PgnSplitterService>();
        services.AddTransient<IPlycountAdderService, PlycountAdderService>();
        services.AddTransient<IRemoveDoublesService, RemoveDoublesService>();
        services.AddTransient<IStockfishNormalizerService, StockfishNormalizerService>();
        services.AddTransient<ITourBreakerService, TourBreakerService>();
        services.AddTransient<ITablebaseDownloaderService, TablebaseDownloaderService>();
        services.AddTransient<ITwicDownloaderService, TwicDownloaderService>();
    }

    private static void RegisterViewModels(IServiceCollection services)
    {
        services.AddTransient<MainViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<WelcomeViewModel>();

        services.AddTransient<CategoryTaggerViewModel>();
        services.AddTransient<CheckmateFilterViewModel>();
        services.AddTransient<ChessAnalyzerViewModel>();
        services.AddTransient<ChesscomDownloaderViewModel>();
        services.AddTransient<ChessUnannotatorViewModel>();
        services.AddTransient<EcoTaggerViewModel>();
        services.AddTransient<EleganceViewModel>();
        services.AddTransient<EloAdderViewModel>();
        services.AddTransient<FilterViewModel>();
        services.AddTransient<Lc0DownloaderViewModel>();
        services.AddTransient<LichessDbDownloaderViewModel>();
        services.AddTransient<LichessDownloaderViewModel>();
        services.AddTransient<LichessToolsViewModel>();
        services.AddTransient<PgnJoinerViewModel>();
        services.AddTransient<PgnInfoViewModel>();
        services.AddTransient<PgnMentorDownloaderViewModel>();
        services.AddTransient<PgnSorterViewModel>();
        services.AddTransient<PgnSplitterViewModel>();
        services.AddTransient<PlycountAdderViewModel>();
        services.AddTransient<RemoveDoublesViewModel>();
        services.AddTransient<StockfishNormalizerViewModel>();
        services.AddTransient<TourBreakerViewModel>();
        services.AddTransient<TablebaseDownloaderViewModel>();
        services.AddTransient<TwicDownloaderViewModel>();
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
