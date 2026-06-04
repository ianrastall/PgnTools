namespace PgnTools.Views.Tools;

public sealed partial class ChesscomEventsDownloaderView : UserControl
{
    private static readonly Uri ChesscomSessionUri = new("https://www.chess.com/events/pgn/23851/0");
    private static readonly Uri ChesscomSignInUri =
        new("https://www.chess.com/login_and_go?returnUrl=https%3A%2F%2Fwww.chess.com%2Fevents%2Fpgn%2F23851%2F0");

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ChesscomEventsDownloaderViewModel),
            typeof(ChesscomEventsDownloaderView),
            new PropertyMetadata(null));

    public ChesscomEventsDownloaderViewModel ViewModel
    {
        get => (ChesscomEventsDownloaderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ChesscomEventsDownloaderView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeSessionBrowserAsync();
        ViewModel?.ClearCapturedSession("Checking for a saved Chess.com session...");

        if (!await CaptureChesscomSessionAsync(reportMissing: false))
        {
            ShowSignInBrowser("No saved Chess.com session found. Sign in below.");
        }
    }

    private async void OpenSignInButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeSessionBrowserAsync();
        ShowSignInBrowser("Sign in to Chess.com in the browser below.");
    }

    private async void CaptureSessionButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureChesscomSessionAsync(reportMissing: true);
    }

    private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
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

    private async Task InitializeSessionBrowserAsync()
    {
        if (SessionWebView.CoreWebView2 != null)
        {
            return;
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools",
            "WebView2",
            "ChesscomEvents");
        Directory.CreateDirectory(userDataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

        await SessionWebView.EnsureCoreWebView2Async();
        var coreWebView = SessionWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 did not initialize.");

        coreWebView.NavigationCompleted += async (_, _) =>
        {
            await CaptureChesscomSessionAsync(reportMissing: false);
        };
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
