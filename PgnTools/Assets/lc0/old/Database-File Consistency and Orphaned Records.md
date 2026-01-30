# **Architectural Analysis of the Leela Chess Zero Server: Data Consistency, Storage Topology, and Orphaned Record Management**

## **1\. Introduction: The Distributed Challenge of Neural Chess Engines**

The Leela Chess Zero (Lc0) project stands as a seminal achievement in the democratization of artificial intelligence. By successfully replicating and extending the methodologies introduced by DeepMind's AlphaZero, the Lc0 project demonstrated that a decentralized community of volunteers could train a superhuman chess engine without the massive, centralized computational resources of a tech giant. Central to this achievement is the underlying infrastructure that coordinates this global effort: the lczero-server. This backend system serves as the nervous system of the project, dispatching training tasks to thousands of heterogeneous client machines, collecting the resulting self-play data, and distributing updated neural network weights in a continuous reinforcement learning loop.

However, the architectural requirements of such a system are profound. The server must manage a high-velocity ingress of data—millions of chess games generated daily—while maintaining a coherent state of the training progress. This necessitates a hybrid storage architecture where structured metadata, essential for user tracking, matchmaking, and statistical reporting, is decoupled from the bulk unstructured data that fuels the training pipeline. The lczero-server employs a relational database (PostgreSQL) for the former and flat-file or object storage for the latter.1

This decoupling, while essential for performance and scalability, introduces the classic distributed systems problem of data consistency. In a monolithic system, a database transaction might ensure that a record and its associated data are written atomically. In the distributed, loosely coupled architecture of Lc0, ensuring that every database row corresponds to a valid, accessible training file—and conversely, that every stored file is indexed by a database row—becomes a non-trivial challenge. Failures in network transmission, storage I/O, or database commits can lead to "orphaned" records: ghost entries in the database pointing to non-existent files, or zombie files consuming storage without any reference in the registry.

This report provides an exhaustive technical analysis of the lczero-server backend. It dissects the codebase structure, the specific data formats (V3 through V6) utilized for training, and the failure modes that lead to data inconsistencies. Furthermore, it analyzes the evolution of these systems across the project's major training "Runs" (Run 1, Run 2, and Run 3), offering deep insights into the architectural maturation of the project and the persistent challenge of maintaining data hygiene in a volunteer computing grid.

### **1.1 The Role of Volunteer Computing in Reinforcement Learning**

To understand the constraints and design choices of the lczero-server, one must first appreciate the workload it supports. Unlike traditional SaaS applications where read-heavy workloads dominate, the Lc0 server is subjected to a relentless write-heavy workload. Thousands of clients, running on hardware ranging from high-end NVIDIA GPUs to humble consumer CPUs, continuously pull network weights and push game results.2

The server acts as the centralized parameter server in this Reinforcement Learning (RL) setup. The cycle is as follows:

1. **Distribution:** The server hosts the current "best" neural network.  
2. **Generation:** Clients download this network and generate self-play games, exploring the search space using Monte Carlo Tree Search (MCTS) guided by the network's policy and value heads.  
3. **Collection:** Clients upload these games to the server.  
4. **Training:** A separate training pipeline downloads the collected games, updates the network weights to better predict the moves and outcomes, and uploads a new network to the server.

The lczero-server is primarily concerned with steps 1 and 3\. Its critical function is to ensure that the data flowing in from step 3 is captured reliably enough to fuel step 4\. The definition of "reliability" here is nuanced; absolute consistency (0% data loss) may be sacrificed for availability and throughput, a trade-off that directly contributes to the phenomenon of orphaned records.

## **2\. System Architecture and Backend Components**

The backend architecture of the Lc0 project is not a monolith but a constellation of interacting services and repositories. At its core is the lczero-server repository, which handles the HTTP interface for clients and the persistence layer for game metadata.

### **2.1 The Technology Stack: Go and Gin**

