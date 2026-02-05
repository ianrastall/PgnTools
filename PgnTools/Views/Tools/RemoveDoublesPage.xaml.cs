namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Deduplicator tool.
/// </summary>
public sealed partial class RemoveDoublesPage : Page
{
    private readonly bool _ownsViewModel;
    public RemoveDoublesViewModel ViewModel { get; }

    public RemoveDoublesPage() : this(App.GetService<RemoveDoublesViewModel>(), ownsViewModel: true)
    {
    }

    public RemoveDoublesPage(RemoveDoublesViewModel viewModel, bool ownsViewModel = false)
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

