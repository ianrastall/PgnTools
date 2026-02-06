# TwicDownloaderService.md

## Service Implementation: TwicDownloaderService

**Version:** Current implementation (updated 2026-02-06)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `HttpClient`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls. Avoid concurrent calls that target the same output path.

## 1. Objective

Download TWIC issues from the official ZIP archive endpoint and combine them into a single PGN file. The service also provides a lightweight "estimate + probe" workflow to identify the most recent issue number.

## 2. Public API (Actual)

```csharp
public interface ITwicDownloaderService
{
    Task<int> DownloadIssuesAsync(int start, int end, string outputFile, IProgress<string> status, CancellationToken ct);
}
```

### 2.1 Helper API (Static)
```csharp
public static int CalculateEstimatedLatestIssue(DateTime? today = null);
public static Task<int> ProbeLatestIssueAsync(int estimatedIssue, IProgress<string> status, CancellationToken ct);
```

### 2.2 Parameter Semantics
- `start` / `end`: Inclusive issue range. Must be positive. `end >= start`.
- `outputFile`: Destination PGN file path. Required.
- `status`: Emits short progress messages.
- `ct`: Cancels the download, extraction, or writing pipeline.
- **Return:** Number of issues successfully appended.

## 3. Constants & Endpoints (Actual)

- **Base URI:** `https://www.theweekinchess.com/zips/`
- **Issue file pattern:** `twic{N}g.zip` (games‑only ZIPs)
- **BufferSize:** `65536` (64 KB)
- **MinimumIssue:** `920` (used as a lower bound for estimation and UI defaults)
- **AnchorIssue:** `1628` published on **2026-01-19 (UTC)** for estimate stability

## 4. High-Level Pipeline (Actual)

1. Validate inputs and compute full output paths.
2. Create a temp output file and a unique temp folder for ZIP downloads.
3. For each issue in `[start, end]`:
   - Wait **2–4 seconds** between issues (polite rate limiting).
   - Download `twic{issue}g.zip` with up to **3 attempts**.
   - If **404**, report and continue to the next issue.
   - Extract the first PGN entry and append it to the combined output.
   - Delete the ZIP immediately to save disk space.
4. If at least one issue was appended, replace the destination file via `FileReplacementHelper.ReplaceFile`.
5. Always clean up temp artifacts in `finally`.

## 5. Latest Issue Detection (Actual)

### 5.1 Estimate
`CalculateEstimatedLatestIssue` uses the weekly cadence from an anchor point:

- Anchor: **TWIC 1628** on **2026-01-19 (UTC)**
- `estimated = AnchorIssue + floor((today - AnchorIssueDate) / 7 days)`
- Result is clamped to `>= MinimumIssue` (920)

### 5.2 Probe
`ProbeLatestIssueAsync` confirms the latest issue by HEAD‑requesting sequential ZIPs:

- Starts at `max(MinimumIssue, estimatedIssue - 3)`
- Sends `HEAD` requests to `twic{N}g.zip`
- Stops after **3 consecutive failures** (404 or other non‑success)
- Waits **~400 ms** between probes
- Manual redirect handling (max **5** redirects)
- If no success is confirmed in the probe window, it **falls back to the estimate** and reports that the result is unconfirmed.

## 6. ZIP Extraction & Append (Actual)

- Only ZIP archives are supported (the endpoint provides ZIP files).
- Entry selection:
  - Prefer `twic{issue}.pgn`
  - Otherwise, the first `.pgn` entry found
- Encoding: **Windows‑1252** with BOM detection (BOM overrides to UTF‑8/UTF‑16 when present).
- Leading blank lines in each PGN are skipped.
- **Two blank lines** are inserted between issues.

## 7. Error Handling & Reporting (Actual)

| Scenario | Behavior |
| --- | --- |
| Download failure (network/HTTP) | Reports status and continues with the next issue |
| HTTP 404 | Reports `"Issue #{N} not found"` and continues |
| ZIP processing error | Returns `false` for that issue; continues |
| No issues appended | Reports `"No issues were successfully downloaded."` and removes temp output |
| Cancellation | Reports `"Download cancelled."` and rethrows for the caller |
| Unexpected exception | Reports `"Error: <message>"`, cleans up, and rethrows |

## 8. Usage Example (ViewModel Context)

```csharp
var progress = new Progress<string>(message =>
{
    StatusMessage = message;
    StatusDetail = BuildProgressDetail();
});

var issuesWritten = await _twicDownloaderService.DownloadIssuesAsync(
    start,
    end,
    OutputFilePath,
    progress,
    _cancellationTokenSource.Token);
```

## 9. UI Integration Notes (Actual)

- The UI offers:
  - **Full download:** issues **920 → latest**
  - **Custom range:** user‑specified start/end
- Latest issue can be probed from the UI via `ProbeLatestIssueAsync`.
- Output file is chosen using the WinUI file picker.

## 10. Limitations (Current Implementation)

- **Single source:** Only `https://www.theweekinchess.com/zips/` is used (no mirrors or FTP).
- **ZIP only:** No `.pgn` or `.gz` handling.
- **No checksums:** Integrity is not verified.
- **No incremental cache:** Previously downloaded issues are not tracked.
- **No dedup/sorting:** Output preserves source order and content as-is.
- **Latest probing is heuristic:** It assumes sequential issue numbering and stops after 3 consecutive failures.
