# PlycountAdderService.md

## Service Implementation: PlycountAdderService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Add a `PlyCount` header to every game by counting SAN‑like tokens in move text.

## 2. Public API (Actual)

```csharp
public interface IPlycountAdderService
{
    Task AddPlyCountAsync(
        string inputFile,
        string outputFile,
        IProgress<double> progress,
        CancellationToken ct);
}
```

## 3. High-Level Pipeline (Actual)

1. Read input line‑by‑line.
2. Buffer headers and moves for a single game.
3. When a game ends, compute ply count and inject `[PlyCount "..."]`.
4. Write output to temp file and replace destination.

## 4. Ply Counting Rules

The counter:
- Skips comments, variations, NAGs, and results.
- Recognizes SAN‑like tokens (`e4`, `Nf3`, `O‑O`, etc.).

## 5. Progress Reporting

Progress is based on **bytes read** (rough estimate) and reported every ~250 ms.

## 6. Limitations

- Line‑based parsing (not a full PGN parser).
- Uses synchronous `ReplaceFile` at the end.
