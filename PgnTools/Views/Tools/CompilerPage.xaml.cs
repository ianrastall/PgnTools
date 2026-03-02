namespace PgnTools.Views.Tools;

/// <summary>
/// Page for compiling chess engines from source.
/// </summary>
public sealed partial class CompilerPage : Page
{
    private readonly bool _ownsViewModel;
    public CompilerViewModel ViewModel { get; }

    public CompilerPage() : this(App.GetService<CompilerViewModel>(), ownsViewModel: true)
    {
    }

    public CompilerPage(CompilerViewModel viewModel, bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _ownsViewModel = ownsViewModel;
        InitializeComponent();
    }

    private void GitHubPatBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            ViewModel.SetGitHubPat(passwordBox.Password);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_ownsViewModel)
        {
            ViewModel.RequestDispose();
        }
    }
}
