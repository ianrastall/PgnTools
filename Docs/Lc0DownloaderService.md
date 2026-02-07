# Lc0DownloaderService.md

## Service Implementation: Lc0DownloaderService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `HtmlAgilityPack`, `HttpClient`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Download and collate Lc0 match PGNs for a specific month, with optional filtering.

## 2. Public API (Actual)

```csharp
public sealed record Lc0DownloadOptions(
    string OutputFilePath,
    DateOnly ArchiveMonth,
    bool ExcludeNonStandard,
    bool OnlyCheckmates);

public interface ILc0DownloaderService
{
    Task<Lc0DownloadResult> DownloadAndProcessAsync(
        Lc0DownloadOptions options,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Scrape matches** from `https://training.lczero.org/matches/` (paginated).
2. **Filter by month** (UTC date range).
3. **Download match PGNs** from `https://storage.lczero.org/files/match_pgns/`:
   - Tries `.pgn` and `.pgn.tar.gz` patterns.
4. **Process games**:
   - Optional filters: exclude non‑standard variants, only checkmates.
   - Normalize headers: `Event`, `Date`, `White`, `Black`.
5. **Write combined PGN** to temp output and replace destination.

## 4. Progress & Results

Progress includes phases:
- `Scraping`
- `Downloading`
- `Processing`
- `Completed`

Result includes totals: matches processed, failed, games seen, games kept.

## 5. Version Mapping

Lc0 version labels are derived from a cutoff table:
- `Assets/lc0-version-map.json` if present
- Built‑in defaults otherwise

## 6. Limitations

- Scrape depth is capped (max 5000 pages).
- Only match PGNs from the official training site are supported.
- No resume for partial downloads.
