namespace PgnTools;

/// <summary>
/// Main application window.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ShellPageContent.CustomTitleBar);

        var appWindow = this.AppWindow;
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        appWindow.SetIcon(iconPath);

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            if (presenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            {
                presenter.Maximize();
            }
        }
    }
}
