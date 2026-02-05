<!-- PGNTOOLS-LC0-BEGIN -->
# **Technical Specification and Architectural Analysis: The Lc0 Automated Match Data Retrieval System**

## **1\. Executive Summary**

The Leela Chess Zero (Lc0) project represents a seminal advancement in the domain of distributed reinforcement learning, democratizing the AlphaZero methodology for the global chess research community. A critical component of this ecosystem is the continuous validation of neural network candidates through self-play matches against reference networks. These matches serve as the primary metric for evolutionary progress, determining Elo ratings and validation pass/fail states for new training iterations.

This report provides an exhaustive technical analysis and architectural blueprint for the design of an automated "Lc0 Matches Downloader." The primary objective is to programmatically synchronize the metadata available in the Lc0 Matches Table (hosted at training.lczero.org) with the unstructured PGN (Portable Game Notation) files stored in the project's storage backend (storage.lczero.org).

The analysis reveals that the data architecture relies on a deterministic mapping between the tabular metadata and the file object storage. Specifically, the Run integer and Match ID integer from the CSV manifest combine to form the unique resource locator (URL) for each game file. This report details this mapping logic, explores the historical context of the directory structures (Runs 1, 2, and 3), and proposes a robust software architecture capable of ingesting, validating, and indexing this massive dataset. The proposed system emphasizes asynchronous I/O, fault tolerance via exponential backoff strategies, and relational data modeling to transform raw PGNs into an analytical asset.

## **2\. Infrastructure Topography and Data Ecosystem**

To engineer a reliable data extraction mechanism, it is imperative to first deconstruct the underlying topology of the Lc0 infrastructure. The system operates on a bifurcated architecture where the control plane (metadata) is decoupled from the data plane (blob storage).

### **2.1 The Control Plane: Training Server Metadata**

The endpoint http://training.lczero.org/matches/?show\_all=1 serves as the authoritative registry for all match activity. This interface exposes a Comma-Separated Values (CSV) manifest that acts as the primary index.

* **Functionality:** It records the scheduling and outcome of every validation match.  
* **Data Structure:** The table is a relational dump containing unique identifiers, network hashes (candidate vs. opponent), outcomes, and run indices.  
* **Volatility:** The data is append-only but updated in near real-time as distributed clients submit results.

The CSV format is the "map" for our downloader. Without this manifest, the storage backend is an opaque data lake, as it does not inherently provide a searchable index or a listing capability for its millions of files.

### **2.2 The Data Plane: Object Storage Backend**

The endpoint https://storage.lczero.org/files/match\_pgns/ functions as the static file server (likely backed by an object storage service like S3 or a high-performance web server like Nginx serving a static volume).

* **Structure:** The URL path provided in the user query indicates a hierarchical directory structure rooted at match\_pgns, branching into integer-based subdirectories (/1/, /2/, /3/).  
* **Access Pattern:** The server allows direct HTTP GET requests for known file paths but likely disables directory indexing (listing all files in a folder) to preserve bandwidth and performance. This necessitates the use of the CSV manifest to "guess" or construct valid URLs.  
* **Content Type:** The files are plain text PGNs, potentially compressed, representing the moves and metadata of a single match.

### **2.3 Distinct Data Classes: Training vs. Matches**

It is crucial for the developer to distinguish between "Training Data" and "Match Data," as they reside in different locations and formats.

* **Training Data:** As noted in technical documentation 1, training data is stored at .../files/training\_data/. These are massive archives (tarballs) containing millions of self-play games generated for the *learning* process. They are chunked and optimized for high-throughput ingestion by the training pipeline.  
* **Match Data:** The subject of this report, stored at .../files/match\_pgns/. These are the *validation* games generated to test the strength of a network *after* a training step. Unlike training chunks, match PGNs are typically stored as individual files or smaller batches, allowing for granular analysis of specific matchups (e.g., specific opening lines or endgame performance).

## **3\. The Run Concept and Directory Semantics**

The user's query specifically identifies folders 1, 2, and 3\. These integers are not arbitrary; they represent distinct epochs in the Lc0 project's history, referred to as "Runs." Understanding the semantics of a "Run" is essential for interpreting the data downloaded from these folders.

