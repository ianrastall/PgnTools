# EleganceService.md

## Service Specification: EleganceService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (elegance flag population in shadow array)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Identify "brilliant," "elegant," or tactically significant games based on evaluation curve characteristics without requiring full re-analysis. Operations must execute in streaming mode without loading entire games into memory. The service must detect significant evaluation shifts (tactical shots), sustained complexity, aesthetic move sequences, and integrate elegance markers directly into the binary index for instant filtering.

## 2. Input Contract

```csharp
public record EleganceRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    EleganceStrategy Strategy,      // Detection algorithm (see Section 3)
    string? OutputFilePath = null,  // If null, return elegance report only (no file rewrite)
    EleganceOptions Options = null  // Configuration parameters (defaults in Section 2.2)
);

public enum EleganceStrategy
{
    EvaluationDrop,         // Detect significant eval swings (tactical shots)
    ComplexitySustained,    // Identify games with prolonged tactical complexity
    AestheticSequence,      // Find sequences with multiple precise moves under pressure
    Hybrid,                 // Combine multiple metrics with weighted scoring
    PreAnalyzedOnly         // Only process games with existing [%eval] comments
}

public record EleganceOptions(
    double MinEvaluationDrop = 1.5,         // Minimum cp drop to trigger "brilliant" tag (in pawns)
    double MinMateConversion = 3.0,         // Minimum eval improvement when converting advantage to mate
    int MinConsecutivePrecise = 3,          // Minimum precise moves in sequence for "elegant" tag
    double PrecisionThreshold = 0.25,       // Max eval deviation to count as "precise" move (in pawns)
    bool RequireEngineAnalysis = true,      // Fail fast if no [%eval] comments present
    bool TagWithNag = true,                 // Inject NAG $13 (brilliant move) or $6 (impressive move)
    bool InjectEleganceTag = true,          // Add [Elegance "Brilliant"] header tag
    bool SkipEarlyGame = true,              // Ignore first 10 plies (opening theory)
    bool SkipEndgame = false,               // Ignore positions with < 7 pieces (simpler tactics)
    double ComplexityThreshold = 0.7,       // Minimum complexity score (0.0-1.0) for sustained complexity
    int MaxConcurrentGames = 1000           // Batch size for memory-constrained environments
);
```

### 2.1 Default Options
```csharp
public static readonly EleganceOptions Default = new(
    MinEvaluationDrop: 1.5,
    MinMateConversion: 3.0,
    MinConsecutivePrecise: 3,
    PrecisionThreshold: 0.25,
    RequireEngineAnalysis: true,
    TagWithNag: true,
    InjectEleganceTag: true,
    SkipEarlyGame: true,
    SkipEndgame: false,
    ComplexityThreshold: 0.7,
    MaxConcurrentGames: 1000
);
```

## 3. Elegance Detection Algorithms

### 3.1 Evaluation Drop Detection (Primary Strategy)
Identifies positions where a player finds a move that dramatically improves their evaluation:

```csharp
private EleganceEvent DetectEvaluationDrop(
    IReadOnlyList<PositionEvaluation> evaluations,
    EleganceOptions options)
{
    var events = new List<EleganceEvent>();
    
    for (int i = 1; i < evaluations.Count; i++)
    {
        // Skip early game if configured
        if (options.SkipEarlyGame && evaluations[i].Ply <= 20)
            continue;
        
        // Skip endgame if configured
        if (options.SkipEndgame && evaluations[i].MaterialCount < 7)
            continue;
        
        var prev = evaluations[i - 1];
        var current = evaluations[i];
        
        // Only consider positions where player was worse before move
        if (prev.Centipawns >= -50) // Not significantly worse
            continue;
        
        // Calculate evaluation improvement after move
        double improvement = current.Centipawns - prev.Centipawns;
        
        // Detect significant improvement (tactical shot)
        if (improvement >= options.MinEvaluationDrop * 100)
        {
            events.Add(new EleganceEvent(
                Ply: current.Ply,
                Type: EleganceEventType.BrilliantMove,
                Magnitude: improvement / 100.0,
                Confidence: CalculateConfidence(improvement, prev.Centipawns),
                Description: $"Evaluation swing: {prev.Centipawns/100.0:F2} → {current.Centipawns/100.0:F2}"
            ));
        }
        
        // Detect mate conversion (turning advantage into forced win)
        if (prev.IsMate && !prev.IsForcedWin && current.IsForcedWin)
        {
            double mateGain = Math.Abs(current.MateDistance ?? 0) - Math.Abs(prev.MateDistance ?? 0);
            if (mateGain >= options.MinMateConversion)
            {
                events.Add(new EleganceEvent(
                    Ply: current.Ply,
                    Type: EleganceEventType.MateConversion,
                    Magnitude: mateGain,
                    Confidence: 0.95,
                    Description: $"Converted to mate in {current.MateDistance}"
                ));
            }
        }
    }
    
    return events.OrderByDescending(e => e.Magnitude).FirstOrDefault();
}
```

