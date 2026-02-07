# PgnMentorDownloaderService.md

## Service Implementation: PgnMentorDownloaderService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `HttpClient`, `FileReplacementHelper`, `System.IO.Compression`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Download all PGN Mentor archives listed on the public file index and combine them into a single PGN file.

## 2. Public API (Actual)

```csharp
public interface IPgnMentorDownloaderService
{
    Task DownloadAndCombineAsync(string outputFile, IProgress<string> status, CancellationToken ct);
}
```

## 3. High-Level Pipeline (Actual)

1. **Fetch file list** from `https://www.pgnmentor.com/files.html`.
2. **Extract links** for `.zip` and `.pgn` via regex.
3. **Download + append** each file in site order:
   - `.zip`: append all `.pgn` entries
   - `.pgn`: append file contents
4. **Replace output** via `FileReplacementHelper.ReplaceFileAsync`.
5. **Cleanup** temp downloads and temp output file.

## 4. Rate Limiting & Retries

- Random delay **2–4 seconds** between downloads.
- Up to **3 attempts** per file with exponential backoff.

## 5. Output Behavior

- Output preserves the site order (no sorting).
- Two blank lines are inserted between sources.
- UTF‑8 output (no BOM).

## 6. Limitations

- No category filtering or deduplication.
- Link extraction only covers `.zip` and `.pgn` on the file list page.
