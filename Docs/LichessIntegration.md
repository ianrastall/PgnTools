# LichessIntegration.md

## Tool Implementation: Lichess

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `HttpClient`, `PgnReader`, `PgnWriter`, `ZstdSharp`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Provide two Lichess workflows:
1. Download **user games** from the Lichess API.
2. Download and **filter monthly database archives** (`.pgn.zst`).

## 2. Public APIs (Actual)

```csharp
public interface ILichessDownloaderService
{
    Task DownloadUserGamesAsync(
        string username,
        string outputFile,
        int? max,
        IProgress<LichessDownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public interface ILichessDbDownloaderService
{
    Task DownloadAndFilterAsync(
        string url,
        string outputPath,
        int minElo,
        bool excludeBullet,
        bool excludeNonStandard,
        bool onlyCheckmates,
        IProgress<LichessDbProgress>? progress = null,
        CancellationToken ct = default);
}
```

## 3. User Games Download (Actual)

### Pipeline
1. Build URL: `https://lichess.org/api/games/user/{username}?max=...`
2. Stream response directly to a temp file.
3. Replace output via `FileReplacementHelper.ReplaceFile`.

### Progress
Reports bytes read, total bytes (if known), and elapsed time:
```csharp
public sealed record LichessDownloadProgress(long BytesRead, long? TotalBytes, TimeSpan Elapsed);
```

## 4. Monthly Database Download + Filter (Actual)

### Pipeline
1. Validate URL and ensure disk space (~5 GB minimum).
2. Stream and **decompress Zstandard** (`.pgn.zst`).
3. Filter games by:
   - Minimum Elo (either player)
   - Exclude bullet (<180 seconds)
   - Exclude non‑standard variants
   - Only checkmates
4. Write filtered games to temp output and replace final file.

### Progress
Reports bytes read, games seen/kept, elapsed time, and stage:
```csharp
public enum LichessDbProgressStage { Downloading, Filtering, Completed }
public sealed record LichessDbProgress(
    LichessDbProgressStage Stage,
    long BytesRead,
    long? TotalBytes,
    long GamesSeen,
    long GamesKept,
    TimeSpan Elapsed);
```

## 5. Limitations

- User downloads rely on Lichess API availability.
- Database filtering requires large disk space and CPU for decompression.
- No resume support for partial downloads.
