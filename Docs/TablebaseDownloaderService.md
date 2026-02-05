<!-- PGNTOOLS-TABLEBASES-BEGIN -->
### `Docs/TablebaseDownloaderService.md`

```markdown
# TablebaseDownloaderService.md

## Service Specification: TablebaseDownloaderService
**Version:** 1.0 (Actual Implementation)
**Layer:** Service Layer (Infrastructure/Network)
**Dependencies:** `HttpClient`, `TablebaseConstants`
**Thread Safety:** Stateless; safe for concurrent execution across different categories.

## 1. Objective
Reliably download and organize Syzygy endgame tablebase files (WDL and DTZ) via HTTP. The service manages the retrieval of massive datasets (split into 3-4-5, 6, and 7-piece categories) by processing a predefined manifest of URLs. It validates downloads against `Content-Length` and temp-file size, performs a best-effort disk space check, and ensures file consistency using a temporary-file-and-swap strategy.

## 2. Input Contract

The service exposes a single primary entry point designed to handle an entire category of tablebases at once.

```csharp
public interface ITablebaseDownloaderService
{
    /// <summary>
    /// Downloads all files associated with a specific tablebase category.
    /// </summary>
    /// <param name="category">The subset of tablebases to download (Syzygy345, Syzygy6, or Syzygy7).</param>
    /// <param name="rootOutputPath">The root directory where category subfolders will be created.</param>
    /// <param name="progress">Optional reporter for download progress (speed, bytes, file count).</param>
    /// <param name="ct">Token to cancel the operation gracefully.</param>
    Task DownloadCategoryAsync(
        TablebaseCategory category,
        string rootOutputPath,
        IProgress<TablebaseProgress>? progress = null,
        CancellationToken ct = default);
}

```

### 2.1 Progress Reporting

Progress is reported via the `TablebaseProgress` record, providing granular feedback for UI updates:

* **Category:** The set currently being processed.
* **CurrentFileName:** Name of the file currently downloading.
* **Counts:** `FilesCompleted`, `FilesSkipped`, and `TotalFiles`.
* **Data Transfer:** `BytesRead` vs. `TotalBytes` (for the current file).
* **Throughput:** `SpeedMbPerSecond` (calculated over 250ms intervals).
* **Stage:** `TablebaseProgressStage` indicating whether the file is downloading, already present, skipped due to lock, or completed.

## 3. Architecture & Data Source

### 3.1 The Manifest System

Unlike dynamic scrapers, this service relies on a static "Source of Truth" located in `Assets/Tablebases/download.txt`.

* **Format:** A flat text file containing direct HTTP URLs to `.rtbw` (WDL) and `.rtbz` (DTZ) files.
* **Parsing Logic:** URLs are categorized at runtime by inspecting the string content (see `TablebaseConstants.cs`):
* **Syzygy345:** URLs containing `/3-4-5-`
* **Syzygy6:** URLs containing `/6-`
* **Syzygy7:** URLs containing `/7/`

### 3.3 HTTP Configuration
The `HttpClient` used by this service is configured with an infinite timeout. Long-running downloads are controlled via the `CancellationToken`.
* **Location Resolution:** The manifest is resolved relative to `AppContext.BaseDirectory`. If not found there (e.g., packaged scenarios), the service falls back to the installed package location.


### 3.2 File Organization

The service enforces a strict directory structure within the user-selected `rootOutputPath`. Subfolders are automatically created based on the category:

| Enum Value | Subfolder Name | Estimated Size |
| --- | --- | --- |
| `TablebaseCategory.Syzygy345` | `3-4-5` | ~1.5 GB |
| `TablebaseCategory.Syzygy6` | `6` | ~160 GB |
| `TablebaseCategory.Syzygy7` | `7` | ~18 TB |

## 4. Algorithm Specification

### 4.1 Pre-Flight Validation

Before initiating network traffic, the service performs a safety check to prevent storage overfill:

1. **Requirement Calculation:** Retrieves the hardcoded estimated size for the requested category (`TablebaseConstants`) and subtracts any existing local bytes for files in the manifest.
2. **Space Check:** Queries `DriveInfo` for the destination root (best effort; skipped if the root cannot be resolved).
3. **Gatekeeping:** If `AvailableFreeSpace < RequiredBytes`, an `IOException` is thrown immediately, aborting the process before any partial files are created.

### 4.2 The Download Loop

For every URL in the category's list, the service executes the following state machine:

1. **Existence Check:**
* Target path: `root/{category}/{filename}`.
* If the file exists **AND** matches the remote size (Head Request check), it is marked as "Complete" and skipped.
* If the file exists **AND** is currently locked (in use), it is skipped and reported as such.


2. **Staging (Temp File Strategy):**
* Downloads are never written directly to the final filename to prevent corruption during interruptions.
* Data is streamed to `path.tmp` (using `FileReplacementHelper.CreateTempFilePath`).
* **Buffer Management:** Uses an 80KB buffer (`81920` bytes) pooled via `ArrayPool<byte>` to minimize garbage collection (GC) pressure.


3. **Streaming & Throttling:**
* Uses `HttpCompletionOption.ResponseHeadersRead` to start processing immediately.
* Updates the `IProgress` reporter every 250ms (`ProgressInterval`) to ensure UI responsiveness without flooding the event queue.


4. **Commit (Atomic Swap):**
* Once the download stream closes successfully, the service validates the temp file size (bytes read and `Content-Length`) and attempts to replace the destination file with the temp file.
* **Resilience:** If the destination is locked, the file is skipped and reported as such. Other replacement failures throw.



### 4.3 Validation Logic (Head Check)

Instead of expensive SHA checksums, the service uses `Content-Length` validation to verify integrity for existing files:

```csharp
// Pseudo-logic for IsExistingFileCompleteAsync
var remoteSize = await httpClient.SendAsync(HeadRequest).Content.Length;
var localSize = new FileInfo(localPath).Length;
return localSize == remoteSize;

