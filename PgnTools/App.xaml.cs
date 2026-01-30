using Microsoft.Extensions.Hosting;
using PgnTools.Models;

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
        // Required for single-file publishing with Windows App SDK
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        
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
        services.AddTransient<IPgnInfoService, PgnInfoService>();
        services.AddTransient<IPgnSplitterService, PgnSplitterService>();
        services.AddTransient<IRemoveDoublesService, RemoveDoublesService>();
        services.AddTransient<IPgnFilterService, PgnFilterService>();
        services.AddTransient<IStockfishNormalizerService, StockfishNormalizerService>();
        services.AddTransient<IPlycountAdderService, PlycountAdderService>();
        services.AddTransient<ICategoryTaggerService, CategoryTaggerService>();
        services.AddTransient<IEcoTaggerService, EcoTaggerService>();
        services.AddTransient<ITourBreakerService, TourBreakerService>();
        services.AddTransient<IChessAnalyzerService, ChessAnalyzerService>();
        services.AddSingleton<IStockfishDownloaderService, StockfishDownloaderService>();
        services.AddTransient<ITwicDownloaderService, TwicDownloaderService>();
        services.AddTransient<IPgnMentorDownloaderService, PgnMentorDownloaderService>();
        services.AddTransient<IPgnJoinerService, PgnJoinerService>();
        services.AddTransient<IPgnSorterService, PgnSorterService>();
        services.AddTransient<IEloAdderService, EloAdderService>();
        services.AddSingleton<IRatingDatabase, EmbeddedRatingsDatabase>();
        services.AddTransient<IChesscomDownloaderService, ChesscomDownloaderService>();
        services.AddTransient<ILichessDownloaderService, LichessDownloaderService>();
        services.AddTransient<ILichessDbDownloaderService, LichessDbDownloaderService>();
        services.AddTransient<ILc0DownloaderService, Lc0DownloaderService>();
        
        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<PgnInfoViewModel>();
        services.AddTransient<PgnSplitterViewModel>();
        services.AddTransient<RemoveDoublesViewModel>();
        services.AddTransient<FilterViewModel>();
        services.AddTransient<StockfishNormalizerViewModel>();
        services.AddTransient<PlycountAdderViewModel>();
        services.AddTransient<CategoryTaggerViewModel>();
        services.AddTransient<EcoTaggerViewModel>();
        services.AddTransient<TourBreakerViewModel>();
        services.AddTransient<ChessAnalyzerViewModel>();
        services.AddTransient<TwicDownloaderViewModel>();
        services.AddTransient<PgnMentorDownloaderViewModel>();
        services.AddTransient<PgnJoinerViewModel>();
        services.AddTransient<PgnSorterViewModel>();
        services.AddTransient<EloAdderViewModel>();
        services.AddTransient<ChesscomDownloaderViewModel>();
        services.AddTransient<LichessDownloaderViewModel>();
        services.AddTransient<LichessDbDownloaderViewModel>();
        services.AddTransient<LichessToolsViewModel>();
        services.AddTransient<Lc0DownloaderViewModel>();

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

        return app.Services.GetRequiredService<T>();
    }
}
