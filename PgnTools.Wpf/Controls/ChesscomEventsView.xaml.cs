using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PgnTools.ViewModels.Tools;

namespace PgnTools.Wpf.Controls;

/// <summary>
/// WPF host for the Chess.com events downloader. The embedded WebView2 lets the user sign in
/// to Chess.com; the auth cookie is scraped and handed to the shared view-model, which does the
/// actual downloading. Mirrors the WinUI ChesscomEventsDownloaderView.
/// </summary>
public partial class ChesscomEventsView : System.Windows.Controls.UserControl
{
    private static readonly Uri ChesscomSessionUri = new("https://www.chess.com/events/pgn/23851/0");
    private static readonly Uri ChesscomSignInUri =
        new("https://www.chess.com/login_and_go?returnUrl=https%3A%2F%2Fwww.chess.com%2Fevents%2Fpgn%2F23851%2F0");

    private bool _webViewInitialized;
    private bool _autoProbeDone;

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ChesscomEventsDownloaderViewModel),
            typeof(ChesscomEventsView),
            new PropertyMetadata(null, OnViewModelChanged));

    public ChesscomEventsDownloaderViewModel? ViewModel
    {
        get => (ChesscomEventsDownloaderViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ChesscomEventsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ChesscomEventsView view)
        {
            view.Root.DataContext = e.NewValue;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Only probe for a saved session the first time the tab is shown.
        if (_autoProbeDone)
        {
            return;
        }

        _autoProbeDone = true;

        try
        {
            await InitializeSessionBrowserAsync();
            ViewModel?.ClearCapturedSession("Checking for a saved Chess.com session...");

            if (!await CaptureChesscomSessionAsync(reportMissing: false))
            {
                ShowSignInBrowser("No saved Chess.com session found. Sign in below.");
            }
        }
        catch (Exception ex)
        {
            ViewModel?.ClearCapturedSession($"Could not start the sign-in browser: {ex.Message}");
        }
    }

    private async void OpenSignInButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeSessionBrowserAsync();
            ShowSignInBrowser("Sign in to Chess.com in the browser below.");
        }
        catch (Exception ex)
        {
            ViewModel?.ClearCapturedSession($"Could not open the sign-in browser: {ex.Message}");
        }
    }

    private async void CaptureSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CaptureChesscomSessionAsync(reportMissing: true);
        }
        catch (Exception ex)
        {
            ViewModel?.ClearCapturedSession($"Could not capture the session: {ex.Message}");
        }
    }

    private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CaptureChesscomSessionAsync(reportMissing: true))
            {
                ShowSignInBrowser("No signed-in Chess.com session was found. Complete login below, then start again.");
                return;
            }

            if (ViewModel?.StartCommand.CanExecute(null) == true)
            {
                ViewModel.StartCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            ViewModel?.ClearCapturedSession($"Could not start the download: {ex.Message}");
        }
    }

    private async Task InitializeSessionBrowserAsync()
    {
        if (_webViewInitialized && SessionWebView.CoreWebView2 != null)
        {
            return;
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools",
            "WebView2",
            "ChesscomEvents");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await SessionWebView.EnsureCoreWebView2Async(environment);

        var coreWebView = SessionWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 did not initialize.");

        coreWebView.NavigationCompleted += async (_, _) =>
        {
            try
            {
                await CaptureChesscomSessionAsync(reportMissing: false);
            }
            catch
            {
                // Best-effort re-probe after each navigation; ignore transient failures.
            }
        };

        _webViewInitialized = true;
    }

    private void ShowSignInBrowser(string message)
    {
        ViewModel?.ClearCapturedSession(message);
        SessionWebView.Visibility = Visibility.Visible;
        SessionWebView.Source = ChesscomSignInUri;
    }

    private async Task<bool> CaptureChesscomSessionAsync(bool reportMissing)
    {
        await InitializeSessionBrowserAsync();

        var coreWebView = SessionWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 did not initialize.");
        var cookies = await coreWebView.CookieManager.GetCookiesAsync(ChesscomSessionUri.ToString());
        var usableCookies = cookies
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
            .GroupBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasAuthCookie = usableCookies.Any(cookie =>
            cookie.Name.Equals("ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            cookie.Name.Equals("CHESSCOM_REMEMBERME", StringComparison.OrdinalIgnoreCase));

        if (!hasAuthCookie)
        {
            if (reportMissing)
            {
                ViewModel?.ClearCapturedSession("No signed-in Chess.com session was found. Use Open Sign In, then capture again.");
                SessionWebView.Visibility = Visibility.Visible;
            }

            return false;
        }

        var cookieHeader = string.Join("; ", usableCookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        ViewModel?.ApplyCapturedCookieHeader(cookieHeader, usableCookies.Count);
        SessionWebView.Visibility = Visibility.Collapsed;
        return true;
    }
}
