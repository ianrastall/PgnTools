# CategoryTaggerService.md

## Service Specification: CategoryTaggerService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (category field population in shadow array)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Categorize games into user-defined or system-defined categories based on metadata, move characteristics, and positional features without loading entire games into memory. Operations must execute in streaming mode with configurable rule sets that combine header fields, move statistics, and engine evaluations. The service must support hierarchical categories, multi-label classification, and integrate category markers directly into the binary index for instant filtering.

## 2. Input Contract

```csharp
public record CategoryTagRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    CategoryRuleSet RuleSet,        // Classification rules (see Section 3)
    string? OutputFilePath = null,  // If null, return category report only (no file rewrite)
    CategoryTagOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record CategoryRuleSet(
    string Name,                    // "Tournament Games", "Miniatures", etc.
    IReadOnlyList<CategoryRule> Rules, // Ordered evaluation sequence
    CategoryEvaluationMode Mode = CategoryEvaluationMode.FirstMatch // FirstMatch | AllMatches | WeightedScore
);

public enum CategoryEvaluationMode
{
    FirstMatch,     // First matching rule assigns category (exclusive)
    AllMatches,     // Game can belong to multiple categories (inclusive)
    WeightedScore   // Rules contribute scores; threshold determines assignment
}

public record CategoryRule(
    string CategoryName,            // "Miniature", "Endgame Study", etc.
    RulePredicate Predicate,        // Condition evaluator (see Section 3.1)
    int Priority = 0,               // Evaluation order (higher = earlier)
    double Weight = 1.0             // For WeightedScore mode
);

public record CategoryTagOptions(
    bool PreserveExisting = true,           // Keep existing [Category] tags alongside new ones
    bool OverwriteConflicts = false,        // Replace existing tags when conflicts occur
    bool InjectCategoryTag = true,          // Add [Category ""] header tag
    bool UpdateIndexField = true,           // Populate category fields in binary index
    bool RequireCompleteAnalysis = false,   // Skip games missing required data (evals, etc.)
    CategoryStorageFormat StorageFormat = CategoryStorageFormat.SingleTag // SingleTag | MultiTag | CustomField
);
```

### 2.1 Default Options
```csharp
public static readonly CategoryTagOptions Default = new(
    PreserveExisting: true,
    OverwriteConflicts: false,
    InjectCategoryTag: true,
    UpdateIndexField: true,
    RequireCompleteAnalysis: false,
    StorageFormat: CategoryStorageFormat.SingleTag
);
```

### 2.2 Category Storage Formats
| Format | Header Representation | Index Storage | Use Case |
|--------|------------------------|---------------|----------|
| `SingleTag` | `[Category "Miniature"]` | Single string ID | Simple exclusive categorization |
| `MultiTag` | `[Category "Miniature Endgame"]` (space-delimited) | Bitmask or string array | Games belonging to multiple categories |
| `CustomField` | `[GameType "miniature"]` + `[Phase "endgame"]` | Dedicated index fields | Advanced filtering with separate dimensions |

## 3. Category Rule System

### 3.1 Rule Predicate Types
Extensible predicate system supporting composable conditions:

```csharp
public abstract record RulePredicate
{
    public abstract bool Evaluate(GameContext context);
}

// Header-based predicates
public record PlayerPredicate(string PlayerName, PlayerRole Role = PlayerRole.Either) : RulePredicate;
public record EloRangePredicate(int? MinElo = null, int? MaxElo = null, PlayerRole Role = PlayerRole.Both) : RulePredicate;
public record DateRangePredicate(DateOnly? Start = null, DateOnly? End = null) : RulePredicate;
public record EventPredicate(string EventName, StringMatchMode Mode = StringMatchMode.Contains) : RulePredicate;
public record ResultPredicate(GameResult Result) : RulePredicate;

// Move-based predicates
public record PlyCountPredicate(int? MinPly = null, int? MaxPly = null) : RulePredicate;
public record MoveCountPredicate(int? MinMoves = null, int? MaxMoves = null) : RulePredicate; // Full moves (plies/2)

// Evaluation-based predicates (requires analysis)
public record EvaluationThresholdPredicate(double ThresholdCp, EvaluationPhase Phase) : RulePredicate;
public record EvaluationSwingPredicate(double MinSwingCp, int MaxPlies) : RulePredicate;
public record SustainedAdvantagePredicate(double MinAdvantageCp, int MinPlies) : RulePredicate;

// Positional predicates
public record MaterialBalancePredicate(int MinDifference, MaterialType Type = MaterialType.All) : RulePredicate;
public record KingSafetyPredicate(int MaxSafetyScore, KingSafetyPhase Phase) : RulePredicate;
public record PawnStructurePredicate(PawnStructureType Type) : RulePredicate;

// Composite predicates
public record AndPredicate(IReadOnlyList<RulePredicate> Predicates) : RulePredicate;
public record OrPredicate(IReadOnlyList<RulePredicate> Predicates) : RulePredicate;
public record NotPredicate(RulePredicate Inner) : RulePredicate;
```

