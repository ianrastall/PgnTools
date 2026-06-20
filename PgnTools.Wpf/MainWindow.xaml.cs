using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;

namespace PgnTools.Wpf;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private static void EnableDarkTitleBar(nint hwnd)
    {
        var enabled = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private readonly MainWindowViewModel _viewModel;
    private readonly IWindowService _windowService;
    private bool _initialized;

    public MainWindow(MainWindowViewModel viewModel, IWindowService windowService)
    {
        _viewModel = viewModel;
        _windowService = windowService;

        InitializeComponent();
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowService.SetMainWindow(this);
        EnableDarkTitleBar(new WindowInteropHelper(this).Handle);
        if (_initialized)
        {
            return;
        }

        _viewModel.Initialize();
        _initialized = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }

    private void OnSupportLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore failures to launch the browser.
        }

        e.Handled = true;
    }
}
