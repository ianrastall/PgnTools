using System.Windows;
using System.Windows.Interop;

namespace PgnTools.Services;

public interface IWindowService
{
    nint WindowHandle { get; }
    Window? MainWindow { get; }
    void SetMainWindow(Window window);
}

public sealed class WindowService : IWindowService
{
    public nint WindowHandle { get; private set; }

    public Window? MainWindow { get; private set; }

    public void SetMainWindow(Window window)
    {
        MainWindow = window;
        WindowHandle = new WindowInteropHelper(window).Handle;
    }
}