### 3.2 Built-in Category Rule Sets
Predefined rule sets for common chess categorizations:

#### Miniatures (≤25 full moves / ≤50 plies)
```csharp
public static CategoryRuleSet Miniatures => new("Miniatures", new[]
{
    new CategoryRule("Miniature", new PlyCountPredicate(MaxPly: 50), Priority: 100),
    new CategoryRule("UltraMiniature", new PlyCountPredicate(MaxPly: 20), Priority: 110) // ≤10 moves
});
```

#### Endgames (≤7 pieces remaining)
```csharp
public static CategoryRuleSet Endgames => new("Endgames", new[]
{
    new CategoryRule("Endgame", new MaterialCountPredicate(MaxPieces: 7), Priority: 90),
    new CategoryRule("PawnEndgame", new AndPredicate(new RulePredicate[]
    {
        new MaterialCountPredicate(MaxPieces: 7),
        new MaterialTypePredicate(MaterialType.PawnsOnly)
    }), Priority: 95)
});
```

#### Opening Classifications (ECO-based)
```csharp
public static CategoryRuleSet Openings => new("Openings", new[]
{
    new CategoryRule("QueensGambit", new EcoRangePredicate("D06-D69"), Priority: 80),
    new CategoryRule("Sicilian", new EcoRangePredicate("B20-B99"), Priority: 80),
    new CategoryRule("RuyLopez", new EcoRangePredicate("C60-C99"), Priority: 80),
    new CategoryRule("KingsIndian", new EcoRangePredicate("E60-E99"), Priority: 80)
});
```

#### Tactical Games (evaluation swings)
```csharp
public static CategoryRuleSet Tactical => new("Tactical", new[]
{
    new CategoryRule("Tactical", new EvaluationSwingPredicate(MinSwingCp: 3.0, MaxPlies: 5), Priority: 70),
    new CategoryRule("Brilliancy", new AndPredicate(new RulePredicate[]
    {
        new EvaluationSwingPredicate(MinSwingCp: 5.0, MaxPlies: 3),
        new SustainedAdvantagePredicate(MinAdvantageCp: 2.0, MinPlies: 10)
    }), Priority: 85)
});
```

## 4. Algorithm Specification

### 4.1 Game Context Abstraction
Critical optimization: Lazy evaluation of expensive features (evaluations, board states):

