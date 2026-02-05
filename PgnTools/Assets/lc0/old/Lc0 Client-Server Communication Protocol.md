<!-- PGNTOOLS-LC0-BEGIN -->
# **The Distributed Architecture of Leela Chess Zero: A Comprehensive Analysis of Client-Server Protocols and Worker Lifecycles**

## **1\. Introduction**

The landscape of computer chess has undergone a radical transformation in the last decade, shifting from the brute-force calculation and handcrafted evaluation functions of engines like Deep Blue and early Stockfish to the intuition-based, neural network-driven approach pioneered by DeepMind's AlphaZero. Leela Chess Zero (Lc0) stands as the open-source implementation of this methodology, representing a distributed computing effort of massive scale. Unlike traditional software development where improvements are made via code changes, Lc0 improves through "learning"—a process that requires the generation of millions of self-play games to train its neural networks.

This report provides an exhaustive technical analysis of the lczero-client, the critical orchestrator software that enables this distributed training. By reverse-engineering the source code, analyzing protocol buffers, and dissecting communication logs, we illuminate the worker lifecycle. We explore how a decentralized fleet of volunteer hardware—ranging from consumer laptops to high-end cloud GPUs—synchronizes with a central server to perform complex reinforcement learning tasks. The analysis covers the initialization sequences, the specific HTTP protocols used for data transport, the authentication mechanisms, and the resilience strategies employed to maintain stability in a volatile volunteer grid.

### **1.1 The AlphaZero Paradigm and Distributed Requirements**

To understand the client's architecture, one must first understand the computational workload it manages. The AlphaZero algorithm relies on a reinforcement learning loop consisting of four distinct stages:

1. **Self-Play:** An agent plays games against itself using its current neural network to evaluate positions.  
2. **Data Generation:** The game moves and outcomes are stored as training examples.  
3. **Training:** A new neural network is trained on this data to predict the game outcomes and move probabilities.  
4. **Evaluation:** The new network is pitted against the old one; if it wins a sufficient margin, it becomes the new "best" network.

Stages 3 and 4 (Training and Evaluation) are centralized or semi-centralized. However, Stage 1 (Self-Play) is embarrassingly parallel and computationally expensive, requiring billions of inference calculations. The lczero-client is designed specifically to distribute this Stage 1 workload across the internet. It transforms the AlphaZero methodology into a client-server architecture where the "brain" (the network) is hosted centrally but executed locally.

### **1.2 The Lc0 Ecosystem Components**

The Lc0 ecosystem is not a monolith but a collection of loosely coupled services and binaries. Understanding the client requires placing it within this topology:

* **The Engine (lc0):** The core C++ binary that implements Monte Carlo Tree Search (MCTS) and interfaces with hardware backends (CUDA, OpenCL, BLAS) to perform neural network inference.1  
* **The Training Server (lczero-server):** A Go-based central authority that manages the database of games, tracks user contributions, and schedules training runs.2  
* **The Client (lczero-client):** The focus of this report. A lightweight Go application that acts as the "glue" between the server and the engine.4  
* **The Network Weights:** Protocol Buffer files containing the floating-point parameters of the neural network.5

The client's role is strictly operational: it does not "know" chess. It knows only how to download a file, execute a subprocess, and upload a result. This "dumb worker" design is a strategic choice to maximize stability and minimize the need for frequent software updates on volunteer machines.

## ---

**2\. System Architecture and Technology Stack**

The architectural decisions behind the lczero-client reflect the constraints of volunteer computing: heterogeneous hardware, unreliable network connections, and the need for cross-platform compatibility.

### **2.1 The Choice of Go (Golang)**

The lczero-client is written in Go.4 This choice is instrumental for several reasons:

* **Static Binaries:** Go compiles to a single, static binary. This eliminates "dependency hell" for volunteers. A user on Windows, Linux, or macOS can download the executable and run it without installing Python environments, DLLs, or complex runtimes.  
* **Concurrency:** The client must simultaneously manage the engine process (which blocks during game play) and network communications (Heartbeats, uploads). Go's goroutines and channels provide a robust primitive for this concurrency.7  
* **Standard Library:** The robust net/http library allows for sophisticated handling of HTTP connections, retries, and timeouts without third-party bloat.