```

*Note: If the remote server does not return a Content-Length header, the file is assumed incomplete and re-downloaded.*

## 5. Edge Cases & Error Handling

| Scenario | Handling Strategy |
| --- | --- |
| **Disk Full (Pre-download)** | Throws `IOException` explicitly stating required vs. available space. |
| **Disk Full (Mid-stream)** | Standard I/O exception bubbles up; temp file deletion is attempted in the catch block. |
| **Network Failure** | `HttpRequestException` bubbles up. Partial temp file is deleted in the `catch` block to prevent junk buildup. |
| **File Locked (Destination)** | The file is skipped and reported as "in use" (not counted as completed). |
| **Manifest Missing** | Throws `InvalidOperationException` if `download.txt` is not found or empty for the category. |
| **Cancellation** | `CancellationToken` is checked at every buffer read. Upon cancellation, the `catch` block ensures the temp file is deleted. |

## 6. Performance Characteristics

### 6.1 Throughput

* **Sequential Processing:** Files within a category are downloaded one by one (`foreach` loop). This limits throughput to the speed of a single TCP connection but ensures maximum stability and friendliness to the mirror server.
* **Memory Footprint:** Extremely low. Uses streaming I/O with a shared buffer pool. Does not load whole files into RAM.

### 6.2 Benchmarks (Estimates)

* **3-4-5 Piece Set:** ~40-60 seconds on Gigabit fiber.
* **6 Piece Set:** ~20-30 minutes on Gigabit fiber.
* **Resumption:** If the process is restarted, the "Head Check" ensures previously completed files are skipped instantly, allowing the download to "resume" at the file level (though individual partial files start over).

## 7. Configuration & Extensibility

### 7.1 Adding New Tables

To add mirrors or new tablebases, modify `PgnTools/Assets/Tablebases/download.txt`.

* Lines starting with `#` are ignored (comments).
* Any valid URL can be added.
* The system automatically routes the URL to the 3-4-5, 6, or 7 folder based on the substring pattern matching defined in `TablebaseConstants`.

### 7.2 Future Improvements (Not Implemented)

* **Resume Support:** Use HTTP Range headers to resume partial temp files.
* **Parallelism:** Use `Parallel.ForEachAsync` to saturate bandwidth (currently sequential).
* **Mirror Rotation:** Logic to switch domains if one fails (currently hardcoded to the specific URL in the text file).

<!-- PGNTOOLS-TABLEBASES-END -->
