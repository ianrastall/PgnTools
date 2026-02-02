using System.Globalization;
using System.Text;
using System.Text.Json;
using PgnTools.Helpers;

namespace PgnTools.Services;

public sealed record EleganceTaggerResult(
    long ProcessedGames,
    double AverageScore,
    double AverageSoundness,
    double AverageCoherence,
    double AverageTactical,
    double AverageQuiet);

public interface IEleganceService
{
    Task<EleganceTaggerResult> TagEleganceAsync(
        string inputFilePath,
        string outputFilePath,
        string enginePath,
        int depth,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tags each game with engine-backed Elegance component scores (0..100).
/// </summary>
public sealed class EleganceService : IEleganceService
{
    private const int BufferSize = 65536;
    private const int MateCpBase = 100000;
    private const int AnalyzerProgressWeight = 70;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);

    private static readonly NormalizationDistributions Norms = NormalizationDistributions.Load();

    private readonly IChessAnalyzerService _analyzer;
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public EleganceService(
        IChessAnalyzerService analyzer,
        PgnReader pgnReader,
        PgnWriter pgnWriter)
    {
        _analyzer = analyzer;
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<EleganceTaggerResult> TagEleganceAsync(
        string inputFilePath,
        string outputFilePath,
        string enginePath,
        int depth,
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
        var tempAnalyzedPath = Path.Combine(Path.GetTempPath(), $"pgntools-elegance-analyzed-{Guid.NewGuid():N}.pgn");
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

        var processedGames = 0L;
        var cumulativeScore = 0d;
        var cumulativeSoundness = 0d;
        var cumulativeCoherence = 0d;
        var cumulativeTactical = 0d;
        var cumulativeQuiet = 0d;
        var lastProgressReport = DateTime.MinValue;

        try
        {
            var analyzerProgress = new Progress<AnalyzerProgress>(p =>
            {
                var scaled = Math.Clamp(p.Percent, 0, 100) * AnalyzerProgressWeight / 100d;
                progress?.Report(scaled);
            });

            await _analyzer.AnalyzePgnAsync(
                inputFullPath,
                tempAnalyzedPath,
                engineFullPath,
                depth,
                tablebasePath: null,
                analyzerProgress,
                cancellationToken).ConfigureAwait(false);

            if (!File.Exists(tempAnalyzedPath))
            {
                progress?.Report(100);
                return new EleganceTaggerResult(0, 0, 0, 0, 0, 0);
            }

            await using var analyzedStream = new FileStream(
                tempAnalyzedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
            {
                var firstOutput = true;

                await foreach (var game in _pgnReader.ReadGamesAsync(analyzedStream, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processedGames++;

                    var startingSideIsWhite = IsWhiteToMove(game.Headers);
                    var parsed = ParseAnalyzedGame(game.MoveText, startingSideIsWhite);
                    var elegance = CalculateElegance(parsed);

                    cumulativeScore += elegance.Score;
                    cumulativeSoundness += elegance.Soundness;
                    cumulativeCoherence += elegance.Coherence;
                    cumulativeTactical += elegance.Tactical;
                    cumulativeQuiet += elegance.Quiet;

                    game.Headers["Elegance"] = elegance.Score.ToString(CultureInfo.InvariantCulture);
                    game.Headers["EleganceDetails"] = FormatDetails(elegance);

                    if (!firstOutput)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await _pgnWriter.WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
                    firstOutput = false;

                    if (ShouldReportProgress(processedGames, ref lastProgressReport))
                    {
                        var scoringPercent = GetProgressPercent(analyzedStream);
                        var totalPercent = AnalyzerProgressWeight + (100 - AnalyzerProgressWeight) * (scoringPercent / 100d);
                        progress?.Report(Math.Clamp(totalPercent, 0, 100));
                    }
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }

            if (processedGames == 0)
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }

                progress?.Report(100);
                return new EleganceTaggerResult(0, 0, 0, 0, 0, 0);
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
            progress?.Report(100);

            return new EleganceTaggerResult(
                processedGames,
                cumulativeScore / processedGames,
                cumulativeSoundness / processedGames,
                cumulativeCoherence / processedGames,
                cumulativeTactical / processedGames,
                cumulativeQuiet / processedGames);
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
        finally
        {
            if (File.Exists(tempAnalyzedPath))
            {
                try
                {
                    File.Delete(tempAnalyzedPath);
                }
                catch
                {
                }
            }
        }
    }

    private static string FormatDetails(in EleganceBreakdown elegance)
    {
        return FormattableString.Invariant(
            $"S={Math.Round(elegance.Soundness, MidpointRounding.AwayFromZero):0};C={Math.Round(elegance.Coherence, MidpointRounding.AwayFromZero):0};T={Math.Round(elegance.Tactical, MidpointRounding.AwayFromZero):0};Q={Math.Round(elegance.Quiet, MidpointRounding.AwayFromZero):0};L={Math.Round(elegance.LengthPenalty, MidpointRounding.AwayFromZero):0}");
    }

    private static EleganceBreakdown CalculateElegance(in ParsedGameMetrics metrics)
    {
        if (metrics.PlyCount <= 0)
        {
            return default;
        }

        var coverage = metrics.PlyCount > 0 ? metrics.EvaluatedPlyCount / (double)metrics.PlyCount : 0d;

        var soundnessRaw = metrics.AverageAbsSwingCp + (metrics.BlunderCount * 25d) + (metrics.MistakeCount * 10d) + (metrics.DubiousCount * 5d);
        var coherenceRaw = metrics.EvalStdDevCp + (metrics.TrendBreakCount * 18d);
        var tacticalRaw = metrics.ForcingMovePercent;
        var quietRaw = metrics.PlyCount > 0 ? metrics.QuietImprovementCount * 100d / metrics.PlyCount : 0d;

        var soundness = Norms.Normalize(DistributionType.Soundness, soundnessRaw);
        var coherence = Norms.Normalize(DistributionType.Coherence, coherenceRaw);
        var tactical = Norms.Normalize(DistributionType.TacticalDensity, tacticalRaw);
        var quiet = Norms.Normalize(DistributionType.QuietBrilliancy, quietRaw);
        var lengthPenalty = ComputeLengthPenalty(metrics.PlyCount);

        if (coverage < 0.8d)
        {
            // Keep scores meaningful when analysis coverage is partial.
            var scale = Math.Clamp((coverage - 0.2d) / 0.6d, 0d, 1d);
            soundness *= scale;
            coherence *= scale;
            quiet *= scale;
        }

        var rawScore =
            (0.25d * soundness) +
            (0.20d * coherence) +
            (0.20d * tactical) +
            (0.30d * quiet) -
            (0.05d * lengthPenalty);

        var score = (int)Math.Round(Math.Clamp(rawScore, 0d, 100d), MidpointRounding.AwayFromZero);

        return new EleganceBreakdown(
            score,
            soundness,
            coherence,
            tactical,
            quiet,
            lengthPenalty);
    }

    private static double ComputeLengthPenalty(int plyCount)
    {
        const int threshold = 160; // 80 full moves
        if (plyCount <= threshold)
        {
            return 0;
        }

        var distance = plyCount - threshold;
        return 100d * (1d - 1d / (1d + Math.Exp(-distance / 30d)));
    }

    private static ParsedGameMetrics ParseAnalyzedGame(string moveText, bool whiteToMove)
    {
        if (string.IsNullOrWhiteSpace(moveText))
        {
            return default;
        }

        var plies = new List<PlyData>(256);
        var token = new StringBuilder(16);
        var commentBuffer = new StringBuilder(32);
        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;
        var sideToMoveIsWhite = whiteToMove;

        void FlushToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            var raw = token.ToString();
            token.Clear();
            ProcessToken(raw, ref sideToMoveIsWhite, plies);
        }

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
                    if (variationDepth == 0 && plies.Count > 0)
                    {
                        ApplyEvalComment(commentBuffer.ToString(), plies);
                    }

                    commentBuffer.Clear();
                }
                else
                {
                    commentBuffer.Append(c);
                }

                continue;
            }

            if (c == '{')
            {
                FlushToken();
                inBraceComment = true;
                commentBuffer.Clear();
                continue;
            }

            if (c == ';')
            {
                FlushToken();
                inLineComment = true;
                continue;
            }

            if (c == '(')
            {
                FlushToken();
                variationDepth++;
                continue;
            }

            if (c == ')')
            {
                FlushToken();
                if (variationDepth > 0)
                {
                    variationDepth--;
                }

                continue;
            }

            if (variationDepth > 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                FlushToken();
                continue;
            }

            token.Append(c);
        }

        FlushToken();
        return SummarizePlies(plies);
    }

    private static void ProcessToken(string rawToken, ref bool sideToMoveIsWhite, List<PlyData> plies)
    {
        var token = rawToken.Trim();
        if (token.Length == 0)
        {
            return;
        }

        if (TryParseNagToken(token, out var standaloneNag))
        {
            ApplyNagToLastPly(plies, standaloneNag);
            return;
        }

        if (token.All(c => c == '.'))
        {
            return;
        }

        if (TryStripMoveNumberPrefix(token, out var stripped))
        {
            token = stripped;
        }

        token = token.TrimStart('.');
        if (token.Length == 0)
        {
            return;
        }

        if (IsResultToken(token))
        {
            return;
        }

        var blunderSignals = 0;
        var mistakeSignals = 0;
        var dubiousSignals = 0;
        token = RemoveInlineNags(token, ref blunderSignals, ref mistakeSignals, ref dubiousSignals);

        if (token.Length == 0)
        {
            return;
        }

        var hasDoubleQuestion = token.Contains("??", StringComparison.Ordinal);
        var hasDubious = !hasDoubleQuestion && token.Contains("?!", StringComparison.Ordinal);
        var hasSingleQuestion = !hasDoubleQuestion && !hasDubious && token.IndexOf('?') >= 0;

        if (hasDoubleQuestion)
        {
            blunderSignals++;
        }
        else if (hasDubious)
        {
            dubiousSignals++;
        }
        else if (hasSingleQuestion)
        {
            mistakeSignals++;
        }

        var hasCapture = token.IndexOf('x') >= 0;
        var hasCheck = token.IndexOf('+') >= 0;
        var hasMate = token.IndexOf('#') >= 0;
        var hasPromotion = token.IndexOf('=') >= 0;

        var moveToken = token.TrimEnd('!', '?', '+', '#');
        if (moveToken.Length == 0)
        {
            return;
        }

        moveToken = NormalizeCastling(moveToken);
        if (IsResultToken(moveToken))
        {
            return;
        }

        if (moveToken.Equals("e.p.", StringComparison.OrdinalIgnoreCase) ||
            moveToken.Equals("ep", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isCastling = moveToken.StartsWith("O-O", StringComparison.Ordinal);
        var first = moveToken[0];
        var isPieceMove = isCastling || first is 'K' or 'Q' or 'R' or 'B' or 'N';
        var isPawnMove = first is >= 'a' and <= 'h';

        if (!isPieceMove && !isPawnMove)
        {
            return;
        }

        var ply = new PlyData
        {
            IsWhiteMove = sideToMoveIsWhite,
            IsCapture = hasCapture,
            IsCheck = hasCheck,
            IsMate = hasMate,
            IsPromotion = hasPromotion,
            IsQuiet = !hasCapture && !hasCheck && !hasMate && !hasPromotion,
            BlunderSignals = blunderSignals,
            MistakeSignals = mistakeSignals,
            DubiousSignals = dubiousSignals
        };

        plies.Add(ply);
        sideToMoveIsWhite = !sideToMoveIsWhite;
    }

    private static void ApplyNagToLastPly(List<PlyData> plies, int nag)
    {
        if (plies.Count == 0)
        {
            return;
        }

        var lastIndex = plies.Count - 1;
        var last = plies[lastIndex];
        ApplyNag(ref last, nag);
        plies[lastIndex] = last;
    }

    private static void ApplyNag(ref PlyData ply, int nag)
    {
        switch (nag)
        {
            case 2:
                ply.MistakeSignals++;
                break;
            case 4:
                ply.BlunderSignals++;
                break;
            case 6:
                ply.DubiousSignals++;
                break;
        }
    }

    private static void ApplyEvalComment(string commentText, List<PlyData> plies)
    {
        if (!TryParseEvalComment(commentText, out var evalAfterWhiteCp))
        {
            return;
        }

        var lastIndex = plies.Count - 1;
        if (lastIndex < 0)
        {
            return;
        }

        var ply = plies[lastIndex];
        ply.HasEval = true;
        ply.EvalAfterWhiteCp = evalAfterWhiteCp;
        plies[lastIndex] = ply;
    }

    private static ParsedGameMetrics SummarizePlies(List<PlyData> plies)
    {
        if (plies.Count == 0)
        {
            return default;
        }

        var forcingCount = 0;
        var evaluatedCount = 0;
        var quietImprovementCount = 0;
        var trendBreakCount = 0;

        var annotationBlunders = 0;
        var annotationMistakes = 0;
        var annotationDubious = 0;
        var evalBlunders = 0;
        var evalMistakes = 0;
        var evalDubious = 0;

        var hasPreviousEval = false;
        var previousEvalWhite = 0d;
        var swingAbsSum = 0d;
        var swingCount = 0;

        var moverDeltaSum = 0d;
        var moverDeltaSquareSum = 0d;
        var moverDeltaCount = 0;

        double? whiteLastEval = null;
        double? blackLastEval = null;
        double? whitePreviousDelta = null;
        double? blackPreviousDelta = null;

        foreach (var ply in plies)
        {
            if (ply.IsCapture || ply.IsCheck || ply.IsMate || ply.IsPromotion)
            {
                forcingCount++;
            }

            annotationBlunders += ply.BlunderSignals;
            annotationMistakes += ply.MistakeSignals;
            annotationDubious += ply.DubiousSignals;

            if (!ply.HasEval)
            {
                continue;
            }

            evaluatedCount++;

            var evalWhite = (double)ply.EvalAfterWhiteCp;

            if (hasPreviousEval)
            {
                var deltaWhite = evalWhite - previousEvalWhite;
                var moverDelta = ply.IsWhiteMove ? deltaWhite : -deltaWhite;

                swingAbsSum += Math.Abs(deltaWhite);
                swingCount++;

                moverDeltaSum += moverDelta;
                moverDeltaSquareSum += moverDelta * moverDelta;
                moverDeltaCount++;

                if (moverDelta <= -300)
                {
                    evalBlunders++;
                }
                else if (moverDelta <= -150)
                {
                    evalMistakes++;
                }
                else if (moverDelta <= -60)
                {
                    evalDubious++;
                }

                if (ply.IsQuiet && moverDelta >= 150)
                {
                    quietImprovementCount++;
                }
            }

            previousEvalWhite = evalWhite;
            hasPreviousEval = true;

            if (ply.IsWhiteMove)
            {
                UpdateTrendMetrics(evalWhite, ref whiteLastEval, ref whitePreviousDelta, ref trendBreakCount);
            }
            else
            {
                UpdateTrendMetrics(-evalWhite, ref blackLastEval, ref blackPreviousDelta, ref trendBreakCount);
            }
        }

        var averageAbsSwing = swingCount > 0 ? swingAbsSum / swingCount : 350d;
        var evalStdDev = 220d;
        if (moverDeltaCount > 0)
        {
            var mean = moverDeltaSum / moverDeltaCount;
            var variance = (moverDeltaSquareSum / moverDeltaCount) - (mean * mean);
            evalStdDev = Math.Sqrt(Math.Max(0d, variance));
        }

        var forcingPercent = plies.Count > 0 ? forcingCount * 100d / plies.Count : 0d;

        return new ParsedGameMetrics(
            plies.Count,
            evaluatedCount,
            forcingPercent,
            quietImprovementCount,
            trendBreakCount,
            Math.Max(annotationBlunders, evalBlunders),
            Math.Max(annotationMistakes, evalMistakes),
            Math.Max(annotationDubious, evalDubious),
            averageAbsSwing,
            evalStdDev);
    }

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

    private static bool IsWhiteToMove(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetHeaderValue("FEN", out var fen) || string.IsNullOrWhiteSpace(fen))
        {
            return true;
        }

        var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return true;
        }

        return !parts[1].Equals("b", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseEvalComment(string commentText, out int evalCp)
    {
        evalCp = 0;

        if (string.IsNullOrWhiteSpace(commentText))
        {
            return false;
        }

        var markerIndex = commentText.IndexOf("eval:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var valuePart = commentText[(markerIndex + 5)..].Trim();
        if (valuePart.Length == 0)
        {
            return false;
        }

        var stopIndex = 0;
        while (stopIndex < valuePart.Length && !char.IsWhiteSpace(valuePart[stopIndex]))
        {
            stopIndex++;
        }

        if (stopIndex <= 0)
        {
            return false;
        }

        var evalToken = valuePart[..stopIndex];
        return TryParseEvalToken(evalToken, out evalCp);
    }

    private static bool TryParseEvalToken(string token, out int evalCp)
    {
        evalCp = 0;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token[0] == '#')
        {
            var matePart = token[1..];
            if (!int.TryParse(matePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mateIn))
            {
                return false;
            }

            if (mateIn == 0)
            {
                evalCp = 0;
                return true;
            }

            var sign = Math.Sign(mateIn);
            var distance = Math.Min(999, Math.Abs(mateIn));
            evalCp = sign * (MateCpBase - distance * 100);
            return true;
        }

        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var pawnValue))
        {
            return false;
        }

        var cp = pawnValue * 100d;
        if (cp > MateCpBase)
        {
            cp = MateCpBase;
        }
        else if (cp < -MateCpBase)
        {
            cp = -MateCpBase;
        }

        evalCp = (int)Math.Round(cp, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryParseNagToken(string token, out int nag)
    {
        nag = 0;
        if (token.Length < 2 || token[0] != '$')
        {
            return false;
        }

        for (var i = 1; i < token.Length; i++)
        {
            if (!char.IsDigit(token[i]))
            {
                return false;
            }
        }

        return int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out nag);
    }

    private static string RemoveInlineNags(string token, ref int blunderSignals, ref int mistakeSignals, ref int dubiousSignals)
    {
        var firstNag = token.IndexOf('$');
        if (firstNag < 0)
        {
            return token;
        }

        var builder = new StringBuilder(token.Length);
        var index = 0;

        while (index < token.Length)
        {
            var c = token[index];
            if (c != '$')
            {
                builder.Append(c);
                index++;
                continue;
            }

            var digitsStart = index + 1;
            var digitsEnd = digitsStart;
            while (digitsEnd < token.Length && char.IsDigit(token[digitsEnd]))
            {
                digitsEnd++;
            }

            if (digitsEnd == digitsStart)
            {
                builder.Append(c);
                index++;
                continue;
            }

            if (int.TryParse(token[digitsStart..digitsEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var nag))
            {
                switch (nag)
                {
                    case 2:
                        mistakeSignals++;
                        break;
                    case 4:
                        blunderSignals++;
                        break;
                    case 6:
                        dubiousSignals++;
                        break;
                }
            }

            index = digitsEnd;
        }

        return builder.ToString();
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

    private readonly record struct EleganceBreakdown(
        int Score,
        double Soundness,
        double Coherence,
        double Tactical,
        double Quiet,
        double LengthPenalty);

    private readonly record struct ParsedGameMetrics(
        int PlyCount,
        int EvaluatedPlyCount,
        double ForcingMovePercent,
        int QuietImprovementCount,
        int TrendBreakCount,
        int BlunderCount,
        int MistakeCount,
        int DubiousCount,
        double AverageAbsSwingCp,
        double EvalStdDevCp);

    private struct PlyData
    {
        public bool IsWhiteMove;
        public bool HasEval;
        public int EvalAfterWhiteCp;
        public bool IsCapture;
        public bool IsCheck;
        public bool IsMate;
        public bool IsPromotion;
        public bool IsQuiet;
        public int BlunderSignals;
        public int MistakeSignals;
        public int DubiousSignals;
    }

    private enum DistributionType
    {
        Soundness,
        Coherence,
        TacticalDensity,
        QuietBrilliancy
    }

    private sealed class NormalizationDistributions
    {
        private readonly Dictionary<DistributionType, DistributionBand> _bands;

        private NormalizationDistributions(Dictionary<DistributionType, DistributionBand> bands)
        {
            _bands = bands;
        }

        public static NormalizationDistributions Load()
        {
            var bands = CreateDefaultBands();
            var configPath = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(configPath))
            {
                return new NormalizationDistributions(bands);
            }

            try
            {
                using var stream = File.OpenRead(configPath);
                var config = JsonSerializer.Deserialize<NormalizationConfig>(stream);
                if (config?.Distributions == null)
                {
                    return new NormalizationDistributions(bands);
                }

                foreach (var entry in config.Distributions)
                {
                    if (!TryMapDistribution(entry.Key, out var type))
                    {
                        continue;
                    }

                    var value = entry.Value;
                    if (value == null || !value.P10.HasValue || !value.P90.HasValue || !value.HigherIsBetter.HasValue)
                    {
                        continue;
                    }

                    bands[type] = new DistributionBand(value.P10.Value, value.P90.Value, value.HigherIsBetter.Value);
                }
            }
            catch
            {
                // Keep defaults if config is missing or malformed.
            }

            return new NormalizationDistributions(bands);
        }

        public double Normalize(DistributionType type, double rawValue)
        {
            if (!_bands.TryGetValue(type, out var band))
            {
                return 50;
            }

            return band.Normalize(rawValue);
        }

        private static Dictionary<DistributionType, DistributionBand> CreateDefaultBands()
        {
            return new Dictionary<DistributionType, DistributionBand>
            {
                // Lower raw values are better for Soundness/Coherence.
                { DistributionType.Soundness, new DistributionBand(45d, 280d, HigherIsBetter: false) },
                { DistributionType.Coherence, new DistributionBand(35d, 240d, HigherIsBetter: false) },
                // Higher is better for Tactical/Quiet components.
                { DistributionType.TacticalDensity, new DistributionBand(22d, 56d, HigherIsBetter: true) },
                { DistributionType.QuietBrilliancy, new DistributionBand(0.4d, 6.5d, HigherIsBetter: true) }
            };
        }

        private static string? ResolveConfigPath()
        {
            var primary = Path.Combine(AppContext.BaseDirectory, "Assets", "elegance-distributions.json");
            if (File.Exists(primary))
            {
                return primary;
            }

            var secondary = Path.Combine(AppContext.BaseDirectory, "elegance-distributions.json");
            return File.Exists(secondary) ? secondary : null;
        }

        private static bool TryMapDistribution(string key, out DistributionType type)
        {
            type = default;

            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.Trim().ToLowerInvariant() switch
            {
                "soundness" => Assign(DistributionType.Soundness, out type),
                "coherence" => Assign(DistributionType.Coherence, out type),
                "tacticaldensity" => Assign(DistributionType.TacticalDensity, out type),
                "quietbrilliancy" => Assign(DistributionType.QuietBrilliancy, out type),
                _ => false
            };
        }

        private static bool Assign(DistributionType mapped, out DistributionType type)
        {
            type = mapped;
            return true;
        }
    }

    private readonly record struct DistributionBand(double P10, double P90, bool HigherIsBetter)
    {
        public double Normalize(double rawValue)
        {
            if (P90 <= P10)
            {
                return 50;
            }

            var t = (rawValue - P10) / (P90 - P10);
            if (!HigherIsBetter)
            {
                t = 1d - t;
            }

            return Math.Clamp(t, 0d, 1d) * 100d;
        }
    }

    private sealed class NormalizationConfig
    {
        public Dictionary<string, DistributionConfigEntry>? Distributions { get; set; }
    }

    private sealed class DistributionConfigEntry
    {
        public double? P10 { get; set; }
        public double? P90 { get; set; }
        public bool? HigherIsBetter { get; set; }
    }
}
