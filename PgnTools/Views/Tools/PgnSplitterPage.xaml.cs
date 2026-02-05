namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the PGN Splitter tool.
/// </summary>
public sealed partial class PgnSplitterPage : Page
{
    private readonly bool _ownsViewModel;
    public PgnSplitterViewModel ViewModel { get; }

    public PgnSplitterPage() : this(App.GetService<PgnSplitterViewModel>(), ownsViewModel: true)
    {
    }

    public PgnSplitterPage(PgnSplitterViewModel viewModel, bool ownsViewModel = false)
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
