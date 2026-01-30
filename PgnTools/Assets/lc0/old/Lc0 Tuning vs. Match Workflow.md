# **The Lc0 Distributed Ecosystem: A Technical Analysis of Match and Tuning Workloads**

## **1\. Introduction: The Distributed Paradigm in Neural Chess**

The development of Leela Chess Zero (Lc0) represents a fundamental departure from traditional chess engine programming. Historically, engines such as Stockfish or Komodo relied on handcrafted evaluation functions—linear combinations of features like material, mobility, and pawn structure—tuned via local optimization techniques. In contrast, Lc0 is an open-source implementation of the AlphaZero algorithm, which replaces human domain knowledge with a deep neural network (DNN) and a general-purpose reinforcement learning (RL) algorithm based on Monte Carlo Tree Search (MCTS).1

This architectural shift necessitates a corresponding shift in infrastructure. The training of deep neural networks requires massive datasets of self-played games to facilitate reinforcement learning. Furthermore, the MCTS algorithm itself depends on a suite of hyperparameters—exploration constants, time management curves, and urgency factors—that cannot be learned via backpropagation and must be optimized externally. Consequently, the Lc0 project operates as a massive distributed system, orchestrating the computational resources of thousands of volunteers to perform two distinct but symbiotic tasks: **Matches** (Self-Play) and **Tuning**.1

This report provides an exhaustive technical analysis of these two task types. It dissects the server-side assignment logic that governs their distribution, the binary and serialization formats used to encode their data, the storage hierarchies that persist their outputs, and the profound impact of Tuning tasks on the operational "manifest" that defines the engine's evolution.

## **2\. System Architecture and Component Interaction**

To understand the distinction between Matches and Tuning, one must first delineate the components of the Lc0 distributed ecosystem. The system is not a monolith but a federated network of independent agents coordinating via strict API protocols.

### **2.1. The Client-Server Topology**

The Lc0 ecosystem is divided into a centralized control plane and a decentralized data plane.

**The Central Infrastructure:**

* **The Data Collection Server:** Written in Go, this component serves as the primary gateway for the distributed grid. It exposes an HTTP API for clients to request work and upload results. Its efficiency is paramount, as it must handle concurrent connections from thousands of clients.1  
* **The Training Server:** A Python-based backend responsible for the actual machine learning workload. It consumes the game data aggregated by the collection server to update the neural network weights using libraries like TensorFlow or PyTorch.5  
* **The Metadata Store:** A PostgreSQL database manages the state of the distributed system, tracking user contributions, Elo ratings, and task assignments. Materialized views (e.g., games\_month, games\_all) are utilized to maintain performant leaderboards and statistical aggregates without locking the primary transaction tables.4

**The Distributed Client:**

* **The Lc0 Engine (lc0):** The core C++ binary. It implements the UCI (Universal Chess Interface) protocol, the MCTS search algorithm, and the neural network inference backend (CUDA, OpenCL, or BLAS).1  
* **The Training Client (lczero-client):** A wrapper application, typically written in Go, that manages the lifecycle of the lc0 engine. It handles network communication, downloads resources (network weights), and enforces the constraints of the assigned task.1

### **2.2. The get\_task Assignment Protocol**

The fundamental mechanism for task distribution is the get\_task API endpoint. When a client initializes, it sends a request to the server declaring its capabilities (e.g., GPU strength, supported instruction sets). The server responds with a JSON payload that dictates the client's behavior for the subsequent cycle.7

This response includes:

1. **Task Type:** Explicitly categorizing the work as selfplay (Match) or match/tuning (Tuning).  
2. **Network Hash:** The specific neural network version (weights file) the client must download and load.  
3. **Hyperparameters:** A dictionary of options overriding the engine's defaults.  
4. **Configuration:** Specific settings for the MCTS, such as Cpuct or noise parameters.

The distinction between Matches and Tuning begins at this protocol level. While Match tasks are generally uniform—assigning the current "best" network for self-play—Tuning tasks are heterogeneous, often assigning experimental networks or non-standard parameters to subsets of the client population to test statistical hypotheses.1

## **3\. Match Tasks: The Engine of Reinforcement Learning**

