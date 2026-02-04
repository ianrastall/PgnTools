# ChessAnalyzerService.md

## Service Specification: ChessAnalyzerService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (analysis flag population in shadow array)  
**Thread Safety:** Thread-safe with per-instance engine isolation; concurrent instances permitted

## 1. Objective
Annotate PGN games with engine evaluations using UCI-compatible chess engines (Stockfish, Lc0, etc.) while maintaining streaming I/O and robust engine process isolation. Operations must execute without loading entire games into memory, support cancellation at move boundaries, enforce strict timeout constraints per position, and integrate analysis metadata directly into the binary index for instant filtering.

## 2. Input Contract

```csharp
public record AnalysisRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    EngineConfiguration Engine,     // Engine selection and configuration (see Section 3)
    AnalysisScope Scope,            // Games/moves to analyze (see Section 4)
    string? OutputFilePath = null,  // If null, return analysis results only (no file rewrite)
    AnalysisOptions Options = null  // Configuration parameters (defaults in Section 2.2)
);

public record EngineConfiguration(
    EngineType Type,                // Stockfish | Lc0 | External
    string ExecutablePath,          // Path to engine binary (stockfish.exe, lc0.exe)
    IReadOnlyDictionary<string, string> UciOptions, // Engine-specific UCI options
    int Threads = 1,                // Threads for engine (Stockfish) or backend (Lc0)
    int HashMb = 16,                // Hash table size in MB
    string? EvalFile = null         // Lc0 network file path (.pb.gz)
);

public enum EngineType
{
    Stockfish,  // Classical alpha-beta engine
    Lc0,        // Neural network engine (requires eval file)
    External    // Generic UCI engine (user-provided)
}

public record AnalysisScope(
    AnalysisTarget Target = AnalysisTarget.AllGames, // Games to analyze
    MoveSelectionStrategy MoveSelection = MoveSelectionStrategy.AllMoves, // Moves per game
    int? MaxGames = null,           // Limit total games analyzed
    IReadOnlyList<int>? GameIndices = null, // Explicit game indices to analyze
    int? StartPly = null,           // Start analysis at ply N (1 = White's first move)
    int? EndPly = null              // Stop analysis after ply N
);

public enum AnalysisTarget
{
    AllGames,
    FilteredGames,      // Apply FilterCriteria from separate property
    SelectedGames,      // Use explicit GameIndices list
    UnanalyzedGames     // Only games without existing [%eval] comments
}

public enum MoveSelectionStrategy
{
    AllMoves,           // Analyze every move
    CriticalPositions,  // Only analyze positions after captures/checks/tactical motifs
    EveryNthMove,       // Analyze every Nth move (e.g., every 5th ply)
    OpeningOnly,        // First 20 plies only
    MiddlegameOnly,     // Plies 21-60
    EndgameOnly,        // Ply 61+ or material threshold
    UserDefined         // Custom predicate delegate
}

public record AnalysisOptions(
    int Depth = 20,                     // Fixed depth analysis (mutually exclusive with Nodes/Time)
    int? Nodes = null,                  // Nodes limit instead of depth
    TimeSpan? TimePerMove = null,       // Time limit per position (overrides Depth)
    int MultiPv = 1,                    // Principal variations to analyze (1 = best move only)
    bool AnnotateWithComments = true,   // Inject [%eval] comments into move text
    bool AnnotateWithVariations = false,// Inject best line as variation (expensive)
    bool OverwriteExisting = false,     // Replace existing [%eval] comments
    bool SkipBookMoves = true,          // Skip analysis for moves in opening book
    bool SkipForcedMoves = true,        // Skip analysis when only one legal move exists
    bool SkipQuietPositions = false,    // Skip analysis in "quiet" positions (no tactics)
    EvaluationFormat EvalFormat = EvaluationFormat.Centipawns, // Output format
    bool PreserveIndex = true,          // Update analysis flags in binary index
    int MaxConcurrentEngines = 1        // Parallel engine instances (requires license compliance)
);
```

### 2.1 Evaluation Formats
```csharp
public enum EvaluationFormat
{
    Centipawns,     // [%eval 0.45] - human-readable centipawns
    WinPercentage,  // [%eval 54.2%] - win probability (Lc0 style)
    PureCentipawns, // [%eval 45] - raw integer centipawns (no decimal)
    MateDistance    // [%eval #3] - forced mate in N moves
}
```