### **3.1 Run 1: The "Old Main" and Early Tests**

The directory /1/ corresponds to the initial phase of the project, often referred to as "Run 1" or "Old Main."

* **Characteristics:** This run utilized the initial network topologies (e.g., 192x15 blocks/filters).2  
* **Historical Context:** This period covers the project's infancy and the first major attempts to replicate AlphaZero's success on consumer hardware. The match IDs in this folder map to the earliest entries in the matches table.  
* **Data Implications:** Files in this directory may use older PGN tagging conventions or differ slightly in metadata formatting compared to modern runs.

### **3.2 Run 2: The Second Era (Test 20 / T40)**

The directory /2/ represents the second major lineage of networks.

* **Characteristics:** This run likely encompasses "Test 20" and the "T40" series of networks.2 These networks were larger and trained on refined data generation pipelines.  
* **Validation Context:** Matches in this run were used to validate networks that were significantly stronger, often competing for the top spots in computer chess championships.  
* **Mapping:** Any CSV row where run \== 2 must be mapped to the /2/ directory.

### **3.3 Run 3: The Modern Era (Test 30 / T60 / T70)**

The directory /3/ corresponds to the most recent or current ongoing runs.

* **Characteristics:** This includes "Run 3" networks, often involving massive architectures (e.g., 512 filters) or experimental setups.3  
* **Active Development:** As the "current" folder, this directory is the most volatile, constantly receiving new files as clients upload results from the latest training steps.  
* **Mapping:** CSV rows with run \== 3 map here.

### **3.4 The Implicit "Run 0" or Test Runs**

Occasionally, the CSV might contain run indices outside the {1, 2, 3} set (e.g., test runs, debug runs). If the CSV indicates run \== 10, but the storage backend only exposes 1, 2, and 3, this indicates that those specific debug matches might not be archived in the main match\_pgns bucket or might be stored in a different location not covered by the standard schema. For the purpose of this downloader, the system should filter or log warnings for any run index that does not have a corresponding storage directory.

## **4\. Comprehensive Data Mapping Logic**

The core functionality of the downloader relies on a deterministic algorithm to construct URLs. This mapping bridges the gap between the abstract metadata (CSV) and the concrete file system (URL).

### **4.1 The CSV Schema Definition**

Based on standard Lc0 data exports, the schema for the matches table includes the following critical columns:

| Column Header | Data Type | Semantics | Usage in Mapping |
| :---- | :---- | :---- | :---- |
| **id** | Integer | Unique Match Identifier | **Filename** |
| **run** | Integer | Training Run Index | **Directory** |
| **candidate** | Hex String | SHA-256 of the new network | Metadata (Tagging) |
| **opponent** | Hex String | SHA-256 of the reference network | Metadata (Tagging) |
| **result** | String | Match outcome (e.g., "1-0") | Filtering |
| **game\_count** | Integer | Number of games in the batch | Metadata |

### **4.2 URL Construction Algorithm**

The algorithm acts as a transformation function ![][image1].

**Inputs:**

* Base URL: https://storage.lczero.org/files/match\_pgns  
* Run ID: Extracted from the run column.  
* Match ID: Extracted from the id column.

**Logic:**

1. **Sanitize Inputs:** Ensure Run ID and Match ID are integers. Strip any whitespace.  
2. **Select Directory:** Append the Run ID as a directory path segment.  
   * Path \= Base URL \+ "/" \+ str(Run ID) \+ "/"  
3. **Construct Filename:** Append the Match ID followed by the .pgn extension.  
   * Resource \= str(Match ID) \+ ".pgn"  
4. **Final Assembly:** Combine Path and Resource.

**Formula:**

**![][image2]**

### **4.3 Illustrative Examples**

To ensure clarity, we apply the logic to hypothetical data points covering all three relevant runs.

#### **Example 1: Historical Data**

* **CSV Row:** id: 10500, run: 1, candidate: ab12...  
* **Target Directory:** /1/  
* **Target File:** 10500.pgn  
* **Resulting URL:** https://storage.lczero.org/files/match\_pgns/1/10500.pgn