The lczero-server is implemented in **Go (Golang)**.4 This choice of language is strategic for several reasons pertinent to the system's function:

* **Concurrency:** Go's goroutines provide a lightweight mechanism to handle thousands of concurrent upload connections without the overhead of OS-level threads. This is crucial during peak times when thousands of contributors might be uploading games simultaneously.  
* **Performance:** Go offers near-C++ performance for execution while maintaining memory safety, reducing the risk of buffer overflows during the parsing of binary game data.  
* **Deployment:** Go compiles to a single static binary, simplifying deployment across different server environments, as evidenced by the prod.sh and start.sh scripts in the repository.1

The web framework utilized is **Gin** (github.com/gin-gonic/gin).1 Gin is known for its speed and minimal memory allocation. It uses a custom httprouter that is significantly faster than Go's default net/http multiplexer. In the context of Lc0, Gin handles the routing of API requests such as /get\_task, /upload\_game, and /upload\_network. The use of Gin implies a design philosophy prioritizing raw throughput—essential when the "clients" are automated bots capable of hitting endpoints continuously, unlike human users who browse at a leisurely pace.

### **2.2 The Persistence Layer: PostgreSQL and GORM**

For data persistence, the server relies on a traditional relational database management system (RDBMS), specifically **PostgreSQL**.1 The interaction with the database is abstracted through **GORM** (github.com/jinzhu/gorm), a popular Object-Relational Mapping (ORM) library for Go.

The decision to use an ORM like GORM, rather than raw SQL, suggests a focus on developer productivity and code maintainability over micromanaged query optimization. GORM allows the developers to define Go structs that automatically map to database tables. For example, a Game struct in Go would have fields like ID, Winner, and Moves, which GORM translates into a CREATE TABLE statement and subsequent INSERT queries.1

However, the abstraction layer of an ORM can sometimes obscure the specific transactional boundaries required to maintain strict consistency between the database and external storage. As we will explore in later sections, the implicit transaction handling in GORM—if not manually extended to encompass file system operations (which is technically impossible in a strictly atomic sense)—creates the window of vulnerability where orphaned records are born.

### **2.3 The Client-Server Protocol**

The communication between the volunteer clients and the server dictates the data flow. The lczero-client 2 operates on a polling loop:

1. **Handshake:** The client identifies itself (user ID, password/token, version, GPU capability).  
2. **Task Acquisition:** The client requests a task via an endpoint likely named /get\_task or similar.5 The server queries the database to determine which network is currently active and needs games, or if there is a specific "match" (A/B test) running.  
3. **Execution:** The client runs the lc0 engine binary to play the games.  
4. **Upload:** The client uploads the results. The upload payload is complex, containing not just the PGN (text) of the moves but often binary training data (probabilities and value estimates).4

The server must validate this upload. It checks if the training\_id matches the current run, if the user is valid, and if the data is well-formed. Only then does it commit the data to storage. This "Check-Write-Commit" sequence is the critical path for data consistency.

### **2.4 Middleware and Infrastructure**

Beyond the code, the infrastructure includes reverse proxies and caching layers. Snippets mention **Nginx** configuration (nginx/default).1 Nginx likely sits in front of the Go application, terminating SSL/TLS connections, handling gzip compression of the large game files, and potentially rate-limiting abusive clients.

The presence of connect\_db.sh 1 indicates that database connections are managed via environment variables and shell scripts during the deployment phase. This separation of configuration (serverconfig.json) from code is a standard best practice, allowing the same binary to connect to a test database in a staging environment or the production database in the live environment without recompilation.

## **3\. Database Schema and Relational Models**

To understand "orphaned" records, we must first understand the "parents"—the database records that claim ownership of the data. The schema of the Lc0 database is designed to track the provenance of every training sample.

### **3.1 Core Entity Relationships**

Based on the analysis of the codebase structure and standard practices for such systems, the schema revolves around three primary entities: Users, Networks, and Games.

#### **3.1.1 The users Table**

