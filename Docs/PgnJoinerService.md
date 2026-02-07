# PgnJoinerService.md

## Service Implementation: PgnJoinerService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Merge multiple PGN files into a single output file, preserving input order.

## 2. Public API (Actual)

```csharp
public interface IPgnJoinerService
{
    Task JoinFilesAsync(
        IEnumerable<string> sourceFiles,
        string destinationFile,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
```

## 3. High-Level Pipeline (Actual)

1. Validate input file list and output path.
2. Open a temp output file.
3. For each source file:
   - Read line‑by‑line (UTF‑8 with BOM detection).
   - Normalize leading blank lines.
   - Insert two blank lines between sources.
4. Replace destination file.

## 4. Progress Reporting

Progress is reported by source index (percent of files completed).

## 5. Limitations

- Line‑based merge (does not re‑parse PGN).
- Uses synchronous `ReplaceFile` at the end.