#### **Example 2: The T40 Era**

* **CSV Row:** id: 243886, run: 2, candidate: cd34...  
* **Target Directory:** /2/  
* **Target File:** 243886.pgn  
* **Resulting URL:** https://storage.lczero.org/files/match\_pgns/2/243886.pgn

#### **Example 3: Modern Data**

* **CSV Row:** id: 911683, run: 3, candidate: ef56...  
* **Target Directory:** /3/  
* **Target File:** 911683.pgn  
* **Resulting URL:** https://storage.lczero.org/files/match\_pgns/3/911683.pgn

### **4.4 Handling Filename Variances**

While .pgn is the standard expectation, large-scale storage systems often introduce compression to save space. The downloader must be resilient to potential variations.

* **Compression:** The server might store files as .pgn.gz. If a request for .pgn returns a 404, the system should probe for .pgn.gz.  
* **Orphaned Records:** It is possible for a match to be scheduled (entry in CSV) but fail to upload (no file in storage). The mapping logic produces a *valid* URL syntax, but the resource may not exist. The architecture must handle HTTP 404 errors not as system failures, but as data consistency states (i.e., "Record Missing").

## **5\. Architectural Design of the Downloader**

Building a tool to download potentially millions of files requires a sophisticated software architecture. A simple iterative script (fetch one, save one, repeat) would be prohibitively slow due to network latency. The proposed architecture utilizes a **Producer-Consumer** model with asynchronous I/O.

### **5.1 System Components**

The system is composed of four primary modules:

1. **The Manifest Ingestor:** Responsible for acquiring and parsing the CSV.  
2. **The Job Scheduler:** Filters rows and populates the download queue.  
3. **The Async Transport Layer:** Manages HTTP connections and bandwidth.  
4. **The Persistence Manager:** Handles file writing and local indexing.

### **5.2 Module 1: Manifest Ingestor**

The CSV file at training.lczero.org is large and constantly growing. Loading the entire file into memory is inefficient and scalable only up to a point.

**Design Recommendation:**

* **Streaming Parse:** Utilize an HTTP stream to fetch the CSV line-by-line.  
* **Iterator Pattern:** Implement a generator function that yields parsed rows one by one. This ensures the memory footprint remains constant ![][image3] regardless of the CSV size.  
* **Header Validation:** The first line must be parsed to dynamically map column names to indices, ensuring robustness against future schema changes (e.g., if a new column is inserted before run).

### **5.3 Module 2: Job Scheduler and Filtering**

Not every user wants every file. The scheduler acts as the logic gate.

**Capabilities:**

* **Run Filtering:** Allow the user to specify \--runs 2,3 to ignore the older Run 1 data.  
* **Incremental Sync:** The scheduler should check the local disk or a local database to see if Match ID X has already been downloaded. If so, it discards the job before it reaches the network layer.  
* **Batching:** Group jobs by Run ID. This optimizes connection reuse (Keep-Alive) by keeping the TCP connection focused on a specific server path, potentially aiding server-side caching mechanisms.

### **5.4 Module 3: Async Transport Layer (The Engine)**

This is the most critical component for performance. Network I/O is the bottleneck.

**Concurrency Model:**

* **Asynchronous I/O (Async/Await):** Use libraries like aiohttp (Python) or reqwest (Rust). This allows a single thread to manage hundreds of open connections, vastly outperforming multi-threading for I/O-bound tasks.  
* **Connection Pooling:** Maintain a pool of persistent TCP connections. The TLS handshake overhead for millions of requests is significant; reusing connections mitigates this.

**Resilience Patterns:**

* **Rate Limiting:** To avoid triggering Denial of Service (DoS) defenses or IP bans, implement a "Token Bucket" rate limiter. A safe starting point is 20-50 requests per second.  
* **Exponential Backoff:** If the server returns 503 Service Unavailable or 429 Too Many Requests, the worker must sleep for an exponentially increasing duration (e.g., 1s, 2s, 4s, 8s) before retrying.  
* **Timeout Management:** Set aggressive read timeouts (e.g., 10 seconds). If a download hangs, kill it and retry. It is faster to restart a stalled connection than to wait for it to recover.

