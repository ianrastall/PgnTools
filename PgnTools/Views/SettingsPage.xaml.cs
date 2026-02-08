using PgnTools.ViewModels;

namespace PgnTools.Views;

/// <summary>
/// Settings page.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly bool _ownsViewModel;
    public SettingsViewModel ViewModel { get; }

    public SettingsPage() : this(App.GetService<SettingsViewModel>(), ownsViewModel: true)
    {
    }

    public SettingsPage(SettingsViewModel viewModel, bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _ownsViewModel = ownsViewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_ownsViewModel)
        {
            ViewModel.Dispose();
        }
    }
}