### **2.2 The "Thin Client" Model**

The client operates on a "pull" model. The server does not push tasks to workers; rather, workers come online and request work. This is crucial for firewall traversal. Since the client initiates all outgoing connections (typically on TCP port 443 via HTTPS), volunteers do not need to configure port forwarding or deal with NAT traversal issues.

### **2.3 Directory Structure and Deployment**

The repository structure provides clues to the deployment workflow 4:

* lc0\_main.go: The entry point and orchestration logic.  
* client/: A package encapsulating the HTTP API interactions.  
* client.sh: A shell script wrapper, likely for Linux/macOS environments to set library paths or restart logic.  
* go.mod / go.sum: Dependency tracking.

When deployed, the client expects to reside in a directory alongside the lc0 engine executable and a weights/ subdirectory. This locality is enforced to allow the client to invoke the engine via relative paths and manage the network files without complex configuration management.

## ---

**3\. The Client Initialization Phase**

The lifecycle of an Lc0 worker begins with initialization. This phase is critical for establishing identity, verifying capabilities, and negotiating the protocol version with the server.

### **3.1 Configuration and Argument Parsing**

Upon startup, the lc0\_main.go routine initializes by parsing command-line arguments and reading persistent configuration files.7

**Key Configuration Parameters:**

* \--user: The volunteer's username (for the leaderboard).  
* \--password: The authentication token.  
* \--backend: The compute backend (e.g., cuda-fp16, opencl, blas).  
* \--threads: The number of CPU threads allocated to the engine.  
* \--train-only: A boolean flag indicating whether the client should participate in match games (validation) or only training games.

The client looks for a settings.json file to load these credentials automatically. If the file is absent, it may prompt the user or default to an anonymous "guest" mode, though for meaningful contribution tracking, authentication is preferred.

### **3.2 Dependency Verification**

Before contacting the server, the client performs a "sanity check" on its environment. It attempts to execute the lc0 binary with the \--version or \--help flag. This serves two purposes:

1. **Existence Check:** Verifies the binary exists and is executable.  
2. **Version Extraction:** The client captures the engine's version string. This is crucial because the server maintains a whitelist of allowed engine versions. If a volunteer is running an obsolete engine (e.g., v0.20 when the server requires v0.28), the client detects this locally or receives a rejection later.7

### **3.3 Hardware Profiling**

To assist the server in task scheduling and analytics, the client profiles the local hardware. It detects:

* **GPU Model:** Identifying whether the user has a high-end NVIDIA Tesla T4 9 or a consumer GTX card.  
* **Backend Capabilities:** Determining if the system supports FP16 (half-precision) arithmetic, which significantly speeds up neural network inference.  
* **Hostname:** A unique identifier for the machine to track session stability.

This metadata is packed into the initial handshake request, allowing the server to filter out hardware that might be too slow to contribute effective training data within the required timeouts.

## ---

**4\. Network Protocol and Communication**

The communication between the lczero-client and lczero-server is the nervous system of the project. It relies on standard HTTP paradigms but implements a specific application-layer protocol for task management.

### **4.1 Transport Layer Security (TLS)**

All communication occurs over HTTPS. Given that the client transmits user credentials (password/token) and potentially proprietary (though open-source) architectural configurations, encryption is mandatory. The snippets verify the use of https:// URLs in the source code.7

### **4.2 The "Get Task" Protocol**

The client operates in an infinite loop. The primary action in this loop is requesting the next job. This is often implicit: the response to an upload request frequently contains the instructions for the *next* game.

#### **4.2.1 The Request Structure**

While a dedicated /get\_task endpoint may exist, the snippets suggest a piggyback mechanism. When the client initializes (or uploads a game), it sends a JSON or query-param payload containing its state:

