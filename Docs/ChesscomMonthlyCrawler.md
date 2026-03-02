# ChesscomMonthlyCrawler.md

## Tool Implementation: Chess.com Monthly Crawler (UI + Service + Script)

**Version:** Current implementation (updated 2026-02-08)  
**Layer:** UI + Service Layer + Scripts  
**Dependencies:** `HttpClient`, file I/O  
**Thread Safety:** Safe for concurrent calls. Avoid concurrent runs targeting the same output or list files.

## 1. Objective

Crawl a specific month across a seed list of Chess.com players, keep only games where **both** players meet a minimum Elo threshold, optionally exclude bullet games, and continuously expand the seed list with newly discovered opponents.

The output is a single PGN file for the target month. Two sidecar lists are maintained:

- **Seed list**: the input and ongoing growth list of players.
- **Processed list**: the players already crawled for the selected month.

## 2. User-Facing Inputs (UI)

The Monthly Crawl tab exposes:

- **Target Month (Year + Month)**  
  Default is the previous month. Example: if today is **February 8, 2026**, the default is **January 2026**.
- **Minimum Elo (both players)**  
  A game is kept only if **both** players meet or exceed this threshold.
- **Exclude bullet games**  
  When enabled, games classified by Chess.com as bullet are skipped.
- **Seed List (TXT)**  
  One username per line. Lines are trimmed and normalized to lowercase in memory.
- **Processed List (TXT)**  
  Automatically appended as the crawl runs.
- **Output File (PGN)**  
  The crawler **appends** to this file if it already exists.
- **Log File (optional)**  
  Enabled by the **Write detailed log file** toggle; writes a detailed run log (player results, errors, discoveries).

Suggested filenames:

- `chesscom-{MinElo}-{yyyy-MM}.pgn`
- `chesscom-processed-{MinElo}-{yyyy-MM}.txt`
- `chesscom-crawl-{MinElo}-{yyyy-MM}.log`

## 3. Public API (Actual)

```csharp
public interface IChesscomMonthlyDownloaderService
{
    Task<ChesscomMonthlyCrawlResult> CrawlMonthAsync(
        ChesscomMonthlyCrawlOptions options,
        IProgress<ChesscomMonthlyCrawlProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record ChesscomMonthlyCrawlOptions(
    DateOnly TargetMonth,
    int MinElo,
    string SeedFilePath,
    string ProcessedFilePath,
    string OutputFilePath,
    string? LogFilePath = null,
    bool ExcludeBullet = false);

public sealed record ChesscomMonthlyCrawlProgress(
    string Message,
    int PlayersProcessed,
    int PlayersTotal,
    long GamesSaved,
    int NewPlayers,
    int FailedPlayers,
    int SeedSize);

public sealed record ChesscomMonthlyCrawlResult(
    int PlayersProcessed,
    int PlayersTotal,
    int FailedPlayers,
    long GamesSaved,
    int NewPlayers,
    int SeedSize);
```

## 4. Data Sources & Endpoints

- **Monthly games JSON endpoint**  
  `https://api.chess.com/pub/player/{username}/games/{year}/{month:00}`

The crawler reads the `pgn`, `url`, `uuid`, `time_class`, and player/rating fields from each game entry in the response.

## 5. High-Level Pipeline (Actual)

1. **Load seed list** (`SeedFilePath`) into a case-insensitive set.  
   The seed list **must exist**.
2. **Load processed list** (`ProcessedFilePath`) into another set (optional file).
3. **Build queue** = `seed - processed`, sorted alphabetically.
4. For each player in the queue:
   1. Fetch monthly games JSON for the selected month.
   2. For each game:
      - **Keep** if both white and black ratings meet the minimum Elo.
      - **Skip** if bullet filtering is enabled and the game is classified as bullet.
      - **Append PGN** to output file only if the game has not already been written.
      - **Discover opponent** and append to seed list if new.
   3. **Mark player processed** (always, even if the fetch failed).
5. Flush output periodically and report progress after each player.

## 6. Queue & Discovery Semantics

- The crawl queue starts as a snapshot, but newly discovered opponents are **enqueued immediately** and processed in the same run.
- This means long runs can extend as new players are found.
- Usernames are normalized to **lowercase** in memory.
- The seed list file can grow indefinitely; duplicates are not written.

## 7. Elo Filter Semantics

A game is saved only if:

- `white.rating >= MinElo` **and**
- `black.rating >= MinElo`

Opponents are discovered **only** from games that pass this filter and have a non-empty PGN payload.