Match tasks, colloquially known as "Self-Play," constitute the vast majority of the computational workload in the Lc0 grid. Their primary objective is the generation of training data—specifically, positions labeled with MCTS search probabilities and game outcomes—which serve as the ground truth for the next generation of the neural network.

### **3.1. Server Assignment Logic for Matches**

The server assigns Match tasks based on the needs of the reinforcement learning loop. The training pipeline requires a continuously sliding window of games played by the most recent network iterations.

**Assignment Criteria:**

* **Network Freshness:** The server prioritizes generating games for the most recently promoted network. As soon as a new network passes the "gatekeeper" (a verification match), all Match tasks are shifted to this new network ID.8  
* **Data Hunger:** If the reservoir of games for the current training window is insufficient, the server increases the probability of assigning selfplay tasks over tuning tasks.  
* **Client Capability:** Match tasks generally require GPU acceleration to be efficient. The server may reserve CPU-only clients for different workloads or assign them smaller networks to ensure they can complete games within reasonable timeframes.9

### **3.2. Execution: The Self-Play Loop**

Upon receiving a Match task, the client executes the lc0 binary in a specialized self-play mode. This mode differs significantly from standard chess engine analysis.

1. **Exploration vs. Exploitation:** To ensure the network sees a diverse range of positions, self-play games introduce noise into the search. The root node of the MCTS tree is perturbed with Dirichlet noise, and moves are chosen proportionally to their visit counts (soft choice) rather than greedily selecting the best move (hard choice) for the first ![][image1] moves (typically 30\) of the game.10  
2. **Rescoring and Adjudication:** As the game progresses, a "Rescorer" component monitors the board state. It checks positions against Syzygy tablebases (endgame databases) to adjudicate games early if a known win/loss/draw position is reached. It also detects "intentional blunders"—moves that significantly drop the evaluation—to ensure the training data reflects high-quality play.1  
3. **No Pondering:** Unlike tournament play, where an engine "ponders" during the opponent's time, self-play engines run sequentially or in parallel batches without pondering to maximize throughput.11

### **3.3. Data Format: The V6 Training Data Standard**

The output of a Match task is not a PGN text file. Text formats are inefficient for the massive volume of data Lc0 generates. Instead, Lc0 uses a highly optimized binary format. The current standard is the **Version 6 (V6)** training data format.12

The V6 format is defined as a PACKED\_STRUCT in C++, ensuring exact byte alignment for direct memory mapping. A single training record is exactly **8356 bytes** in size.12

#### **3.3.1. Structural Decomposition of V6 Data**

The V6TrainingData struct encapsulates all information required to train the neural network to predict the MCTS output.

| Field | Type | Size | Description |
| :---- | :---- | :---- | :---- |
| version | uint32\_t | 4 bytes | Format version identifier (must be 6). |
| input\_format | uint32\_t | 4 bytes | Defines the encoding of planes (e.g., INPUT\_CLASSICAL\_112\_PLANE). |
| probabilities | float | 7432 bytes | The policy target: a probability distribution over all 1858 possible legal moves in chess (including promotions). |
| planes | uint64\_t | 832 bytes | The compressed input representation of the board state (see Section 3.3.2). |
| castling\_rights | uint8\_t | 4 bytes | Broken into us\_ooo, us\_oo, them\_ooo, them\_oo. |
| side\_to\_move | uint8\_t | 1 byte | Or en passant column mask in V5+. |
| rule50\_count | uint8\_t | 1 byte | Half-move clock for the 50-move rule. |
| result\_q | float | 4 bytes | The game outcome from the root perspective (-1, 0, 1). |
| result\_d | float | 4 bytes | The game outcome from the perspective of the side to move. |
| root\_q / best\_q | float | 8 bytes | MCTS value estimates for the root and best move. |
| visits | uint32\_t | 4 bytes | The total node count of the search, serving as a confidence weight. |
| **Total** |  | **8356 bytes** | Confirmed by static\_assert. |

#### **3.3.2. The 104 Input Planes**

A critical distinction exists between the *conceptual* input to the neural network and the *stored* input in the V6 format. The Lc0 architecture typically utilizes **112 input planes** (8x8 grids of binary features).13 However, the V6 struct allocates storage for only **104 planes** (uint64\_t planes).

The resolution to this discrepancy lies in data compression and reconstruction:

