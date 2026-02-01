namespace PgnTools.Models;

/// <summary>
/// Represents a tool in the navigation menu.
/// </summary>
public record ToolMenuItem(
    string Key,
    string Name,
    string Description,
    string IconGlyph,
    Type PageType)
{
    public string? IconAssetRelativePath =>
        ToolIconResolver.ResolveIconRelativePath(Key, Name);

    public Uri? IconAssetUri =>
        ToolIconResolver.ResolveIconUri(Key, Name);

    public bool HasIconAsset =>
        ToolIconResolver.ResolveIconRelativePath(Key, Name) is not null;
}

/// <summary>
/// Tool registry containing all available tools.
/// </summary>
public static class ToolRegistry
{
    public static IReadOnlyList<ToolMenuItem> Tools { get; } =
    [
        new("CategoryTagger", "Category Tagger", "Tag games by tournament category", "\uE8EC", typeof(CategoryTaggerPage)),
        new("ChessAnalyzer", "Chess Analyzer", "Engine analysis of games", "\uE9D9", typeof(ChessAnalyzerPage)),
        new("ChessComDownloader", "Chess.com", "Download via PubAPI", "\uE896", typeof(ChesscomDownloaderPage)),
        new("RemoveDoubles", "Deduplicator", "Deduplicate games", "\uE8D4", typeof(RemoveDoublesPage)),
        new("EcoTagger", "ECO Tagger", "Tag games with ECO, Opening, and Variation", "\uE8EC", typeof(EcoTaggerPage)),
        new("EloAdder", "Elo Adder", "Add historical Elos", "\uE8D3", typeof(EloAdderPage)),
        new("Filter", "Filter", "Filter games by Elo, ply count, checkmate, and annotations", "\uE7C1", typeof(FilterPage)),
        new("Lc0Downloader", "Lc0", "Download and collate Lc0 match PGNs", "\uE896", typeof(Lc0DownloaderPage)),
        new("LichessDownloader", "Lichess", "User games and monthly database tools", "\uE896", typeof(LichessToolsPage)),
        new("PgnJoiner", "Merger", "Merge multiple PGN files", "\uEA3C", typeof(PgnJoinerPage)),
        new("PgnInfo", "PGN Info", "Summary statistics for PGN files", "\uE946", typeof(PgnInfoPage)),
        new("PgnMentorDownloader", "PGN Mentor", "Download from PGN Mentor", "\uE896", typeof(PgnMentorDownloaderPage)),
        new("PlyCountAdder", "Ply Count Adder", "Add PlyCount tags", "\uE8EF", typeof(PlycountAdderPage)),
        new("PgnSorter", "Sorter", "Sort games by Elo, Date, etc", "\uE8CB", typeof(PgnSorterPage)),
        new("PgnSplitter", "Splitter", "Split into chunks or filter", "\uE8A4", typeof(PgnSplitterPage)),
        new("StockfishNormalizer", "Stockfish Normalizer", "Fix engine names", "\uE8AC", typeof(StockfishNormalizerPage)),
        new("TourBreaker", "Tour Breaker", "Extract valid tournaments", "\uE7EE", typeof(TourBreakerPage)),
        new("TwicDownloader", "TWIC Downloader", "The Week in Chess", "\uE896", typeof(TwicDownloaderPage)),
    ];

    public static ToolMenuItem? GetTool(string key) =>
        Tools.FirstOrDefault(t => t.Key == key);
}
