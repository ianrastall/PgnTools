using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chess;

namespace PgnTools.Services;

public interface IChessAnalyzerService
{
    Task AnalyzePgnAsync(
        string inputFilePath,
        string outputFilePath,
        string enginePath,
        int depth,
        string? tablebasePath,
        IProgress<AnalyzerProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public readonly record struct AnalyzerProgress(long ProcessedGames, long TotalGames, double Percent);

internal readonly record struct EngineScore(int Centipawns, bool IsMate, int MateIn)
{
    public static EngineScore FromCentipawns(int cp) => new(cp, false, 0);
    public static EngineScore FromMate(int mateIn, int cpEquivalent) => new(cpEquivalent, true, mateIn);
}

/// <summary>
/// Performs engine-based analysis of PGN games.
/// </summary>
/// <remarks>
/// This implementation uses the Gera.Chess library to parse SAN moves and generate FEN
/// positions for a UCI engine (e.g., Stockfish).
/// </remarks>
public sealed class ChessAnalyzerService : IChessAnalyzerService
{
    private const int BufferSize = 65536;
    private const int BlunderThresholdCp = -300;
    private const int MistakeThresholdCp = -150;
    private const int InaccuracyThresholdCp = -60;

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public ChessAnalyzerService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task AnalyzePgnAsync(
        string inputFilePath,
        string outputFilePath,
        string enginePath,
        int depth,
        string? tablebasePath,
        IProgress<AnalyzerProgress>? progress = null,
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

        if (string.IsNullOrWhiteSpace(enginePath))
        {
            throw new ArgumentException("Engine path is required.", nameof(enginePath));
        }

        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be greater than zero.");
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        var engineFullPath = Path.GetFullPath(enginePath);
        var tempOutputPath = outputFullPath + ".tmp";

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (!File.Exists(engineFullPath))
        {
            throw new FileNotFoundException("UCI engine not found.", engineFullPath);
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

        // Pass 1: count games for progress reporting
        var totalGames = await CountGamesAsync(inputFullPath, cancellationToken);
        if (totalGames == 0)
        {
            progress?.Report(new AnalyzerProgress(0, 0, 100));
            return;
        }

        try
        {
            await using var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

            using var engine = new UciEngine(engineFullPath);
            await engine.StartAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(tablebasePath))
            {
                await engine.SetOptionAsync("SyzygyPath", tablebasePath, cancellationToken);
            }

            using var cancellationRegistration = cancellationToken.Register(engine.RequestAbort);

            var processedGames = 0L;
            var firstOutput = true;
            progress?.Report(new AnalyzerProgress(0, totalGames, 0));

            await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedGames++;

                await engine.NewGameAsync(cancellationToken);

                try
                {
                    game.MoveText = await AnalyzeGameAsync(game, engine, depth, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    game.Headers["Annotator"] = "PgnTools";
                    game.Headers["AnalysisDepth"] = depth.ToString();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // If analysis fails for a given game, preserve the original movetext
                    // and continue processing the rest of the file.
                }

                if (!firstOutput)
                {
                    await writer.WriteLineAsync();
                }

                await _pgnWriter.WriteGameAsync(writer, game, cancellationToken);
                firstOutput = false;

                var percent = Math.Clamp((double)processedGames / totalGames * 100.0, 0, 100);
                progress?.Report(new AnalyzerProgress(processedGames, totalGames, percent));
            }

            await writer.FlushAsync();

            if (File.Exists(outputFullPath))
            {
                File.Delete(outputFullPath);
            }

            File.Move(tempOutputPath, outputFullPath);
            progress?.Report(new AnalyzerProgress(processedGames, totalGames, 100));
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

    private static readonly Regex MoveNumberPrefixRegex = new(
        @"^(?<num>\d+)\.(?:\.\.)?(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex MoveNumberOnlyRegex = new(
        @"^\d+\.(?:\.\.)?$",
        RegexOptions.Compiled);

    private static readonly Regex NagRegex = new(
        @"^\$\d+$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ResultTokens =
    [
        "1-0",
        "0-1",
        "1/2-1/2",
        "*"
    ];

    private async Task<string> AnalyzeGameAsync(PgnGame game, UciEngine engine, int depth, CancellationToken cancellationToken)
    {
        var board = CreateBoardFromHeaders(game.Headers);
        var moves = ExtractSanMoves(game.MoveText);
        if (moves.Count == 0)
        {
            return game.MoveText;
        }

        var moveNumber = ExtractInitialFullMoveNumber(game.Headers);
        var builder = new StringBuilder(game.MoveText.Length + moves.Count * 24);
        var isFirstMove = true;
        var scoreBefore = await engine.AnalyzeAsync(board.ToFen(), depth, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var rawMove in moves)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sideToMove = board.Turn;

            var san = SanitizeSan(rawMove);

            // Apply the move. If this throws, the caller will preserve original movetext.
            board.Move(san);

            var sideAfter = board.Turn;
            var fenAfter = board.ToFen();
            var scoreAfter = await engine.AnalyzeAsync(fenAfter, depth, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var deltaCp = ComputeDeltaForMover(scoreBefore, scoreAfter);
            var nag = GetNagForDelta(deltaCp);

            var afterWhite = ToWhitePerspective(scoreAfter, sideAfter);
            var evalText = FormatEval(afterWhite);

            var annotatedMove = BuildAnnotatedMove(san, nag, evalText);
            AppendMove(builder, annotatedMove, sideToMove, moveNumber, isFirstMove);

            if (sideToMove == PieceColor.Black)
            {
                moveNumber++;
            }

            // After the move, the engine evaluation is from the perspective of the
            // next side to move, which is exactly the "before" score for the next ply.
            scoreBefore = scoreAfter;
            isFirstMove = false;
        }

        var result = game.Headers.GetHeaderValueOrDefault("Result", "*");
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(result);
        }

        return builder.ToString();
    }

    private static ChessBoard CreateBoardFromHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetHeaderValue("FEN", out var fen) && !string.IsNullOrWhiteSpace(fen))
        {
            try
            {
                return ChessBoard.LoadFromFen(fen);
            }
            catch
            {
                // Fall back to default start position on invalid FEN.
            }
        }

        return new ChessBoard();
    }

    private static int ExtractInitialFullMoveNumber(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetHeaderValue("FEN", out var fen) && !string.IsNullOrWhiteSpace(fen))
        {
            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 6 && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fullMove) && fullMove > 0)
            {
                return fullMove;
            }
        }

