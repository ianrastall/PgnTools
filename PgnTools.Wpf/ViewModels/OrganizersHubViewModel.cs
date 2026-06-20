namespace PgnTools.Wpf;

public sealed class OrganizersHubViewModel(
    FilterViewModel filter,
    PgnSorterViewModel sorter,
    PgnSplitterViewModel splitter,
    PgnJoinerViewModel joiner,
    RemoveDoublesViewModel deduplicator,
    TourBreakerViewModel tourBreaker) : IInitializable, IDisposable
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
        Filter.Initialize();
        Sorter.Initialize();
        Splitter.Initialize();
        Joiner.Initialize();
        Deduplicator.Initialize();
        TourBreaker.Initialize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Filter.Dispose();
        Sorter.Dispose();
        Splitter.Dispose();
        Joiner.Dispose();
        Deduplicator.Dispose();
        TourBreaker.Dispose();
    }
}
