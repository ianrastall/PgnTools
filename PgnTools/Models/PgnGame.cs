using System.Collections.Generic;
using System.Linq;

namespace PgnTools.Models;

/// <summary>
/// Represents a single PGN game with headers and move text.
/// </summary>
public sealed class PgnGame(Dictionary<string, string>? headers = null, string? moveText = null)
{
    public Dictionary<string, string> Headers { get; } = headers ??
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public List<string> HeaderOrder { get; } = headers?.Keys.ToList() ??
        new List<string>();

    public string MoveText { get; set; } = moveText ?? string.Empty;
}
