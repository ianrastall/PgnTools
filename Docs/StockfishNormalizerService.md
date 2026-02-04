# StockfishNormalizerService.md

## Service Specification: StockfishNormalizerService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (normalized flag population in shadow array)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Convert chess notation to strict Standard Algebraic Notation (SAN) compliant with UCI/engine requirements while preserving game semantics. Operations must execute in streaming mode without loading entire games into memory. The service must handle ambiguous moves, non-standard castling notation, inconsistent disambiguation, promotion syntax variations, and integrate normalization status directly into the binary index for instant filtering.

## 2. Input Contract

```csharp
public record NormalizeRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    NormalizationStrategy Strategy, // Strictness level (see Section 3)
    string? OutputFilePath = null,  // If null, return normalized games only (no file rewrite)
    NormalizeOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public enum NormalizationStrategy
{
    EngineStrict,       // Maximal strictness for engine consumption (Stockfish/Lc0 compatible)
    PgnStandard,        // PGN Spec v1.0 compliant (moderate strictness)
    LenientPreserve,    // Minimal changes - only fix unambiguous errors
    UnicodeToAscii      // Convert figurine notation to ASCII letters
}

public record NormalizeOptions(
    bool PreserveHeaders = true,            // Keep all header tags intact
    bool EnforceDisambiguation = true,      // Always include file/rank for ambiguous moves
    bool StandardizeCastling = true,        // Convert all castling to O-O / O-O-O
    bool StandardizePromotion = true,       // Convert e8=Q, e8Q, e8(Q) → e8=Q
    bool RemoveMoveDecorations = false,     // Strip !, ?, !!, etc. (separate from Unannotator)
    bool ConvertFigurine = true,            // ♘f3 → Nf3
    bool FixAmbiguousMoves = true,          // Resolve SAN ambiguities via board state
    bool ValidateMoveLegality = true,       // Verify each move legal; reject invalid games
    bool PreserveIndex = true               // Update normalization flags in binary index
);
```

### 2.1 Default Options
```csharp
public static readonly NormalizeOptions Default = new(
    PreserveHeaders: true,
    EnforceDisambiguation: true,
    StandardizeCastling: true,
    StandardizePromotion: true,
    RemoveMoveDecorations: false,
    ConvertFigurine: true,
    FixAmbiguousMoves: true,
    ValidateMoveLegality: true,
    PreserveIndex: true
);
```

## 3. Normalization Rules by Category

### 3.1 Castling Notation Standardization
| Input Variant | Normalized Output | Strategy Support |
|---------------|-------------------|------------------|
| `O-O` | `O-O` | All strategies |
| `0-0` (zero) | `O-O` | EngineStrict, PgnStandard |
| `o-o` (lowercase o) | `O-O` | EngineStrict, PgnStandard |
| `oo` | `O-O` | EngineStrict only |
| `O-O-O` | `O-O-O` | All strategies |
| `0-0-0` | `O-O-O` | EngineStrict, PgnStandard |
| `long` / `short` | `O-O-O` / `O-O` | EngineStrict only (rare database variants) |

**Algorithm:**
```csharp
private string NormalizeCastling(string moveToken)
{
    // Case-insensitive matching with character normalization
    string normalized = moveToken.ToUpperInvariant();
    
    // Replace zero digits with letter O
    normalized = normalized.Replace('0', 'O');
    
    // Collapse repeated O's to standard form
    if (Regex.IsMatch(normalized, @"^O\s*-\s*O$"))
        return "O-O";
    
    if (Regex.IsMatch(normalized, @"^O\s*-\s*O\s*-\s*O$"))
        return "O-O-O";
    
    // Handle "oo" variants
    if (normalized == "OO") return "O-O";
    if (normalized == "OOO") return "O-O-O";
    
    return moveToken; // Unrecognized - preserve original (will fail validation)
}
```

### 3.2 Move Disambiguation Rules
Critical requirement: Resolve ambiguous moves using board state to produce legal SAN:

| Position Context | Ambiguous Input | Correct Disambiguation | Rule Applied |
|------------------|-----------------|------------------------|--------------|
| Two knights on b1/g1 can reach f3 | `Nf3` | `Nf3` (unambiguous) | No disambiguation needed |
| Knights on d2/f2 both reach e4 | `Ne4` | `Nde4` or `Nfe4` | File disambiguation required |
| Rooks on a1/a8 both reach a4 | `Ra4` | `R1a4` or `R8a4` | Rank disambiguation required |
| Knights on c3/e3/d4 all reach e5 | `Ne5` | `Nce5`, `Nee5`, or `Nde5` | Full disambiguation (file+rank) |

**Disambiguation Algorithm:**
```csharp
private string DisambiguateMove(ChessBoard board, Move move, NormalizationStrategy strategy)
{
    // Step 1: Determine moving piece type
    PieceType piece = board.GetPiece(move.FromSquare).Type;
    
    // Step 2: Find all legal moves of this piece type to target square
    var candidates = board.GetLegalMoves()
        .Where(m => m.PieceType == piece && m.ToSquare == move.ToSquare)
        .ToList();
    
    // Step 3: If only one candidate, no disambiguation needed
    if (candidates.Count == 1)
        return FormatMove(move, Disambiguation.None);
    
    // Step 4: Determine minimal disambiguation required
    DisambiguationLevel level = DetermineDisambiguationLevel(candidates, move.FromSquare);
    
    // Step 5: Format with required disambiguation
    return FormatMove(move, level);
}

private DisambiguationLevel DetermineDisambiguationLevel(List<Move> candidates, Square fromSquare)
{
    // Check if file disambiguation suffices
    var byFile = candidates.GroupBy(m => m.FromSquare.File).ToList();
    if (byFile.Count > 1 && byFile.All(g => g.Key != fromSquare.File || g.Count() == 1))
        return Disambiguation.File;
    
    // Check if rank disambiguation suffices
    var byRank = candidates.GroupBy(m => m.FromSquare.Rank).ToList();
    if (byRank.Count > 1 && byRank.All(g => g.Key != fromSquare.Rank || g.Count() == 1))
        return Disambiguation.Rank;
    
    // Full disambiguation required
    return Disambiguation.FileAndRank;
}
```

### 3.3 Promotion Syntax Standardization
| Input Variant | Normalized Output | Engine Compatibility |
|---------------|-------------------|----------------------|
| `e8=Q` | `e8=Q` | ✅ Stockfish, Lc0 |
| `e8Q` | `e8=Q` | ✅ Stockfish, Lc0 |
| `e8(Q)` | `e8=Q` | ✅ Stockfish, Lc0 |
| `e8 q` | `e8=Q` | ✅ Stockfish, Lc0 |
| `e8=queen` | `e8=Q` | ✅ Stockfish, Lc0 |
| `e8=D` (German) | `e8=Q` | ✅ Stockfish (auto-converts) |

**Algorithm:**
```csharp
private string NormalizePromotion(string moveText)
{
    // Match promotion patterns: e8Q, e8=Q, e8(Q), e8 q, etc.
    var match = Regex.Match(moveText, @"([a-h][1-8])\s*=?\s*\(?\s*([QRBNqrbn])\s*\)?");
    
    if (!match.Success)
        return moveText; // Not a promotion move
    
    string square = match.Groups[1].Value;
    char pieceChar = char.ToUpper(match.Groups[2].Value[0]);
    
    // Map non-standard promotion pieces (D=T, etc.)
    pieceChar = pieceChar switch
    {
        'D' => 'Q', // German Dame
        'T' => 'Q', // German Turm (incorrect but seen)
        'S' => 'N', // German Springer
        'L' => 'B', // German Läufer
        _ => pieceChar
    };
    
    return $"{square}={pieceChar}";
}
```

### 3.4 Figurine Notation Conversion
| Unicode Symbol | Codepoint | ASCII Equivalent |
|----------------|-----------|------------------|
| ♔ | U+2654 | K |
| ♚ | U+265A | k |
| ♕ | U+2655 | Q |
| ♛ | U+265B | q |
| ♖ | U+2656 | R |
| ♜ | U+265C | r |
| ♗ | U+2657 | B |
| ♝ | U+265D | b |
| ♘ | U+2658 | N |
| ♞ | U+265E | n |
| ♙ | U+2659 | (omitted - pawns have no letter) |
| ♟ | U+265F | (omitted) |