```csharp
public readonly struct GameContext
{
    private readonly GameRecord _record;
    private readonly PgnBinaryIndex _index;
    private readonly Lazy<ChessBoard> _finalPosition;
    private readonly Lazy<IReadOnlyList<PositionEvaluation>> _evaluations;
    
    public GameContext(GameRecord record, PgnBinaryIndex index, Stream pgnStream)
    {
        _record = record;
        _index = index;
        _finalPosition = new Lazy<ChessBoard>(() => ParseFinalPosition(record, pgnStream));
        _evaluations = new Lazy<IReadOnlyList<PositionEvaluation>>(() => 
            ExtractEvaluations(record, pgnStream));
    }
    
    // Header fields (zero-cost access via index)
    public string WhitePlayer => _index.StringHeap.GetString(_record.WhiteNameId);
    public string BlackPlayer => _index.StringHeap.GetString(_record.BlackNameId);
    public int WhiteElo => _record.WhiteElo;
    public int BlackElo => _record.BlackElo;
    public DateOnly? GameDate => _record.DateCompact != 0 
        ? DateOnly.FromDateTime(DateTime.ParseExact(_record.DateCompact.ToString(), "yyyyMMdd", CultureInfo.InvariantCulture)) 
        : null;
    public GameResult Result => (GameResult)_record.Result;
    public int PlyCount => _record.PlyCount > 0 ? _record.PlyCount : EstimatePlyCount(_record);
    
    // Lazy-evaluated fields (only computed when needed by predicates)
    public ChessBoard FinalPosition => _finalPosition.Value;
    public IReadOnlyList<PositionEvaluation> Evaluations => _evaluations.Value;
    public int MaterialCount => FinalPosition.PieceCount;
    
    // Predicate helpers
    public bool HasAnalysis => _record.HasAnalysis || _evaluations.IsValueCreated;
}
```

### 4.2 Rule Evaluation Engine
```csharp
private IReadOnlyList<string> EvaluateGameCategories(
    GameContext context,
    CategoryRuleSet ruleSet,
    CategoryTagOptions options)
{
    var matches = new List<(CategoryRule Rule, double Score)>();
    
    foreach (var rule in ruleSet.Rules.OrderByDescending(r => r.Priority))
    {
        // Skip rules requiring analysis if unavailable and strict mode enabled
        if (options.RequireCompleteAnalysis && 
            RequiresAnalysis(rule.Predicate) && 
            !context.HasAnalysis)
        {
            continue;
        }
        
        bool matchesRule = rule.Predicate.Evaluate(context);
        double score = matchesRule ? rule.Weight : 0.0;
        
        if (ruleSet.Mode == CategoryEvaluationMode.FirstMatch && matchesRule)
        {
            return new[] { rule.CategoryName };
        }
        
        if (score > 0)
        {
            matches.Add((rule, score));
            
            if (ruleSet.Mode == CategoryEvaluationMode.AllMatches && !options.RequireCompleteAnalysis)
            {
                // Continue evaluating for additional matches
            }
        }
    }
    
    if (ruleSet.Mode == CategoryEvaluationMode.WeightedScore)
    {
        double totalScore = matches.Sum(m => m.Score);
        if (totalScore >= ruleSet.Threshold)
        {
            return matches.Select(m => m.Rule.CategoryName).ToList();
        }
    }
    
    return ruleSet.Mode == CategoryEvaluationMode.AllMatches 
        ? matches.Select(m => m.Rule.CategoryName).ToList() 
        : Array.Empty<string>();
}
```

### 4.3 Output Generation Strategies

#### Strategy A: Report-Only Mode (OutputFilePath = null)
```csharp
public readonly struct CategoryReport
{
    public int TotalGames { get; }
    public IReadOnlyDictionary<string, int> CategoryDistribution { get; }
    public IReadOnlyList<CategorizedGame> Games { get; }
    
    public record CategorizedGame(
        int GameIndex,
        IReadOnlyList<string> Categories,
        IReadOnlyList<CategoryMatch> Matches, // Detailed rule matches with scores
        string WhitePlayer,
        string BlackPlayer
    );
    
    public record CategoryMatch(
        string CategoryName,
        double Score,
        IReadOnlyList<string> TriggeredPredicates
    );
}
```