1. **History Stack:** The network input includes the current board position plus the history of the last 7 moves (T=8 time steps).  
2. **Per-Step Features:** For each of the 8 time steps, the system extracts 13 fundamental binary features:  
   * 6 planes: Current player's pieces (P, N, B, R, Q, K).  
   * 6 planes: Opponent's pieces (P, N, B, R, Q, K).  
   * 1 plane: Repetitions (indicating if the position has occurred 1 or 2 times previously).  
3. **Storage Calculation:** ![][image2]. These are stored explicitly in the planes array.13  
4. **Metadata Expansion:** The remaining 8 conceptual planes (to reach 112\) represent global state information that is constant across the entire board: castling rights, side to move, and move counts. These are stored as scalar integers (uint8\_t) in the V6 struct to save space. During the training phase, the lczero-training pipeline reads these scalars and broadcasts them into full 8x8 planes of constant 0s or 1s before feeding the tensor to the GPU.13

### **3.4. Storage and Transmission**

The generation of Match data is a high-volume operation. To manage this, the system employs a chunking strategy.

**Local Aggregation:** The lczero-client does not upload individual games. Instead, it aggregates completed games into "chunks." A chunk is a binary file containing a concatenated sequence of V6 records. These files are temporarily stored in the client's local storage, often in a directory named files/ or a hashed subdirectory structure, to handle network interruptions.17

**Server-Side Ingestion:**

When a chunk reaches a specific size threshold or time limit, the client performs an HTTP POST request to the data collection server. The server verifies the integrity of the data and then persists the chunk to a blob storage service.

* **Storage Location:** The primary storage backend is typically Google Cloud Storage, as evidenced by references to storage.lczero.org.  
* **Metadata Indexing:** While the binary blobs are stored in object storage, the metadata (chunk ID, user ID, network ID, number of games) is inserted into the PostgreSQL database. This allows the training servers to efficiently query and download specific subsets of data (e.g., "all chunks from Run 3 generated in the last 24 hours").4

## **4\. Tuning Tasks: The Optimization of Search and Architecture**

While Match tasks provide the *knowledge* (weights), Tuning tasks provide the *wisdom* (parameters). Tuning tasks are fundamentally different in objective: they do not seek to generate training data but to evaluate statistical hypotheses regarding the engine's configuration.

### **4.1. Categories of Tuning**

Within the Lc0 system, "Tuning" encompasses two primary methodologies: **CLOP** and **OpenBench**.

#### **4.1.1. CLOP (Confidence Local Optimization of Parameters)**

CLOP is a Bayesian optimization algorithm used to tune the scalar hyperparameters of the MCTS search. Parameters such as Cpuct (which balances exploration/exploitation), FPU (First Play Urgency), and CpuctFactor define the shape of the search tree. These values have non-linear effects on playing strength and cannot be derived analytically.20

* **Mechanism:** CLOP maintains a probability distribution over the optimal values of these parameters. It issues tasks to clients to play games using specific parameter values chosen to maximize the information gain (reducing uncertainty about the optimum).

#### **4.1.2. OpenBench (SPRT)**

OpenBench is a distributed framework for Sequential Probability Ratio Testing (SPRT). It is used to validate binary changes, such as a new neural network architecture or a patch to the C++ engine code.1

* **Mechanism:** Clients are assigned a "Match" between a **Candidate** (the new configuration) and a **Base** (the current standard). The system tracks the sequence of wins, losses, and draws to calculate a Log-Likelihood Ratio (LLR). The test continues until the LLR exceeds a threshold for acceptance (proving superiority) or rejection (proving inferiority).22

### **4.2. Server Assignment and Execution**

The assignment of Tuning tasks is driven by the active "Tests" in the system.

**Assignment Logic:**

When a client requests a task via get\_task, the server checks if the client qualifies for any active tuning runs. Qualification is often stricter for Tuning than for Matches:

1. **Client Version:** OpenBench tests often require specific, newer versions of the lc0 binary or the client to support new features (e.g., "Squeeze-Excitation" support in Run 2).6  
2. **Hardware Stability:** Tuning requires reliable results. A client that frequently crashes or produces inconsistent evaluations may be excluded from tuning pools.  
3. **Task Payload:** The server responds with a match type task. The payload includes the **Options** dictionary, which is critical.  
   * *Match Task Payload:* { "type": "selfplay", "network": "T80\_123",... }  
   * *Tuning Task Payload:* { "type": "match", "network": "T80\_123", "options": { "Cpuct": 2.8, "FPU": 0.5 }, "opponent": {... } }

