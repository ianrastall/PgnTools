using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public interface IEcoTaggerService
{
    Task TagEcoAsync(
        string inputFilePath,
        string outputFilePath,
        string ecoReferenceFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tags games with ECO, Opening, and Variation based on a reference ECO PGN.
/// </summary>
public sealed class EcoTaggerService : IEcoTaggerService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly char[] AnnotationTrimChars = ['+', '#', '!', '?'];

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;
    private readonly SemaphoreSlim _trieLock = new(1, 1);

    private EcoTrieNode? _trieRoot;
    private string? _trieSourcePath;

    public EcoTaggerService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task TagEcoAsync(
        string inputFilePath,
        string outputFilePath,
        string ecoReferenceFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFilePath));
        }

        if (string.IsNullOrWhiteSpace(ecoReferenceFilePath))
        {
            throw new ArgumentException("ECO reference file path is required.", nameof(ecoReferenceFilePath));
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);
        var resolvedEcoPath = ResolveEcoReferencePath(ecoReferenceFilePath);

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (!File.Exists(resolvedEcoPath))
        {
            throw new FileNotFoundException("ECO reference file not found.", resolvedEcoPath);
        }

        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Input and output files must be different.");
        }

        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var trie = await GetTrieAsync(resolvedEcoPath, cancellationToken);

        try
        {
            await using var inputStream = new FileStream(
                inputFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            progress?.Report(0);

            var processedGames = 0L;
            var firstOutput = true;
            var lastProgressReport = DateTime.MinValue;

            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
            {
                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processedGames++;

                    if (!string.IsNullOrWhiteSpace(game.MoveText))
                    {
                        var match = FindDeepestMatch(trie, game.MoveText);
                        if (match != null)
                        {
                            ApplyEcoTags(game, match);
                        }
                    }

                    if (!firstOutput)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await _pgnWriter.WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
                    firstOutput = false;

                    if (ShouldReportProgress(processedGames, ref lastProgressReport))
                    {
                        var percent = GetProgressPercent(inputStream);
                        progress?.Report(percent);
                    }
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
            progress?.Report(100);
        }
        catch
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private async Task<EcoTrieNode> GetTrieAsync(string ecoPath, CancellationToken cancellationToken)
    {
        if (_trieRoot != null &&
            string.Equals(_trieSourcePath, ecoPath, StringComparison.OrdinalIgnoreCase))
        {
            return _trieRoot;
        }

        await _trieLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_trieRoot != null &&
                string.Equals(_trieSourcePath, ecoPath, StringComparison.OrdinalIgnoreCase))
            {
                return _trieRoot;
            }

            _trieRoot = await BuildTrieAsync(ecoPath, cancellationToken).ConfigureAwait(false);
            _trieSourcePath = ecoPath;
            return _trieRoot;
        }
        finally
        {
            _trieLock.Release();
        }
    }

    private async Task<EcoTrieNode> BuildTrieAsync(string ecoPath, CancellationToken cancellationToken)
    {
        var root = new EcoTrieNode();

        await foreach (var game in _pgnReader.ReadGamesAsync(ecoPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = root;
            var hasMoves = false;

            foreach (var move in TokenizeMoves(game.MoveText))
            {
                hasMoves = true;
                if (!node.Children.TryGetValue(move, out var child))
                {
                    child = new EcoTrieNode();
                    node.Children[move] = child;
                }

                node = child;
            }

            if (!hasMoves)
            {
                continue;
            }

            if (TryGetEcoData(game.Headers, out var eco, out var opening, out var variation))
            {
                ApplyEcoData(node, eco, opening, variation);
            }
        }

        return root;
    }

    private static EcoTrieNode? FindDeepestMatch(EcoTrieNode root, string moveText)
    {
        if (root.Children.Count == 0 || string.IsNullOrWhiteSpace(moveText))
        {
            return null;
        }

        var node = root;
        EcoTrieNode? lastMatch = null;

        foreach (var move in TokenizeMoves(moveText))
        {
            if (!node.Children.TryGetValue(move, out var child))
            {
                break;
            }

            node = child;
            if (node.HasEcoData)
            {
                lastMatch = node;
            }
        }

        return lastMatch;
    }

    private static void ApplyEcoTags(PgnGame game, EcoTrieNode match)
    {
        if (!string.IsNullOrWhiteSpace(match.Eco))
        {
            game.Headers["ECO"] = match.Eco;
        }

        if (!string.IsNullOrWhiteSpace(match.Opening))
        {
            game.Headers["Opening"] = match.Opening;
        }

        if (!string.IsNullOrWhiteSpace(match.Variation))
        {
            game.Headers["Variation"] = match.Variation;
        }
    }

    private static bool TryGetEcoData(
        IReadOnlyDictionary<string, string> headers,
        out string? eco,
        out string? opening,
        out string? variation)
    {
        eco = null;
        opening = null;
        variation = null;
        var hasAny = false;

        if (headers.TryGetHeaderValue("ECO", out var ecoValue) && !string.IsNullOrWhiteSpace(ecoValue))
        {
            eco = ecoValue.Trim();
            hasAny = true;
        }

        if (headers.TryGetHeaderValue("Opening", out var openingValue) && !string.IsNullOrWhiteSpace(openingValue))
        {
            opening = openingValue.Trim();
            hasAny = true;
        }

        if (headers.TryGetHeaderValue("Variation", out var variationValue) && !string.IsNullOrWhiteSpace(variationValue))
        {
            variation = variationValue.Trim();
            hasAny = true;
        }

        return hasAny;
    }

    private static void ApplyEcoData(EcoTrieNode node, string? eco, string? opening, string? variation)
    {
        if (!string.IsNullOrWhiteSpace(eco))
        {
            node.Eco = eco;
        }

        if (!string.IsNullOrWhiteSpace(opening))
        {
            node.Opening = opening;
        }

        if (!string.IsNullOrWhiteSpace(variation))
        {
            node.Variation = variation;
        }
    }

    private static IEnumerable<string> TokenizeMoves(string moveText)
    {
        if (string.IsNullOrWhiteSpace(moveText))
        {
            yield break;
        }

        var token = new StringBuilder();
        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;

        for (var i = 0; i < moveText.Length; i++)
        {
            var c = moveText[i];

            if (inLineComment)
            {
                if (c is '\n' or '\r')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBraceComment)
            {
                if (c == '}')
                {
                    inBraceComment = false;
                }

                continue;
            }

            if (variationDepth > 0)
            {
                if (c == '(')
                {
                    variationDepth++;
                }
                else if (c == ')')
                {
                    variationDepth--;
                }

                continue;
            }

            if (c == '{')
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                inBraceComment = true;
                continue;
            }

            if (c == ';')
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                inLineComment = true;
                continue;
            }

            if (c == '(')
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                variationDepth = 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                continue;
            }

            token.Append(c);
        }

        if (token.Length > 0)
        {
            var raw = token.ToString();
            if (TryNormalizeMoveToken(raw, out var move, out var isResult))
            {
                yield return move!;
            }
            else if (isResult)
            {
                yield break;
            }
        }
    }

    private static bool TryNormalizeMoveToken(string raw, out string? move, out bool isResult)
    {
        move = null;
        isResult = false;

        var token = raw.Trim();
        if (token.Length == 0)
        {
            return false;
        }

        if (token[0] == '$' && token.Skip(1).All(char.IsDigit))
        {
            return false;
        }

        if (token.All(c => c == '.'))
        {
            return false;
        }

        if (TryStripMoveNumberPrefix(token, out var stripped))
        {
            token = stripped;
        }

        token = token.TrimStart('.');
        if (token.Length == 0)
        {
            return false;
        }

        if (token.All(c => c is '!' or '?' or '+' or '#'))
        {
            return false;
        }

        if (IsResultToken(token))
        {
            isResult = true;
            return false;
        }

        token = token.TrimEnd(AnnotationTrimChars);
        if (token.Length == 0)
        {
            return false;
        }

        token = StripTrailingNag(token);
        if (token.Length == 0)
        {
            return false;
        }

        if (IsResultToken(token))
        {
            isResult = true;
            return false;
        }

        if (token.Equals("e.p.", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("ep", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = NormalizeCastling(token);
        move = token;
        return true;
    }

    private static string StripTrailingNag(string token)
    {
        var index = token.LastIndexOf('$');
        if (index < 0 || index == token.Length - 1)
        {
            return token;
        }

        var digits = token[(index + 1)..];
        return digits.All(char.IsDigit) ? token[..index] : token;
    }

    private static bool TryStripMoveNumberPrefix(string token, out string stripped)
    {
        stripped = token;
        var firstDot = token.IndexOf('.');
        if (firstDot < 0)
        {
            return false;
        }

        var prefix = token[..firstDot];
        if (prefix.Length == 0 || !prefix.All(char.IsDigit))
        {
            return false;
        }

        var lastDot = token.LastIndexOf('.');
        if (lastDot >= token.Length - 1)
        {
            stripped = string.Empty;
            return true;
        }

        stripped = token[(lastDot + 1)..];
        return true;
    }

    private static bool IsResultToken(string token)
    {
        return token is "1-0" or "0-1" or "1/2-1/2" or "*";
    }

    private static string NormalizeCastling(string token)
    {
        if (token.Equals("0-0", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("o-o", StringComparison.OrdinalIgnoreCase))
        {
            return "O-O";
        }

        if (token.Equals("0-0-0", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("o-o-o", StringComparison.OrdinalIgnoreCase))
        {
            return "O-O-O";
        }

        return token;
    }

    private static string ResolveEcoReferencePath(string ecoReferenceFilePath)
    {
        var trimmed = ecoReferenceFilePath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        var baseDir = AppContext.BaseDirectory;
        var assetsCandidate = Path.Combine(baseDir, "Assets", trimmed);
        if (File.Exists(assetsCandidate))
        {
            return assetsCandidate;
        }

        var baseCandidate = Path.Combine(baseDir, trimmed);
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var fullCandidate = Path.GetFullPath(trimmed);
        if (File.Exists(fullCandidate))
        {
            return fullCandidate;
        }

        return trimmed;
    }

    private static double GetProgressPercent(Stream stream)
    {
        if (!stream.CanSeek || stream.Length == 0)
        {
            return 0;
        }

        var percent = stream.Position / (double)stream.Length * 100;
        if (percent < 0)
        {
            return 0;
        }

        return percent > 100 ? 100 : percent;
    }

    private static bool ShouldReportProgress(long games, ref DateTime lastReportUtc)
    {
        if (games <= 0)
        {
            return false;
        }

        if (games != 1 && games % ProgressGameInterval != 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastReportUtc < ProgressTimeInterval)
        {
            return false;
        }

        lastReportUtc = now;
        return true;
    }

    private sealed class EcoTrieNode
    {
        public Dictionary<string, EcoTrieNode> Children { get; } = new(StringComparer.Ordinal);
        public string? Eco { get; set; }
        public string? Opening { get; set; }
        public string? Variation { get; set; }

        public bool HasEcoData =>
            !string.IsNullOrWhiteSpace(Eco) ||
            !string.IsNullOrWhiteSpace(Opening) ||
            !string.IsNullOrWhiteSpace(Variation);
    }
}