#### Strategy B: Physical Tagging Mode (OutputFilePath specified)
```csharp
private byte[] InjectCategoryTags(
    byte[] gameBytes,
    IReadOnlyList<string> categories,
    CategoryTagOptions options)
{
    string header = ExtractHeaderSection(gameBytes);
    string moves = ExtractMoveText(gameBytes);
    
    // Handle existing category tags based on options
    if (!options.PreserveExisting)
    {
        header = Regex.Replace(header, @"\[Category\s+""[^""]*""\]\s*", "");
    }
    
    // Inject new category tags
    if (options.StorageFormat == CategoryStorageFormat.SingleTag && categories.Count > 0)
    {
        // Single tag with primary category (first match)
        string tag = $"\n[Category \"{categories[0]}\"]\n";
        header = InsertBeforeTerminator(header, tag);
    }
    else if (options.StorageFormat == CategoryStorageFormat.MultiTag && categories.Count > 0)
    {
        // Space-delimited multi-category tag
        string combined = string.Join(" ", categories);
        string tag = $"\n[Category \"{combined}\"]\n";
        header = InsertBeforeTerminator(header, tag);
    }
    else if (options.StorageFormat == CategoryStorageFormat.CustomField)
    {
        // Multiple custom tags (e.g., [GameType "miniature"] [Phase "endgame"])
        foreach (var category in categories)
        {
            string tagType = DetermineTagType(category);
            string tag = $"\n[{tagType} \"{category}\"]\n";
            header = InsertBeforeTerminator(header, tag);
        }
    }
    
    return Encoding.UTF8.GetBytes(header + "\n" + moves);
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Games matching multiple exclusive rules | Assign highest-priority match in FirstMatch mode; log conflict in diagnostic stream |
| Rules requiring analysis on unanalyzed games | Skip rule evaluation if `RequireCompleteAnalysis=false`; skip entire game if `true` |
| Circular rule dependencies | Detect during rule set validation; reject with `CircularRuleException` |
| Category names with special characters (`"`, `\`, etc.) | Escape properly in PGN tag injection (`\"` → `""`) |
| Hierarchical categories (e.g., "Endgame:Pawn") | Support colon-delimited hierarchy; index stores full path but enables parent filtering |
| Conflicting categories from different rule sets | Apply rule set priority; higher-priority rule sets override lower ones |
| Games with zero plies (headers only) | Assign "Unplayed" category; skip move-based predicates |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Operation | Complexity | Notes |
|-----------|------------|-------|
| Header-only rule evaluation | O(1) per rule | Index field access only |
| Ply count estimation | O(1) | From index field or rough header estimate |
| Final position parsing | O(M) | M = moves in game (typically < 100) |
| Evaluation extraction | O(P) | P = plies with eval comments |
| Full rule set evaluation | O(R × C) | R = rules, C = predicate cost (mostly O(1)) |

### 6.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| 1M games (header-only rules) | < 4 MB | Category assignments only (4 bytes per game) |
| Games requiring position parsing | < 128 KB | Single board state reused across games |
| Evaluation-dependent rules | < 64 KB | Evaluation window buffers |
| All modes | < 256 KB working buffer | No game data retained across iterations |

### 6.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Rule Set Complexity | 100K Games | 1M Games | 10M Games |
|---------------------|------------|----------|-----------|
| Header-only (players, dates) | 1.1 s | 11 s | 1m 55s |
| Ply count + material | 2.3 s | 23 s | 4m 10s |
| Evaluation-dependent | 8.7 s | 1m 27s | 15m 20s (requires eval extraction) |

## 7. Binary Index Integration Points

### 7.1 Category Fields in GameRecord
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 7: HasCategories
    
    // Extended format (v3.1+) for category storage
    public ushort PrimaryCategoryId; // 0 = uncategorized, 1-65535 = category ID
    public ushort SecondaryCategoryId; // For multi-category support
}
```

### 7.2 Category String Heap
Dedicated heap for category names to minimize index size:

```csharp
public class CategoryHeap
{
    private readonly Dictionary<string, ushort> _categoryToId = new();
    private readonly Dictionary<ushort, string> _idToCategory = new();
    private ushort _nextId = 1; // 0 = uncategorized
    
    public ushort GetOrAddCategoryId(string categoryName)
    {
        if (_categoryToId.TryGetValue(categoryName, out ushort id))
            return id;
        
        id = _nextId++;
        _categoryToId[categoryName] = id;
        _idToCategory[id] = categoryName;
        return id;
    }
    