### **5.5 Module 4: Persistence Manager**

Writing millions of small files to a filesystem (like NTFS or EXT4) can cause "inode exhaustion" or severe fragmentation, slowing down the OS.

**Storage Strategy:**

* **Hierarchical structure:** Mirror the server: ./downloads/{run}/{id}.pgn.  
* **Sharding (Optional):** If a run contains \>100,000 files, further shard locally: ./downloads/{run}/{id\_prefix}/{id}.pgn (e.g., by first 3 digits).  
* **Database Archival (Advanced):** Instead of loose files, write the PGN text directly into a SQLite or PostgreSQL database. This serves as both storage and index, making subsequent analysis (e.g., "Find all games where White won") instantaneous via SQL queries.

## **6\. Implementation Logic and Code Flow**

This section translates the architectural concepts into concrete logic flow, suitable for implementation in a high-level language like Python.

### **6.1 The Main Loop Pseudocode**

Initialize SQLite Database for Indexing (matches table)

Load Config (Target Runs, Output Directory, Concurrency Limit)

# **Phase 1: Manifest Processing**

Stream GET request to training.lczero.org/matches/

Read Header Line \-\> Identify indices for 'id', 'run', 'result'

For each Row in CSV Stream:

Extract MatchID, RunID

If RunID not in Target Runs \-\> Continue

If MatchID exists in DB \-\> Continue

Push (MatchID, RunID) to DownloadQueue

# **Phase 2: Asynchronous Workers (Spawn N workers)**

While DownloadQueue is not Empty:

Pop (MatchID, RunID)

URL \= f"[https://storage.lczero.org/files/match\_pgns/](https://storage.lczero.org/files/match_pgns/){RunID}/{MatchID}.pgn"

Try:  
    Response \= HTTP\_GET(URL)  
    If Status \== 200:  
        Content \= Response.Body  
        Verify Content (Check for PGN tags)  
        Save to Disk (./data/{RunID}/{MatchID}.pgn)  
        Update DB (MatchID, Status='Downloaded')  
    Else If Status \== 404:  
        Log Warning "Match ID {MatchID} missing on server"  
        Update DB (MatchID, Status='Missing')  
    Else If Status in :  
        Log Error "Server Strain, Backing off"  
        Sleep(Backoff\_Time)  
        Push (MatchID, RunID) back to Queue  
Catch NetworkError:  
    Retry (up to Max\_Retries)

### **6.2 Handling 404 Not Found**

It is vital to distinguish between a *network failure* and a *content failure*.

* **Scenario:** The CSV lists Match 100, but storage.../100.pgn returns 404\.  
* **Interpretation:** The match was scheduled but the client failed to upload the PGN, or the file was corrupted and deleted.  
* **Action:** Do not retry indefinitely. Mark the record as "Permanently Missing" in the local index to prevent the downloader from checking it on every run.

### **6.3 Validating the Download**

A "200 OK" status does not guarantee a valid file. A proxy or captive portal might return a 200 OK with an HTML login page.

