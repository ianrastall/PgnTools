namespace PgnTools.Models;

/// <summary>
/// Statistics for a PGN file.
/// </summary>
public class PgnStatistics
{
    public long Games { get; set; }
    public int PlayerCount { get; set; }
    public int TournamentCount { get; set; }
    public int TournamentsWithIncompleteGames { get; set; }
    public long IncompleteGames { get; set; }
    
    // Results
    public long WhiteWins { get; set; }
    public long BlackWins { get; set; }
    public long Draws { get; set; }
    public long StarResults { get; set; }
    public long MissingResults { get; set; }
    public long OtherResults { get; set; }
    
    // Elo
    public double? AverageElo { get; set; }
    public int? HighestElo { get; set; }
    public int? LowestElo { get; set; }
    public long GamesWithAnyElo { get; set; }
    public long GamesWithBothElo { get; set; }
    
    // Countries
    public int CountryCount { get; set; }
    
    // Dates
    public DateTime? MinDate { get; set; }
    public DateTime? MaxDate { get; set; }
    public long DateMissing { get; set; }
    
    // ECO
    public Dictionary<char, long> EcoLetterCounts { get; set; } = new();
    public Dictionary<string, long> TopEcoCodes { get; set; } = new();
    public long EcoMissing { get; set; }
    public long EcoInvalid { get; set; }
    
    // Moves
    public double? AveragePlies { get; set; }
    public int? MinPlies { get; set; }
    public int? MaxPlies { get; set; }
    public long GamesWithMoves { get; set; }
    public long GamesWithoutMoves { get; set; }
}
