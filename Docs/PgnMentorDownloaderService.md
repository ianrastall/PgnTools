# PgnMentorDownloaderService.md

## Service Implementation: PgnMentorDownloaderService

**Version:** Current implementation (updated 2026-02-05)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `HttpClient`, `FileReplacementHelper`  
**Thread Safety:** Not safe for concurrent calls (static `Random` for jitter; shared `HttpClient` is fine, but jitter is not thread-safe).

## 1. Objective

Download all PGN Mentor archives referenced on the public file listing page, extract PGNs, and combine them into a single output file. The combined file is written in the same order the site provides.

## 2. Public API (Actual)

```csharp
public interface IPgnMentorDownloaderService
{
    Task DownloadAndCombineAsync(string outputFile, IProgress<string> status, CancellationToken ct);
}
```

### 2.1 Parameter Semantics
- `outputFile`: Destination PGN file path. Required.
- `status`: Status messages (phase + current file).
- `ct`: Cancels download, extraction, or writing.

## 3. High-Level Pipeline (Actual)
1. **Validate output path** and compute full paths.
2. **Fetch file list** from `https://www.pgnmentor.com/files.html`.
3. **Extract file links** for `.zip` and `.pgn` using regex.
4. **Download + append** each file in site order.
5. **Replace final output** using `FileReplacementHelper.ReplaceFile`.
6. **Cleanup** temp folder and temp output in `finally`.

## 4. Workflow Details

### 4.1 Link Discovery
- URL: `https://www.pgnmentor.com/files.html`
- Regex: `href="...(.zip|.pgn)"`
- Links are de‑duplicated with a case‑insensitive set and returned **in site order**.

### 4.2 Download Loop
For each link:
- Wait **2–4 seconds** between downloads (random jitter).
- Download to a temp folder via `DownloadWithRetryAsync`:
  - Up to 3 attempts.
  - Exponential backoff: 2s, 4s.
  - On failure after 3 attempts, exception is caught in the loop and logged via `status`.

### 4.3 Append Strategy
- Output is written to a **single temp file** with `StreamWriter` (UTF‑8, no BOM).
- **`.zip`**: Each `.pgn` entry is streamed and appended.
- **`.pgn`**: File contents are streamed and appended.
- Two blank lines are inserted between appended sources.
- **No sorting** or de‑duplication is performed. Output order is the website order.

### 4.4 Completion Rules
- If **no valid PGN content** is appended, the method reports `"No valid PGN data collected."` and exits without replacing the destination file.
- If content exists, the temp output replaces the destination file atomically.

## 5. Error Handling & Cleanup

| Scenario | Behavior |
| --- | --- |
| Download failure | Status reports `"Failed <file>"`, continues with next link |
| Extraction error | Returns false for that file; continues |
| Cancellation | Throws `OperationCanceledException`, cleanup runs |
| Temp cleanup | Always deletes temp folder and temp output file in `finally` |

## 6. Key Constraints & Constants

- **BufferSize:** 64KB (`65536`)
- **BaseUri:** `https://www.pgnmentor.com/`
- **FilesUri:** `https://www.pgnmentor.com/files.html`
- **Rate Limit:** 2–4 seconds between downloads

## 7. Usage Example (ViewModel Context)

```csharp
var progress = new Progress<string>(message =>
{
    StatusMessage = message;
    StatusDetail = BuildProgressDetail();
});

await _pgnMentorDownloaderService.DownloadAndCombineAsync(
    OutputFilePath,
    progress,
    _cancellationTokenSource.Token);
```

## 8. Limitations (Current Implementation)
- Output is **not sorted** or normalized.
- Order is **site order** only; no filtering by category or source.
- Regex link extraction only supports `.zip` and `.pgn` links on the file list page.