**Execution Mechanics:** The client launches the engine with the overrides provided in the options field. If the task is an OpenBench match, the client may launch *two* engine instances (Candidate and Base) and arbitrate a game between them locally, reporting only the result.24

### **4.3. Data Format: JSON Results**

Unlike Match tasks, Tuning tasks generally do not produce binary V6 data. The output of a Tuning task is the **Result** of the game, not the content of the search tree.

**The Result Payload:**

The client submits a lightweight JSON object to the server API:

* **Game Result:** Encoded as "1-0" (White wins), "0-1" (Black wins), or "1/2-1/2" (Draw).  
* **PGN (Portable Game Notation):** A text record of the moves played. This is stored for verification and debugging but is not used for network training.20  
* **Metadata:** Client ID, hardware info, and the specific parameters used.

**Efficiency:**

This distinction significantly reduces bandwidth usage for Tuning tasks. A Match task might upload megabytes of training planes; a Tuning task uploads kilobytes of text.

### **4.4. The Manifest Impact: Evolution of the Engine**

The most critical distinction of Tuning tasks lies in their downstream impact. While Match tasks iteratively improve the *current* network, Tuning tasks determine the *future* structure of the entire system. This structural state is referred to as the **Manifest**.

#### **4.4.1. Parameter Crystallization**

When a CLOP tuning run concludes that a specific parameter value (e.g., Cpuct \= 2.4) is optimal, this value is "crystallized" into the system manifest.

1. **Immediate Effect:** The server updates the default configuration sent to all clients for *future* Match tasks.  
2. **Long-Term Effect:** The default values in the C++ source code (lc0 repo) are updated, changing the baseline for all future releases.25

#### **4.4.2. Network Architecture and Runs**

The history of Lc0 is divided into "Runs," each defined by a specific network architecture. The transition between runs is the result of massive Tuning campaigns.

* **Run 1 (T40):** Defined by 20 residual blocks x 256 filters.  
* **Run 2 (T60):** Introduced larger architectures (24 blocks x 320 filters) and architectural changes like Squeeze-Excitation (SE) layers.  
* **Run 3 (T80):** Introduced even deeper networks and new activation functions like **Mish** (replacing ReLU).

**The Role of Tuning in Transitions:**

Before the system moves from Run 1 to Run 2, Tuning tasks (SPRT) must prove that the new architecture (e.g., SE-ResNet) provides a statistically significant Elo gain per unit of compute compared to the old architecture.

* *Manifest Shift:* Once a Tuning task validates the new architecture, the "Manifest" changes. The server instructs all clients to stop downloading T40 networks and start downloading T60 networks. Clients with outdated binaries that cannot support the new architecture (e.g., lacking Mish support) are effectively deprecated until updated.23

## **5\. Detailed Comparative Analysis**

The following table summarizes the technical distinctions between the two task types:

| Feature | Match Tasks (Self-Play) | Tuning Tasks (CLOP / OpenBench) |
| :---- | :---- | :---- |
| **Objective** | Generate probabilistic training data (p and v targets). | Optimize scalar parameters or validate architectures. |
| **Engine Configuration** | Self-Play (Same Network vs. Same Network). | Adversarial (Candidate Params vs. Base Params). |
| **MCTS Behavior** | High exploration (Dirichlet noise enabled). | Competitive play (Temperature \-\> 0, low noise). |
| **Data Format** | **V6 Binary** (PACKED\_STRUCT, 8356 bytes/record). | **JSON Result** (Win/Loss/Draw \+ PGN). |
| **Data Content** | Full board history (104 planes) \+ Probabilities. | Game outcome only. |
| **Storage** | Binary Chunks in Object Storage (Blob). | Relational Database Records (SQL). |
| **System Impact** | Incremental improvement of network weights. | **Discrete jumps** in architecture or default parameters. |

## **6\. Storage Hierarchies and Persistence**

The Lc0 distributed system relies on a tiered storage architecture to manage the flow of data from thousands of clients.

### **6.1. Client-Side Storage Structure**

