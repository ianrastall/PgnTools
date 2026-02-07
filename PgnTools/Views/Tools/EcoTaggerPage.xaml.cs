namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the ECO Tagger tool.
/// </summary>
public sealed partial class EcoTaggerPage : Page
{
    private readonly bool _ownsViewModel;
    public EcoTaggerViewModel ViewModel { get; }

    public EcoTaggerPage() : this(App.GetService<EcoTaggerViewModel>(), ownsViewModel: true)
    {
    }

    public EcoTaggerPage(EcoTaggerViewModel viewModel, bool ownsViewModel = false)
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
            ViewModel.Dispose();
        }
    }
}

