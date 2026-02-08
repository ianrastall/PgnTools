# ChesscomDownloaderService.md

## Service Implementation: ChesscomDownloaderService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `HttpClient`  
**Thread Safety:** Safe for concurrent calls.

## 1. Objective

Download all Chess.com monthly archives for a user and combine them into a single PGN file.

## 2. Public API (Actual)

```csharp
public interface IChesscomDownloaderService
{
    Task<List<string>> GetArchivesAsync(string username, CancellationToken ct = default);
    Task<string> DownloadPlayerGamesPgnAsync(string username, int year, int month, CancellationToken ct = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Fetch archives list** from `https://api.chess.com/pub/player/{username}/games/archives` (username URL‑encoded).
2. **Iterate archives** in returned order.
3. **Download PGN** for each `year/month` from:
   - `https://api.chess.com/pub/player/{username}/games/{year}/{month}/pgn`
4. **Append** each monthly PGN to a temp output file with a single blank‑line separator.
5. **Replace output** via `FileReplacementHelper.ReplaceFileAsync`.

## 4. Rate Limiting

Each request is delayed by **800–1400 ms** to be polite to the API (jitter uses `Random.Shared`).

## 5. UI Integration Notes (Actual)

The ViewModel:
- Parses archive URLs into `(year, month)`.
- Writes to a unique temp file and replaces on success.
- Reports progress by archive count.

## 6. Limitations

- No retry logic per archive download (failures are counted and skipped).
- Output order matches the Chess.com API archive order.
