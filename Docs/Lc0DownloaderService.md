# Lc0DownloaderService.md

## Service Specification: Lc0DownloaderService

**Version:** 1.0 (Current Implementation)

**Layer:** Service Layer (Domain Logic)

**Dependencies:** `PgnReader`, `PgnWriter`, `HttpClient`, `HtmlAgilityPack`

**Thread Safety:** Stateless execution; `HttpClient` is shared static.

## 1. Objective

The `Lc0DownloaderService` automates the retrieval, processing, and normalization of Leela Chess Zero (Lc0) self-play training games. It scrapes match metadata from the training dashboard, retrieves the raw game files (PGN or Tar/GZ) from the storage backend, filters games based on user criteria (e.g., checkmates only, standard chess), and normalizes PGN headers to map training dates to specific Lc0 version numbers.

## 2. Input Contract

The service operates on a request-response pattern defined by `Lc0DownloadOptions` and `Lc0DownloadResult`.

### 2.1 Configuration (`Lc0DownloadOptions`)

```csharp
public sealed record Lc0DownloadOptions(
    string OutputFilePath,      // Destination for the consolidated PGN
    DateOnly ArchiveMonth,      // The specific month/year to scrape (e.g., "2024-06")
    bool ExcludeNonStandard,    // If true, filters out variants (e.g., "From Position" if not standard)
    bool OnlyCheckmates         // If true, keeps only games ending in checkmate (#) or Termination header
);

```

### 2.2 Result (`Lc0DownloadResult`)

```csharp
public sealed record Lc0DownloadResult(
    int TotalMatches,       // Number of match IDs found for the month
    int ProcessedMatches,   // Number of matches successfully downloaded and parsed
    int FailedMatches,      // Number of matches that 404'd or failed processing
    long GamesSeen,         // Total raw games iterated
    long GamesKept          // Total games written to output after filtering
);

```

### 2.3 Progress Reporting

The service reports progress via `IProgress<Lc0DownloadProgress>`, covering four phases:

1. **Scraping:** Iterating metadata pages.
2. **Downloading:** Fetching specific match files.
3. **Processing:** Decompressing and parsing PGNs.
4. **Completed:** Final summary.

## 3. Workflow & Algorithm Specification

The service executes in a linear pipeline: **Scrape Metadata** → **Iterate Matches** → **Download/Stream** → **Normalize/Write**.

### 3.1 Phase 1: Metadata Scraping

The service scrapes `https://training.lczero.org/matches/` to build a manifest of available matches for the requested month.

* **Pagination:** Iterates `?page=X` (up to `MaxScrapePages = 5000`).
* **Parsing:** Uses **HtmlAgilityPack** to parse the HTML table (`//tbody/tr` + `td` cells).
* **Termination:** Stops scraping when:
* `EmptyPageLimit` (2 consecutive empty pages) is reached.
* The scraped match dates predate the requested `ArchiveMonth`.


* **Sorting:** Matches are sorted by date (Oldest  Newest) for the parsing phase.

### 3.2 Phase 2: Heuristic URL Resolution

Because the Lc0 storage bucket structure varies between training runs, the service uses a heuristic approach to find the correct download URL for a match ID.

**Strategy:** Iterate through generated candidate URLs until a `200 OK` is received.

```csharp
private static IReadOnlyList<(Uri Url, Lc0FileKind FileKind)> BuildMatchUrls(int trainingRunId, int matchId)
{
    // 1. Run Segments: explicit run ID folder vs root
    // 2. Base Names: "12345" vs "match_12345"
    // 3. Suffixes: ".pgn" vs ".pgn.tar.gz"
    
    // Generates permutations like:
    // https://storage.lczero.org/files/match_pgns/1/match_12345.pgn.tar.gz
    // https://storage.lczero.org/files/match_pgns/match_12345.pgn
    // ...
}

```

### 3.3 Phase 3: Streaming Processing

To handle large archives without memory exhaustion, the service streams data directly from disk after a temp download.

1. **Download:** File is downloaded to `%TEMP%/lc0_match_{GUID}.{ext}`.
2. **Decompression:**
* If `.tar.gz`: Uses `GZipStream` wrapped in `TarReader`. Iterates entries looking for `.pgn` files.
* If `.pgn`: Streams directly.


3. **Parsing:** Uses `PgnReader` to iterate games asynchronously.

### 3.4 Phase 4: Normalization & Filtering

Every game passing the filters (`IsStandardVariant`, `IsCheckmateGame`) undergoes header normalization before being written.

**Version Mapping Logic:**
The service maps the match `Date` to a specific Lc0 version string using `Assets/lc0-version-map.json`, with a built-in default map if the file is missing or invalid.

| Date Range (Start) | Mapped Version |
| --- | --- |
| >= 2025-01-01 | v0.32.0 |
| >= 2024-06-01 | v0.31.0 |
| >= 2023-07-01 | v0.30.0 |
| ... | ... |
| Default (< 2018) | v0.23.0 |

**Header Updates:**

* `[Event]`: Set to `"Lc0 match {MatchId}"`.
* `[Date]`: Normalized to `yyyy-MM-dd`.
* `[White]`/`[Black]`: Set to `"Lc0 {Version}"`.

## 4. Error Handling & Resilience

| Failure Mode | Strategy |
| --- | --- |
| **HTTP 404** | The URL resolution loop tries the next candidate URL pattern. If all patterns fail, the match is recorded as `FailedMatches`. |
| **Network Failure** | `DownloadToTempAsync` implements a retry loop (`MaxRetries = 3`) with exponential backoff (2s * attempt). |
| **Corrupt Archive** | Exceptions during `TarReader` or `GZipStream` processing are caught; the specific match is marked failed, but the overall batch continues. |
| **Temp File Cleanup** | `finally` blocks ensure temporary files in `%TEMP%` are deleted regardless of success or failure. |
| **Rate Limiting** | A `RandomJitter` (200-450ms) is applied between match downloads to be a polite scraper. |

## 5. Key Constraints & Constants

* **BufferSize:** 64KB (`65536` bytes) for file streams.
* **MaxScrapePages:** 5000 (Safety limit to prevent infinite loops).
* **Base URIs:**
* Scraping: `https://training.lczero.org/matches/`
* Storage: `https://storage.lczero.org/files/match_pgns/`



## 6. Usage Example (ViewModel Context)

```csharp
// Initialize service
var service = new Lc0DownloaderService(new PgnReader(), new PgnWriter());

// Configure request
var options = new Lc0DownloadOptions(
    OutputFilePath: @"C:\Chess\Lc0_2024_01.pgn",
    ArchiveMonth: new DateOnly(2024, 1, 1),
    ExcludeNonStandard: true,
    OnlyCheckmates: false
);

// Execute
var result = await service.DownloadAndProcessAsync(options, progressReporter, cancellationToken);

if (result.FailedMatches > 0)
{
    Log($"Warning: {result.FailedMatches} matches could not be retrieved.");
}