## 8. Output Write Strategy (Actual)

- **Append-only**: The output PGN is opened in append mode and new games are added to the end.
- **Per-game dedup**: existing output is scanned for Chess.com game ids first, and duplicate games are skipped during the crawl.
- **Separators**: A blank line is inserted between games.
- **Encoding**: UTF‑8 (no BOM).
- **Flush cadence**: output is flushed at each player boundary (and when batches exceed ~50 games).

This allows long crawls without holding PGNs in memory.

## 9. Processed List Semantics

Every player is appended to the processed list **even if their fetch fails**.  
If a crawl is cancelled mid-player, that player is **not** marked as processed.  
To retry a player that failed, remove their username from the processed list manually.

## 10. Rate Limiting & Retry Logic (Actual)

- **Polite delay**: 800–1400 ms between requests (random jitter).
- **HTTP 429**: Honors `Retry-After` if provided; otherwise waits 65 seconds, with a capped retry count.
- **404 / 410**: Treated as “no games for this player/month.”
- **Other errors**: Up to **3** attempts if the status is retryable (timeout / 5xx).

## 11. Progress Reporting (Actual)

Progress updates fire **after each player**, with messages like:

- `playername: 4 games | +2 new players`
- `playername: no games`
- `playername: error`

The status detail includes:

- players processed vs total
- games saved
- new players discovered
- seed size (unique usernames)
- pending players remaining
- failed players
- elapsed time / ETA (from `BaseViewModel.BuildProgressDetail`)

## 12. Logging (Actual)

When a log file is provided, the crawler appends a detailed, timestamped log (UTF‑8, no BOM).
The log includes:

- run header (target month, min Elo, paths, seed/queue sizes)
- per‑player results (games, new players, failures)
- discovered player names
- completion or cancellation summary

Example lines:

- `[2026-02-08 03:42:11 -05:00] PLAYER done: magnuscarlsen games=12 newPlayers=3`
- `[2026-02-08 03:42:11 -05:00] DISCOVERED alirezafirouzja`

## 13. Cancellation & UI Wiring

The ViewModel:

- Runs the service behind a **semaphore lock** to prevent parallel starts.
- Supports **Cancel** via a `CancellationTokenSource`.
- Validates that seed, processed, and output files are **distinct** paths.
- Suggests output/processed filenames based on `MinElo` + `TargetMonth`.

## 14. File Locking & Concurrency

The crawler acquires lock files alongside the three target files:

- `{SeedFilePath}.lock`
- `{ProcessedFilePath}.lock`
- `{OutputFilePath}.lock`
- `{LogFilePath}.lock` (when logging is enabled)

If another process already holds any of these locks, the crawl fails fast with a clear error.

## 15. PowerShell Combine Script

File: `Scripts/combine-chesscom-monthly.ps1`

**Purpose:** Merge multiple monthly crawler outputs into one PGN file.

Key behaviors:

- Streams data to avoid loading large PGNs in memory.
- Sorts inputs by the `yyyy-MM` token in the filename (default).
- Inserts a blank line between files.
- Strips UTF‑8 BOMs from input files to avoid mid‑file BOMs.

### 15.1 Usage

```powershell
# Combine all chesscom-*.pgn files in a folder
.\Scripts\combine-chesscom-monthly.ps1 -InputFolder "D:\Chess\Chesscom\Monthly" `
    -Pattern "chesscom-*.pgn" `
    -OutputPath "D:\Chess\Combined\chesscom-monthly.pgn"

# Include subfolders and sort by filename instead of month token
.\Scripts\combine-chesscom-monthly.ps1 -InputFolder "D:\Chess\Chesscom" `
    -Pattern "chesscom-*.pgn" `
    -Recurse `
    -Sort Name `
    -OutputPath "D:\Chess\Combined\chesscom-monthly.pgn"
```

### 15.2 Parameters

- `InputFolder`  
  Root folder to search. Defaults to repo root.
- `Pattern`  
  File glob. Defaults to `chesscom-*.pgn`.
- `OutputPath`  
  Combined PGN output path.
- `Sort`  
  `Month` (default) or `Name`.
- `Recurse`  
  Include subfolders.

## 16. Limitations & Gotchas

- Existing duplicates already present in the output file are **not rewritten or cleaned up** during a crawl.  
  The crawler only avoids appending new duplicates.
- **Processed list is sticky**: failures are still marked as processed.
- **No non-standard variant filtering**: aside from the optional bullet filter, other Chess.com variants are not excluded.