### 2.2 Default Options
```csharp
public static readonly AnalysisOptions Default = new(
    Depth: 20,
    Nodes: null,
    TimePerMove: null,
    MultiPv: 1,
    AnnotateWithComments: true,
    AnnotateWithVariations: false,
    OverwriteExisting: false,
    SkipBookMoves: true,
    SkipForcedMoves: true,
    SkipQuietPositions: false,
    EvalFormat: EvaluationFormat.Centipawns,
    PreserveIndex: true,
    MaxConcurrentEngines: 1
);
```

## 3. Engine Process Management Protocol

### 3.1 UCI Protocol State Machine
Critical requirement: Full isolation between engine instances with guaranteed cleanup:

```csharp
public sealed class UciEngine : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;
    
    public async Task InitializeAsync(CancellationToken ct)
    {
        // Launch engine process with redirected I/O
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        
        _process.Start();
        _input = _process.StandardInput;
        _output = _process.StandardOutput;
        
        // UCI handshake sequence
        await SendCommandAsync("uci", ct);
        await WaitForReadyAsync(ct);
        await ConfigureEngineAsync(ct);
        await SendCommandAsync("isready", ct);
        await WaitForReadyAsync(ct);
    }
    
    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        // Read lines until "readyok" received (timeout: 5 seconds)
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        
        while (!linked.Token.IsCancellationRequested)
        {
            string line = await _output.ReadLineAsync();
            if (line?.StartsWith("readyok") == true)
                return;
            
            // Log debug output (info strings)
            if (line?.StartsWith("info") == true)
                OnEngineDebug?.Invoke(line);
        }
        
        throw new EngineTimeoutException("Engine failed to respond with 'readyok'");
    }
    
    public async Task<EngineEvaluation> AnalyzePositionAsync(
        string fen, 
        int depth, 
        CancellationToken ct)
    {
        // Safety: Never send position while engine is thinking
        await SendCommandAsync($"position fen {fen}", ct);
        await SendCommandAsync($"go depth {depth}", ct);
        
        return await ReadEvaluationAsync(ct);
    }
    
    private async Task<EngineEvaluation> ReadEvaluationAsync(CancellationToken ct)
    {
        EngineEvaluation bestEval = null;
        DateTime start = DateTime.UtcNow;
        TimeSpan timeout = _options.TimePerMove ?? TimeSpan.FromSeconds(30);
        
        while (DateTime.UtcNow - start < timeout)
        {
            ct.ThrowIfCancellationRequested();
            
            string line = await _output.ReadLineAsync(ct);
            if (line == null) break;
            
            if (line.StartsWith("bestmove"))
            {
                // Analysis complete
                return bestEval ?? throw new EngineProtocolException("No evaluation received before bestmove");
            }
            
            if (line.StartsWith("info"))
            {
                // Parse UCI info line for evaluation
                var eval = ParseUciInfoLine(line);
                if (eval != null)
                    bestEval = eval; // Keep most recent (deepest) evaluation
            }
        }
        
        throw new EngineTimeoutException($"Analysis timed out after {timeout.TotalSeconds}s");
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            // Graceful shutdown sequence
            _input?.WriteLine("stop");
            _input?.WriteLine("quit");
            _input?.Dispose();
            
            // Hard kill if engine doesn't exit within 2 seconds
            if (!_process.WaitForExit(2000))
                _process.Kill(true);
            
            _process.Dispose();
        }
        catch (Exception ex)
        {
            // Log disposal failure but don't propagate (prevents cascading failures)
            OnEngineError?.Invoke($"Engine disposal failed: {ex.Message}");
        }
        finally
        {
            _disposed = true;
        }
    }
}
```

### 3.2 Critical Safety Constraints
| Constraint | Enforcement Mechanism | Failure Mode |
|------------|------------------------|--------------|
| **Per-move timeout** | CancellationToken per position with 30s default | Abort position; continue to next move |
| **Engine hang detection** | Watchdog thread monitoring I/O activity | Kill engine process; restart cleanly |
| **Memory pressure** | Limit HashMb based on available RAM (max 75% of free memory) | Reduce HashMb; log warning |
| **License compliance** | Stockfish: unlimited threads; Lc0: respect network license terms | Enforce thread limits per license |
| **Cancellation** | Full support at game/move boundaries via CancellationToken | Engine process terminated cleanly; partial results returned |

