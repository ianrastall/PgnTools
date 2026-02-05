# Lichess Integration: Technical Reference

**Namespace:** `PgnTools.Services`

This document details the internal architecture of the Lichess integration services. Unlike the high-level functional summaries, this reference focuses on the "under-the-hood" mechanics of streaming pipelines, byte-level processing, and memory management.

---

## 1. Service: `LichessDbDownloaderService`

This service implements a **Zero-Intermediate-Storage** pipeline. It transforms a massive, compressed web stream (Zstandard) into a filtered, uncompressed local PGN file without ever extracting the full dataset to a temporary location.

### 1.1 The Streaming Pipeline

The core of this service is a nested stream architecture designed to process gigabytes of data with a constant, low memory footprint.

**Data Flow Diagram:**

```text
[Lichess Server] 
      ↓ (HTTP Response / Network Stream)
[CountingStream] <Internal Wrapper>
      ↓ (Tracks raw compressed bytes read)
[ZstdSharp.DecompressionStream]
      ↓ (Decompresses chunks on-the-fly)
[PgnReader]
      ↓ (Parses text into PgnGame objects)
[Filter Logic]
      ↓ (Evaluates Game Headers/Moves)
[PgnWriter] → [StreamWriter] → [FileStream] → [Disk]

```

### 1.2 Critical Implementation Details

#### A. Network Optimization

The service utilizes `HttpClient.GetAsync` with `HttpCompletionOption.ResponseHeadersRead`.

* **Why:** Default behavior buffers the entire response content into memory. For 30GB+ files, this would cause an immediate `OutOfMemoryException`.
* **Effect:** Returns control as soon as headers are received, allowing the body to be consumed as a stream.

#### B. The `CountingStream` Wrapper

A private internal class wraps the raw network stream to provide accurate progress reporting for compressed data.

* **Role:** The standard `DecompressionStream` does not expose the number of compressed bytes consumed. `CountingStream` intercepts `Read()` and `ReadAsync()` calls to increment a `BytesRead` counter.
* **Usage:** This counter is the "Numerator" in the progress bar calculation (Bytes Read / Total Content Length).

#### C. Memory Management

* **Buffer Size:** Fixed at `65536` bytes (64KB) for file operations.
* **Encoding:** Uses `UTF8Encoding(false)` (UTF-8 without BOM) to match standard PGN specifications.
* **Object Cycling:** `PgnReader` yields `PgnGame` objects one at a time. Once a game is processed (written or discarded), it falls out of scope, making it eligible for Gen0 garbage collection immediately. This prevents heap fragmentation during long-running downloads.

### 1.3 Filtering Algorithms (Deep Dive)

Filtering occurs post-parse but pre-write. This ensures that rejected games consume zero disk space on the target drive.

#### 1. Elo Filtering

* **Logic:** Determines if a game meets the quality threshold.
* **Condition:** `if (whiteElo < minElo && blackElo < minElo) return false;`
* **Nuance:** This is an "OR" retention policy. If *either* player is above the threshold, the game is kept. A game is only rejected if *both* players are below the standard.
* **Parsing:** Uses `int.TryParse` on headers `WhiteElo` and `BlackElo`. Missing or non-integer ratings are treated as *missing* and do not force rejection.

#### 2. Bullet Game Detection

* **Logic:** Identifies games played at very fast time controls.
* **Mechanism:** Parses the `TimeControl` header (format: `seconds+increment`).
* **Algorithm:** `TryParseLeadingInt` extracts the initial seconds.
* **Threshold:** If `seconds < 180` (3 minutes), it is classified as Bullet and rejected if `excludeBullet` is true.

#### 3. Variant Normalization

* **Logic:** Ensures compatibility with engines that only support standard chess.
* **Allowed Values:** "Standard", "Chess", or "From Position" (case-insensitive).
* **Rejection:** Any other string (e.g., "Atomic", "Crazyhouse", "Chess960") triggers exclusion.

#### 4. Checkmate Validation

* **Fast Check:** Looks for "checkmate" in the `Termination` header.
* **Slow Check:** If the header is missing, it scans the `MoveText` string for the `#` character.
* **Optimization:** The check stops as soon as a match is found to avoid unnecessary string scanning.

### 1.4 Safety Mechanisms

#### Disk Space Pre-Validation

Before initiating the download, the service performs a **permissive** check to avoid obvious disk‑full failures while still allowing highly filtered outputs:

1. **Drive Check:** Queries `DriveInfo.AvailableFreeSpace` for the target root.
2. **Minimum Free Space:** Requires at least ~5 GB free.
3. **Action:** Throws `IOException` only if the minimum free space threshold is not met.

#### Progress Throttling

To avoid flooding the UI thread, progress is reported based on a dual trigger:

1. **Game Count:** Every `5000` games processed.
2. **Time Elapsed:** Every `750ms`.
This ensures the UI remains responsive without being overwhelmed by millions of event callbacks.

---

## 2. Service: `LichessDownloaderService`

This service manages targeted downloads via the Lichess API. It prioritizes data integrity and correct content negotiation over streaming performance, as user game archives are significantly smaller than the full database.

### 2.1 API Content Negotiation

* **Endpoint:** `https://lichess.org/api/games/user/{username}`
* **Header Strategy:** Explicitly sets `Accept: application/x-chess-pgn`.
* **Reason:** By default, Lichess might return NDJSON (newline-delimited JSON). Forcing PGN format shifts the conversion load to the Lichess server, allowing `PgnTools` to simply save the byte stream directly to disk without complex parsing logic.



### 2.2 Atomic File Operations

The service implements a "Safe Save" pattern to prevent data corruption during network failures:

1. **Temp File Creation:**
* Uses `FileReplacementHelper.CreateTempFilePath` to generate a filename like `games.pgn.tmp`.


2. **Stream Copy:**
* The `ResponseStream` is copied to this temporary `FileStream` asynchronously.


3. **Atomic Swap:**
* Only upon successful completion (no exceptions), `FileReplacementHelper.ReplaceFile` is called.
* This deletes the old target file (if it exists) and moves the temp file to the final location in a single operation.


4. **Cleanup:**
* If an exception occurs (e.g., timeout, network drop), the `catch` block explicitly deletes the `.tmp` file to avoid leaving garbage on the user's disk.



### 2.3 Configuration

* **Timeout:** Uses `Timeout.InfiniteTimeSpan` and relies on the caller’s cancellation token for user‑initiated cancellation. This is distinct from the 2-hour timeout used in the DB downloader.
* **User-Agent:** `PgnTools/1.0 (GitHub; PgnTools)`. This is required by Lichess API terms of service to prevent blocking.