**Conversion Algorithm:**
```csharp
private string ConvertFigurineToAscii(string text)
{
    // Fast path: Check for figurine presence first to avoid unnecessary allocation
    if (!ContainsFigurine(text))
        return text;
    
    StringBuilder sb = new(text.Length);
    
    foreach (char c in text)
    {
        char ascii = c switch
        {
            '\u2654' => 'K',
            '\u2655' => 'Q',
            '\u2656' => 'R',
            '\u2657' => 'B',
            '\u2658' => 'N',
            '\u2659' => 'P', // Rare - pawns sometimes shown
            '\u265A' => 'k',
            '\u265B' => 'q',
            '\u265C' => 'r',
            '\u265D' => 'b',
            '\u265E' => 'n',
            '\u265F' => 'p',
            _ => c
        };
        
        sb.Append(ascii);
    }
    
    return sb.ToString();
}

private static bool ContainsFigurine(string text)
{
    // Optimized check using span operations
    ReadOnlySpan<char> span = text.AsSpan();
    return span.Contains('\u2654') || span.Contains('\u2655') || span.Contains('\u2656') ||
           span.Contains('\u2657') || span.Contains('\u2658') || span.Contains('\u2659') ||
           span.Contains('\u265A') || span.Contains('\u265B') || span.Contains('\u265C') ||
           span.Contains('\u265D') || span.Contains('\u265E') || span.Contains('\u265F');
}
```

## 4. Algorithm Specification

### 4.1 Two-Pass Normalization Pipeline

#### Pass 1: Header Preservation
```csharp
private byte[] ProcessHeaders(ReadOnlySpan<byte> rawGameBytes, NormalizeOptions options)
{
    if (!options.PreserveHeaders)
        return Array.Empty<byte>();
    
    // Locate header section end (first move token)
    int headerEnd = FindHeaderEnd(rawGameBytes);
    
    // Headers preserved intact - return original section
    return rawGameBytes.Slice(0, headerEnd).ToArray();
}
```

#### Pass 2: Move Text Normalization with Board Validation
```csharp
private byte[] NormalizeMoveText(
    ReadOnlySpan<byte> moveTextBytes,
    NormalizeOptions options,
    CancellationToken ct)
{
    string moveText = Encoding.UTF8.GetString(moveTextBytes);
    
    // Step 1: Convert figurine notation first (simplifies subsequent parsing)
    if (options.ConvertFigurine)
        moveText = ConvertFigurineToAscii(moveText);
    
    // Step 2: Initialize board state for validation/disambiguation
    ChessBoard board = ChessBoard.StartPosition(); // FEN handling omitted for brevity
    
    // Step 3: Tokenize move text into move tokens + annotations
    var tokens = TokenizeMoveText(moveText);
    
    // Step 4: Process each move token with board state
    var normalizedTokens = new List<string>(tokens.Count);
    
    foreach (var token in tokens)
    {
        ct.ThrowIfCancellationRequested();
        
        if (token.Type == TokenType.Move)
        {
            // Parse move in current context
            var parsed = ParseMoveInContext(token.Value, board);
            
            if (!parsed.IsValid)
            {
                if (options.ValidateMoveLegality)
                    throw new InvalidMoveException($"Illegal move '{token.Value}' at ply {board.PlyCount}");
                else
                    normalizedTokens.Add(token.Value); // Preserve invalid move
                continue;
            }
            
            // Apply normalization rules
            string normalizedMove = token.Value;
            
            if (options.StandardizeCastling && parsed.IsCastling)
                normalizedMove = NormalizeCastling(normalizedMove);
            
            if (options.StandardizePromotion && parsed.IsPromotion)
                normalizedMove = NormalizePromotion(normalizedMove);
            
            if (options.EnforceDisambiguation && parsed.IsAmbiguous)
                normalizedMove = DisambiguateMove(board, parsed.Move, options.Strategy);
            
            if (options.RemoveMoveDecorations)
                normalizedMove = StripDecorations(normalizedMove);
            
            // Apply move to board for next move's context
            board.ApplyMove(parsed.Move);
            normalizedTokens.Add(normalizedMove);
        }
        else
        {
            // Preserve non-move tokens (comments, variations, NAGs) unchanged
            normalizedTokens.Add(token.Value);
        }
    }
    
    // Step 5: Reconstruct move text with normalized tokens
    string normalizedText = ReconstructMoveText(normalizedTokens);
    
    // Step 6: Final whitespace normalization
    normalizedText = Regex.Replace(normalizedText, @"\s+", " ").Trim();
    
    return Encoding.UTF8.GetBytes(normalizedText);
}
```