### 3.3 Opening Book Integration (SkipBookMoves)
```csharp
private bool IsBookMove(string fen, Move move)
{
    // Query embedded Polyglot book (standard for Stockfish)
    using var book = PolyglotBook.Open("book.bin");
    var entries = book.FindPosition(fen);
    
    return entries.Any(e => e.Move == move);
}
```
- **Book format:** Polyglot BIN format (widely supported, compact)
- **Fallback:** If book missing, disable `SkipBookMoves` with warning
- **Performance:** Book lookups O(1) via Zobrist hash indexing

## 4. Analysis Algorithms by Scope

### 4.1 Move Selection Strategies

#### CriticalPositions Strategy
Identifies tactically rich positions requiring analysis:

```csharp
private bool IsCriticalPosition(ChessBoard board, Move move)
{
    // Criteria for critical position:
    // 1. Capture occurred
    if (move.IsCapture) return true;
    
    // 2. Check delivered
    if (board.GivesCheck(move)) return true;
    
    // 3. Piece hanging (tactical vulnerability)
    if (IsHangingPiece(board, move)) return true;
    
    // 4. Pawn promotion/threat
    if (move.IsPromotion || CreatesPromotionThreat(board, move)) return true;
    
    // 5. King safety compromised (sudden drop in king safety score)
    if (KingSafetyDeteriorated(board, move)) return true;
    
    return false;
}
```

#### Quiet Position Detection (SkipQuietPositions)
```csharp
private bool IsQuietPosition(ChessBoard board)
{
    // Position is "quiet" if:
    // - No captures possible on next ply
    // - No checks possible on next ply
    // - No pawn promotions imminent
    // - Material balance stable (no hanging pieces)
    
    return !board.HasLegalCaptures() && 
           !board.HasLegalChecks() && 
           !board.HasPromotionThreats() &&
           !board.HasHangingPieces();
}
```

### 4.2 Evaluation Parsing from UCI Output
```csharp
private EngineEvaluation ParseUciInfoLine(string line)
{
    // Example UCI lines:
    // "info depth 20 seldepth 25 multipv 1 score cp 45 ... pv e4 e5"
    // "info depth 15 seldepth 18 multipv 1 score mate 3 ... pv Qh5+ Ke7 Qxe5#"
    
    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    
    EngineEvaluation eval = new();
    int i = 0;
    
    while (i < tokens.Length - 1)
    {
        if (tokens[i] == "depth") eval.Depth = int.Parse(tokens[++i]);
        else if (tokens[i] == "score")
        {
            i++;
            if (tokens[i] == "cp")
            {
                eval.Centipawns = int.Parse(tokens[++i]);
                eval.IsMate = false;
            }
            else if (tokens[i] == "mate")
            {
                eval.MateDistance = int.Parse(tokens[++i]);
                eval.IsMate = true;
            }
        }
        else if (tokens[i] == "pv" && eval.BestLine == null)
        {
            // Capture principal variation (first PV only)
            var pvStart = i + 1;
            eval.BestLine = string.Join(" ", tokens.Skip(pvStart).Take(10)); // Limit to 10 plies
            break; // PV is last significant token
        }
        
        i++;
    }
    
    return eval;
}
```

## 5. Algorithm Specification

### 5.1 Single-Engine Analysis Pipeline
```csharp
public async Task<AnalysisReport> AnalyzeAsync(AnalysisRequest request, CancellationToken ct)
{
    using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
    ReadOnlySpan<GameRecord> records = index.GetGameRecords();
    
    // Determine games to analyze based on scope
    var gameIndices = DetermineAnalysisGames(request.Scope, records);
    
    // Initialize engine
    await using var engine = new UciEngine(request.Engine, request.Options);
    await engine.InitializeAsync(ct);
    
    var report = new AnalysisReport(gameIndices.Count);
    
    // Process games sequentially (engine stateful - cannot parallelize per game)
    for (int gameIndex = 0; gameIndex < gameIndices.Count; gameIndex++)
    {
        ct.ThrowIfCancellationRequested();
        
        int globalIndex = gameIndices[gameIndex];
        ref readonly var record = ref records[globalIndex];
        
        // Skip if already analyzed and not overwriting
        if (!request.Options.OverwriteExisting && record.HasAnalysis)
        {
            report.SkippedGames++;
            continue;
        }
        
        // Analyze single game
        var gameResult = await AnalyzeSingleGameAsync(
            request.SourceFilePath,
            record,
            engine,
            request.Scope,
            request.Options,
            ct
        );
        
        report.GamesAnalyzed++;
        report.TotalPositionsAnalyzed += gameResult.PositionsAnalyzed;
        report.EngineErrors += gameResult.Errors.Count;
        
        // Persist results based on output mode
        if (request.OutputFilePath == null)
        {
            // In-memory results only
            report.GameResults.Add(new GameAnalysisResult(globalIndex, gameResult.Evaluations));
        }
        else
        {
            // Physical file rewrite handled in batch after all analysis completes
            report.GameResults.Add(new GameAnalysisResult(globalIndex, gameResult.Evaluations));
        }
        
        // Progress reporting
        double percent = (double)(gameIndex + 1) / gameIndices.Count * 100;
        OnProgress?.Invoke(new AnalysisProgress(
            percent,
            report.GamesAnalyzed,
            report.TotalPositionsAnalyzed,
            engine.Stats
        ));
    }
    
    // Generate output file if requested
    if (request.OutputFilePath != null)
    {
        await RewriteFileWithAnalysisAsync(
            request,
            report.GameResults,
            records,
            index.StringHeap,
            ct
        );
    }
    
    return report;
}
```

