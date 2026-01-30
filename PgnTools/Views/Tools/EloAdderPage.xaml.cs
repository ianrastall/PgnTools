namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Elo Adder tool.
/// </summary>
public sealed partial class EloAdderPage : Page
{
    private readonly bool _ownsViewModel;
    public EloAdderViewModel ViewModel { get; }

    public EloAdderPage() : this(App.GetService<EloAdderViewModel>(), ownsViewModel: true)
    {
    }

    public EloAdderPage(EloAdderViewModel viewModel, bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        _ownsViewModel = ownsViewModel;
        this.InitializeComponent();
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

