# TablebaseDownloaderService.md

## Service Implementation: TablebaseDownloaderService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `HttpClient`, `FileReplacementHelper`, `TablebaseConstants`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Download Syzygy tablebase files for a chosen category, with disk‑space checks and resume‑aware skipping.

## 2. Public API (Actual)

```csharp
public interface ITablebaseDownloaderService
{
    Task DownloadCategoryAsync(
        TablebaseCategory category,
        string rootOutputPath,
        IProgress<TablebaseProgress>? progress = null,
        CancellationToken ct = default);
}
```

## 3. High-Level Pipeline (Actual)

1. Resolve URL list from `Assets/Tablebases/download.txt` (via `TablebaseConstants`).
2. Estimate required disk space and validate availability.
3. For each file:
   - Skip if already present with matching size.
   - Skip if the target file is locked.
   - Download to temp file, report progress and speed.
   - Validate size and replace destination.

## 4. Progress Model

```csharp
public record TablebaseProgress(
    TablebaseCategory Category,
    string CurrentFileName,
    int FilesCompleted,
    int TotalFiles,
    long BytesRead,
    long? TotalBytes,
    double SpeedMbPerSecond,
    int FilesSkipped = 0,
    TablebaseProgressStage Stage = TablebaseProgressStage.Downloading);
```

Stages include: `Starting`, `AlreadyPresent`, `SkippedLocked`, `Downloading`, `Completed`, `Failed`.

## 5. Limitations

- No parallel downloads.
- Requires valid URL list asset.