        return 1;
    }

    private static void AppendMove(StringBuilder builder, string annotatedMove, PieceColor sideToMove, int moveNumber, bool isFirstMove)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        var normalizedMoveNumber = Math.Max(1, moveNumber);

        if (sideToMove == PieceColor.White)
        {
            builder.Append(normalizedMoveNumber).Append(". ");
        }
        else if (isFirstMove)
        {
            builder.Append(normalizedMoveNumber).Append("... ");
        }

        builder.Append(annotatedMove);
    }

    private static string BuildAnnotatedMove(string san, string? nag, string evalText)
    {
        var sb = new StringBuilder(san.Length + 24);
        sb.Append(san);

        if (!string.IsNullOrWhiteSpace(nag))
        {
            sb.Append(' ').Append(nag);
        }

        sb.Append(" { eval: ").Append(evalText).Append(" }");
        return sb.ToString();
    }

    private static int ComputeDeltaForMover(EngineScore before, EngineScore after)
    {
        // The engine score is from the perspective of the side to move.
        // After making a move, the engine evaluates from the opponent's perspective,
        // so we invert the sign to get the mover's perspective again.
        var afterForMover = -after.Centipawns;
        return afterForMover - before.Centipawns;
    }

    private static string? GetNagForDelta(int deltaCp)
    {
        if (deltaCp <= BlunderThresholdCp)
        {
            return "$4"; // blunder
        }

        if (deltaCp <= MistakeThresholdCp)
        {
            return "$2"; // mistake
        }

        if (deltaCp <= InaccuracyThresholdCp)
        {
            return "$6"; // inaccuracy/dubious move
        }

        return null;
    }

    private static EngineScore ToWhitePerspective(EngineScore score, PieceColor sideToMove)
    {
        if (sideToMove == PieceColor.White)
        {
            return score;
        }

        return score.IsMate
            ? EngineScore.FromMate(-score.MateIn, -score.Centipawns)
            : EngineScore.FromCentipawns(-score.Centipawns);
    }

    private static string FormatEval(EngineScore score)
    {
        if (score.IsMate)
        {
            if (score.MateIn == 0)
            {
                return "#0";
            }

            return score.MateIn > 0
                ? $"#{score.MateIn}"
                : $"#-{Math.Abs(score.MateIn)}";
        }

        var pawns = score.Centipawns / 100.0;
        return pawns.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string SanitizeSan(string san)
    {
        if (string.IsNullOrWhiteSpace(san))
        {
            return string.Empty;
        }

        var token = san.Trim();

        // Normalize castling that uses zeros.
        token = token
            .Replace("0-0-0", "O-O-O", StringComparison.Ordinal)
            .Replace("0-0", "O-O", StringComparison.Ordinal);

        // Remove trailing annotation markers like !, ?, !?, ?!, etc.
        while (token.Length > 0)
        {
            var last = token[^1];
            if (last is '!' or '?')
            {
                token = token[..^1];
                continue;
            }

            break;
        }

        return token;
    }

    private static List<string> ExtractSanMoves(string moveText)
    {
        if (string.IsNullOrWhiteSpace(moveText))
        {
            return new List<string>(0);
        }

        var tokens = TokenizeMoveText(moveText);
        var moves = new List<string>(tokens.Count);

        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            // Handle tokens like "1.e4" or "12...Nf6".
            var match = MoveNumberPrefixRegex.Match(token);
            if (match.Success)
            {
                token = match.Groups["rest"].Value.Trim();
                if (token.Length == 0)
                {
                    continue;
                }
            }

            if (MoveNumberOnlyRegex.IsMatch(token))
            {
                continue;
            }

            if (token == "..." || token == "..")
            {
                continue;
            }

            if (ResultTokens.Contains(token))
            {
                continue;
            }

            if (NagRegex.IsMatch(token))
            {
                continue;
            }

            if (string.Equals(token, "e.p.", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "ep", StringComparison.OrdinalIgnoreCase))
            {
                if (moves.Count > 0)
                {
                    moves[^1] = moves[^1] + " e.p.";
                }

                continue;
            }

            token = SanitizeSan(token);
            if (token.Length == 0)
            {
                continue;
            }

            moves.Add(token);
        }

        return moves;
    }

    private static List<string> TokenizeMoveText(string moveText)
    {
        var tokens = new List<string>();
        var current = new StringBuilder(16);

        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;

        foreach (var c in moveText)
        {
            if (inLineComment)
            {
                if (c is '\r' or '\n')
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
                    variationDepth = Math.Max(0, variationDepth - 1);
                }

                continue;
            }

            if (c == '{')
            {
                FlushCurrentToken(current, tokens);
                inBraceComment = true;
                continue;
            }

            if (c == ';')
            {
                FlushCurrentToken(current, tokens);
                inLineComment = true;
                continue;
            }

            if (c == '(')
            {
                FlushCurrentToken(current, tokens);
                variationDepth = 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                FlushCurrentToken(current, tokens);
                continue;
            }

            current.Append(c);
        }

        FlushCurrentToken(current, tokens);
        return tokens;
    }

    private static void FlushCurrentToken(StringBuilder current, List<string> tokens)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private async Task<long> CountGamesAsync(string inputFilePath, CancellationToken cancellationToken)
    {
        var totalGames = 0L;
        await foreach (var _ in _pgnReader.ReadGamesAsync(inputFilePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalGames++;
        }

        return totalGames;
    }
}