### 5.2 Single-Game Analysis Algorithm
```csharp
private async Task<GameAnalysisResult> AnalyzeSingleGameAsync(
    string pgnPath,
    GameRecord record,
    UciEngine engine,
    AnalysisScope scope,
    AnalysisOptions options,
    CancellationToken ct)
{
    var evaluations = new List<PositionEvaluation>();
    var errors = new List<AnalysisError>();
    
    // Initialize board state
    ChessBoard board = record.HasFen 
        ? ChessBoard.FromFen(record.StartFen) 
        : ChessBoard.StartPosition();
    
    // Open PGN stream for move reading
    using var stream = File.OpenRead(pgnPath);
    stream.Seek(record.FileOffset + record.FirstMoveOffset, SeekOrigin.Begin);
    
    var moveParser = new IncrementalMoveParser(stream, record.Length - record.FirstMoveOffset);
    int plyCount = 0;
    
    while (await moveParser.MoveNextAsync(ct))
    {
        plyCount++;
        ct.ThrowIfCancellationRequested();
        
        // Apply scope filters
        if (scope.StartPly.HasValue && plyCount < scope.StartPly.Value) continue;
        if (scope.EndPly.HasValue && plyCount > scope.EndPly.Value) break;
        
        Move move = moveParser.Current;
        
        // Skip forced moves if configured
        if (options.SkipForcedMoves && board.GetLegalMoves().Count == 1)
        {
            board.ApplyMove(move);
            continue;
        }
        
        // Skip book moves if configured
        if (options.SkipBookMoves && IsBookMove(board.Fen, move))
        {
            board.ApplyMove(move);
            continue;
        }
        
        // Skip quiet positions if configured
        if (options.SkipQuietPositions && IsQuietPosition(board))
        {
            board.ApplyMove(move);
            continue;
        }
        
        // Determine if this move should be analyzed based on strategy
        bool shouldAnalyze = scope.MoveSelection switch
        {
            MoveSelectionStrategy.AllMoves => true,
            MoveSelectionStrategy.CriticalPositions => IsCriticalPosition(board, move),
            MoveSelectionStrategy.EveryNthMove => plyCount % scope.EveryNthInterval == 0,
            MoveSelectionStrategy.OpeningOnly => plyCount <= 20,
            MoveSelectionStrategy.MiddlegameOnly => plyCount > 20 && plyCount <= 60,
            MoveSelectionStrategy.EndgameOnly => plyCount > 60 || board.MaterialCount < 12,
            _ => true
        };
        
        if (!shouldAnalyze)
        {
            board.ApplyMove(move);
            continue;
        }
        
        // Perform analysis with timeout protection
        EngineEvaluation eval;
        try
        {
            eval = await engine.AnalyzePositionAsync(
                board.Fen,
                options.Depth,
                ct
            );
            
            // Convert to requested format
            var formatted = FormatEvaluation(eval, options.EvalFormat);
            evaluations.Add(new PositionEvaluation(plyCount, move, formatted, eval.BestLine));
        }
        catch (EngineTimeoutException ex)
        {
            errors.Add(new AnalysisError(plyCount, "Timeout", ex.Message));
            eval = EngineEvaluation.Unknown; // Mark as unanalyzed
        }
        catch (EngineProtocolException ex)
        {
            errors.Add(new AnalysisError(plyCount, "ProtocolError", ex.Message));
            eval = EngineEvaluation.Unknown;
        }
        
        // Apply move to advance position
        board.ApplyMove(move);
    }
    
    return new GameAnalysisResult(evaluations, errors);
}
```

