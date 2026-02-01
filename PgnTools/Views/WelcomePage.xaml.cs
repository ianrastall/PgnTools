namespace PgnTools.Views;

/// <summary>
/// Welcome page shown when the app starts.
/// </summary>
public sealed partial class WelcomePage : Page
{
    public WelcomeViewModel ViewModel { get; }

    public WelcomePage() : this(App.GetService<WelcomeViewModel>())
    {
    }

    public WelcomePage(WelcomeViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        this.InitializeComponent();
    }
}