/// <summary>
/// Minimal UCI engine wrapper.
/// </summary>
internal sealed class UciEngine : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private const int MateCpBase = 100_000;
    private int _abortRequested;

    private static readonly Regex ScoreCpRegex = new(@"\bscore\s+cp\s+(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex ScoreMateRegex = new(@"\bscore\s+mate\s+(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex DepthRegex = new(@"\bdepth\s+(\d+)", RegexOptions.Compiled);

    private readonly Process _process;

    public UciEngine(string enginePath)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start UCI engine process.");
        }

        await SendAsync("uci", cancellationToken);
        await WaitForTokenAsync("uciok", DefaultTimeout, cancellationToken);

        await SendAsync("isready", cancellationToken);
        await WaitForTokenAsync("readyok", DefaultTimeout, cancellationToken);
    }

    public async Task NewGameAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync("ucinewgame", cancellationToken);
        await SendAsync("isready", cancellationToken);
        await WaitForTokenAsync("readyok", DefaultTimeout, cancellationToken);
    }

    public async Task SetOptionAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Option name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        await SendAsync($"setoption name {name} value {value}", cancellationToken);
        await SendAsync("isready", cancellationToken);
        await WaitForTokenAsync("readyok", DefaultTimeout, cancellationToken);
    }

    public async Task<EngineScore> AnalyzeAsync(string fen, int depth, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fen))
        {
            throw new ArgumentException("FEN is required.", nameof(fen));
        }

        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await SendAsync($"position fen {fen}", cancellationToken);
        await SendAsync($"go depth {depth}", cancellationToken);

        var timeout = GetAnalysisTimeout(depth);
        return await ReadScoreUntilBestMoveAsync(timeout, cancellationToken);
    }

    private async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    private static TimeSpan GetAnalysisTimeout(int depth)
    {
        // Heuristic timeout scaling with depth; capped to avoid hangs.
        var seconds = Math.Clamp((int)Math.Ceiling(depth * 2.5), 15, 120);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<EngineScore> ReadScoreUntilBestMoveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var bestDepth = -1;
        var bestScore = EngineScore.FromCentipawns(0);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var lineTask = _process.StandardOutput.ReadLineAsync();
            var line = await lineTask.WaitAsync(remaining, cancellationToken);
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("bestmove", StringComparison.OrdinalIgnoreCase))
            {
                return bestScore;
            }

            if (!line.StartsWith("info", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var depth = TryParseDepth(line);
            var score = TryParseScore(line);

            if (!score.HasValue)
            {
                continue;
            }

            if (depth >= bestDepth)
            {
                bestDepth = depth;
                bestScore = score.Value;
            }
        }

        // Return the best score we saw before timing out.
        return bestScore;
    }

    private static int TryParseDepth(string line)
    {
        var match = DepthRegex.Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
            ? depth
            : -1;
    }

    private static EngineScore? TryParseScore(string line)
    {
        var mateMatch = ScoreMateRegex.Match(line);
        if (mateMatch.Success && int.TryParse(mateMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mateIn))
        {
            var sign = Math.Sign(mateIn);
            var distance = Math.Min(999, Math.Abs(mateIn));
            var cpEquivalent = sign * (MateCpBase - distance * 100);
            return EngineScore.FromMate(mateIn, cpEquivalent);
        }

        var cpMatch = ScoreCpRegex.Match(line);
        if (cpMatch.Success && int.TryParse(cpMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cp))
        {
            return EngineScore.FromCentipawns(cp);
        }

        return null;
    }

    private async Task WaitForTokenAsync(string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var lineTask = _process.StandardOutput.ReadLineAsync();
            var line = await lineTask.WaitAsync(remaining, cancellationToken);
            if (line == null)
            {
                break;
            }

            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new TimeoutException($"Timed out waiting for engine response: {token}");
    }

    public void RequestAbort()
    {
        if (Interlocked.Exchange(ref _abortRequested, 1) == 1)
        {
            return;
        }

        // Best-effort attempt to stop the current search and unblock readers.
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("stop");
                _process.StandardInput.Flush();
            }
        }
        catch
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("quit");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}
