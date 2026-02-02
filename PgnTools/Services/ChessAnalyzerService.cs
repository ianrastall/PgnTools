using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chess;
using PgnTools.Helpers;

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
        CancellationToken cancellationToken = default,
        bool addEleganceTags = false);
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

    private readonly record struct PgnToken(string Text, bool IsMove, string? San);
    private readonly record struct AnalyzedGameResult(string MoveText, EleganceScore? Elegance);

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
        CancellationToken cancellationToken = default,
        bool addEleganceTags = false)
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
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

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
            var processedGames = 0L;
            var analyzedGames = 0L;
            var failedGames = 0L;
            string? firstFailureMessage = null;
            
            UciEngine? engine = null;
            var cancellationRegistration = default(CancellationTokenRegistration);

            async Task<UciEngine> StartEngineAsync()
            {
                var newEngine = new UciEngine(engineFullPath);
                try
                {
                    await newEngine.StartAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(tablebasePath))
                    {
                        await newEngine.SetOptionAsync("SyzygyPath", tablebasePath, cancellationToken);
                    }

                    cancellationRegistration.Dispose();
                    cancellationRegistration = cancellationToken.Register(newEngine.RequestAbort);
                    return newEngine;
                }
                catch
                {
                    newEngine.Dispose();
                    throw;
                }
            }

            try
            {
                await using (var outputStream = new FileStream(
                    tempOutputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan | FileOptions.Asynchronous))
                using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
                {
                    engine = await StartEngineAsync();

                    var firstOutput = true;
                    progress?.Report(new AnalyzerProgress(0, totalGames, 0));

                    await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        processedGames++;

                        try
                        {
                            await Task.Run(async () =>
                            {
                                await engine.NewGameAsync(cancellationToken).ConfigureAwait(false);
                                var analysis = await AnalyzeGameAsync(game, engine, depth, addEleganceTags, cancellationToken)
                                    .ConfigureAwait(false);

                                game.MoveText = analysis.MoveText;
                                cancellationToken.ThrowIfCancellationRequested();
                                game.Headers["Annotator"] = "PgnTools";
                                game.Headers["AnalysisDepth"] = depth.ToString(CultureInfo.InvariantCulture);

                                if (addEleganceTags && analysis.Elegance.HasValue)
                                {
                                    var elegance = analysis.Elegance.Value;
                                    game.Headers["Elegance"] = elegance.Score.ToString(CultureInfo.InvariantCulture);
                                    game.Headers["EleganceDetails"] = EleganceScoreCalculator.FormatDetails(elegance);
                                }
                            }, cancellationToken).ConfigureAwait(false);

                            analyzedGames++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failedGames++;
                            firstFailureMessage ??= $"Game #{processedGames}: {ex.GetType().Name}: {ex.Message}";

                            if (engine.HasExited)
                            {
                                engine.Dispose();
                                engine = await StartEngineAsync();
                            }

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
                }

                if (analyzedGames == 0 && failedGames > 0)
                {
                    throw new InvalidOperationException(
                        $"No games could be analyzed. First failure: {firstFailureMessage ?? "Unknown move parsing error."}");
                }

                FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
                progress?.Report(new AnalyzerProgress(processedGames, totalGames, 100));
            }
            finally
            {
                cancellationRegistration.Dispose();
                engine?.Dispose();
            }
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

    private static readonly Regex InlineNagRegex = new(
        @"\$\d+",
        RegexOptions.Compiled);

    private static readonly Regex CoordinateMoveRegex = new(
        @"^(?<piece>[KQRBNP])?(?<from>[a-h][1-8])(?:[-x:]?)(?<to>[a-h][1-8])(?:=?(?<promo>[QRBN]))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ResultTokens =
    [
        "1-0",
        "0-1",
        "1/2-1/2",
        "*"
    ];

    private async Task<AnalyzedGameResult> AnalyzeGameAsync(
        PgnGame game,
        UciEngine engine,
        int depth,
        bool addEleganceTags,
        CancellationToken cancellationToken)
    {
        var board = CreateBoardFromHeaders(game.Headers);
        var tokens = TokenizeMoveTextPreserving(game.MoveText);

        if (tokens.Count == 0 || tokens.TrueForAll(token => !token.IsMove))
        {
            return new AnalyzedGameResult(game.MoveText, null);
        }

        var builder = new StringBuilder(game.MoveText.Length + tokens.Count * 24);
        var fenBefore = board.ToFen();
        var scoreBefore = await engine.AnalyzeAsync(fenBefore, depth, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var metrics = addEleganceTags ? new RunningEleganceMetrics() : default;
        var whiteMaterialBefore = 0;
        var blackMaterialBefore = 0;
        if (addEleganceTags)
        {
            ComputeMaterialTotalsCp(fenBefore, out whiteMaterialBefore, out blackMaterialBefore);
        }

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!token.IsMove)
            {
                builder.Append(token.Text);
                continue;
            }

            if (string.IsNullOrWhiteSpace(token.San))
            {
                throw new InvalidOperationException($"Unable to parse move token: {token.Text}");
            }

            var moverSide = board.Turn;
            if (!TryResolveMoveNotation(board, token.San, out var resolvedSan))
            {
                throw new InvalidOperationException(
                    $"Unsupported move notation '{token.San}' in token '{token.Text}'.");
            }

            var hasCapture = resolvedSan.IndexOf('x') >= 0;
            var hasCheck = resolvedSan.IndexOf('+') >= 0;
            var hasMate = resolvedSan.IndexOf('#') >= 0;
            var hasPromotion = resolvedSan.IndexOf('=') >= 0;
            var isQuiet = !hasCapture && !hasCheck && !hasMate && !hasPromotion;

            // Apply the move. If this throws, the caller will preserve original movetext.
            if (!board.Move(resolvedSan))
            {
                throw new InvalidOperationException(
                    $"Failed to apply move '{resolvedSan}' parsed from token '{token.Text}'.");
            }

            var fenAfter = board.ToFen();
            var scoreAfter = await engine.AnalyzeAsync(fenAfter, depth, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var deltaCp = ComputeDeltaForMover(scoreBefore, scoreAfter);
            var nag = GetNagForDelta(deltaCp);

            var sideToMoveAfter = board.Turn;
            var afterWhite = ToWhitePerspective(scoreAfter, sideToMoveAfter);
            var evalText = FormatEval(afterWhite);

            var annotatedMove = BuildAnnotatedMove(token.Text, nag, evalText);
            builder.Append(annotatedMove);

            if (addEleganceTags)
            {
                metrics.PlyCount++;
                metrics.EvaluatedPlyCount++;

                ComputeMaterialTotalsCp(fenAfter, out var whiteMaterialAfter, out var blackMaterialAfter);

                var whiteMaterialLossCp = whiteMaterialBefore - whiteMaterialAfter;
                if (whiteMaterialLossCp > 0)
                {
                    UpdateSacrificeMetrics(
                        PieceColor.White,
                        whiteMaterialLossCp,
                        scoreBefore,
                        moverSide,
                        scoreAfter,
                        sideToMoveAfter,
                        ref metrics);
                }

                var blackMaterialLossCp = blackMaterialBefore - blackMaterialAfter;
                if (blackMaterialLossCp > 0)
                {
                    UpdateSacrificeMetrics(
                        PieceColor.Black,
                        blackMaterialLossCp,
                        scoreBefore,
                        moverSide,
                        scoreAfter,
                        sideToMoveAfter,
                        ref metrics);
                }

                whiteMaterialBefore = whiteMaterialAfter;
                blackMaterialBefore = blackMaterialAfter;

                if (hasCapture || hasCheck || hasMate || hasPromotion)
                {
                    metrics.ForcingMoveCount++;
                }

                if (deltaCp <= BlunderThresholdCp)
                {
                    metrics.BlunderCount++;
                }
                else if (deltaCp <= MistakeThresholdCp)
                {
                    metrics.MistakeCount++;
                }
                else if (deltaCp <= InaccuracyThresholdCp)
                {
                    metrics.DubiousCount++;
                }

                if (isQuiet && deltaCp >= 150)
                {
                    metrics.QuietImprovementCount++;
                }

                var evalWhiteCp = afterWhite.Centipawns;
                if (metrics.PreviousEvalWhiteCp.HasValue)
                {
                    metrics.SwingAbsSum += Math.Abs(evalWhiteCp - metrics.PreviousEvalWhiteCp.Value);
                    metrics.SwingCount++;
                }

                metrics.PreviousEvalWhiteCp = evalWhiteCp;

                metrics.MoverDeltaSum += deltaCp;
                metrics.MoverDeltaSquareSum += deltaCp * (double)deltaCp;
                metrics.MoverDeltaCount++;

                var evalForMoverSide = moverSide == PieceColor.White ? evalWhiteCp : -evalWhiteCp;
                if (moverSide == PieceColor.White)
                {
                    UpdateTrendMetrics(
                        evalForMoverSide,
                        ref metrics.WhiteLastEval,
                        ref metrics.WhitePreviousDelta,
                        ref metrics.TrendBreakCount);
                }
                else
                {
                    UpdateTrendMetrics(
                        evalForMoverSide,
                        ref metrics.BlackLastEval,
                        ref metrics.BlackPreviousDelta,
                        ref metrics.TrendBreakCount);
                }
            }

            // After the move, the engine evaluation is from the perspective of the
            // next side to move, which is exactly the "before" score for the next ply.
            scoreBefore = scoreAfter;
            fenBefore = fenAfter;
        }

        if (!ContainsResultToken(tokens))
        {
            var result = game.Headers.GetHeaderValueOrDefault("Result", "*");
            if (!string.IsNullOrWhiteSpace(result))
            {
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                {
                    builder.Append(' ');
                }

                builder.Append(result);
            }
        }

        EleganceScore? elegance = null;
        if (addEleganceTags && metrics.PlyCount > 0)
        {
            var forcingPercent = metrics.PlyCount > 0
                ? metrics.ForcingMoveCount * 100d / metrics.PlyCount
                : 0d;

            var averageAbsSwing = metrics.SwingCount > 0
                ? metrics.SwingAbsSum / metrics.SwingCount
                : 350d;

            var evalStdDev = 220d;
            if (metrics.MoverDeltaCount > 0)
            {
                var mean = metrics.MoverDeltaSum / metrics.MoverDeltaCount;
                var variance = (metrics.MoverDeltaSquareSum / metrics.MoverDeltaCount) - (mean * mean);
                evalStdDev = Math.Sqrt(Math.Max(0d, variance));
            }

            var eleganceMetrics = new EleganceEvaluationMetrics(
                metrics.PlyCount,
                metrics.EvaluatedPlyCount,
                forcingPercent,
                metrics.QuietImprovementCount,
                metrics.TrendBreakCount,
                metrics.BlunderCount,
                metrics.MistakeCount,
                metrics.DubiousCount,
                averageAbsSwing,
                evalStdDev,
                metrics.SoundSacrificeCount,
                metrics.UnsoundSacrificeCount,
                metrics.SoundSacrificeCp,
                metrics.UnsoundSacrificeCp);

            elegance = EleganceScoreCalculator.Calculate(eleganceMetrics);
        }

        return new AnalyzedGameResult(builder.ToString(), elegance);
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

    private static bool ContainsResultToken(List<PgnToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.IsMove)
            {
                continue;
            }

            var trimmed = token.Text.Trim();
            if (trimmed.Length > 0 && ResultTokens.Contains(trimmed))
            {
                return true;
            }
        }

        return false;
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

    private static void UpdateSacrificeMetrics(
        PieceColor sideLosingMaterial,
        int materialLossCp,
        EngineScore scoreBefore,
        PieceColor sideToMoveBefore,
        EngineScore scoreAfter,
        PieceColor sideToMoveAfter,
        ref RunningEleganceMetrics metrics)
    {
        // Ignore tiny losses (for example, transient rounding or malformed data).
        if (materialLossCp < 100)
        {
            return;
        }

        var evalBeforeForLosingSide = ToPerspectiveCentipawns(scoreBefore, sideLosingMaterial, sideToMoveBefore);
        var evalAfterForLosingSide = ToPerspectiveCentipawns(scoreAfter, sideLosingMaterial, sideToMoveAfter);
        var evalDeltaForLosingSide = evalAfterForLosingSide - evalBeforeForLosingSide;

        var maxAllowedDrop = materialLossCp switch
        {
            >= 500 => -140,
            >= 300 => -120,
            _ => -90
        };

        var absoluteFloor = materialLossCp switch
        {
            >= 500 => -220,
            >= 300 => -180,
            _ => -140
        };

        if (evalAfterForLosingSide >= absoluteFloor && evalDeltaForLosingSide >= maxAllowedDrop)
        {
            metrics.SoundSacrificeCount++;
            metrics.SoundSacrificeCp += materialLossCp;
        }
        else
        {
            metrics.UnsoundSacrificeCount++;
            metrics.UnsoundSacrificeCp += materialLossCp;
        }
    }

    private static int ToPerspectiveCentipawns(EngineScore score, PieceColor perspective, PieceColor sideToMove)
    {
        var whitePerspective = ToWhitePerspective(score, sideToMove).Centipawns;
        return perspective == PieceColor.White ? whitePerspective : -whitePerspective;
    }

    private static void ComputeMaterialTotalsCp(string fen, out int whiteMaterialCp, out int blackMaterialCp)
    {
        whiteMaterialCp = 0;
        blackMaterialCp = 0;

        if (string.IsNullOrWhiteSpace(fen))
        {
            return;
        }

        var span = fen.AsSpan();
        var boardEnd = span.IndexOf(' ');
        if (boardEnd < 0)
        {
            boardEnd = span.Length;
        }

        foreach (var symbol in span[..boardEnd])
        {
            if (symbol == '/' || (symbol >= '1' && symbol <= '8'))
            {
                continue;
            }

            var value = GetPieceMaterialValueCp(symbol);
            if (value == 0)
            {
                continue;
            }

            if (char.IsUpper(symbol))
            {
                whiteMaterialCp += value;
            }
            else
            {
                blackMaterialCp += value;
            }
        }
    }

    private static int GetPieceMaterialValueCp(char piece) => piece switch
    {
        'P' or 'p' => 100,
        'N' or 'n' => 320,
        'B' or 'b' => 330,
        'R' or 'r' => 500,
        'Q' or 'q' => 900,
        _ => 0
    };

    private static void UpdateTrendMetrics(
        double evalForSide,
        ref double? lastEval,
        ref double? previousDelta,
        ref int trendBreakCount)
    {
        if (!lastEval.HasValue)
        {
            lastEval = evalForSide;
            return;
        }

        var delta1 = evalForSide - lastEval.Value;
        if (previousDelta.HasValue)
        {
            var previousSign = Math.Sign(previousDelta.Value);
            var currentSign = Math.Sign(delta1);
            var delta2 = delta1 - previousDelta.Value;

            if (previousSign != 0 &&
                currentSign != 0 &&
                previousSign != currentSign &&
                Math.Abs(delta2) >= 50d)
            {
                trendBreakCount++;
            }
        }

        previousDelta = delta1;
        lastEval = evalForSide;
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

    private static bool TryResolveMoveNotation(ChessBoard board, string moveToken, out string resolvedSan)
    {
        resolvedSan = string.Empty;
        if (string.IsNullOrWhiteSpace(moveToken))
        {
            return false;
        }

        var candidate = moveToken.Trim();
        if (board.IsValidMove(candidate))
        {
            resolvedSan = candidate;
            return true;
        }

        candidate = TrimNonSanSuffixes(candidate);
        if (candidate.Length == 0)
        {
            return false;
        }

        if (board.IsValidMove(candidate))
        {
            resolvedSan = candidate;
            return true;
        }

        var match = CoordinateMoveRegex.Match(candidate);
        if (!match.Success)
        {
            return false;
        }

        var from = match.Groups["from"].Value;
        var to = match.Groups["to"].Value;
        var pieceGroup = match.Groups["piece"];
        var promoGroup = match.Groups["promo"];

        var expectedPieceChar = pieceGroup.Success
            ? char.ToLowerInvariant(pieceGroup.ValueSpan[0])
            : '\0';

        var expectedPromotionChar = promoGroup.Success
            ? char.ToLowerInvariant(promoGroup.ValueSpan[0])
            : '\0';

        foreach (var legalMove in board.Moves(allowAmbiguousCastle: true, generateSan: true))
        {
            if (!SquareEquals(legalMove.OriginalPosition, from) ||
                !SquareEquals(legalMove.NewPosition, to))
            {
                continue;
            }

            if (expectedPieceChar != '\0')
            {
                var movePieceChar = char.ToLowerInvariant(legalMove.Piece.Type.AsChar);
                if (expectedPieceChar != movePieceChar)
                {
                    continue;
                }
            }

            if (expectedPromotionChar != '\0')
            {
                if (!legalMove.IsPromotion)
                {
                    continue;
                }

                var promotionPiece = legalMove.Promotion;
                if (promotionPiece == null)
                {
                    continue;
                }

                var movePromotionChar = char.ToLowerInvariant(promotionPiece.Type.AsChar);
                if (expectedPromotionChar != movePromotionChar)
                {
                    continue;
                }
            }
            else if (legalMove.IsPromotion)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(legalMove.San))
            {
                continue;
            }

            resolvedSan = legalMove.San;
            return true;
        }

        return false;
    }

    private static string TrimNonSanSuffixes(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var candidate = token.Trim();
        if (candidate.EndsWith("e.p.", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^4].TrimEnd();
        }
        else if (candidate.EndsWith("ep", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^2].TrimEnd();
        }

        while (candidate.Length > 0)
        {
            var last = candidate[^1];
            if (last is '+' or '#')
            {
                candidate = candidate[..^1].TrimEnd();
                continue;
            }

            break;
        }

        return candidate;
    }

    private static bool SquareEquals(Position position, string square)
    {
        return position.ToString().Equals(square, StringComparison.OrdinalIgnoreCase);
    }

    private static List<PgnToken> TokenizeMoveTextPreserving(string moveText)
    {
        var tokens = new List<PgnToken>();
        if (string.IsNullOrEmpty(moveText))
        {
            return tokens;
        }

        var current = new StringBuilder(16);
        var variationDepth = 0;

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            var text = current.ToString();
            current.Clear();

            var isMove = false;
            string? san = null;

            if (variationDepth == 0 && TryParseMoveToken(text, out var parsedSan))
            {
                isMove = true;
                san = parsedSan;
            }

            tokens.Add(new PgnToken(text, isMove, san));
        }

        var i = 0;
        while (i < moveText.Length)
        {
            var c = moveText[i];

            if (c == '{')
            {
                FlushCurrent();
                var start = i;
                i++;
                while (i < moveText.Length && moveText[i] != '}')
                {
                    i++;
                }

                if (i < moveText.Length)
                {
                    i++;
                }

                tokens.Add(new PgnToken(moveText.Substring(start, i - start), false, null));
                continue;
            }

            if (c == ';')
            {
                FlushCurrent();
                var start = i;
                i++;
                while (i < moveText.Length)
                {
                    var cc = moveText[i];
                    if (cc == '\r')
                    {
                        i++;
                        if (i < moveText.Length && moveText[i] == '\n')
                        {
                            i++;
                        }

                        break;
                    }

                    if (cc == '\n')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                tokens.Add(new PgnToken(moveText.Substring(start, i - start), false, null));
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                FlushCurrent();
                var start = i;
                i++;
                while (i < moveText.Length && char.IsWhiteSpace(moveText[i]))
                {
                    i++;
                }

                tokens.Add(new PgnToken(moveText.Substring(start, i - start), false, null));
                continue;
            }

            if (c == '(')
            {
                FlushCurrent();
                tokens.Add(new PgnToken("(", false, null));
                variationDepth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                FlushCurrent();
                tokens.Add(new PgnToken(")", false, null));
                if (variationDepth > 0)
                {
                    variationDepth--;
                }

                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        FlushCurrent();
        return tokens;
    }

    private static bool TryParseMoveToken(string token, out string san)
    {
        san = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();

        var match = MoveNumberPrefixRegex.Match(trimmed);
        if (match.Success)
        {
            trimmed = match.Groups["rest"].Value.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }
        }

        if (MoveNumberOnlyRegex.IsMatch(trimmed))
        {
            return false;
        }

        if (trimmed == "..." || trimmed == "..")
        {
            return false;
        }

        if (ResultTokens.Contains(trimmed))
        {
            return false;
        }

        if (NagRegex.IsMatch(trimmed))
        {
            return false;
        }

        if (string.Equals(trimmed, "e.p.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "ep", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        trimmed = InlineNagRegex.Replace(trimmed, string.Empty);
        trimmed = SanitizeSan(trimmed);

        if (trimmed.Length == 0)
        {
            return false;
        }

        san = trimmed;
        return true;
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

    private struct RunningEleganceMetrics
    {
        public int PlyCount;
        public int EvaluatedPlyCount;
        public int ForcingMoveCount;
        public int QuietImprovementCount;
        public int TrendBreakCount;
        public int BlunderCount;
        public int MistakeCount;
        public int DubiousCount;
        public int SoundSacrificeCount;
        public int UnsoundSacrificeCount;
        public int SoundSacrificeCp;
        public int UnsoundSacrificeCp;
        public double SwingAbsSum;
        public int SwingCount;
        public double MoverDeltaSum;
        public double MoverDeltaSquareSum;
        public int MoverDeltaCount;
        public int? PreviousEvalWhiteCp;
        public double? WhiteLastEval;
        public double? WhitePreviousDelta;
        public double? BlackLastEval;
        public double? BlackPreviousDelta;
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
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        // Drain stderr to avoid blocking if the engine writes warnings or errors.
        _process.ErrorDataReceived += (_, _) => { };
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start UCI engine process.");
        }

        _process.BeginErrorReadLine();

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
        var timedOut = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                break;
            }

            string? line;
            try
            {
                var lineTask = _process.StandardOutput.ReadLineAsync();
                line = await lineTask.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                break;
            }

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

        if (timedOut)
        {
            await StopSearchAndDrainAsync(cancellationToken);
        }

        // Return the best score we saw before timing out.
        return bestScore;
    }

    private async Task StopSearchAndDrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync("stop", cancellationToken);
        }
        catch
        {
            return;
        }

        try
        {
            await WaitForTokenAsync("bestmove", TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        catch (TimeoutException)
        {
        }
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
