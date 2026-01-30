using PgnTools.Models;

namespace PgnTools.Views.Tools;

/// <summary>
/// Page for the PGN Info tool.
/// </summary>
public sealed partial class PgnInfoPage : Page
{
    private readonly bool _ownsViewModel;
    public PgnInfoViewModel ViewModel { get; }

    public PgnInfoPage() : this(App.GetService<PgnInfoViewModel>(), ownsViewModel: true)
    {
    }

    public PgnInfoPage(PgnInfoViewModel viewModel, bool ownsViewModel = false)
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

    // Formatting helpers for x:Bind - must be instance methods for WinUI 3 x:Bind function binding
    public string FormatNumber(long value) => value.ToString("N0");
    public string FormatNumber(int value) => value.ToString("N0");

    public string FormatTournamentStats(PgnStatistics? stats)
    {
        if (stats == null) return "N/A";
        return $"{stats.TournamentCount:N0} (incomplete games: {stats.TournamentsWithIncompleteGames:N0})";
    }

    public string FormatDateRange(PgnStatistics? stats)
    {
        if (stats == null) return "N/A";
        if (!stats.MinDate.HasValue || !stats.MaxDate.HasValue)
        {
            return $"N/A (missing dates: {stats.DateMissing:N0})";
        }
        return $"{stats.MinDate:yyyy-MM-dd} to {stats.MaxDate:yyyy-MM-dd} (missing dates: {stats.DateMissing:N0})";
    }

    public string FormatElo(double? value)
    {
        return value.HasValue ? value.Value.ToString("F1") : "N/A";
    }

    public string FormatEloInt(int? value)
    {
        return value.HasValue ? value.Value.ToString("N0") : "N/A";
    }

    public string FormatEloGames(PgnStatistics? stats)
    {
        if (stats == null) return "N/A";
        return $"{stats.GamesWithAnyElo:N0} (both players: {stats.GamesWithBothElo:N0})";
    }

    public string FormatResults(PgnStatistics? stats)
    {
        if (stats == null) return "N/A";
        return $"White wins (1-0): {stats.WhiteWins:N0} | Black wins (0-1): {stats.BlackWins:N0} | Draws (1/2-1/2): {stats.Draws:N0} | Unfinished (*): {stats.StarResults:N0} | Missing result: {stats.MissingResults:N0} | Other result: {stats.OtherResults:N0}";
    }

    public string FormatEcoSpread(PgnStatistics? stats)
    {
        if (stats == null) return "N/A";
        var counts = stats.EcoLetterCounts ?? new Dictionary<char, long>();
        var a = counts.GetValueOrDefault('A', 0);
        var b = counts.GetValueOrDefault('B', 0);
        var c = counts.GetValueOrDefault('C', 0);
        var d = counts.GetValueOrDefault('D', 0);
        var e = counts.GetValueOrDefault('E', 0);
        return $"ECO A: {a:N0} | ECO B: {b:N0} | ECO C: {c:N0} | ECO D: {d:N0} | ECO E: {e:N0} (missing ECO: {stats.EcoMissing:N0}, invalid ECO: {stats.EcoInvalid:N0})";
    }

    public IEnumerable<string> GetTopEco(PgnStatistics? stats)
    {
        if (stats?.TopEcoCodes == null || stats.TopEcoCodes.Count == 0)
            return Enumerable.Empty<string>();

        return stats.TopEcoCodes.Select(kvp => $"  {kvp.Key}: {kvp.Value:N0}");
    }

    public string FormatPlies(double? value)
    {
        return value.HasValue ? value.Value.ToString("F1") : "N/A";
    }

    public string FormatPlyRange(PgnStatistics? stats)
    {
        if (stats == null) return "N/A";
        if (!stats.MinPlies.HasValue || !stats.MaxPlies.HasValue)
            return "N/A";
        return $"{stats.MinPlies:N0} / {stats.MaxPlies:N0}";
    }
}