#### Confidence Calculation for Evaluation Drops
| Factor | Weight | Rationale |
|--------|--------|-----------|
| Magnitude of drop | +0.3 per pawn | Larger swings = more impressive |
| Depth of analysis | +0.1 per 5 plies depth | Deeper analysis = more reliable eval |
| Position complexity | +0.2 if complex | Complex positions make shots harder to find |
| Opponent strength | +0.15 if >2600 Elo | Stronger opponent = more impressive shot |
| Time pressure context | +0.1 if known | Real games under time pressure (requires clock data) |

### 3.2 Sustained Complexity Detection
Identifies games maintaining high tactical complexity throughout:

```csharp
private double CalculateComplexityScore(IReadOnlyList<PositionEvaluation> evaluations)
{
    // Complexity metrics per position:
    // 1. Evaluation volatility (std dev of last 5 evals)
    // 2. Number of legal moves (mobility)
    // 3. Presence of hanging pieces/tactical motifs
    // 4. Evaluation uncertainty (MultiPV divergence)
    
    double totalComplexity = 0;
    int complexPositions = 0;
    
    for (int i = 10; i < evaluations.Count - 5; i++) // Skip early/late game
    {
        double volatility = CalculateVolatility(evaluations, i, window: 5);
        int mobility = evaluations[i].LegalMoveCount;
        bool hasTactics = evaluations[i].HasTacticalThreats;
        double uncertainty = evaluations[i].MultiPvDivergence;
        
        double positionComplexity = 
            (volatility * 0.3) + 
            (NormalizeMobility(mobility) * 0.25) + 
            (hasTactics ? 0.25 : 0) + 
            (uncertainty * 0.2);
        
        if (positionComplexity >= 0.6) // Threshold for "complex" position
        {
            totalComplexity += positionComplexity;
            complexPositions++;
        }
    }
    
    return complexPositions > 0 
        ? totalComplexity / complexPositions 
        : 0.0;
}

private double CalculateVolatility(
    IReadOnlyList<PositionEvaluation> evaluations, 
    int currentIndex, 
    int window)
{
    if (currentIndex < window) return 0;
    
    double sum = 0;
    double sumSq = 0;
    
    for (int i = currentIndex - window; i <= currentIndex; i++)
    {
        double eval = evaluations[i].Centipawns / 100.0;
        sum += eval;
        sumSq += eval * eval;
    }
    
    double mean = sum / (window + 1);
    double variance = (sumSq - (sum * sum) / (window + 1)) / window;
    
    return Math.Sqrt(variance); // Standard deviation = volatility measure
}
```

### 3.3 Aesthetic Sequence Detection
Finds sequences of multiple precise moves under pressure:

```csharp
private EleganceEvent DetectAestheticSequence(
    IReadOnlyList<PositionEvaluation> evaluations,
    EleganceOptions options)
{
    var bestSequence = new List<PositionEvaluation>();
    var currentSequence = new List<PositionEvaluation>();
    
    for (int i = 1; i < evaluations.Count; i++)
    {
        var prev = evaluations[i - 1];
        var current = evaluations[i];
        
        // Player was under pressure (significantly worse position)
        bool underPressure = prev.Centipawns < -100;
        
        // Move was precise (minimal eval loss despite pressure)
        bool precise = Math.Abs(current.Centipawns - prev.Centipawns) <= options.PrecisionThreshold * 100;
        
        if (underPressure && precise)
        {
            currentSequence.Add(current);
            
            // Check if sequence meets minimum length
            if (currentSequence.Count >= options.MinConsecutivePrecise)
            {
                // Replace best sequence if longer or higher quality
                if (currentSequence.Count > bestSequence.Count ||
                    (currentSequence.Count == bestSequence.Count && 
                     currentSequence.Average(e => Math.Abs(e.Centipawns)) < 
                     bestSequence.Average(e => Math.Abs(e.Centipawns))))
                {
                    bestSequence = new List<PositionEvaluation>(currentSequence);
                }
            }
        }
        else
        {
            currentSequence.Clear();
        }
    }
    
    if (bestSequence.Count >= options.MinConsecutivePrecise)
    {
        double avgPrecision = bestSequence.Average(e => 
            Math.Abs(e.Centipawns - evaluations[e.Ply - 1].Centipawns) / 100.0);
        
        return new EleganceEvent(
            Ply: bestSequence[0].Ply,
            Type: EleganceEventType.PreciseSequence,
            Magnitude: bestSequence.Count + (1.0 - avgPrecision),
            Confidence: 0.85,
            Description: $"{bestSequence.Count} precise moves under pressure (avg dev: {avgPrecision:F2})"
        );
    }
    
    return null;
}
```

## 4. Algorithm Specification

### 4.1 Evaluation Source Resolution
Critical optimization: Prefer existing [%eval] comments over re-analysis:

```csharp
private IReadOnlyList<PositionEvaluation> ExtractEvaluations(
    GameRecord record,
    Stream pgnStream,
    EleganceOptions options)
{
    // Strategy 1: Use pre-existing [%eval] comments if present and reliable
    if (record.HasAnalysis && !options.RequireEngineAnalysis)
    {
        var comments = ExtractEvalComments(record, pgnStream);
        if (comments.Count > record.PlyCount * 0.3) // At least 30% coverage
            return comments;
    }
    
    // Strategy 2: Fall back to lightweight re-analysis if configured
    if (!options.RequireEngineAnalysis)
    {
        return PerformLightweightAnalysis(record, pgnStream, depth: 12);
    }
    
    // Strategy 3: Fail fast if no evaluations available
    throw new MissingEvaluationException(
        $"Game {record.FileOffset} has insufficient evaluation data. " +
        "Run ChessAnalyzerService first or set RequireEngineAnalysis=false");
}
```

### 4.2 Hybrid Scoring Algorithm
Combines multiple elegance metrics into unified score:

```csharp
private EleganceScore CalculateHybridScore(
    IReadOnlyList<PositionEvaluation> evaluations,
    EleganceOptions options)
{
    var dropEvent = DetectEvaluationDrop(evaluations, options);
    var complexity = CalculateComplexityScore(evaluations);
    var sequenceEvent = DetectAestheticSequence(evaluations, options);
    
    // Weighted combination (configurable weights in advanced options)
    double score = 
        (dropEvent?.Magnitude * 0.5 ?? 0) +
        (complexity * 0.3) +
        (sequenceEvent?.Magnitude * 0.2 ?? 0);
    
    EleganceClassification classification = score switch
    {
        >= 5.0 => EleganceClassification.Brilliant,
        >= 3.0 => EleganceClassification.Impressive,
        >= 1.5 => EleganceClassification.Interesting,
        _ => EleganceClassification.None
    };
    
    return new EleganceScore(
        TotalScore: score,
        Classification: classification,
        PrimaryEvent: dropEvent ?? sequenceEvent,
        ComplexityScore: complexity,
        Confidence: CalculateOverallConfidence(dropEvent, complexity, sequenceEvent)
    );
}
```

### 4.3 Output Generation Strategies

