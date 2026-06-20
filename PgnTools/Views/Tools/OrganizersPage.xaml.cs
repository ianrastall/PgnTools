namespace PgnTools.Views.Tools;

/// <summary>
/// Page hosting all Organizer tools (filter, sort, split, merge, dedup, tour break).
/// </summary>
public sealed partial class OrganizersPage : Page
{
    private readonly bool _ownsViewModel;
    public OrganizersViewModel ViewModel { get; }

    public OrganizersPage() : this(App.GetService<OrganizersViewModel>(), ownsViewModel: true) { }

    public OrganizersPage(OrganizersViewModel viewModel, bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        _ownsViewModel = ownsViewModel;
        this.InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (!_ownsViewModel) return;

        bool anyRunning =
            ViewModel.Filter.IsRunning || ViewModel.Sorter.IsRunning ||
            ViewModel.Splitter.IsRunning || ViewModel.Joiner.IsRunning ||
            ViewModel.Deduplicator.IsRunning || ViewModel.TourBreaker.IsRunning;

        if (anyRunning)
        {
            if (ViewModel.Filter.IsRunning) ViewModel.Filter.CancelCommand.Execute(null);
            if (ViewModel.Sorter.IsRunning) ViewModel.Sorter.CancelCommand.Execute(null);
            if (ViewModel.Splitter.IsRunning) ViewModel.Splitter.CancelCommand.Execute(null);
            if (ViewModel.Joiner.IsRunning) ViewModel.Joiner.CancelCommand.Execute(null);
            if (ViewModel.Deduplicator.IsRunning) ViewModel.Deduplicator.CancelCommand.Execute(null);
            if (ViewModel.TourBreaker.IsRunning) ViewModel.TourBreaker.CancelCommand.Execute(null);
        }
        else
        {
            ViewModel.Dispose();
        }
    }
}
