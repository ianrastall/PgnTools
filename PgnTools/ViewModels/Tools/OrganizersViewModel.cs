namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel hosting Organizer tools (filter, sort, split, merge, dedup, tour break) on a single page.
/// </summary>
public sealed partial class OrganizersViewModel(
    FilterViewModel filter,
    PgnSorterViewModel sorter,
    PgnSplitterViewModel splitter,
    PgnJoinerViewModel joiner,
    RemoveDoublesViewModel deduplicator,
    TourBreakerViewModel tourBreaker) : BaseViewModel, IInitializable, IDisposable
{
    private bool _disposed;

    public FilterViewModel Filter { get; } = filter;
    public PgnSorterViewModel Sorter { get; } = sorter;
    public PgnSplitterViewModel Splitter { get; } = splitter;
    public PgnJoinerViewModel Joiner { get; } = joiner;
    public RemoveDoublesViewModel Deduplicator { get; } = deduplicator;
    public TourBreakerViewModel TourBreaker { get; } = tourBreaker;

    public void Initialize()
    {
        Title = "Organizers";
        StatusSeverity = InfoBarSeverity.Informational;
        Filter.Initialize();
        Sorter.Initialize();
        Splitter.Initialize();
        Joiner.Initialize();
        Deduplicator.Initialize();
        TourBreaker.Initialize();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Filter.Dispose();
        Sorter.Dispose();
        Splitter.Dispose();
        Joiner.Dispose();
        Deduplicator.Dispose();
        TourBreaker.Dispose();
    }
}