Upon installation, the client creates a specific directory structure.

* **lc0.exe / client.exe:** The executables.  
* **weights/:** A cache directory where downloaded protobuf network files are stored. Filenames typically correspond to the network hash (e.g., weights\_11198).28  
* **files/ (or temp):** The staging area for V6 binary chunks. As the engine plays Match tasks, raw memory buffers are flushed to this directory. The client monitors this folder and initiates uploads when files reach a target size (e.g., \~1-2 MB). This ensures that data is not lost if the client crashes or restarts.18  
* **leelaz\_opencl\_tuning:** A specific file generated during the client's first run. This is *local* hardware tuning (optimizing OpenCL kernel workgroup sizes for the specific GPU) and is distinct from the distributed Tuning tasks.29

### **6.2. Server-Side Persistence Layers**

* **Blob Storage:** The raw V6 training chunks are immutable and voluminous. They are stored in object storage (Google Cloud Storage buckets), organized by Run ID and Network ID. This allows the training pipeline to stream data sequentially without seeking.19  
* **PostgreSQL:** The relational database acts as the index. It links a specific GameID to a UserID and NetworkID.  
  * **Materialized Views:** To handle the scale, the server uses materialized views like games\_all to pre-calculate user statistics. This avoids expensive COUNT(\*) operations on the massive training\_games table during API requests.4

## **7\. Conclusion**

The Lc0 distributed system is a sophisticated dual-loop machine. The **Match Loop** is a high-bandwidth, data-intensive cycle that continuously refines the neural network's intuition through massive generation of V6 binary training data. The **Tuning Loop** is a high-latency, control-logic cycle that utilizes statistical methods like CLOP and SPRT to refine the parameters and architecture that govern the system.

The distinction is enforced at every level of the stack: from the get\_task API protocol that dispatches the work, to the binary vs. JSON data formats used to report it, and finally to the storage backends that persist it. While Match tasks provide the *fuel* (data), Tuning tasks provide the *steering* (manifest), ensuring that Leela Chess Zero evolves not just in knowledge, but in structural efficiency.

#### **Works cited**