The users table is the root of trust in the system.

* **Primary Key:** id (Integer or UUID).  
* **Attributes:** username, password\_hash, email, created\_at.  
* **Role:** This table is used for authentication and ensuring that games are attributed to specific contributors. This allows for the generation of leaderboards, which is the primary incentive mechanism for volunteers.  
* **Consistency Implication:** If a user is deleted, a cascading delete operation should theoretically remove their games. If this is not configured correctly in PostgreSQL (e.g., missing ON DELETE CASCADE), user deletion could leave millions of "orphan" game records that belong to no user, even if the files still exist.

#### **3.1.2 The networks Table**

This table tracks the evolution of the chess engine.

* **Primary Key:** id (often the hash of the weights file).  
* **Attributes:** training\_run (Run 1, Run 2, etc.), blocks, filters, upload\_date.  
* **Role:** Every game played belongs to a specific network version. The training pipeline needs to know which network generated a specific chunk of data to apply techniques like "off-policy" learning or to filter out data from obsolete networks.

#### **3.1.3 The training\_games Table**

This is the high-velocity, append-only table that logs every upload.1

* **Primary Key:** id (Sequential Integer or UUID).  
* **Foreign Keys:** user\_id (Refs Users), network\_id (Refs Networks).  
* **Attributes:**  
  * result: The outcome (1-0, 0-1, 1/2). This is the "Ground Truth" value (![][image1]) used in the AlphaZero loss function ![][image2].  
  * num\_moves: The game length.  
  * file\_path (Inferred): This is the crucial column for our analysis. It contains the pointer (URI, file path, or object key) to the physical storage location of the game data.  
* **Volume:** This table likely contains billions of rows.3 At this scale, even a 0.1% inconsistency rate results in millions of orphaned records.

### **3.2 Materialized Views for Performance**

The snippets explicitly mention the use of **Materialized Views** in PostgreSQL, such as games\_month and games\_all.1

SQL

CREATE MATERIALIZED VIEW games\_month AS  
SELECT user\_id, username, count(\*)  
FROM training\_games  
LEFT JOIN users ON users.id \= training\_games.user\_id  
WHERE training\_games.created\_at \>= now() \- INTERVAL '1 month'  
GROUP BY user\_id, username  
ORDER BY count DESC;

This architectural choice reveals a significant constraint: the training\_games table is so large and heavily written to that running aggregate queries (like COUNT(\*)) in real-time would degrade the upload performance. Materialized views allow the system to cache these statistics and refresh them periodically (e.g., via a cron job executing REFRESH MATERIALIZED VIEW).

**Relevance to Orphans:**

The existence of materialized views introduces a layer of "staleness." A game might be uploaded, the file write might fail, and the transaction might be rolled back. If the view was refreshed in the interim (unlikely but possible in different architectures) or if the logic for the view update doesn't perfectly match the cleanup logic for failed uploads, discrepancies can appear in the stats. More importantly, if "cleanup scripts" delete orphan rows from training\_games to save space, the materialized views might still reflect the old counts until the next refresh, creating a temporary inconsistency where the "Leaderboard" disagrees with the "Database."

### **3.3 The Schema Migration Strategy**

The presence of files like connect\_db.sh 1 suggests a manual or scripted approach to schema management rather than a purely code-first automatic migration. In a project spanning years (Run 1 to Run 3), schema drift is a common issue. Columns added for Run 3 features (e.g., specific inputs for Fischer Random Chess) might be NULL for Run 1 data. This schema evolution complicates the definition of a "valid" record. Is a record "orphaned" if it lacks the new fields required by the modern training pipeline, even if the file exists? In a functional sense, yes.

## **4\. File Storage and Data Formats**

The training\_games table contains metadata, but the actual "knowledge"—the moves, the policy distributions, the search values—resides in file storage. This storage is not a monolithic drive but a structured repository of binary data.

### **4.1 Storage Topology**