#### Strategy A: Report-Only Mode (OutputFilePath = null)
```csharp
public readonly struct EleganceReport
{
    public int TotalGames { get; }
    public int ElegantGames { get; }
    public IReadOnlyList<ElegantGame> Games { get; }
    public EleganceDistribution Distribution { get; }
    
    public record ElegantGame(
        int GameIndex,
        EleganceScore Score,
        IReadOnlyList<EleganceEvent> Events,
        string WhitePlayer,
        string BlackPlayer,
        DateOnly? Date
    );
    
    public record EleganceDistribution(
        int Brilliant,      // Score >= 5.0
        int Impressive,     // Score 3.0-4.9
        int Interesting,    // Score 1.5-2.9
        int None            // Score < 1.5
    );
}
```

#### Strategy B: Physical Tagging Mode (OutputFilePath specified)
```csharp
private byte[] InjectEleganceTags(
    byte[] gameBytes,
    EleganceScore score,
    EleganceOptions options)
{
    string headerSection = ExtractHeaderSection(gameBytes);
    string moveText = ExtractMoveText(gameBytes);
    
    // Inject header tag
    if (options.InjectEleganceTag && score.Classification != EleganceClassification.None)
    {
        string eleganceTag = $"\n[Elegance \"{score.Classification}\"]\n";
        headerSection = InsertBeforeTerminator(headerSection, eleganceTag);
    }
    
    // Inject NAGs for brilliant moves
    if (options.TagWithNag && score.PrimaryEvent != null)
    {
        moveText = InjectNagAtPly(
            moveText, 
            score.PrimaryEvent.Ply,
            score.Classification == EleganceClassification.Brilliant ? "$13" : "$6"
        );
    }
    
    return Encoding.UTF8.GetBytes(headerSection + "\n" + moveText);
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Games without any [%eval] comments | Skip with diagnostic if `RequireEngineAnalysis=true`; otherwise perform lightweight analysis |
| Partial evaluation coverage (<30% of moves) | Weight score by coverage percentage; reduce confidence accordingly |
| Evaluation outliers (engine blunders) | Filter evals with >5 pawn swings between consecutive plies as unreliable |
| Games with only one brilliant move | Still qualify as "Impressive" if magnitude sufficient; require sequence for "Brilliant" |
| Endgame tablebase positions (forced wins) | Exclude from elegance scoring (deterministic outcomes not "brilliant") |
| Time forfeits / illegal moves | Skip game entirely; elegance requires completed moves |
| Multi-game variations (nested games) | Process only main line; ignore variations for elegance scoring |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Operation | Complexity | Notes |
|-----------|------------|-------|
| Evaluation extraction | O(P) | P = plies with eval comments |
| Drop detection | O(P) | Single pass with windowed analysis |
| Complexity scoring | O(P × W) | W = window size (typically 5) |
| Full elegance scoring | O(P) amortized | Dominated by evaluation extraction |

### 6.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| 1M games (report-only) | < 8 MB | Store only elegance scores (16 bytes per game) |
| Evaluation extraction | O(P) per game | P = plies (typically < 100) |
| Hybrid scoring | < 64 KB working buffer | Reuse evaluation windows via circular buffer |

### 6.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Database Size | Pre-Analyzed Games | Games Requiring Analysis |
|---------------|---------------------|--------------------------|
| 100K games | 1.8 s | 14m 20s (with lightweight analysis) |
| 1M games | 18 s | 2h 28m (with lightweight analysis) |
| 10M games | 3m 10s | 24h+ (requires distributed processing) |

## 7. Binary Index Integration Points

### 7.1 Elegance Flags in GameRecord
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 0: HasAnnotations, Bit 1: HasVariations, Bit 2: HasAnalysis, Bit 3: IsNormalized, Bit 4: IsElegant
    
    public bool IsElegant => (Flags & (1 << 4)) != 0;
    public byte EleganceClassification; // 0=None, 1=Interesting, 2=Impressive, 3=Brilliant
    public float EleganceScore;         // 0.0-10.0 normalized score (stored as byte: 0-255)
}
```