    public string GetCategoryName(ushort id) => 
        id == 0 ? "Uncategorized" : _idToCategory.TryGetValue(id, out var name) ? name : $"Category_{id}";
}
```

### 7.3 In-Place Index Update (No File Rewrite)
```csharp
private void MarkCategorizedGamesInIndex(
    MemoryMappedViewAccessor accessor,
    IReadOnlyList<CategorizedGame> categorizedGames,
    CategoryHeap categoryHeap)
{
    foreach (var game in categorizedGames)
    {
        if (game.Categories.Count == 0) continue;
        
        int offset = IndexHeader.Size + (game.GameIndex * GameRecord.Size);
        
        // Set HasCategories flag
        byte flags = accessor.ReadByte(offset + GameRecord.FlagsOffset);
        flags |= (1 << 7); // Set bit 7
        accessor.Write(offset + GameRecord.FlagsOffset, flags);
        
        // Store primary category ID
        ushort primaryId = categoryHeap.GetOrAddCategoryId(game.Categories[0]);
        accessor.Write(offset + GameRecord.PrimaryCategoryIdOffset, primaryId);
        
        // Store secondary category ID if multi-category
        if (game.Categories.Count > 1)
        {
            ushort secondaryId = categoryHeap.GetOrAddCategoryId(game.Categories[1]);
            accessor.Write(offset + GameRecord.SecondaryCategoryIdOffset, secondaryId);
        }
    }
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `MissingAnalysisException` | Rule requires evals but none present with `RequireCompleteAnalysis=true` | Skip game + log diagnostic; continue processing |
| `CircularRuleException` | Rule set contains circular dependencies | Fail fast during rule validation before any I/O |
| `InvalidCategoryNameException` | Category name contains invalid PGN characters | Sanitize name; log warning; continue processing |
| `RuleEvaluationException` | Predicate throws exception during evaluation | Skip rule; log diagnostic; continue with remaining rules |
| `PartialWriteException` | Disk full during output generation | Delete partial output; preserve source integrity |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `pgn_miniatures.pgn` (games ≤25 moves for miniature detection)
- `pgn_endgames.pgn` (games with ≤7 pieces for endgame detection)
- `pgn_tactical_swings.pgn` (games with large evaluation swings)
- `pgn_multi_category.pgn` (games belonging to multiple categories)
- `pgn_unanalyzed_games.pgn` (games without evals for fallback testing)
- `pgn_edge_case_games.pgn` (zero-move games, malformed headers, etc.)

### 9.2 Assertion Examples
```csharp
// Verify miniature detection
var report = service.Categorize(new CategoryTagRequest(
    "miniatures.pgn",
    CategoryRuleSet.Miniatures
));

Assert.Equal(50, report.CategoryDistribution["Miniature"]);
Assert.Equal(5, report.CategoryDistribution["UltraMiniature"]); // Subset of miniatures

// Verify multi-category assignment
var multiReport = service.Categorize(new CategoryTagRequest(
    "tactical_endgames.pgn",
    new CategoryRuleSet("Hybrid", new[]
    {
        new CategoryRule("Endgame", new MaterialCountPredicate(MaxPieces: 7)),
        new CategoryRule("Tactical", new EvaluationSwingPredicate(MinSwingCp: 3.0, MaxPlies: 5))
    }, CategoryEvaluationMode.AllMatches)
));

var hybridGame = multiReport.Games.First(g => g.Categories.Count == 2);
Assert.Contains("Endgame", hybridGame.Categories);
Assert.Contains("Tactical", hybridGame.Categories);

// Verify index field population
var index = PgnBinaryIndex.OpenRead("categorized.pgn.pbi");
var record = index.GetGameRecord(0);
Assert.True(record.HasCategories);
Assert.NotEqual(0, record.PrimaryCategoryId);

var categoryName = index.CategoryHeap.GetCategoryName(record.PrimaryCategoryId);
Assert.Equal("Miniature", categoryName);
```

## 10. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade Flags field during processing
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Injected tags must conform to PGN Spec v1.0 (`[Category "Miniature"]`)
- **Rule set serialization:** Support JSON/YAML serialization of rule sets for user customization

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Rule injection via malicious rule sets | Validate rule predicates against allowlist of safe types before execution |
| Resource exhaustion via pathological rules | Limit rule set size to 1000 rules; enforce evaluation timeout per game |
| Path traversal via category names | Sanitize category names before filesystem operations (e.g., category-based file splitting) |
| Privacy leakage via category metadata | Category tags contain only objective game features; no PII included |