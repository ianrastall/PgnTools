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

        return app.Services.GetRequiredService<T>();
    }

    private static void RegisterToolServices(IServiceCollection services)
    {
        var assembly = typeof(App).Assembly;
        var serviceTypes = assembly.GetTypes()
            .Where(type => type.IsClass &&
                           !type.IsAbstract &&
                           type.Namespace == "PgnTools.Services" &&
                           type.Name.EndsWith("Service", StringComparison.Ordinal));

        foreach (var implementation in serviceTypes)
        {
            var interfaceType = implementation.GetInterface($"I{implementation.Name}");
            if (interfaceType == null)
            {
                continue;
            }

            if (services.Any(descriptor => descriptor.ServiceType == interfaceType))
            {
                continue;
            }

            services.AddTransient(interfaceType, implementation);
        }
    }

    private static void RegisterViewModels(IServiceCollection services)
    {
        var assembly = typeof(App).Assembly;
        var viewModelTypes = assembly.GetTypes()
            .Where(type => type.IsClass &&
                           !type.IsAbstract &&
                           type.IsSubclassOf(typeof(BaseViewModel)));

        foreach (var viewModel in viewModelTypes)
        {
            services.AddTransient(viewModel);
        }
    }
}
