# EcoTaggerService.md

## Service Implementation: EcoTaggerService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls (trie cache is protected by a semaphore).

## 1. Objective

Tag games with `ECO`, `Opening`, and `Variation` by matching move sequences against a reference ECO PGN.

## 2. Public API (Actual)

```csharp
public interface IEcoTaggerService
{
    Task TagEcoAsync(
        string inputFilePath,
        string outputFilePath,
        string ecoReferenceFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate paths** and resolve the ECO reference file path.
2. **Build/Reuse ECO trie** from the reference PGN.
3. **Stream input games**, find the deepest move‑sequence match in the trie.
4. **Apply tags** (`ECO`, `Opening`, `Variation`) when a match exists.
5. **Write to temp** and replace output file.

## 4. ECO Reference Resolution

If `ecoReferenceFilePath` is relative, the service searches:
1. `AppContext.BaseDirectory/Assets/<path>`
2. `AppContext.BaseDirectory/<path>`
3. `Path.GetFullPath(<path>)`

## 5. Matching Rules

- Move text is tokenized with comment/variation/NAG removal.
- Matches are **prefix‑based**; the deepest node with ECO data wins.

## 6. Progress Reporting

Progress is reported by input stream position (percent of bytes), throttled by:
- Every 200 games, or
- At least every 100 ms.

## 7. Limitations

- Requires a valid ECO reference PGN.
- Matching is SAN‑token based; no legality checking.