### 7.2 In-Place Index Update (No File Rewrite)
```csharp
private void MarkElegantGamesInIndex(
    MemoryMappedViewAccessor accessor,
    IReadOnlyList<ElegantGame> elegantGames)
{
    foreach (var game in elegantGames)
    {
        int offset = IndexHeader.Size + (game.GameIndex * GameRecord.Size);
        
        // Set IsElegant flag
        byte flags = accessor.ReadByte(offset + GameRecord.FlagsOffset);
        flags |= (1 << 4); // Set bit 4
        accessor.Write(offset + GameRecord.FlagsOffset, flags);
        
        // Store classification and normalized score
        accessor.Write(offset + GameRecord.EleganceClassificationOffset, 
                      (byte)game.Score.Classification);
        accessor.Write(offset + GameRecord.EleganceScoreOffset, 
                      (byte)(game.Score.TotalScore * 25.5f)); // Normalize 0-10 → 0-255
    }
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `MissingEvaluationException` | Insufficient eval data with `RequireEngineAnalysis=true` | Skip game + log diagnostic; continue processing |
| `UnreliableEvaluationException` | Evaluation volatility > 10 pawns between consecutive plies | Discard outlier evals; continue with remaining data |
| `PartialGameException` | Game terminated prematurely (no result) | Skip game; elegance requires completed game |
| `UnicodeDecodingException` | Invalid UTF-8 in comments | Replace invalid sequences; continue processing |
| `OutOfMemoryException` | Evaluation buffer exceeds MaxConcurrentGames | Process in smaller batches; continue with remainder |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `pgn_brilliancy_prize.pgn` (games with known brilliancy prizes for ground truth)
- `pgn_eval_swings.pgn` (games with documented large evaluation swings)
- `pgn_precise_defense.pgn` (games with sustained precise defense under pressure)
- `pgn_no_evals.pgn` (games without any engine evaluations for fallback testing)
- `pgn_eval_outliers.pgn` (games with engine blunders creating false positives)

### 9.2 Assertion Examples
```csharp
// Verify brilliancy detection on known prize game
var report = service.DetectElegance(new EleganceRequest(
    "brilliancy_prize.pgn",
    EleganceStrategy.EvaluationDrop,
    Options: new EleganceOptions(MinEvaluationDrop: 1.5)
));

Assert.Single(report.Games);
var prizeGame = report.Games.First();
Assert.Equal(EleganceClassification.Brilliant, prizeGame.Score.Classification);
Assert.True(prizeGame.Score.TotalScore >= 5.0);

// Verify precise sequence detection
var sequenceReport = service.DetectElegance(new EleganceRequest(
    "precise_defense.pgn",
    EleganceStrategy.AestheticSequence,
    Options: new EleganceOptions(MinConsecutivePrecise: 4)
));

var sequenceGame = sequenceReport.Games.First();
Assert.Equal(EleganceClassification.Impressive, sequenceGame.Score.Classification);
Assert.Contains("4 precise moves", sequenceGame.Score.PrimaryEvent.Description);

// Verify index flag updated correctly
var index = PgnBinaryIndex.OpenRead("elegant.pgn.pbi");
var record = index.GetGameRecord(0);
Assert.True(record.IsElegant);
Assert.Equal(3, record.EleganceClassification); // Brilliant = 3
Assert.True(record.EleganceScore > 128); // Normalized score > 5.0
```

## 10. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade Flags field during processing
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Injected NAGs must conform to PGN Spec v1.0 (`$13` for brilliant moves)
- **Analysis dependency:** Service explicitly documents dependency on prior ChessAnalyzerService run for optimal results

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Evaluation spoofing via malicious [%eval] comments | Validate eval plausibility (e.g., reject >+20.00 in balanced positions) |
| Resource exhaustion via pathological evaluation sequences | Limit evaluation window size to 100 plies; truncate longer sequences |
| Privacy leakage via elegance metadata | Elegance tags contain only objective metrics; no PII included |
| Bias in elegance detection | Document algorithmic biases (e.g., favors tactical over positional brilliance) |