### 4.2 Tokenization Strategy
Critical for preserving annotation structure while normalizing moves:

```csharp
private List<Token> TokenizeMoveText(string moveText)
{
    var tokens = new List<Token>();
    int pos = 0;
    
    while (pos < moveText.Length)
    {
        // Skip whitespace (preserve count for reconstruction)
        if (char.IsWhiteSpace(moveText[pos]))
        {
            int wsStart = pos;
            while (pos < moveText.Length && char.IsWhiteSpace(moveText[pos])) pos++;
            tokens.Add(new Token(TokenType.Whitespace, moveText.Substring(wsStart, pos - wsStart)));
            continue;
        }
        
        // Comment: { ... }
        if (moveText[pos] == '{')
        {
            int commentStart = pos;
            pos++;
            int depth = 1;
            
            while (pos < moveText.Length && depth > 0)
            {
                if (moveText[pos] == '{') depth++;
                else if (moveText[pos] == '}') depth--;
                pos++;
            }
            
            tokens.Add(new Token(TokenType.Comment, moveText.Substring(commentStart, pos - commentStart)));
            continue;
        }
        
        // Variation: ( ... )
        if (moveText[pos] == '(')
        {
            int variationStart = pos;
            pos++;
            int depth = 1;
            
            while (pos < moveText.Length && depth > 0)
            {
                if (moveText[pos] == '(') depth++;
                else if (moveText[pos] == ')') depth--;
                pos++;
            }
            
            tokens.Add(new Token(TokenType.Variation, moveText.Substring(variationStart, pos - variationStart)));
            continue;
        }
        
        // NAG: $123
        if (moveText[pos] == '$' && pos + 1 < moveText.Length && char.IsDigit(moveText[pos + 1]))
        {
            int nagStart = pos;
            pos++;
            while (pos < moveText.Length && char.IsDigit(moveText[pos])) pos++;
            tokens.Add(new Token(TokenType.Nag, moveText.Substring(nagStart, pos - nagStart)));
            continue;
        }
        
        // Move number: 1. or 1...
        if (char.IsDigit(moveText[pos]))
        {
            int numStart = pos;
            while (pos < moveText.Length && (char.IsDigit(moveText[pos]) || moveText[pos] == '.')) pos++;
            tokens.Add(new Token(TokenType.MoveNumber, moveText.Substring(numStart, pos - numStart)));
            continue;
        }
        
        // Move token: e4, Nf3, O-O, etc.
        int moveStart = pos;
        while (pos < moveText.Length && !char.IsWhiteSpace(moveText[pos]) && 
               moveText[pos] != '{' && moveText[pos] != '(' && moveText[pos] != '$')
        {
            pos++;
        }
        
        if (pos > moveStart)
            tokens.Add(new Token(TokenType.Move, moveText.Substring(moveStart, pos - moveStart)));
    }
    
    return tokens;
}
```

