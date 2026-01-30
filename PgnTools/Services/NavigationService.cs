namespace PgnTools.Services;

/// <summary>
/// Interface for navigation service.
/// </summary>
public interface INavigationService
{
    bool CanGoBack { get; }
    Frame? Frame { get; set; }
    
    bool NavigateTo(string pageKey, object? parameter = null);
    bool NavigateTo(Type pageType, object? parameter = null);
    bool GoBack();
}

/// <summary>
/// Navigation service implementation for frame-based navigation.
/// </summary>
public class NavigationService : INavigationService
{
    private Frame? _frame;
    private readonly Dictionary<string, Type> _pageRegistry = new();

    public Frame? Frame
    {
        get => _frame;
        set
        {
            if (_frame != null)
            {
                _frame.Navigated -= OnNavigated;
            }
            
            _frame = value;
            
            if (_frame != null)
            {
                _frame.Navigated += OnNavigated;
            }
        }
    }

    public bool CanGoBack => Frame?.CanGoBack ?? false;

    public event EventHandler<NavigationEventArgs>? Navigated;

    /// <summary>
    /// Registers a page type with a key for navigation.
    /// </summary>
    public void RegisterPage(string key, Type pageType)
    {
        _pageRegistry[key] = pageType;
    }

    public bool NavigateTo(string pageKey, object? parameter = null)
    {
        if (!_pageRegistry.TryGetValue(pageKey, out var pageType))
        {
            throw new ArgumentException($"Page not found: {pageKey}. Did you forget to register it?");
        }

        return NavigateTo(pageType, parameter);
    }

    public bool NavigateTo(Type pageType, object? parameter = null)
    {
        if (Frame == null)
        {
            return false;
        }

        var vmBeforeNavigation = Frame.GetPageViewModel();

        return Frame.Navigate(pageType, parameter);
    }

    public bool GoBack()
    {
        if (Frame?.CanGoBack != true)
        {
            return false;
        }

        Frame.GoBack();
        return true;
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        Navigated?.Invoke(this, e);
    }
}

/// <summary>
/// Extension methods for Frame.
/// </summary>
public static class FrameExtensions
{
    public static object? GetPageViewModel(this Frame frame)
    {
        return (frame.Content as Page)?.DataContext;
    }
}