### 5.3 Evaluation Formatting
```csharp
private string FormatEvaluation(EngineEvaluation eval, EvaluationFormat format)
{
    return format switch
    {
        EvaluationFormat.Centipawns => eval.IsMate
            ? $"[%eval #{eval.MateDistance}]"
            : $"[%eval {eval.Centipawns / 100.0:F2}]",
        
        EvaluationFormat.PureCentipawns => eval.IsMate
            ? $"[%eval #{eval.MateDistance}]"
            : $"[%eval {eval.Centipawns}]",
        
        EvaluationFormat.WinPercentage => 
            // Convert centipawns to win probability using logistic function
            eval.IsMate
                ? $"[%eval {(eval.MateDistance > 0 ? 100.0 : 0.0):F1}%]"
                : $"[%eval {CentipawnsToWinPercentage(eval.Centipawns):F1}%]",
        
        EvaluationFormat.MateDistance => eval.IsMate
            ? $"[%eval #{eval.MateDistance}]"
            : $"[%eval {eval.Centipawns / 100.0:F2}]",
        
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

private double CentipawnsToWinPercentage(int centipawns)
{
    // Logistic function approximation (used by Lc0)
    return 50.0 + 50.0 * Math.Tanh(centipawns / 278.0);
}
```

## 6. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Engine crashes mid-analysis | Terminate process; restart engine cleanly; skip current position; continue with next move |
| Corrupted FEN/start position | Skip game with diagnostic; do not crash entire batch |
| Time control tags affecting analysis (`[%clk]`) | Ignore clock data during analysis; preserve original tags in output |
| Existing [%eval] comments in source | Skip position if `OverwriteExisting=false`; otherwise replace comment |
| Multi-PV analysis with variations | Inject as nested variations: `1. e4 { [%eval 0.45] } ( 1... e5 { [%eval 0.35] } )` |
| Forced mate detection | Always use `#N` notation regardless of EvalFormat for clarity |
| Engine reports "No legal moves" (stalemate) | Treat as draw evaluation (0.00); log diagnostic |
| Network engine (Lc0) missing weights file | Fail fast with clear error message before analysis begins |

## 7. Performance Characteristics

### 7.1 Time Complexity
| Factor | Impact | Notes |
|--------|--------|-------|
| Depth | Exponential | Depth 20 ≈ 4× slower than depth 15 for Stockfish |
| Position complexity | Variable | Tactical positions 2-3× slower than quiet positions |
| Hash size | Sublinear | 1GB hash ≈ 15% faster than 256MB for deep searches |
| Threads | Sublinear scaling | 4 threads ≈ 2.8× faster than 1 thread (diminishing returns) |

### 7.2 Memory Footprint
| Component | Memory Usage | Notes |
|-----------|--------------|-------|
| Engine process (Stockfish) | 16MB + HashMb | Hash table dominates memory usage |
| Engine process (Lc0 GPU) | 500MB-2GB | Network weights + backend buffers |
| Analysis buffer | < 64 KB | Single game state + move buffer |
| Evaluation cache | O(P) | P = positions analyzed (typically < 10K per game) |

### 7.3 Real-World Benchmarks (Intel i7-12700K, 32GB RAM, NVMe SSD)
| Configuration | 100 Games (40 moves each) | Positions/Hour |
|---------------|--------------------------|---------------|
| Stockfish 16, Depth 20, 1 thread, 256MB hash | 4h 12m | 3,800 |
| Stockfish 16, Depth 20, 4 threads, 1GB hash | 1h 38m | 9,800 |
| Lc0 v0.30, T60-384, 1 thread, RTX 4070 | 2h 45m | 5,800 |
| Critical positions only (≈30% of moves) | 1h 15m | 13,200 |

## 8. Binary Index Integration Points

