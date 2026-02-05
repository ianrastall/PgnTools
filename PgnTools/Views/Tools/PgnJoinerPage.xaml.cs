namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the PGN Joiner tool.
/// </summary>
public sealed partial class PgnJoinerPage : Page
{
    private readonly bool _ownsViewModel;
    public PgnJoinerViewModel ViewModel { get; }

    public PgnJoinerPage() : this(App.GetService<PgnJoinerViewModel>(), ownsViewModel: true)
    {
    }

    public PgnJoinerPage(PgnJoinerViewModel viewModel, bool ownsViewModel = false)
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

