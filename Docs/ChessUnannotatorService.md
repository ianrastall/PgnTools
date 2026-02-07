# ChessUnannotatorService.md

## Service Implementation: ChessUnannotatorService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Remove annotations from PGN move text: comments, variations, and NAGs.

## 2. Public API (Actual)

```csharp
public interface IChessUnannotatorService
{
    Task UnannotateAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate paths** and create a temp output file.
2. Stream games from input with `PgnReader`.
3. Strip annotations from `MoveText` using a custom scanner.
4. Write cleaned games to temp output.
5. Replace destination file with `FileReplacementHelper.ReplaceFile`.

## 4. Annotation Stripping Rules

The scanner removes:
- `{ ... }` brace comments  
- `;` line comments  
- Variations inside `(...)`  
- NAGs like `$1`, `$6`, etc.  

Whitespace is normalized to avoid repeated spaces.

## 5. Progress Reporting

Progress is reported every ~200 games (or 100 ms), with a simple `"Processing Game N..."` message.

## 6. Limitations

- Does not validate move legality.
- Uses synchronous `ReplaceFile` at the end.
