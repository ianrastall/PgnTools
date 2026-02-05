namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Ply Count Adder tool.
/// </summary>
public sealed partial class PlycountAdderPage : Page
{
    private readonly bool _ownsViewModel;
    public PlycountAdderViewModel ViewModel { get; }

    public PlycountAdderPage() : this(App.GetService<PlycountAdderViewModel>(), ownsViewModel: true)
    {
    }

    public PlycountAdderPage(PlycountAdderViewModel viewModel, bool ownsViewModel = false)
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
            if (ViewModel.IsRunning)
            {
                ViewModel.CancelCommand.Execute(null);
            }
            else
            {
                ViewModel.Dispose();
            }
        }
    }
}

