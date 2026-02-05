namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the Category Tagger tool.
/// </summary>
public sealed partial class CategoryTaggerPage : Page
{
    private readonly bool _ownsViewModel;
    public CategoryTaggerViewModel ViewModel { get; }

    public CategoryTaggerPage() : this(App.GetService<CategoryTaggerViewModel>(), ownsViewModel: true)
    {
    }

    public CategoryTaggerPage(CategoryTaggerViewModel viewModel, bool ownsViewModel = false)
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
                ViewModel.DisposeWhenIdle();
            }
            else
            {
                ViewModel.Dispose();
            }
        }
    }
}


