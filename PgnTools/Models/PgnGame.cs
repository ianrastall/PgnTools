namespace PgnTools.Models;

/// <summary>
/// Represents a single PGN game with headers and move text.
/// </summary>
public sealed class PgnGame(IEnumerable<KeyValuePair<string, string>>? headers = null, string? moveText = null)
{
    public Dictionary<string, string> Headers { get; } = headers != null
        ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string MoveText { get; set; } = moveText ?? string.Empty;
}