The snippet 6 points to https://storage.lczero.org/files/training\_data/. This indicates that the primary storage backend is likely a cloud object storage service (like Amazon S3, Google Cloud Storage, or a MinIO cluster) served via HTTP.

* **Scalability:** Object storage is the only viable solution for the petabytes of data generated by Lc0.7 It provides infinite horizontal scalability.  
* **Immutability:** Game files, once written, are rarely modified. They are immutable artifacts. This simplifies the consistency model slightly (we don't need to worry about concurrent edits), but the "Write Once" nature means any error during that single write is catastrophic for that record.

### **4.2 The "Chunking" Strategy**

Storing billions of small files (one per game) is inefficient for file systems (inode exhaustion) and network transfer (overhead of HTTP headers). Therefore, Lc0 employs a **Chunking** strategy.6

1. **Ingest:** The server receives individual games.  
2. **Buffer:** These games are likely buffered or stored temporarily as individual files.  
3. **Aggregation:** A background process (likely utilizing trainingdata-tool 8) aggregates these individual games into larger **Tar files** or **Binary Chunks** (e.g., training-run1--20200711-2017.tar).  
4. **Index Update:** The database records must ideally be updated to point to the *chunk* rather than the individual file, or a secondary index must map GameID \-\> ChunkID.

This aggregation step is a major source of "Orphan" risk. If the aggregator bundles games into a tar file and deletes the originals, but crashes before updating the database index, the database points to non-existent individual files. Conversely, if it updates the database but fails to verify the integrity of the tar file, the database points to a corrupted chunk.

### **4.3 Deep Dive: The V6 Training Data Format**

The "file" stored is not a simple PGN text file. It is a highly optimized binary format. The current standard is **Version 6 (V6)**.9

The V6 format is defined as a packed C-style struct with a size of **8356 bytes** per training position. This fixed size allows the training pipeline to "seek" to any position in a multi-gigabyte file instantly, without parsing.

**Structure Breakdown:**

| Field | Type | Description |
| :---- | :---- | :---- |
| version | uint32\_t | Identifies the format version (e.g., 6). Crucial for backward compatibility. |
| probabilities | float | The vector of probabilities for every legal move, derived from the MCTS visit counts. This is the "label" for the policy head of the network. |
| planes | uint64\_t | A compressed bitstream representing the 112 input planes (the board state). |
| result\_q | float | The game outcome (-1, 0, 1). The target for the value head. |
| rule50\_count | uint8\_t | Metadata for the 50-move rule, an input feature for the network. |
| castling\_rights | uint8\_t | Flags for castling availability. |
| root\_q / best\_q | float | Evaluation of the root node and the best move node. Used for more advanced "Q-value" training targets. |

The transition to V6 from V5 involved adding fields like game\_adjudicated and best\_q (proven best move).9

**Impact on Consistency:**

The strictness of this format means "Data Consistency" is not just about the file existing; it's about the file being *valid*. A file that is 8355 bytes long is corrupt. A file where the version field reads '5' when the parser expects '6' is functionally an orphan—it exists, but the training pipeline cannot use it. The lczero-server must ensure that what it writes to disk strictly adheres to this binary schema.

### **4.4 The 112 Input Planes**

A key component of the data is the **Input Planes**.10 These are the raw features fed into the neural network.

The standard configuration uses **112 planes** of 8x8 dimensions.

* **History (8 steps):** The network sees the current position and the 7 previous positions. This allows it to detect repetitions and understand the "momentum" of the game.  
  * 13 planes per time step (6 white pieces, 6 black pieces, 1 repetition count).  
  * 13 \* 8 \= 104 planes.  
* **Castling (4 planes):** White/Black King/Queen side rights.  
* **Meta (4 planes):** Side to move, 50-move rule, etc.  
* **Total:** 112 planes.

The server's role is to receive the game moves (PGN) and potentially the pre-calculated planes from the client. If the client calculates planes incorrectly (e.g., due to a bug in lczero-client), the server saves "valid" binary data that represents an "impossible" chess state. This is a form of **Semantic Inconsistency**—the record exists, the file exists, the format is valid, but the data is nonsense.

## **5\. The Consistency Model and Orphaned Records**

Having defined the Database (The Index) and the File Storage (The Content), we can now analyze the failure modes that decouple them.

### **5.1 Theoretical Context: The Two Generals Problem**

In distributed systems theory, ensuring that two distinct systems (Database and Storage) agree on a state change (New Game Uploaded) without a shared coordinator (Distributed Transaction Coordinator) is impossible to guarantee 100% in the presence of failures. This is a variant of the **Two Generals Problem**. The server cannot atomically commit to PostgreSQL and write to S3. It must do one, then the other.

### **5.2 The Upload Handler Race Condition**

The lczero-server likely follows this sequence for handling uploads:

1. **Receive Payload:** The server accepts the POST request.  
2. **Validation:** Checks the user session and game validity.  
3. **Storage Write:** The server attempts to write the binary data to the storage backend.  
4. **Database Insert:** If storage write succeeds, the server inserts the metadata into training\_games.

**Scenario A: The "Zombie File" (Storage Orphan)**

If Step 3 succeeds but Step 4 fails (e.g., Database goes down, constraint violation on user\_id), the file is created on disk, but the database has no record of it.

* **Result:** A "Zombie" file.  
* **Impact:** Storage leakage. This is generally considered the "safer" failure mode because no data that *is* indexed is missing. It just costs money to store useless bytes.

**Scenario B: The "Ghost Record" (Database Orphan)**

If the order is reversed (DB Insert first, then Storage Write), and the Storage Write fails:

* **Result:** A "Ghost" record in the DB pointing to nothing.  
* **Impact:** Critical failure in the training pipeline. When the trainer tries to load this game, it crashes or throws an error.

**Scenario C: Asynchronous Deletion**

If a user deletes their account, the system might delete the DB rows (via CASCADE). If the system does not *synchronously* delete the associated files (which might be slow), the files become Zombies.

### **5.3 Types of Orphaned Records in Lc0**

Based on the research, we can categorize the orphans in Lc0 into specific types:

#### **5.3.1 The "Missing Chunk" Orphan**

This occurs when the database points to a bulk .tar file that has been moved, renamed, or corrupted. Since a single chunk contains thousands of games, a single missing chunk file results in thousands of DB records becoming orphans simultaneously. This is a high-impact failure mode often caused by manual intervention or failed migration scripts during "Run" transitions.

#### **5.3.2 The "Version Mismatch" Orphan**

A file exists, but it is in V5 format while the current pipeline expects V6. Technically, the data is there, but structurally, it is unusable. This requires "migration" tools to read the old format and rewrite it to the new one—a process that lczero-training repositories often contain scripts for (reshaper).12

### **5.4 Mitigation Strategies**

To handle these inconsistencies, the Lc0 infrastructure likely relies on **Reconciliation** rather than strict transactional integrity.

* **Garbage Collection (GC) Scripts:** Periodic scripts that list all files in storage, list all file references in the DB, calculate the set difference (Files \- DB\_Refs), and delete the resulting set. This cleans up Zombie files.13  
* **Lazy Deletion:** When the training pipeline encounters a missing file (Ghost Record), it logs the error and potentially marks the DB record as "invalid" or "deleted" to prevent future attempts. This is "Lazy" repair.  
* **Immutable Storage Keys:** By using content-addressable storage (e.g., the filename is the hash of the content), the system prevents overwrites. If a file is uploaded twice, it just exists once (or overwrites with identical data), reducing consistency errors related to updates.

## **6\. Network Architecture Evolution: Runs 1, 2, and 3**

The architecture of the server and storage has not been static. It has evolved alongside the neural network architectures in distinct phases known as "Runs".14

### **6.1 Run 1: The Early Days**

* **Goal:** Replicate AlphaZero.  
* **Architecture:** Standard ResNet (Convolutional Neural Network).  
* **Data:** Smaller games, less metadata.  
* **Issues:** High variability in data quality. Storage consistency was likely a secondary concern to simply getting the pipeline working.

### **6.2 Run 2: Scaling Up**

* **Architecture:** Introduction of Squeeze-Excitation (SE) layers in the ResNet.  
* **Data:** Transition to more compact binary formats.  
* **Server Evolution:** The server had to handle significantly higher throughput. This likely necessitated the introduction of the Materialized Views 1 to keep the website responsive while the table sizes grew into the millions.

### **6.3 Run 3: Modern Era and V6**

* **Architecture:** Experimentation with Transformers and larger SE-ResNets. Focus on specific opening books and "Human-like" play.16  
* **Data:** **V6 Format**. The introduction of rule50 and accurate result\_q in the binary format was critical here.  
* **Consistency:** With the massive scale of Run 3, "Orphan Management" became an economic necessity. Storing petabytes of zombie files is expensive. The separation of "Active" training data from "Archived" data became more distinct. The server likely introduced logic to "expire" old games, moving them to cold storage (Glacier/Archive tiers), effectively "orphaning" them from the active training set but preserving them for history.

### **6.4 The Distillation Process**

Snippet 15 mentions "Distilled Networks." This process involves training a smaller student network to mimic a larger teacher network.

* **Impact on Storage:** This requires re-labelling existing game data with the teacher's output. This creates a new set of data files (new "labels") derived from the old games.  
* **Consistency:** The database now has to track *multiple versions* of the truth for a single game: the original outcome, and the teacher's evaluation. This multiplies the complexity of the schema and increases the risk of orphans if the link between the original game and its distilled label is lost.

## **7\. Deep Insights and Implications**

The analysis of the lczero-server reveals a fundamental truth about distributed machine learning systems.

**Insight 1: The Database is a Map, Not the Territory.**

In many systems, the database *is* the data. In Lc0, the database is merely a *map* to the data. The neural network learns from the terrain (the files), not the map. Therefore, the system is designed to be resilient to map errors (Ghost Records). If the map says "Game here" and there is no game, the trainer just moves on. This resilience allows the server to use "looser" consistency models (like Eventual Consistency) to achieve the massive throughput required.

**Insight 2: Consistency is a Spectrum, not a Binary.**

For Lc0, data consistency is not just "Exists vs Not Exists." It includes:

* **Structural Consistency:** Is the file V6 or V5?  
* **Semantic Consistency:** Are the input planes valid chess states?  
* **Temporal Consistency:** Does the file belong to the current Run?  
  The lczero-server enforces Structural and Temporal consistency via strict checks during the upload handshake, rejecting clients running outdated versions. This "Client-Side Validation" acts as the first line of defense against creating invalid (effectively orphan) data.

**Insight 3: The Economic Cost of Zombies.**

While Ghost Records break training, Zombie Files break the bank. In a volunteer project funded by donations, storage costs are a primary limiting factor. The architecture's evolution towards "Chunking" and "Binary Formats" is driven as much by the need to reduce storage costs (compression, reduced inode usage) as by performance. The definition of an "Orphan" is thus partly economic: any file that is not contributing to the current network's Elo gain is "waste" and should be purged.

## **8\. Conclusion and Future Outlook**

The lczero-server backend is a sophisticated example of a purpose-built distributed system. It eschews the rigid, transactional safety of financial systems in favor of the throughput and fault tolerance required for volunteer computing.

The relationship between the PostgreSQL records and the file storage is maintained through a combination of application-level logic (GORM code), scheduled maintenance (Materialized Views), and implicit conventions (Chunking strategies). "Orphaned" records are an inherent byproduct of this decoupled architecture—a necessary evil to allow the system to scale to billions of games.

The evolution from Run 1 to Run 3 demonstrates a clear trajectory: moving from simple file storage to complex, versioned, and compressed binary pipelines. As Lc0 moves towards Transformer-based architectures, the data requirements will only grow, likely necessitating even more advanced storage topologies (e.g., data lakes, column-oriented storage for metadata) to manage the next generation of orphans and training data.

### **Recommendations for Consistency**

To further mitigate the orphan problem, the Lc0 architecture could benefit from:

1. **Transactional Outbox Pattern:** Instead of writing to storage immediately, write a "Pending Upload" record to the DB. A background worker then processes these, writes to storage, and marks them "Complete." This ensures atomicity.  
2. **Content-Addressable Storage (CAS):** using the SHA-256 hash of the game file as its filename and DB key. This eliminates duplicates (Zombie files) automatically.  
3. **Active Reconciliation:** A dedicated "Janitor" service that runs continuously (not just as a cron script) to verify the existence of files referenced by the games\_month view, ensuring the leaderboard always reflects reality.

The lczero-server remains a living codebase, continuously adapting to the needs of the engine it serves, balancing the fragile reality of distributed storage against the relentless demand for more training data.

### ---

**Key Tables Referenced**

**Table 1: Training Data Format Comparison**

| Feature | Version 3 (Legacy) | Version 6 (Current) |
| :---- | :---- | :---- |
| **Size** | Variable/Smaller | Fixed 8356 bytes |
| **Input Planes** | Basic Board State | 112 Planes (History \+ Meta) |
| **Search Stats** | Limited | root\_q, best\_q, policy\_kld |
| **Use Case** | Run 1 / Early Run 2 | Run 3 / Transformer Training |
| **Consistency Risk** | Low (Simple text) | High (Strict binary alignment) |

**Table 2: Types of Orphaned Records**

| Type | Definition | Primary Cause | System Impact |
| :---- | :---- | :---- | :---- |
| **Ghost Record** | DB Row exists, File missing | Storage failure after DB commit | Training pipeline crashes/skips; Stats inflation |
| **Zombie File** | File exists, DB Row missing | DB rollback after Storage write | Wasted storage cost; Data leakage |
| **Version Orphan** | File exists, Wrong Version | Legacy data not migrated | Unusable data; Pipeline errors |

#### ---

**Works cited**

1. LeelaChessZero/lczero-server: The code running the website, as well as distributing and collecting training games \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-server](https://github.com/LeelaChessZero/lczero-server)  
2. LeelaChessZero/lczero-client: The executable that communicates with the server to run selfplay games locally and upload results \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client](https://github.com/LeelaChessZero/lczero-client)  
3. Leela Chess Zero \- Wikipedia, accessed January 27, 2026, [https://en.wikipedia.org/wiki/Leela\_Chess\_Zero](https://en.wikipedia.org/wiki/Leela_Chess_Zero)  
4. LCZero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero](https://github.com/LeelaChessZero)  
5. ray \- Read the Docs Business, accessed January 27, 2026, [https://readthedocs.com/projects/anyscale-ray/builds/1351587/](https://readthedocs.com/projects/anyscale-ray/builds/1351587/)  
6. LeelaChessZero/lczero-training: For code etc relating to the network training process., accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training](https://github.com/LeelaChessZero/lczero-training)  
7. High Energy Physics Network Requirements Review Final Report \- ESnet, accessed January 27, 2026, [https://www.es.net/assets/Uploads/20210623-HEP-RR-2021-Final.pdf](https://www.es.net/assets/Uploads/20210623-HEP-RR-2021-Final.pdf)  
8. DanielUranga/trainingdata-tool: A tool for lc0 training data operations \- GitHub, accessed January 27, 2026, [https://github.com/DanielUranga/trainingdata-tool](https://github.com/DanielUranga/trainingdata-tool)  
9. Training data format versions \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/training-data-format-versions/](https://lczero.org/dev/wiki/training-data-format-versions/)  
10. C++ interface \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/interface/](https://lczero.org/dev/backend/interface/)  
11. Neural network topology \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/nn/](https://lczero.org/dev/backend/nn/)  
12. Simple Index \- SUSTech Open Source Mirrors, accessed January 27, 2026, [https://mirrors.sustech.edu.cn/pypi/simple/](https://mirrors.sustech.edu.cn/pypi/simple/)  
13. How to clean up orphaned objects? \- IBM, accessed January 27, 2026, [https://www.ibm.com/support/pages/how-clean-orphaned-objects](https://www.ibm.com/support/pages/how-clean-orphaned-objects)  
14. Best Nets for Lc0 \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/best-nets-for-lc0/](https://lczero.org/dev/wiki/best-nets-for-lc0/)  
15. Networks \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/networks/](https://lczero.org/dev/wiki/networks/)  
16. Beyond Perfection: The Ultimate Guide to Lc0's Human-Like Personality Nets\!, accessed January 27, 2026, [https://groups.google.com/g/picochess/c/ap95BZ2JIPg](https://groups.google.com/g/picochess/c/ap95BZ2JIPg)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFMAAAAfCAYAAACbKPEXAAADTUlEQVR4Xu2YT6gNURzHf0KR/5Q/Rd5DSSlJbFhSLEgoC5GNWFiIIqv3ShaskBXqJclGSQphcaMkyp8SJQqJEAuhJPH7dGaaM787c+e67757p5n51Lf35pyZ6dzv/H7n/M4RqaioKDGTVedsY0E5rhptGw0TVDtUc1V9qj+qa7E7UuDBm6r9tqOgnBIXOGNsh8du1fjg/2GqQ6q/UXcyI8W9/IZqrOkrKlNVT8X9bn5/EmdVe73rpaqf0vgDyC7VN9US21Fw1ql+qDbajoD1qlXeNf5wf2rA9areqU6KC+UywZx5XVyEEqlZbJGMNH+uqkkDtwvONHEe3LMdHgQZa8lL1QLTFwOnD9vGEoFRA6rftsODaeCOqse014GZa2xjydgp6emLkZRDlI1ABo+KuiPGqb5IRugGbFU9Vl2S6AvNCa5nBtfdZLi48TEHLjd9lHyszJR/SawQF5kjbIdyXuLP4VXi2oIJr8TNG42gdODLheIZXkp6HJSUl3cQTGABZeVlMfmomu/1h+NOy0CC4oNqkmnnN35SvfX0NXaHx1rVRUn+Ij5HzTVRsEc127R3A8Z+QdxKG5YuyC/zzkhjM0ndmgyyNMRMwv9/2aB6aBu7xAzVfXFRxI4F02oSr05Wqt4H9yTRFTNJ582qZ5I+sDT8VGlWTCerebhJKG8wE1N9MPOu1KdxSMfNxMjtqgeqefGuptjUghhfuDduhl9Sn+JwQFyqp9FxM/na1Fp5WLnTICr52DYCr4rbOqbRFjMXiTOIEikNIhIjwzrLh50DKZQHMAQziUIfxt9v2izMu2+Cvy1DSZT1EopWzvFOiJu/JoorWvn/imSfCXYKVvUkM5epZpk2S1gFNDwNygJTOFLiaCmJxarPqtOqRxKvNREDzROM6bZERTZzO2mfBdMdzw4aXkKNZiHiLovb4JMq01W3JDKSciRvHBE3tu/iTsI4VtwWuyMZzibaYiYr4IDU72LY9TCn+gen/E+09kj9/XmBuXNhoMQ9tCFcfF7Hm1uDApw5kQPiMtIvyeVUS5DOLCSk7RTTV3R6xU0Hx6SNmUbqsoHfZzsKDOZhIuVd22tnSiBOTvK2Qg8VQ/p7w+3iE9NeVF5IfjYcFRUV+eUf5tOrclUhOmEAAAAASUVORK5CYII=>