JSON

{  
  "version": "34",  
  "gpu": "NVIDIA GeForce RTX 3080",  
  "engine\_version": "0.30.0",  
  "train\_only": true  
}

#### **4.2.2 The Server Response (NextGameResponse)**

The server responds with a structured object, identified in the Go code as NextGameResponse.7 This object dictates the parameters for the next self-play session.

**Table 1: NextGameResponse Fields**

| Field Name | Type | Description |
| :---- | :---- | :---- |
| TrainingId | Integer | The global identifier for the current training run. This allows the server to manage multiple experiments simultaneously. |
| NetworkId | String (Hash) | The SHA-256 hash of the neural network weights to be used. |
| NetworkUrl | String (URL) | A direct link (often to a CDN) to download the weights if missing. |
| Options | Map/Object | Dynamic engine parameters (e.g., cpuct, fpu\_reduction). |
| Type | Enum | The type of game: TRAINING, VALIDATION, or TEST. |

### **4.3 Network Weight Synchronization**

A critical aspect of the protocol is ensuring the client uses the correct neural network. In Reinforcement Learning, the network changes rapidly (often every few hours).

#### **4.3.1 Hash-Based Caching**

The client uses the NetworkId (a hash) as a cache key. Before every game, it checks the local weights/ directory for a file matching this hash.10

* **Hit:** If the file exists and the checksum matches, the client proceeds immediately.  
* **Miss:** If the file is missing, the client initiates a download from NetworkUrl.

#### **4.3.2 The Protobuf Weight Format**

The weight files are not arbitrary binaries; they are serialized Protocol Buffers. The lczero-common repository defines this format in net.proto.5 This definition is vital for understanding what is actually being transmitted.

**Structure of net.proto:**

* **Weights Message:** The root container.  
* **Layer Message:** Defines a tensor, including min\_val, max\_val, params (the raw bytes), and encoding.  
* **ConvBlock:** Represents a convolutional layer (Weights \+ Biases \+ BatchNorm parameters).  
* **Residual:** Represents a residual block, the building block of the ResNet architecture used by AlphaZero.  
* **SEunit:** Represents "Squeeze-Excitation" units, an architectural improvement added to Lc0 later in its development.5

This Protobuf definition allows the client to download networks with different topologies (e.g., moving from a 20-block network to a 40-block network) without needing a software update, provided the engine supports the layers defined in the proto file. The client simply passes the file path to the engine, which handles the deserialization.

### **4.4 CDN and Storage**

To reduce load on the API server, the heavy weight files (often 50MB \- 100MB) are hosted on storage.lczero.org or similar object storage services.11 The client handles these downloads using standard HTTP GET requests, likely verifying the integrity of the downloaded file against the NetworkId hash before execution to prevent corrupted data from ruining the training run.

## ---

**5\. The Engine Execution Lifecycle**

Once the task is received and the weights are verified, the client transitions from a network communicator to a process orchestrator.

### **5.1 Invocation and Pipes**

The client spawns the lc0 executable as a child process. It establishes communication via standard streams:

* **STDIN:** The client writes commands to the engine.  
* **STDOUT:** The client reads the engine's move stream and debug info.  
* **STDERR:** Used for logging errors or panic stacks.

### **5.2 The selfplay Command**

Unlike a standard chess GUI which uses the UCI go command, the client invokes a specialized mode, typically via command-line arguments or a custom UCI command like selfplay.13

**Command Structure:**

Bash

./lc0 \--weights=/path/to/hash.pb.gz \--backend=cuda \--verbose-move-stats

The client injects specific parameters derived from the NextGameResponse:

* **\--noise:** Enables Dirichlet noise at the root node. This introduces randomness, ensuring the engine explores different moves rather than always playing the perceived "best" move. This is fundamental to RL.15  
* **\--noponder:** Disables pondering (thinking on the opponent's time), as self-play happens sequentially on one machine.  
* **\--playouts:** Sets the fixed number of MCTS simulations per move (e.g., 800 nodes). This ensures uniform search depth across the training set.13

### **5.3 Monitoring and Game State**

The client monitors the engine's output. It parses the stream to detect:

1. **Moves:** The sequence of moves played (e.g., e2e4 c7c5).  
2. **MCTS Statistics:** For every move, the engine outputs the "policy" (the visit count distribution of child nodes) and the "value" (the Q-value or win probability). This data *is* the training label.  
3. **Terminal State:** The client waits for the game to end (Checkmate, Draw by repetition, 50-move rule, or Adjudication).

### **5.4 Error Handling during Execution**

The client must handle engine crashes gracefully. If the GPU driver hangs or the engine segfaults:

* The client catches the process termination.  
* It logs the error.  
* It may attempt to restart the engine a few times.  
* Crucially, it discards the partial game data to prevent corrupt samples from reaching the server.

## ---

**6\. Data Upload and Protocol Mechanisms**

Upon game completion, the client enters the upload phase. This is the most complex part of the network protocol, involving detailed metadata and robust retry logic.

### **6.1 The uploadGame Function**

The lc0\_main.go file contains the uploadGame function, which serves as the primary data ingress mechanism.7

**Function Signature:**

Go

func uploadGame(httpClient \*http.Client, path string, pgn string, nextGame client.NextGameResponse, version string, fp\_threshold float64) error

### **6.2 The Multipart Payload**

The client uses the multipart/form-data MIME type for uploads. This is necessary because the upload consists of a "file" (the PGN game record) and various metadata fields.

**Table 2: Upload Payload Parameters (extraParams)**

| Parameter | Source | Description |
| :---- | :---- | :---- |
| user | settings.json | The contributor's username. |
| password | settings.json | The API key or password. |
| version | Const | The client protocol version (e.g., "34"). |
| token | Runtime | A random session ID (strconv.Itoa(randId)) used to deduplicate sessions. |
| training\_id | NextGameResponse | Links the game to the specific training run requested. |
| network\_id | NextGameResponse | Confirms which network generated the data. |
| pgn | Engine Output | The full game record, including MCTS stats as PGN comments. |
| gpu\_id | Config | Which GPU index was used (for multi-GPU rigs). |
| fp\_threshold | Dynamic | A floating-point threshold, likely used for value validation or gating specific data. |

### **6.3 Portable Game Notation (PGN) Format**

The uploaded pgn string is not standard chess PGN. It is enriched with training data.

* **Standard Headers:** , .  
* **Move Comments:** Inside the move text, Lc0 embeds the training labels.  
  * Example: 1\. e4 { \[%eval 0.14\]\[%pi 0.23 0.11...\] }  
  * %eval: The Q-value (expected win rate) from the root node.  
  * %pi: The policy vector (probabilities for all legal moves).

This embedding allows the server to extract the training targets (![][image1] for policy, ![][image2] for value) directly from the game record.

### **6.4 Retry Logic and Exponential Backoff**

The upload process is designed to be resilient to network failures. The source code snippet 7 reveals a specific retry loop:

Go

var retryCount uint32  
for {  
    retryCount++  
    if retryCount \> 3 {  
        return errors.New("UploadGame failed: Too many retries")  
    }  
    //... Construct Request...  
    resp, err := httpClient.Do(request)  
    if err\!= nil {  
        log.Print("Error uploading, retrying...")  
        time.Sleep(time.Second \* (2 \<\< retryCount)) // Backoff  
        continue  
    }  
    //...  
}

**Analysis of Backoff:**

The client uses a bitwise shift (2 \<\< retryCount) to calculate the sleep duration.

* **Attempt 1 Failure:** retryCount becomes 1\. Sleep 2 \<\< 1 \= 4 seconds.  
* **Attempt 2 Failure:** retryCount becomes 2\. Sleep 2 \<\< 2 \= 8 seconds.  
* **Attempt 3 Failure:** retryCount becomes 3\. Sleep 2 \<\< 3 \= 16 seconds.

This exponential growth prevents a "thundering herd" scenario. If the server goes down, thousands of clients will not hammer it instantly upon reboot; their retry attempts will be staggered over time.

### **6.5 Response Handling and Control Flow**

The server's HTTP status code dictates the client's next move.

* **200 OK:** The upload was successful. The response body usually contains the NextGameResponse for the subsequent cycle.  
* **5xx Errors:** Trigger the retry loop.  
* **Special Error Strings:** The client scans the response body for specific phrases.  
  * **"upgrade":** If the response contains the word "upgrade" 7, " upgrade ")\`), the client interprets this as a fatal version mismatch. It logs: *"The lc0 version you are using is not accepted by the server"*. This is a soft-deprecation mechanism used to phase out buggy or incompatible clients.

## ---

**7\. Server-Side Integration and Data Management**

While the client is the focus, its behavior is mirrored by the server's expectations. By analyzing the client's output and the server's schema snippets, we can reconstruct the server-side ingestion pipeline.

### **7.1 Database Schema**

The server stores the uploaded data in a relational database, likely PostgreSQL given the SQL syntax observed.3

**Table training\_games (Inferred):**

* id: Primary Key.  
* user\_id: Foreign Key to the users table.  
* training\_id: The run ID.  
* network\_id: The hash of the network.  
* pgn: The raw game data.  
* created\_at: Timestamp.

**Materialized Views:**

To support the high-traffic leaderboard without querying the massive games table constantly, the server uses materialized views:

* games\_month: Aggregates user contributions for the current month.  
* games\_all: Aggregates lifetime contributions.  
* SQL: CREATE MATERIALIZED VIEW games\_month AS SELECT user\_id, username, count(\*)....3

### **7.2 Validation and the "Rescorer"**

The client is trusted to run the code, but not trusted to generate valid data. The server employs a "Rescorer" component.2

* **Function:** It replays the game moves using a trusted, server-side engine instance or a specialized validator.  
* **Syzygy Tablebases:** The rescorer checks endgame positions against solved tablebases. If the client claims a position is a win, but the tablebase says it's a draw, the game is flagged or corrected.  
* **Illegal Moves:** If the PGN contains illegal moves (e.g., "phantom games" where pieces teleport due to memory corruption), the rescorer rejects the upload.

### **7.3 Duplicate Detection**

The token sent in the upload parameters helps the server detect duplicate uploads. If a client retries an upload that actually succeeded (but the acknowledgment was lost), the server can identify the duplicate token and discard the second copy while still sending a success response to the client.

## ---

**8\. Authentication and Security Model**

The Lc0 project operates on a "Trust but Verify" model typical of volunteer computing.

### **8.1 Credential Transmission**

Authentication is handled via the user and password fields in the multipart body.7 This is a variation of Basic Auth but implemented at the application layer.

* **Risk:** Sending passwords in the body requires the transport to be secure. The reliance on HTTPS is absolute.  
* **Session Management:** There is no complex OAuth handshake. The client sends credentials with *every* upload. This statelessness simplifies the client logic (no token refresh cycles) but increases the overhead per request.

### **8.2 The "Train Only" Flag and Validation**

The train\_only flag 7 is a security and integrity feature.

* **Training Games:** High noise, exploratory. Used to generate data.  
* **Match Games:** Low/Zero noise. Used to evaluate network strength.  
* **Separation:** By flagging a client as train\_only, the server knows not to schedule critical match games on that worker. This is useful for users with unstable hardware or those who wish to contribute without potentially skewing the Elo ratings of new networks with a crash.

### **8.3 Anti-Cheating Measures**

While the client code doesn't show explicit anti-cheat logic, the server-side statistical analysis fills this gap.

* **Distribution checks:** If a client consistently reports higher win rates than the population average for the same network, it is flagged.  
* **Move matching:** The Rescorer ensures the moves are legal and plausible.

## ---

**9\. Failure Modes and Error Handling**

The distributed nature of the system means errors are the norm, not the exception. The client handles several specific failure modes.

### **9.1 Network Mismatches**

If a client has a cached network file that is corrupt, the hash check (NetworkId) will fail. The client handles this by forcing a re-download from the NetworkUrl. This self-healing property ensures that bit-rot on the client disk doesn't propagate to the engine process.

### **9.2 Engine Version Incompatibility**

As noted in the retry logic section, the server can reject clients with an "upgrade" message. This handles the lifecycle of the *engine* binary. If a new network architecture (e.g., the introduction of Squeeze-Excitation units) requires a new engine binary to parse, the server can lock out old clients until they update, preventing them from crashing when trying to load the new .pb.gz files.

### **9.3 "Phantom" Games and Memory Errors**

One of the insidious issues in distributed chess computing is "phantom" pieces or illegal moves caused by non-ECC RAM errors on consumer hardware.17 The client does not strictly validate the *chess logic* (that is the engine's job), but it does monitor the process exit code. If the engine crashes due to an illegal state, the client discards the result. The server's Rescorer provides the final line of defense against subtle corruption that doesn't cause a crash.

## ---

**10\. Conclusion**

The lczero-client represents a masterclass in minimalist distributed system design. By stripping the client of all domain knowledge (chess rules, neural network training logic) and reducing it to a process orchestrator and HTTP courier, the Lc0 developers created a system robust enough to scale to thousands of heterogeneous nodes.

**Key Architectural Takeaways:**

1. **Decoupling:** The separation of the client (Go), Engine (C++), and Training (Python) allows each component to iterate independently.  
2. **Stateless Protocol:** The HTTP-based "ask, play, upload" loop is effectively stateless, simplifying error recovery and scaling.  
3. **Data-Centricity:** The protocol revolves around the integrity of the data (PGN \+ Training Labels) and the definition of the model (Protobufs), ensuring that the distributed compute translates directly into neural network gains.  
4. **Resilience:** Through exponential backoff, hash verification, and strict version policing, the client mitigates the inherent unreliability of the public internet and volunteer hardware.

This architecture has enabled Leela Chess Zero to become one of the strongest chess engines in history, demonstrating the immense power of decentralized, open-source reinforcement learning.

## ---

**11\. Future Outlook**

As the project matures, several evolutions in the client architecture are possible or underway:

* **WebSockets/gRPC:** Moving from HTTP polling to persistent connections could reduce latency for short games and allow real-time server control.  
* **Searchless Chess:** Snippet 12 hints at "Searchless Chess" using Transformers. This would fundamentally change the client's role, potentially requiring it to run different types of inference tasks (e.g., sequence prediction) rather than MCTS games.  
* **WASM Clients:** Porting the client and engine to WebAssembly could allow users to contribute compute directly from a web browser, lowering the barrier to entry even further.

The lczero-client remains the unsung hero of the ecosystem, a silent worker tirelessly orchestrating the millions of games required to solve the mysteries of chess.

### ---

**Sources Used**

* 4 GitHub \- LeelaChessZero/lczero-client  
* 1 GitHub \- LeelaChessZero Pinned Repositories  
* 13 Lc0 Wiki \- Technical Explanation  
* 2 Lc0 Dev \- Overview  
* 7 Source Code \- lc0\_main.go and client package analysis  
* 11 Lc0 Training Data Storage  
* 3 GitHub \- lczero-server Database Schema  
* 10 Lc0 Wiki \- Network Weights  
* 5 GitHub \- lczero-common net.proto Definition  
* 18 Chess Programming Wiki \- Phantom Games  
* 12 GitHub \- Google DeepMind Searchless Chess

#### **Works cited**

1. LCZero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero](https://github.com/LeelaChessZero)  
2. Overview | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/overview/](https://lczero.org/dev/overview/)  
3. LeelaChessZero/lczero-server: The code running the website, as well as distributing and collecting training games \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-server](https://github.com/LeelaChessZero/lczero-server)  
4. LeelaChessZero/lczero-client: The executable that communicates with the server to run selfplay games locally and upload results \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client](https://github.com/LeelaChessZero/lczero-client)  
5. lczero-common/proto/net.proto at master \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-common/blob/master/proto/net.proto](https://github.com/LeelaChessZero/lczero-common/blob/master/proto/net.proto)  
6. lczero-client command \- github.com/LeelaChessZero/lczero-client \- Go Packages, accessed January 27, 2026, [https://pkg.go.dev/github.com/LeelaChessZero/lczero-client](https://pkg.go.dev/github.com/LeelaChessZero/lczero-client)  
7. lczero-client/lc0\_main.go at release \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client/blob/release/lc0\_main.go](https://github.com/LeelaChessZero/lczero-client/blob/release/lc0_main.go)  
8. LeelaChessZero/lczero: A chess adaption of GCP's Leela Zero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero](https://github.com/LeelaChessZero/lczero)  
9. Run Leela Chess Zero client on a Tesla T4 GPU for free (Google Colaboratory), accessed January 27, 2026, [https://lczero.org/dev/wiki/run-leela-chess-zero-client-on-a-tesla-t4-gpu-for-free-google-colaboratory/](https://lczero.org/dev/wiki/run-leela-chess-zero-client-on-a-tesla-t4-gpu-for-free-google-colaboratory/)  
10. Getting Started \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/getting-started/](https://lczero.org/dev/wiki/getting-started/)  
11. LeelaChessZero/lczero-training: For code etc relating to the network training process., accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training](https://github.com/LeelaChessZero/lczero-training)  
12. google-deepmind/searchless\_chess: Grandmaster-Level Chess Without Search \- GitHub, accessed January 27, 2026, [https://github.com/google-deepmind/searchless\_chess](https://github.com/google-deepmind/searchless_chess)  
13. Unsorted docs from GitHub wiki \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/](https://lczero.org/dev/wiki/)  
14. New Neural Network From Scratch \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/HnZ004HssWY](https://groups.google.com/g/lczero/c/HnZ004HssWY)  
15. Debug and test procedures \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/debug-and-test-procedures/](https://lczero.org/dev/wiki/debug-and-test-procedures/)  
16. linrock/lc0-data-converter \- stockfish nnue \- GitHub, accessed January 27, 2026, [https://github.com/linrock/lc0-data-converter](https://github.com/linrock/lc0-data-converter)  
17. ChessQA: Evaluating Large Language Models for Chess Understanding \- arXiv, accessed January 27, 2026, [https://arxiv.org/pdf/2510.23948](https://arxiv.org/pdf/2510.23948)  
18. Monte-Carlo Tree Search \- Chessprogramming wiki, accessed January 27, 2026, [https://www.chessprogramming.org/Monte-Carlo\_Tree\_Search](https://www.chessprogramming.org/Monte-Carlo_Tree_Search)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAgCAYAAAAmG5mqAAABJElEQVR4Xu2RP0uCURTGn6CgKIggkKClMRSapEWaQmxp7SME0dSQHyD8BtHQIg7S0uLg1tYi1uDS2BJNijQlRFA9z3uO1+trUK3hAz9e7nP+vPecC0w11Y8qkluyQ2bIAjkiHf/qHLTtgRZ5IwekTRpki7ySG7Ks5FlyRY5JjXw6p7A/SefuKQcb5J5skqYHqmTOk6Wy+4qjBPvdEnkmH2Q3pJoqsIK7lJ+YKl5M+Y8eu45NzSJT3dJ6h8V0taCMm3ux6ZLfI9nYLJA+bPhYWqUKzjDaWqIT8kJysQl7k/AGQ+n+GkidLjFaqbalJut+DsqTAew6StY8a2Q+Top1COu+kg58Jw1ShxX8SqvkAX8o6MKSxRPZHw9PSlsZcoHJd/h3+gKDYjz3nz0vXQAAAABJRU5ErkJggg==>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>
<!-- PGNTOOLS-LC0-END -->
