namespace PgnTools.Services;

/// <summary>
/// Interface for window-related services.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Gets the main window handle (HWND).
    /// </summary>
    nint WindowHandle { get; }
    
    /// <summary>
    /// Gets the main window instance.
    /// </summary>
    Window? MainWindow { get; }
    
    /// <summary>
    /// Sets the main window instance.
    /// </summary>
    void SetMainWindow(Window window);
}

/// <summary>
/// Window service implementation.
/// </summary>
public class WindowService : IWindowService
{
    private Window? _mainWindow;

    public nint WindowHandle { get; private set; }

    public Window? MainWindow => _mainWindow;

    public void SetMainWindow(Window window)
    {
        _mainWindow = window;
        WindowHandle = WindowNative.GetWindowHandle(window);
    }
}