1. Overview | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/overview/](https://lczero.org/dev/overview/)  
2. Backend \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/](https://lczero.org/dev/backend/)  
3. Leela Chess Zero \- Chessprogramming wiki, accessed January 27, 2026, [https://www.chessprogramming.org/Leela\_Chess\_Zero](https://www.chessprogramming.org/Leela_Chess_Zero)  
4. LeelaChessZero/lczero-server: The code running the website, as well as distributing and collecting training games \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-server](https://github.com/LeelaChessZero/lczero-server)  
5. LCZero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero](https://github.com/LeelaChessZero)  
6. LeelaChessZero/lczero-client: The executable that communicates with the server to run selfplay games locally and upload results \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client](https://github.com/LeelaChessZero/lczero-client)  
7. ray \- Read the Docs Business, accessed January 27, 2026, [https://readthedocs.com/projects/anyscale-ray/builds/1351587/](https://readthedocs.com/projects/anyscale-ray/builds/1351587/)  
8. LCZero, accessed January 27, 2026, [http://api.lczero.org/](http://api.lczero.org/)  
9. Which settings to use for Lc0 to run optimally? : r/chess \- Reddit, accessed January 27, 2026, [https://www.reddit.com/r/chess/comments/192c95c/which\_settings\_to\_use\_for\_lc0\_to\_run\_optimally/](https://www.reddit.com/r/chess/comments/192c95c/which_settings_to_use_for_lc0_to_run_optimally/)  
10. Technical Explanation of Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/technical-explanation-of-leela-chess-zero/](https://lczero.org/dev/wiki/technical-explanation-of-leela-chess-zero/)  
11. Optimal Engine Parameters for an Engine Match between Lc0 and SF12 \- Reddit, accessed January 27, 2026, [https://www.reddit.com/r/ComputerChess/comments/ja75jn/optimal\_engine\_parameters\_for\_an\_engine\_match/](https://www.reddit.com/r/ComputerChess/comments/ja75jn/optimal_engine_parameters_for_an_engine_match/)  
12. Training data format versions \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/training-data-format-versions/](https://lczero.org/dev/wiki/training-data-format-versions/)  
13. Contrastive Sparse Autoencoders for Interpreting Planning of Chess-Playing Agents \- arXiv, accessed January 27, 2026, [https://arxiv.org/html/2406.04028v1](https://arxiv.org/html/2406.04028v1)  
14. Learning to Play \- LIACS, accessed January 27, 2026, [https://liacs.leidenuniv.nl/\~plaata1/papers/ptl2.pdf](https://liacs.leidenuniv.nl/~plaata1/papers/ptl2.pdf)  
15. lczero-training/tf/chunkparser.py at master · LeelaChessZero/lczero, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training/blob/master/tf/chunkparser.py](https://github.com/LeelaChessZero/lczero-training/blob/master/tf/chunkparser.py)  
16. Contrastive Sparse Autoencoders for Interpreting ... \- OpenReview, accessed January 27, 2026, [https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf](https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf)  
17. Koivulehto Arvi | PDF | Kernel (Operating System) \- Scribd, accessed January 27, 2026, [https://www.scribd.com/document/911325618/Koivulehto-Arvi](https://www.scribd.com/document/911325618/Koivulehto-Arvi)  
18. README.md \- LeelaChessZero/lczero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero/blob/release/README.md](https://github.com/LeelaChessZero/lczero/blob/release/README.md)  
19. New Neural Network From Scratch \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/HnZ004HssWY](https://groups.google.com/g/lczero/c/HnZ004HssWY)  
20. CLOP tuning \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/clop-tuning/](https://lczero.org/dev/wiki/clop-tuning/)  
21. LeelaChessZero repositories \- GitHub, accessed January 27, 2026, [https://github.com/orgs/LeelaChessZero/repositories](https://github.com/orgs/LeelaChessZero/repositories)  
22. OpenBench, accessed January 27, 2026, [https://bench.lczero.org/](https://bench.lczero.org/)  
23. Project History \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/project-history/](https://lczero.org/dev/wiki/project-history/)  
24. Running a benchmark \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/running-a-benchmark/](https://lczero.org/dev/wiki/running-a-benchmark/)  
25. Tuning Search Parameters \- TalkChess.com, accessed January 27, 2026, [https://talkchess.com/viewtopic.php?t=78896](https://talkchess.com/viewtopic.php?t=78896)  
26. A Layman's Guide to Configuring lc0 | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/blog/2020/04/a-laymans-guide-to-configuring-lc0/](https://lczero.org/blog/2020/04/a-laymans-guide-to-configuring-lc0/)  
27. Best Nets for Lc0 \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/best-nets-for-lc0/](https://lczero.org/dev/wiki/best-nets-for-lc0/)  
28. Creating yet another Lc0 benchmark on gpu/cpu \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/1sJcvkttLfA](https://groups.google.com/g/lczero/c/1sJcvkttLfA)  
29. GPU set up/ tuning GPU \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/UKmBXeJH72c](https://groups.google.com/g/lczero/c/UKmBXeJH72c)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABYAAAAfCAYAAADjuz3zAAABeklEQVR4Xu2UvytGURjHH0URkiKLesumlEEGZcJgIbEoG8WfoEwG/gGZRCYxWJXxLYsfg8mPlEVYTQYT32/PPe99zrnvvefOup/6LOc8995zvs85V6TiX7EK34yvcMqrUGrwRvxaegY7TV2DOXgAT+AP/IWnsNUWgX64A+uiNfQILsCWtCzLMPwQfeALjvjTDbpEX34cjOeyl/gu+vInOOBVKOOicXEhUdwqVuC+pFtdNjUO1tRFn4kyBB/gKJyQ4qwPRXdWinl4C3thB7yU5llz/g4umrFCdkVX4mAELo5tM85cubNS+TKGRzgWjPNjYdauwaWYgfewLxi3WTOabngl2rxSbIlejvCQ26z5AW7/RbTBUdrhBdwIJxJs1kuijWMDowzCZzgZTiTwgvCiuDhsgwvZhOeSPasWHjceu2/JNjgXroIZF8GP8qLwmIUN9miD03BNdIv89a0nY5xrxqzoTydssEcNfkraFOc17DF1Fo7nNbiioiLGHyEOWWRG7jTXAAAAAElFTkSuQmCC>

[image2]: <data:image/png;base64,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>