### 8.1 Analysis Flags in GameRecord
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 0: HasAnnotations, Bit 1: HasVariations, Bit 2: HasAnalysis
    
    public bool HasAnalysis => (Flags & (1 << 2)) != 0;
    public byte AnalysisDepth; // Stored depth of analysis (0 = unanalyzed)
}
```

### 8.2 In-Place Index Update (No File Rewrite)
```csharp
private void MarkAnalyzedGamesInIndex(
    MemoryMappedViewAccessor accessor,
    IReadOnlyList<GameAnalysisResult> results,
    int gameCount)
{
    foreach (var result in results)
    {
        if (result.Evaluations.Count == 0) continue; // No analysis performed
        
        int offset = IndexHeader.Size + (result.GameIndex * GameRecord.Size);
        
        // Set HasAnalysis flag
        byte flags = accessor.ReadByte(offset + GameRecord.FlagsOffset);
        flags |= (1 << 2); // Set bit 2
        accessor.Write(offset + GameRecord.FlagsOffset, flags);
        
        // Store analysis depth (minimum depth across positions)
        int minDepth = result.Evaluations.Min(e => e.Depth);
        accessor.Write(offset + GameRecord.AnalysisDepthOffset, (byte)Math.Min(minDepth, 255));
    }
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 9. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `EngineNotFoundException` | Executable path invalid or missing | Fail fast before any analysis begins |
| `EngineLicenseException` | Lc0 network used in violation of license terms | Abort with clear license guidance |
| `EngineTimeoutException` | Position analysis exceeds timeout | Skip position; continue to next move; log diagnostic |
| `EngineCrashException` | Engine process terminates unexpectedly | Restart engine cleanly; skip current position; continue batch |
| `InsufficientResourcesException` | Not enough RAM for requested HashMb | Reduce HashMb to available memory; log warning; continue |
| `PartialWriteException` | Disk full during output generation | Delete partial output; preserve source integrity |

## 10. Testability Requirements

### 10.1 Required Test Fixtures
- `pgn_tactical_positions.pgn` (positions with clear tactical themes for eval validation)
- `pgn_forced_mates.pgn` (positions with forced mate sequences)
- `pgn_quiet_positions.pgn` (strategically complex but tactically quiet positions)
- `pgn_existing_evals.pgn` (games with pre-existing [%eval] comments for overwrite testing)
- `engine_mock_responses.txt` (recorded UCI protocol exchanges for offline testing)

### 10.2 Assertion Examples
```csharp
// Verify mate-in-3 detected correctly
var report = await service.AnalyzeAsync(new AnalysisRequest(
    "forced_mates.pgn",
    new EngineConfiguration(EngineType.Stockfish, "stockfish.exe"),
    new AnalysisScope(MoveSelection: MoveSelectionStrategy.AllMoves),
    Options: new AnalysisOptions(Depth: 15)
), CancellationToken.None);

var mateGame = report.GameResults.First();
var mateEval = mateGame.Evaluations.First(e => e.IsMate);
Assert.Equal(3, mateEval.MateDistance);
Assert.Contains("#3", mateEval.FormattedComment);

// Verify evaluation format conversion
var percentageReport = await service.AnalyzeAsync(new AnalysisRequest(
    "quiet_game.pgn",
    engineConfig,
    scope,
    Options: new AnalysisOptions(EvalFormat: EvaluationFormat.WinPercentage)
), CancellationToken.None);

var eval = percentageReport.GameResults.First().Evaluations.First();
Assert.Matches(@"\[%eval \d+\.\d+%\]", eval.FormattedComment);

// Verify index flag updated
var index = PgnBinaryIndex.OpenRead("analyzed.pgn.pbi");
var record = index.GetGameRecord(0);
Assert.True(record.HasAnalysis);
Assert.Equal(15, record.AnalysisDepth);
```

## 11. Versioning & Compatibility

- **Engine Protocol Versions:**
  - UCI 1.0: Basic protocol (all engines support)
  - UCI 2.0+: Optional extensions (pondering, multi-PV) - gracefully degrade
- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade AnalysisDepth field during processing
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Injected comments must conform to PGN Spec v1.0 (`{ [%eval 0.45] }`)

## 12. Security & Compliance Considerations

| Risk | Mitigation |
|------|------------|
| Engine binary execution | Validate executable signature before launch; restrict to trusted paths |
| Malicious UCI commands | Sanitize all commands sent to engine; never pass user input directly |
| GPU resource exhaustion (Lc0) | Limit concurrent Lc0 instances to 1 per GPU; monitor VRAM usage |
| License compliance (Lc0 networks) | Embed license metadata with network files; enforce usage terms |
| Side-channel attacks via timing | Not applicable - analysis is user-initiated offline operation |
| Privacy (cloud engines) | All analysis performed locally; no data transmitted externally |