* **Integrity Check:** The downloader must inspect the first few bytes of the payload.  
* **Criteria:** A valid Lc0 PGN should start with a PGN tag bracket \[ (e.g., \[Event "Lc0 match"\]). If the file starts with \<html\> or is empty, discard it and flag as an error.

## **7\. Contextual Analysis: What is in the Files?**

To design a truly effective downloader, one must understand the nature of the data being retrieved. The content of the PGN files dictates the parsing requirements for any post-processing tools.

### **7.1 Anatomy of an Lc0 PGN**

The PGN files generated by Lc0 4 contain specific non-standard headers and comments that are crucial for analysis.

* **Header Tags:**  
  * \`\`: Confirms origin.  
  * \`\`: Identifies the specific network version.  
  * \`\`: The adjudicated result.  
  * \`\`: Critical context. Lc0 matches are rarely played to checkmate. They are adjudicated based on Syzygy tablebases (perfect play) or rule-based draw adjudication (e.g., TCEC draw rule). The downloader's metadata parser should capture this field.  
* **Move Comments:**  
  * Moves are annotated with engine statistics: { \[%eval 0.25\]\[%clk 0:00:05\] }.  
  * **Implication:** These comments significantly bloat the file size. If the user intends to use the data purely for opening book generation (and not engine eval analysis), the downloader could include a "Trim" option to strip comments on the fly, reducing storage requirements by \~60%.

### **7.2 The Role of "Ordo" and "Cutechess"**

The research snippets 6 highlight the use of cutechess-cli and ordo in the Lc0 ecosystem.

* **Cutechess-cli:** This is the tournament manager that actually plays the games. It ensures that the PGNs are compliant with standard chess specifications.  
* **Ordo:** This is the tool used to calculate Elo ratings from the PGN results.  
* **Relevance:** The downloader is essentially reconstructing the raw dataset that ordo uses to calculate the Elo ratings shown on the website. By downloading the matches, the user can run their own ordo calculations on subsets of data (e.g., "What is the Elo of Run 2 networks only in the King's Indian Defense?").

## **8\. Data Science Potential and Future Outlook**

Once the "Lc0 Matches Downloader" is operational, the dataset it constructs offers immense potential for secondary research.

### **8.1 Opening Theory Analysis**

By aggregating millions of games from the "superhuman" Run 2 and Run 3 networks, researchers can detect novelties—moves that Lc0 prefers which deviate from established human theory. The downloader facilitates this by enabling the creation of a local Polyglot or bin book.

### **8.2 Network Archaeology**

The mapping logic allows for "Network Archaeology." By downloading files from Folder 1 (Run 1), one can compare the engine's understanding of positionality in 2018 versus its understanding in 2024 (Folder 3). This requires the downloader to maintain strict separation of runs, which the directory-based mapping (/1/, /2/, /3/) naturally enforces.

### **8.3 Blunder Detection**

The evaluation comments inside the PGNs allow for the automated detection of "blunders" or significant swings in win probability. A sophisticated extension of the downloader could stream the PGNs into a parser that flags games where the evaluation flips sign (e.g., from \+1.0 to \-1.0), isolating interesting tactical sequences for human review.

## **9\. Conclusion**

The design of the Lc0 Matches Downloader is a problem of **data integration**. It bridges the gap between the metadata management of the Training Server and the static storage of the File Backend.

The mapping logic is robust and deterministic:

![][image4]  
Success in implementing this tool relies on respecting the distributed nature of the system. The architecture must account for the high latency of HTTP requests, the potential for missing files (404s), and the need for efficient local storage of millions of records. By adhering to the Run directory structure (1, 2, 3\) and implementing the asynchronous fetching strategies detailed in this report, a developer can successfully mirror the evolutionary history of Leela Chess Zero, unlocking a treasure trove of chess data for analysis and research.

## **10\. Technical Appendix: Protocol Specifications**

### **10.1 HTTP Request Headers**

To ensure reliable delivery, the downloader should include specific headers in its GET requests:

* User-Agent: Lc0-Matches-Downloader/1.0 (Research Tool) \- Identifies the traffic to server administrators.  
* Accept-Encoding: gzip, deflate \- Enables transparent compression if the server supports it, reducing bandwidth usage.  
* Connection: keep-alive \- Critical for reusing TCP sockets across multiple PGN downloads.

### **10.2 Error Code Reference Table**

| HTTP Code | Meaning | Downloader Action |
| :---- | :---- | :---- |
| **200** | OK | Process and Save File. |
| **403** | Forbidden | Stop immediately; Check IP ban status or permissions. |
| **404** | Not Found | Log as "Missing"; Do not retry. |
| **429** | Too Many Requests | Pause all threads; Increase backoff timer. |
| **500/502** | Server Error | Retry with backoff (Server may be temporarily overloaded). |
| **503** | Service Unavailable | Retry with backoff. |

This specification provides the complete roadmap for the realization of the Lc0 Matches Downloader, fulfilling the user's requirement to map the abstract table to the concrete file system.

#### **Works cited**

1. LeelaChessZero/lczero-training: For code etc relating to the network training process., accessed January 26, 2026, [https://github.com/LeelaChessZero/lczero-training](https://github.com/LeelaChessZero/lczero-training)  
2. Training runs \- Leela Chess Zero, accessed January 26, 2026, [https://lczero.org/dev/wiki/training-runs/](https://lczero.org/dev/wiki/training-runs/)  
3. Networks \- Leela Chess Zero, accessed January 26, 2026, [https://lczero.org/dev/wiki/networks/](https://lczero.org/dev/wiki/networks/)  
4. lczero-client/lc0\_main.go at release \- GitHub, accessed January 26, 2026, [https://github.com/LeelaChessZero/lczero-client/blob/release/lc0\_main.go](https://github.com/LeelaChessZero/lczero-client/blob/release/lc0_main.go)  
5. Unexpected exit from client · Issue \#39 · LeelaChessZero/lczero-client \- GitHub, accessed January 26, 2026, [https://github.com/LeelaChessZero/lczero-client/issues/39](https://github.com/LeelaChessZero/lczero-client/issues/39)  
6. Testing guide \- Leela Chess Zero, accessed January 26, 2026, [https://lczero.org/dev/wiki/testing-guide/](https://lczero.org/dev/wiki/testing-guide/)

[image1]: <data:image/png;base64,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>

[image2]: <data:image/png;base64,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>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADIAAAAfCAYAAAClDZ5ZAAADFElEQVR4Xu2YTahNURTH/0IRJVEGSEpiKF+RJKXIx8BHDMwMSEyJDJRM3pBCUjKQfGQiZSDtjMSUFEqJZyBkwIB8rP9bZ7937nrr3LPuvef1UvdX/153r333PWuvtdde5wF9+vzXTBNdFG2zhiCzRXfsYDdMF20Q7S7Ez1FmiG6LLosmG1sn7BKttYNRJoqeiv6KvojeiQaLz7dE80amuvDB6QDXmGVsFkZtiWiCNRRwnM+wyhrqWAx9gLuihcZ2AerMJ9EKYyuzX/RLtMkaDOtFz0UJ7aP9WPQQGuVa6Pke0U/R9+KzhZE6D3XmlWhBq3mY19DIeSm1WnRYdBO6DpXQ3pHNoj+i49ZgYR7+hi7K3WzHJOgB5Nz3xkbmiJ6JZlqDQ0LMEXIEOpd/K8n5f0801dg8TkLnc5cs+0Rn7WAFCXFHlkMzJaFiLsPPxRiRupzOMMQ5LcowHa+KdpjxKhLijjDCjPRn0VJjG4IPz8UeIBYNcgW+I/nH3B9ySIg7wk26Dp2/09iG8v0GArlXYoroPnxH6AB3jOckQkLcEZJTmn9b2FIYooeTrIOWVn7vo7FtL8bpbISEzhzJ67MUt5BznSHzyq0HD3KOBqNZ5mAxHiWhO0eSGR92hDkfgSnzEvodVizW9zJ5vSgJDTtSe9EUsLTmaHiletwcyakQrfu5//oKv/dhNRkXR9i7PIHe0LavsvD25yKn4bceZKXoB+KFI6EzR3LEz1kD4eVF4wn4B55jnMNb9RSqnSDsirkpdd1xJqEzR+gA5zOTRsEHpZE3+wFoY5hhC56bxDfwHS3DlpylkSW6Ct41+f2Gm8O1B0WHirG5I1NbyGuz9Feuv1f0DbroB9E10SOocxw/itEHuwqeN3fHCsrtjSeeA48c7RfQN8dKmDIbRQPQl6Jj0N0rRyjCGmh3zK6hSXIr756PsYI/GG15IrAQMRq8gJveoLbwjuHL1Xxr6JIz0JK/zBrGmkXQA8wHqCsQEfjOzlRvYq2O2QrthHn/9ALvul7/E9MzdOYt/C4gAh/+EuIVs0+fpvgHR8fRsTwiLQMAAAAASUVORK5CYII=>

[image4]: <data:image/png;base64,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>
<!-- PGNTOOLS-LC0-END -->