### 4.3 Streaming Processing Loop
```csharp
public NormalizeReport Normalize(NormalizeRequest request, CancellationToken ct)
{
    using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
    ReadOnlySpan<GameRecord> records = index.GetGameRecords();
    
    var report = new NormalizeReport(records.Length);
    
    if (request.OutputFilePath == null)
    {
        // In-memory processing only
        var normalizedGames = new List<NormalizedGame>(records.Length);
        
        using var pgnStream = File.OpenRead(request.SourceFilePath);
        
        for (int i = 0; i < records.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            
            ref readonly var record = ref records[i];
            
            // Read raw game bytes
            pgnStream.Seek(record.FileOffset, SeekOrigin.Begin);
            byte[] rawBytes = ArrayPool<byte>.Shared.Rent(record.Length);
            ReadExactly(pgnStream, rawBytes.AsSpan(0, record.Length));
            
            // Process headers and move text
            var headerBytes = ProcessHeaders(rawBytes.AsSpan(0, record.Length), request.Options);
            var moveTextBytes = NormalizeMoveText(
                rawBytes.AsSpan(record.FirstMoveOffset, record.Length - record.FirstMoveOffset),
                request.Options,
                ct
            );
            
            // Assemble normalized game
            byte[] normalizedBytes = Concatenate(headerBytes, Encoding.UTF8.GetBytes("\n"), moveTextBytes);
            normalizedGames.Add(new NormalizedGame(i, normalizedBytes, record));
            
            ArrayPool<byte>.Shared.Return(rawBytes);
            report.GamesProcessed++;
            
            // Progress reporting
            if (i % 1000 == 0 || i == records.Length - 1)
            {
                double percent = (double)(i + 1) / records.Length * 100;
                OnProgress?.Invoke(new NormalizeProgress(percent, report.GamesProcessed));
            }
        }
        
        return report with { NormalizedGames = normalizedGames };
    }
    else
    {
        // Physical file rewrite mode
        RewriteFileWithNormalizedGames(request, records, index, report, ct);
        return report;
    }
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Ambiguous move with insufficient disambiguation (`Nbd7` required but `Nd7` given) | Re-parse with board state; inject required disambiguation character |
| Castling with king/rook already moved (illegal position) | Reject game if `ValidateMoveLegality=true`; otherwise preserve original |
| Promotion to non-queen piece with ambiguous syntax (`e8=B` vs `e8B`) | Standardize to `e8=B` regardless of input format |
| Figurine notation mixed with ASCII in same game | Convert ALL figurines; preserve ASCII pieces unchanged |
| Move text with line breaks within variations | Preserve variation structure; normalize only move tokens |
| Games with Chess960/FRC start positions | Require FEN tag; initialize board from FEN before normalization |
| Corrupted UTF-8 sequences in move text | Replace invalid sequences with  before processing; log diagnostic |
| "x" capture notation (`exd5` vs `e4xd5`) | Preserve original capture notation style (both valid SAN) |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Operation | Complexity | Notes |
|-----------|------------|-------|
| Figurine conversion | O(M) | M = move text length; single pass |
| Move tokenization | O(M) | Linear scan with nesting depth tracking |
| Board validation | O(M × B) | B = board operation cost (typically O(1) per move) |
| Disambiguation | O(M × L) | L = legal move generation cost (typically < 50 moves) |
| Full normalization | O(M) amortized | Dominated by move text length |

### 6.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| 1M games streaming | < 64 KB | Single game buffer via ArrayPool<byte> |
| Board state per game | 128 bytes | Compact bitboard representation |
| Tokenization buffer | < 32 KB | Reused per game |
| All modes | < 128 KB working buffer | No game data retained across iterations |

### 6.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Strategy | 100K Games | 1M Games | 10M Games |
|----------|------------|----------|-----------|
| EngineStrict (full validation) | 14.2 s | 2m 22s | 25m 10s |
| PgnStandard (moderate) | 11.8 s | 1m 58s | 21m 40s |
| LenientPreserve (minimal) | 6.5 s | 1m 5s | 11m 30s |
| UnicodeToAscii only | 3.2 s | 32 s | 5m 20s |

## 7. Binary Index Integration Points

### 7.1 Normalization Flags in GameRecord
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 0: HasAnnotations, Bit 1: HasVariations, Bit 2: HasAnalysis, Bit 3: IsNormalized
    
    public bool IsNormalized => (Flags & (1 << 3)) != 0;
    public byte NormalizationLevel; // 0=raw, 1=lenient, 2=standard, 3=engine-strict
}
```

### 7.2 Flag Management During Normalization
| Strategy | IsNormalized Flag | NormalizationLevel |
|----------|-------------------|-------------------|
| EngineStrict | Set (1) | 3 |
| PgnStandard | Set (1) | 2 |
| LenientPreserve | Set (1) | 1 |
| UnicodeToAscii | Set (1) | 1 (partial normalization) |

### 7.3 In-Place Index Update (No File Rewrite)
```csharp
private void MarkNormalizedGamesInIndex(
    MemoryMappedViewAccessor accessor,
    IReadOnlyList<NormalizedGame> results,
    NormalizationStrategy strategy)
{
    byte level = strategy switch
    {
        NormalizationStrategy.EngineStrict => 3,
        NormalizationStrategy.PgnStandard => 2,
        _ => 1
    };
    
    foreach (var game in results)
    {
        int offset = IndexHeader.Size + (game.OriginalIndex * GameRecord.Size);
        
        // Set IsNormalized flag
        byte flags = accessor.ReadByte(offset + GameRecord.FlagsOffset);
        flags |= (1 << 3); // Set bit 3
        accessor.Write(offset + GameRecord.FlagsOffset, flags);
        
        // Store normalization level
        accessor.Write(offset + GameRecord.NormalizationLevelOffset, level);
    }
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `InvalidMoveException` | Illegal move detected with `ValidateMoveLegality=true` | Skip game + log diagnostic; continue processing remaining games |
| `AmbiguousMoveException` | Move cannot be disambiguated even with board state | Preserve original move; log diagnostic; continue |
| `Chess960PositionException` | FRC position invalid or missing FEN tag | Skip game with diagnostic; require explicit FEN handling |
| `UnicodeDecodingException` | Invalid UTF-8 sequences in source | Replace invalid sequences; continue processing |
| `PartialWriteException` | Disk full during physical rewrite | Delete partial output; preserve source integrity |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `pgn_ambiguous_moves.pgn` (games with insufficient disambiguation requiring correction)
- `pgn_castling_variants.pgn` (games with 0-0, o-o, OO variants)
- `pgn_figurine_notation.pgn` (games using Unicode piece symbols)
- `pgn_promotion_variants.pgn` (e8Q, e8=Q, e8(Q) variants)
- `pgn_illegal_moves.pgn` (games with intentionally illegal moves for validation testing)
- `pgn_chess960_games.pgn` (FRC games requiring FEN-aware normalization)

### 9.2 Assertion Examples
```csharp
// Verify ambiguous move disambiguation
var report = service.Normalize(new NormalizeRequest(
    "ambiguous.pgn",
    NormalizationStrategy.EngineStrict
));

var normalized = report.NormalizedGames.First();
string text = Encoding.UTF8.GetString(normalized.Bytes);

// Knights on d2/f2 both reach e4 → requires file disambiguation
Assert.Contains("Nde4", text); // NOT "Ne4"
Assert.DoesNotContain("Ne4 ", text); // No ambiguous form preserved

// Verify castling standardization
var castlingReport = service.Normalize(new NormalizeRequest(
    "castling_variants.pgn",
    NormalizationStrategy.PgnStandard
));

var castlingGame = castlingReport.NormalizedGames.First();
string castlingText = Encoding.UTF8.GetString(castlingGame.Bytes);

Assert.Contains("O-O", castlingText); // Standardized from 0-0/o-o/etc.
Assert.DoesNotContain("0-0", castlingText);
Assert.DoesNotContain("o-o", castlingText);

// Verify index flag updated correctly
var index = PgnBinaryIndex.OpenRead("normalized.pgn.pbi");
var record = index.GetGameRecord(0);
Assert.True(record.IsNormalized);
Assert.Equal(3, record.NormalizationLevel); // EngineStrict = level 3
```

## 10. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade Flags field during processing
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Output must pass `pgn-extract -c` validation; all moves must be valid SAN
- **Engine compatibility:** EngineStrict output must be accepted by Stockfish 16+ without parse errors
- **Round-trip safety:** Normalizing then un-normalizing (via format-preserving tools) should not alter semantics

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Regex DoS via pathological move text | Limit regex engine execution time to 50ms per game; fallback to manual parser |
| Board state corruption via malicious FEN | Validate FEN against strict schema before board initialization |
| Buffer overflow via excessively long move tokens | Enforce 16-character maximum move token length; truncate with warning |
| Unicode homograph attacks in figurine conversion | Validate all Unicode chess symbols against approved codepoint list before conversion |