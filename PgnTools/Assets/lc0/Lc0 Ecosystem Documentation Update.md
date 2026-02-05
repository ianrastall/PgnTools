<!-- PGNTOOLS-LC0-BEGIN -->
# **Lc0 Ecosystem: Advanced Technical Documentation and Architecture Report**

## **1\. Executive Summary and Ecosystem Topography**

The Leela Chess Zero (Lc0) project represents a paradigm shift in computer chess, moving from handcrafted evaluation functions to a generalized, self-learning neural network architecture inspired by DeepMind's AlphaZero. As of late 2025, this ecosystem has matured into a complex, distributed infrastructure that facilitates a closed-loop reinforcement learning cycle. This report serves to update and expand upon existing technical documentation, providing a granular analysis of the system's components, their interactions, and the data artifacts that drive the learning process.

The Lc0 ecosystem is not merely a chess engine; it is a distributed supercomputer. It comprises a central orchestration layer, a massive grid of volunteer compute nodes, a sophisticated training pipeline, and a rigorous validation framework. The primary objective of this system is to generate "crystallized intelligence"—neural network weights—by distilling the experience of billions of self-played games. This report analyzes the architecture based on primary source code repositories, commit histories, and official documentation, ensuring a code-anchored understanding of the system's mechanics.

The analysis reveals a significant evolution from the early days of the project. The transition from simple Convolutional Neural Networks (CNNs) to Transformer-based architectures, the adoption of rigorous sequential probability ratio testing (SPRT) via OpenBench, and the decoupling of inference backends through ONNX all point to a maturing platform. This document will dissect these advancements, providing the technical depth required for developers and researchers operating within or alongside the Lc0 ecosystem.

## **2\. Methodology and Gap Analysis**

To ensure this report provides maximum utility, we first established a functional inventory of the Lc0 ecosystem as it exists in the 2024-2025 timeframe and compared it against the implicit baseline of historical documentation (circa 2021). This gap analysis highlights the critical areas where the ecosystem has diverged or expanded, serving as the prioritized roadmap for the subsequent chapters.

### **2.1 prioritized Gap List**

The following list identifies the discrepancies between legacy understanding and the current operational reality of Lc0. These gaps constitute the primary focus areas for this report.

| Priority | Gap Category | Description of Divergence |
| :---- | :---- | :---- |
| **Critical** | **Training Data Semantics** | The shift from **V5** to **V6** training data formats involves fundamental changes in how game results and search probabilities are encoded. The introduction of "Value Repair" fields (orig\_q, played\_q) and detailed bitfields for invariance represents a major upgrade in data richness that legacy documentation fails to capture.1 |
| **Critical** | **Evaluation Model** | The transition from a scalar value target to a **Win/Draw/Loss (WDL)** probability vector fundamentally alters the engine's "contempt" logic. Understanding WDLCalibrationElo and how the engine navigates drawish positions is now essential for configuring the engine correctly.2 |
| **High** | **Backend Architecture** | The introduction of **ONNX** as a first-class citizen decouples the network architecture from the engine binary. This allows for the use of external inference runtimes like **TensorRT** and **DirectML**, moving beyond the hardcoded CUDA/OpenCL kernels of previous versions.3 |
| **High** | **Validation Framework** | The integration of **OpenBench** as the standard for validating engine and network changes has formalized the QA process. The workflow for submitting and analyzing SPRT tests is now a critical part of the developer loop.5 |
| **Medium** | **Network Topology** | The emergence of **Transformer (BT)** architectures and "smolgen" (small generator) experiments indicates a move away from pure ResNets. Documentation must now account for attention mechanisms and their specific inference requirements.6 |

## **3\. The Lc0 Ecosystem Topography**

The Lc0 project is organized as a federation of loosely coupled services and tools, primarily hosted under the LeelaChessZero GitHub organization. Each repository serves a distinct functional role in the pipeline, from generating data to training networks and validating results.

### **3.1 Repository Inventory and Functional Roles**

The following table provides an exhaustive inventory of the official repositories, defining their purpose, programming language, and criticality to the central learning loop. This inventory is derived from an analysis of repository activity and descriptions.7

| Repository | Functional Role | Language | Criticality | Description & Artifacts |
| :---- | :---- | :---- | :---- | :---- |
| **lc0** | **Inference Engine** | C++ | **Core** | The UCI chess engine. Responsible for MCTS search, neural inference via backends (CUDA, OpenCL, etc.), and generating self-play data. Produces PGNs and training chunks. 8 |
| **lczero-client** | **Grid Worker** | Go | **Core** | The distributed client executable. Manages authentication, network downloads, and the upload of game data to the central server. Wraps the engine execution. 10 |
| **lczero-server** | **Orchestrator** | Go | **Core** | The central API and database backend. Manages user accounts, distributes training tasks (network hashes, parameters), and ingests game data. 11 |
| **lczero-training** | **Learning Pipeline** | Python | **Core** | The deep learning codebase. Handles data parsing (TFRecords/Chunks), training loops (TensorFlow/PyTorch), and network export (Protobuf/ONNX). 12 |
| **OpenBench** | **Validation** | Python | High | A distributed SPRT framework fork. Orchestrates match-play testing to statistically validate improvements in the engine or networks. 5 |
| **lczero-common** | **Data Schema** | C++ | High | Shared library containing protobuf definitions for weights files and network topology specifications. Ensures compatibility between engine and trainer. 13 |
| **lczero-live** | **Visualization** | TypeScript | Medium | Frontend code for live broadcasting of engine analysis, typically used for visualizing TCEC matches or other events. 7 |
| **lczero.org** | **Documentation** | HTML/CSS | Medium | The source for the official project website and documentation wiki. 7 |

### **3.2 Architectural Data Flow**

The architecture of Lc0 is best understood as a cyclic graph where data flows from the edge (clients) to the center (server/training) and back to the edge (new networks). The following diagram illustrates this flow, highlighting the interaction between the distributed components and the centralized infrastructure.

Code snippet

graph TD  
    subgraph Edge\_Compute  
        C1\[Client Node 1\]  
        C2\[Client Node 2\]  
        C3\[Client Node N\]  
        OB\_Client  
    end

    subgraph Central\_Services \[Central Infrastructure\]  
        LB\[Nginx Proxy\]  
        API\[lczero-server API\]  
        DB  
        S3  
        OB\_Server  
    end

    subgraph Training\_Core  
        Rescorer  
        Trainer  
        Exporter\[Network Exporter\]  
    end

    %% Self-Play Loop  
    C1 \--\>|1. Request Task| API  
    API \--\>|2. Network Hash & Params| C1  
    C1 \--\>|3. Download Weights| S3  
    C1 \-- Generates Games \--\> C1  
    C1 \--\>|4. Upload PGN/Chunks| LB  
    LB \--\> API  
    API \--\>|Meta| DB  
    API \--\>|Raw Data| S3

    %% Training Loop  
    S3 \--\>|5. Fetch Chunks| Rescorer  
    Rescorer \--\>|6. Formatted Data (V6)| Trainer  
    Trainer \-- Learning \--\> Trainer  
    Trainer \--\>|7. Export Weights| Exporter  
    Exporter \--\>|8. Upload New Net| API  
      
    %% Validation Loop  
    Exporter \-.-\>|Candidate Net| OB\_Server  
    OB\_Server \--\>|SPRT Task| OB\_Client  
    OB\_Client \--\>|Match Results| OB\_Server  
    OB\_Server \-.-\>|Pass/Fail| API

This diagram underscores the centrality of the API server and Object Storage (S3). The lczero-server acts as the command-and-control center, while the storage layer holds the massive datasets required for training. The OpenBench system sits tangentially to the main loop, acting as a gatekeeper to ensure that only statistically superior networks or code changes are promoted.

## **4\. The Inference Engine: lc0**

The lc0 repository hosts the source code for the chess engine itself. Unlike traditional engines that rely on heuristic evaluation functions, lc0 is a neural-network-driven Monte Carlo Tree Search (MCTS) engine. It is the "brain" that runs on the client nodes.

### **4.1 Internal Architecture and Search Logic**

The engine's architecture is modular, designed to separate the chess rules, the search algorithm, and the neural inference backend.

#### **4.1.1 The MCTS Search Core**

The search logic resides primarily in the search/ directory. Lc0 implements a variant of MCTS that uses the neural network to guide the expansion of the tree.

* **Selection:** The search traverses the tree using the PUCT (Predictor \+ Upper Confidence Bound applied to Trees) formula. This balances exploitation (visiting moves with high value ![][image1]) and exploration (visiting moves with high prior probability ![][image2] but low visit count ![][image3]).  
* **Expansion & Evaluation:** When a leaf node is reached, the engine queries the neural network. The network returns a **Policy** vector (![][image2], probabilities for all legal moves) and a **Value** (![][image4], the expected outcome of the game).  
* **Backup:** The value ![][image4] is propagated up the tree, updating the ![][image1] values of all parent nodes.

Recent updates to the engine have introduced the **dag-preview** search algorithm.14 Unlike the standard tree search, which treats transpositions (reaching the same position via different move orders) as separate nodes, the DAG (Directed Acyclic Graph) search recognizes these convergences. This allows the engine to share evaluation results across different branches of the search, significantly improving efficiency in transposition-heavy positions.

#### **4.1.2 The Rescorer**

A critical component recently integrated directly into the lc0 codebase is the **Rescorer**.3 Previously a separate tool, the rescorer is responsible for processing game data *after* it has been played but *before* it is used for training.

* **Function:** It traverses the game record and uses endgame tablebases (Syzygy) to assign perfect value targets to positions that resolved into known endgames.  
* **Impact:** This "Value Repair" ensures that the network learns from perfect information rather than the potentially flawed evaluation of the engine during the game. The integration of this logic into the main binary simplifies the toolchain for researchers.

### **4.2 Backend Abstraction Layer**

One of Lc0's most powerful architectural features is its hardware abstraction. The neural/ directory defines an interface that allows the engine to run on a wide variety of hardware without changing the search code.15

#### **4.2.1 Compute Backends**

The engine supports multiple backends, each optimized for specific hardware drivers:

* **CUDA (NVIDIA):** The gold standard for performance. It utilizes the cuDNN library to execute the neural network. It supports **FP16** (half-precision) inference, leveraging the Tensor Cores available on NVIDIA RTX (Volta, Turing, Ampere, Ada) cards to double throughput compared to FP32.9  
* **OpenCL:** A general-purpose backend used for AMD GPUs and older NVIDIA cards. While generally slower than CUDA, it allows for manual tuning of kernel parameters (e.g., tile sizes) to optimize performance on specific architectures.16  
* **Metal (Apple):** Optimized for macOS devices with Apple Silicon (M1/M2/M3). This backend uses Apple's Metal Performance Shaders (MPS) to run the network on the Neural Engine and GPU of Mac devices.9  
* **BLAS/DNNL (CPU):** Fallback backends for systems without GPUs. These use OpenBLAS or Intel's oneDNN (Deep Neural Network Library) to run the network on the CPU. Performance is orders of magnitude lower than GPU backends, making them suitable only for very small networks or debugging.17

#### **4.2.2 The Rise of ONNX**

A significant evolution in the ecosystem is the adoption of **ONNX** (Open Neural Network Exchange).

* **Mechanism:** Lc0 can now load networks converted to the ONNX format. This allows the engine to use the **ONNX Runtime** library, which can automatically select the best execution provider for the host system.4  
* **TensorRT:** The ONNX backend enables the use of NVIDIA's **TensorRT**, an inference optimizer that can fuse layers and tune kernels specifically for the target GPU, potentially outperforming the handwritten CUDA kernels in the standard backend.3  
* **DirectML:** For Windows users on AMD or Intel graphics, the ONNX-DirectML backend provides a high-performance alternative to OpenCL, leveraging Microsoft's machine learning APIs.17

## **5\. The Distributed Grid: Client Mechanics**

The lczero-client is the workhorse of the ecosystem. It is a standalone Go application that wraps the lc0 engine, managing the logistics of distributed computation.

### **5.1 Client-Server Protocol**

The interaction between the client and the lczero-server is a stateless, HTTP-based polling loop. Analysis of the lc0\_main.go source code reveals the following protocol workflow 10:

1. **Initialization & Authentication:**  
   * The client starts and reads its configuration, which includes the username, password, and server URL (defaulting to https://api.lczero.org or a test instance).  
   * It detects the available hardware (GPU type) and selects the appropriate engine binary.  
2. **Task Acquisition (GET /next\_game):**  
   * The client polls the /next\_game endpoint.  
   * **Response Payload:** The server returns a JSON object containing:  
     * TrainingId: The ID of the current training run.  
     * NetworkId: The SHA-256 hash of the network weights to be used.  
     * Params: A set of UCI options (e.g., CPuct, Noise, Playouts) that define the search parameters for the self-play game. This ensures all clients generate data with consistent characteristics.  
3. **Resource Management:**  
   * The client checks if the requested network (NetworkId) is present in its local cache.  
   * If missing, it downloads the weights from the server or a configured mirror (--network-mirror).10  
4. **Game Execution:**  
   * The client launches lc0 as a subprocess, communicating via standard input/output using the UCI protocol.  
   * It instructs the engine to play a game against itself (self-play).  
   * The engine outputs the game moves and, crucially, the **training data** (probabilities and evaluations for each position).  
5. **Data Upload (POST /upload\_game):**  
   * Upon game completion, the client bundles the result into a request.  
   * The uploadGame function constructs a multipart MIME POST request containing:  
     * pgn: The game record.  
     * training\_id & network\_id: Metadata for indexing.  
     * engineVersion: To track client software versions.  
   * The client implements retry logic (exponential backoff) to handle transient network failures during upload.10

### **5.2 Fault Tolerance and Binary Management**

The client is designed to be resilient. It includes logic to handle "bad" networks or engine crashes. Recent versions of the client have streamlined the binary structure; often, the client and engine are packaged together, or the client is renamed to lc0-training-client to distinguish it from the user-facing engine.19 The client also enforces version checks on the Tilps/chess library it uses for local move validation.10

## **6\. The Learning Loop: Training Pipeline and Data Artifacts**

The lczero-training repository contains the machinery that turns raw games into intelligence. This pipeline is complex, handling massive datasets and executing advanced deep learning operations.

### **6.1 Data Formats: The Shift to V6**

A critical aspect of the ecosystem is the strict definition of data formats. The training data generated by the engine has evolved to support more advanced training techniques.

#### **6.1.1 V6 Training Data Specification**

The current standard is **Format V6**. This format is significantly richer than previous iterations (V3-V5), designed specifically to support **Value Repair** and more granular analysis. According to the source definitions, the V6TrainingData struct is exactly **8356 bytes** and includes the following fields 1:

* **Probabilities:** The raw policy vector from the MCTS search.  
* **Planes:** The compressed representation of the board state (pieces, history).  
* **Value Fields:**  
  * result\_q: The final game outcome (1.0 for win, 0.0 for loss, 0.5 for draw).  
  * result\_d: A specialized field distinguishing decisive results from draws.  
  * root\_q / best\_q: The Q-values (expected score) at the root of the search tree.  
* **Repair Fields (New in V6):**  
  * orig\_q, orig\_d, orig\_m: The values as they were *during the game*.  
  * played\_q, played\_d, played\_m: The values derived from the move *actually played*.  
  * **Significance:** By storing both the original values and the outcome, the training pipeline can calculate the error in the engine's evaluation at generation time. This allows for "Value Repair," where the training target is adjusted based on the known outcome, correcting for search artifacts.  
* **Invariance Info:** A detailed bitfield encoding the side-to-move and symmetry transforms (mirror/flip) applied to the position. This allows the trainer to augment the data by leveraging the geometric symmetries of the chess board.

### **6.2 Neural Network Architectures**

The training pipeline supports generating various neural network topologies.

#### **6.2.1 Residual Networks (ResNets)**

The workhorse of Lc0 is the ResNet.

* **Input:** 112 planes of 8x8 squares (representing piece locations, history, castling rights, etc.).  
* **Body:** A stack of "Residual Blocks". A typical strong network might be "20x256" (20 blocks, 256 filters per convolutional layer).  
* **Squeeze-and-Excitation (SE):** Most modern Lc0 ResNets include SE layers. These layers allow the network to perform global context pooling, re-weighting the channels based on the overall board state rather than just local 3x3 features.13

#### **6.2.2 Transformer Architectures (Big Transformer / BT)**

The ecosystem is actively experimenting with Transformer-based architectures, inspired by Large Language Models (LLMs).

* **Attention Mechanisms:** Instead of fixed convolutions, these networks use attention heads to dynamically focus on relevant parts of the board, regardless of distance.  
* **Smolgen:** The pipeline includes experimental support for "smolgen" (Small Generator), a technique possibly related to efficiently generating value/policy targets or latent representations.6  
* **Status:** While ResNets remain the standard for efficiency, "BT" nets are pushing the boundary of maximum strength, albeit with much higher inference costs.

### **6.3 Loss Functions: WDL and Contempt**

The training objective has shifted from minimizing the error of a single scalar value to modeling the full **Win/Draw/Loss (WDL)** distribution.

* **WDL Heads:** The value head of the network now outputs three probabilities: ![][image5], ![][image6], ![][image7].  
* **Training Configuration:** The YAML configs for training now specify distinct weights for policy loss and value loss (e.g., policy\_loss\_weight: 1.0, value\_loss\_weight: 1.0).20  
* **Contempt:** This WDL output allows the engine to implement "contempt." By adjusting the utility of a Draw (e.g., valuing a draw as 0.0 instead of 0.5 in a must-win scenario), the engine can steer the game towards complex, decisive positions even if they are objectively slightly riskier. This is configured at runtime via WDLCalibrationElo.2

## **7\. Quality Assurance: OpenBench and SPRT**

The **OpenBench** framework provides the statistical rigor required to validate improvements in such a complex system. It prevents regression by ensuring that every change—whether a new network or a code optimization—is statistically significant.

### **7.1 The SPRT Workflow**

OpenBench implements the **Sequential Probability Ratio Test (SPRT)**.

1. **Submission:** A developer submits a "Test" (e.g., a patch to the MCTS logic or a new network training checkpoint).  
2. **Distribution:** The OpenBench server distributes match pairs to volunteer clients.  
3. **Execution:** Clients run thousands of games between the "Test" engine and the "Base" engine (current master).  
4. **Analysis:** The system calculates the Log-Likelihood Ratio (LLR) in real-time.  
   * If the LLR exceeds the upper bound (e.g., 2.94), the test **Passes** (the change is superior).  
   * If it drops below the lower bound, the test **Fails**.  
   * This sequential method is far more efficient than fixed-game matches, as clearly good or clearly bad changes are detected early.21

### **7.2 OpenBench Architecture**

The OpenBench instance for Lc0 is a specialized fork.

* **Server:** A Python application that manages the queue and calculates statistics.  
* **Client:** A lightweight Python client that wraps the engine. It differs from the training client in that it runs matches (A vs B) rather than self-play (A vs A).  
* **Integration:** The Lc0 engine has added specific "bench" modes and UCI options to facilitate this high-throughput testing, ensuring that the overhead of initializing the engine does not skew the results of short time-control games.3

## **8\. Delta Analysis: Evolution from Baseline**

Comparing the current state of the Lc0 ecosystem (2025) to the implicit baseline (circa 2021), several fundamental shifts define the modern era of the project.

### **8.1 From Scalar to Distributional Evaluation**

* **Baseline:** The engine evaluated positions as a single centipawn number or winning probability.  
* **Current:** The engine thinks in **WDL distributions**. This granular understanding of draw probabilities enables sophisticated match strategies (contempt) that were previously impossible.

### **8.2 From Fixed to Flexible Backends**

* **Baseline:** Users were largely locked into CUDA (NVIDIA) or OpenCL (AMD). Network files were strictly Protobuf.  
* **Current:** The ecosystem has embraced **ONNX**. This standard allows Lc0 to ride the wave of broader AI hardware optimization. Users can now run Lc0 on Windows via DirectML or on high-end NVIDIA clusters via TensorRT, using the exact same network topology but with vastly different runtime execution paths.

### **8.3 From Tree to Graph**

* **Baseline:** MCTS was a strict tree search. Transpositions were handled via hash tables but not structurally integrated.  
* **Current:** The introduction of **DAG Search** (Directed Acyclic Graph) represents a structural change in how the engine explores the state space, formally recognizing that different move orders can lead to the same state and merging those search branches.

### **8.4 From V5 to V6 Data**

* **Baseline:** Training data was a simple record of "State \-\> Result".  
* **Current:** V6 data is a forensic record of the search process itself, capturing original evaluations vs. final outcomes. This enables "Value Repair," allowing the network to learn from the *correction* of errors rather than just the final truth.

## **9\. Future Trajectories and Open Questions**

While the ecosystem is robust, several areas remain active frontiers of research:

1. **The Transformer Ceiling:** While "BT" nets have shown promise, their inference cost is massive compared to ResNets. An open question is whether hardware acceleration (e.g., specialized Transformer engines in newer GPUs) will catch up fast enough to make them the default, or if ResNets will remain the efficiency kings.  
2. **Server Decoupling:** The project has signaled an intent to separate the API server from the web UI entirely. The implications for legacy clients and the ease of deploying private training clusters remain to be seen.  
3. **PyTorch Standardization:** While TensorFlow has been the historic standard for the training pipeline, the broader AI community has shifted heavily to PyTorch. The "unsorted" nature of PyTorch documentation in the wiki suggests a transition is underway but not yet complete. Completing this migration is likely necessary to attract new researchers to the project.

## **10\. References**

* 7  
  LeelaChessZero GitHub Organization Inventory.  
* 8  
  LeelaChessZero Repository Descriptions (lc0, client, server, training).  
* 3  
  Lc0 Release Notes (v0.32) \- Rescorer and ONNX updates.  
* 12  
  lczero-training Repository Readme and File Structure.  
* 19  
  lczero-client Release Notes \- Binary consolidation.  
* 15  
  Lc0 Architecture Documentation \- Engine subsystems.  
* 13  
  lczero-common \- Weights File and Network Format Specification.  
* 11  
  lczero-server \- Server Architecture and Database Schema.  
* 1  
  Training Data Format Version History (V3-V6 Specifications).  
* 4  
  Lc0 Backend Documentation \- ONNX and OpenXLA.  
* 10  
  lczero-client Source Code \- Protocol Logic (lc0\_main.go).  
* 9  
  Lc0 Backend Installation Guides (CUDA, OpenCL, Metal).  
* 14  
  Lc0 v0.32 Changelog \- Search API and DAG Preview.  
* 2  
  Lc0 Blog \- WDL Contempt Implementation.  
* 5  
  OpenBench Repository \- Distributed SPRT Framework.  
* 6  
  Lc0 Blog \- Transformer Progress and Smolgen.

#### **Works cited**

1. Training data format versions \- Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/dev/wiki/training-data-format-versions/](https://lczero.org/dev/wiki/training-data-format-versions/)  
2. The Lc0 v0.30.0 WDL rescale/contempt implementation | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/blog/2023/07/the-lc0-v0.30.0-wdl-rescale/contempt-implementation/](https://lczero.org/blog/2023/07/the-lc0-v0.30.0-wdl-rescale/contempt-implementation/)  
3. Releases · LeelaChessZero/lc0 \- GitHub, accessed January 28, 2026, [https://github.com/leelachesszero/lc0/releases](https://github.com/leelachesszero/lc0/releases)  
4. Overview | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/dev/overview/](https://lczero.org/dev/overview/)  
5. OpenBench is a Distributed SPRT Testing Framework for Chess Engines \- GitHub, accessed January 28, 2026, [https://github.com/AndyGrant/OpenBench](https://github.com/AndyGrant/OpenBench)  
6. Transformer Progress | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/blog/2024/02/transformer-progress/](https://lczero.org/blog/2024/02/transformer-progress/)  
7. LeelaChessZero repositories \- GitHub, accessed January 28, 2026, [https://github.com/orgs/LeelaChessZero/repositories](https://github.com/orgs/LeelaChessZero/repositories)  
8. LCZero \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero](https://github.com/LeelaChessZero)  
9. LeelaChessZero/lc0: Open source neural network chess engine with GPU acceleration and broad hardware support. \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lc0](https://github.com/LeelaChessZero/lc0)  
10. lczero-client/lc0\_main.go at release \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lczero-client/blob/release/lc0\_main.go](https://github.com/LeelaChessZero/lczero-client/blob/release/lc0_main.go)  
11. LeelaChessZero/lczero-server: The code running the website, as well as distributing and collecting training games \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lczero-server](https://github.com/LeelaChessZero/lczero-server)  
12. LeelaChessZero/lczero-training: For code etc relating to the network training process., accessed January 28, 2026, [https://github.com/LeelaChessZero/lczero-training](https://github.com/LeelaChessZero/lczero-training)  
13. Weights file format | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/dev/old/weights/](https://lczero.org/dev/old/weights/)  
14. lc0/changelog.txt at master · LeelaChessZero/lc0 \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lc0/blob/master/changelog.txt](https://github.com/LeelaChessZero/lc0/blob/master/changelog.txt)  
15. High level architecture | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/dev/lc0/architecture/](https://lczero.org/dev/lc0/architecture/)  
16. Leela Chess Zero \- Chessprogramming wiki, accessed January 28, 2026, [https://www.chessprogramming.org/Leela\_Chess\_Zero](https://www.chessprogramming.org/Leela_Chess_Zero)  
17. Download Lc0 | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/play/download/](https://lczero.org/play/download/)  
18. FAQ | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/play/faq/](https://lczero.org/play/faq/)  
19. Releases · LeelaChessZero/lczero-client \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lczero-client/releases](https://github.com/LeelaChessZero/lczero-client/releases)  
20. README.md \- LeelaChessZero/lczero \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lczero/blob/release/README.md](https://github.com/LeelaChessZero/lczero/blob/release/README.md)  
21. OpenBench Testing Framework, accessed January 28, 2026, [https://bench.lczero.org/test/303/](https://bench.lczero.org/test/303/)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABvElEQVR4Xu2UzysFURTHj7AQvZKyUo/YvNhZyM6CopBi4dfCzp6SJDs7KyvJgpWSP0D+ArFSSokFyYIoCwvKj+/3nZl5d+7MvDsjdu9bn6a533vPnTnnnitS0X+pCoyACTAG2kF1aEYK5cASeAQP4A68gG9wC4ZKU5PFLxkGr+ADLFveAHgSDcr3RNWCVdGJl6AQtgP1gDcwbhu+GGhDNNAVyIftkGrAPrgAzZZXFPPDQNyRO7s0Izp/1DYaPOMetFlekrpF1+zZxqRnrNtGGcUGqwNH4B30moZDPHuRYKzYMzgDjabh0IpoMD4D+YncMQcdYjUPJaYA/g4L5qBDLBKLFSkYk85gzEEa+Tnmmk7LK35RlmBsp0/RNZF26gdfYN4a7xDNZ58x1gRORQMdG+OBOOFctD2YWO62KHpTsOz0OMZ22xYNxDbKc3GcZqXURl3gBLR6HjfjHbYlGohHqMXzYsVdOfEGrEn4IPKreA7pb4J6wysrTmQhuIi/xOeURNuMG/CqyixeAkw6f5dioDmw671nkn+jXIv+Pp+s/qA5Ka1YYQYzORDN869kBnJW0yUWgQWZFm2niv5APzhlYlu0e/VEAAAAAElFTkSuQmCC>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABOklEQVR4Xu2UsUoDQRRFn6CFCKIQEMFC0qUSsQspRbBWsJBUIvoH+QILKzsFiVhJGj/Awj+wsbHTQhGsJChYSIjxXt+MO/PczRrSpNgDB5a9b4aZN7MrUjBy1OHTP7yFB25MJmvwGF7AT9iDH/A08Azeu+wQTvyM7EMFvooOaJqMjMEd0XzfZH9YFy2k2ybzzIvmN3DWZBHsBwvf4ZLJPH6yR/ecygJ8Fi08F91SGlwxa1pw3GS/rMIv0cI9k3km4RVsw2WTRTQkOcUVkxGudAt24YbJIrjcS9HJ7mApyDjJIjxx+Zt7l8kcfJDkJNPk1ni/pt2YTGqwIzroSPSUQmeS0v6EW3yB5TgeDPaHfeJk13Aqjgcj3GLuR5wH75RvMj+noRi6X7wnVbgpeuT+6HfdO/49CgoKcvkGllRR4tTDUpoAAAAASUVORK5CYII=>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABYAAAAfCAYAAADjuz3zAAABeklEQVR4Xu2UvytGURjHH0URkiKLesumlEEGZcJgIbEoG8WfoEwG/gGZRCYxWJXxLYsfg8mPlEVYTQYT32/PPe99zrnvvefOup/6LOc8995zvs85V6TiX7EK34yvcMqrUGrwRvxaegY7TV2DOXgAT+AP/IWnsNUWgX64A+uiNfQILsCWtCzLMPwQfeALjvjTDbpEX34cjOeyl/gu+vInOOBVKOOicXEhUdwqVuC+pFtdNjUO1tRFn4kyBB/gKJyQ4qwPRXdWinl4C3thB7yU5llz/g4umrFCdkVX4mAELo5tM85cubNS+TKGRzgWjPNjYdauwaWYgfewLxi3WTOabngl2rxSbIlejvCQ26z5AW7/RbTBUdrhBdwIJxJs1kuijWMDowzCZzgZTiTwgvCiuDhsgwvZhOeSPasWHjceu2/JNjgXroIZF8GP8qLwmIUN9miD03BNdIv89a0nY5xrxqzoTydssEcNfkraFOc17DF1Fo7nNbiioiLGHyEOWWRG7jTXAAAAAElFTkSuQmCC>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAhCAYAAAA74pBqAAABSklEQVR4Xu2UMStGcRTGH8UgCilSJiwyGGSTzWBA+QbvZmbAB1BmAyWLZGG0Gt5MYpBiVEhMFsXAwHM69989/3Nd7n0n6f7qN7z3nPfpds7pAhV/gnl6n+Oc6RNa6V5Ssy6Ehkm6TT8T3+kh3aCDoSlBwlboFbT3gm7SYdskPEAb6rQ9LmWYoC+0yRcC19CwG9rrahZ5uyO65QuWOjRM3rA/LkXIjG/pkHsesQsNe6VjrhbooZcwA89jDekSpl1NkPms0hPa4WoZZpCGLbtaCz2gO7TZ1b5FNvQBDZM/WWahi/lxTpZR6LolbB/x2k/pkvn9K330Dtlbk9BCc7J00XNkb22EToWmorTRY2jYEx1AOvhCQ/f4WwuDbwh7azXogZYavGURaZgEndHuqKME4/QN6a01NKuAfJee6SNKHGgenXQdOviKiv/NF8KKUGzPSoFMAAAAAElFTkSuQmCC>

[image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFMAAAAfCAYAAACbKPEXAAAE+ElEQVR4Xu2ZXaimUxTHl4wi41smoUEiUUjT5OvO11xwwRSFUtJIogglNWfChQsfSUhKoyYzuEOJiROFcOECIx/5SCShhJo0zPrN2mve9a6zn2ef95w5L6/eX/2bc/Z6nuc8e+2111r7GZEpU6ZMWTR7q25T3ZINE8bRqhPy4DjZS3WH6jXVQck2iXyiOjkP9nGN6tuGPlTdJ+2Vulz1g+qMNP6izH0merDYl6meqNjRgeUaqL3rZtX+xb6P7NlFfFj1mWplNnRxoeox1SbVdtUfqmdUTwZ9ofqn6H6xl67xk+rOPKisF3smz/bn8NxLix1nXiG2EG7/qFyDzblE9Xyx/6naorqy2E5UfaXaobqgjC0Wtvo2sffomnMVwvln1VPZILZ9r1P9LTaRG4bNu2DS/OEV2VAgeraK3f9NsjlEvztqVbJFXlcdmsZYRF8Id/Ce4CZZwAL5RE7LhsKRYk6oOQNnP6o6M41nNordT4TWeE4GDiEKa+C0GK1Ljc+NXXtWslXZV/Wy2CQOSTYHJ/8mds2XyXaU6mvpvte5S/qdSQS4M9clG5CzP8iDY4B0xDsRcE3IDd+J3cBK1LhKBhN9NtnOF0sBLeJWzCwXK1Ruz7mX96Ig3JPGx4GnwNk0XsWdUZsk7Kd6Rcz+q8yt1h5xLW6VbmderVqj+kvMzjMjpJD3VMekcfra61Vvqc5JNqAwvaT6VCz/UUhIWfeWMXbZA7uvrsOOe1+sQDbxiOnaflRatiCi/YkQMXQCNQdlyIM1Zx4h9rI4zCs++dXBAVTuXPgYf1z1hlj1/1F10tAV9twbxboJnsuBguvYsvy928XmxTt0QY5+QWyhe6H6sjo+yZpoh2K/F/HCNJ9Vo0JTqaMziTQijonFIkcOJ5ez/XEWkRthgqSba8vv7BgWIhZB7vf2qytf8/xZ6S54jgdcL+fKYGs9JDahqIMHl1ZxB+QKXyNGHhDV5EByIT9HZ86KTZQ2B2fyc4Rr31YdX36P9zhcQ6R6ZHENlTniwUSK6WNezvQ8Rs4kd47KKM6MHQGcIlad/WR1uOrjYicHHqB6U9o9Hg7kHnJijbj7PFIdDyZflC6azowrxjZtPbDGKM6MkcexjwNCrNq+5bBzHdeTK1unD/o/tvmp2VBwh1GR81nb+2tSQh9NZ8ZI4HTiZ9xRWKgzL1K9I8OJPztztbQPAoBDPMfWoGeN0e6woO8WW4umM/2PoFbO6MKPiRSWFtFZFJ3DhqzGRhm80/fJVuNisRR1XPl9RvXIbutwwNArRzwqWQhv//I1jr9XJ4vd4g7btfcPFeL5/LJkc3CEO3Nm2FTFHULKIjeySGx7xzuIvMXjuxBU3LNNBosS8SAg3w9B1TxbtVYsz/Aw/qXxZWwheITPB19hIqGGb6fPZW6DXsMLKHmVrzv0nfHs7ic3+s143I1HaNoivsHSg9bw9ESEVw2++lkLwVue+XyAwJnb82DAnZMb9C5Win1z/EXs81z+nrlB7HkzaRzI27z376q7pbvQeQF7OhuWAk/kfPBoQd94cx4MnC72/TP3lX3ghGOl/l2B7Uyv2eUoIpQA68N3S26rlgy2JB8qurbvpEIuZSfFFm4skOjPy4MTjB9ZOff3nd2XhFeLRtmi/2U4eZFTx7a9IxQD2gv+h7KWvyaNf30u9HL8F2n+VDdpUFRnpLtwTZky5f/PTjtyaPqQH3SVAAAAAElFTkSuQmCC>

[image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAGQAAAAfCAYAAAARB2hWAAAFYklEQVR4Xu2ZWahuYxjHH6HILEOmdnQ6ZYoyRZTiggwXyI3hRiLkgjLlYpckJSSlUHIh4wVJhPiiUCQXhjJkSCRJhAwZnt/3rGd/z3r2+6619unYZ3/nrF/923u/71rvWut9xrW2yMjIyMhI4mDVU6r988QWxiWq2/LganOk6mPVuXliC2QX1cuq7fNEFyepPlN91SMWxuI722lFFsSMcYtqqzQXyWvXdK1qn+aceYU9uV+1bZ6osV51j9hJn6v+VT3f/O16WvVnM/eT6vTpmct5QvW+aq88keB6D6m+F1vzb2lfDz0e5q6TFTzQGuR32YCMsYNYFHwt5dy/u+oVsU36Mc05bN6FebDC3mKRyXqT9tQSN4ityTEYpSvq1jIvqD5RHZAnujhV9Y/qpjwROEtsc9COae401a1prIvLZLbW2WkucpDqW7Hj7k1z8wLO91Ejfh/E9WIPjWFqYCzfxG3COL8/qjoxjHWBp5OyWOcHsa6sBoafiB37QXtqbojP2+V8S2ynek7shFK6gngMiuyn+qL5OYQ9xDaXdSayPNoi0SBEyrxygdgzUD978Q3lBDa+xFGqX8WOeS3NERl/Sf3cTFyrL81Fg3AOUO/uEuvEHmjGDlE9lsYyNAakXXI69ev2Zoz6GOvT1qrzVe81KhXkdWL34dDuvy31FvcY1W+q11U7pblleP3Inu94P808D5JTjNeDoQytH0Db+6XMIoQNpAt7RKwNp+jfrPpOdaWYcxB9Jecgh9Mlch7PwDp0hawdO0caiHdVJ6ueFbtGxGuCnxOzRy3lk3lomGpN0xI5FZX0puoMMc8p8bCszCBD0xVE4/H2T0R52HtkemcXIy8aZEHs/ejyMOZwLPdDGgXqIfUUfOPzs3kt3S2M4Si0/TWDxEivvTZMiemKcMIjs7qIBh3K0HTF5mAENwjGeVVmUeXG8s1kUy6V9rqkorvFjssdDnOME22esjgGwwLXYZ7Gw+l6XuoEXWGJaBDSZpWYrgYVnES80FB8g/vS1YFiIc6x/ORvhw1kI5nrej8hNbGhpeMwpBs6450j8ziF46mn9LznSD3iBxvE290hG1RipQZxr6Qe1LzJuUpm9+ZpxImdWhcx5WWIBAotBTcTnQHPd9yBY9Q4XRE/yCAx/PreB2pETx2Ce+VE6t7keMGloaCxiHjXgrp4UGbPl8FYsX5EPF1lx3EHpqOK8CwvprHIIIPEdNVl3T68yA3BPbYrGqkFi2Lfuo5uT02JThDTSQk3yCSNe4Rd0/zN/bwhM8N7o8JPJ25qjtjFRjW8W+x0fIzA4n0b1Ae5kzVyjs54ke66KVLFM2Lr1Y6J6aqU/yPuAJMwRrd4YzOOU3LffJZZDMf43nC/3DfwLYpvUtmB16neUR0axjJey5ZFJBc/QXWxzD7u0Tpi8fOk/UlkKEeofpZ2G+jg7aeIrc17Ah8mP1Vd1Iy5rhBLA9wPX5Z5F6jh7S3yjqgGRZjWlWOBF7c7Vd+IvZPQOpc29HjVH2LnHSd2HtFGCuMePWXxUklK5V8FXdDqZgNP4dM4PblHRlZfXi+BIbjBkkcfLmaEfJ0sjPCW9P/PBcjBnMPxubaU4L4wCkWaTeZ9gY0ksn8Rq0NsaIxwfj9TzGhci+OeVO0p9l9AXhZZD0e8WurvaI6n9b6I3mgsSrsb+T/h4ddL+9NFH5xDHt81jZfGIn7evtI2GA0R0TXEgYkuPtd0peqNDmFIJByWJ0amzkpkkgZXFboi8uyG1KHNGerVfbIJ9oVvSuRj/k8/YtDUULMX8sRqwMXvECugm+QG1iB8uj82D64mbpSXZFgHtDmDIT7MgyMjIyNzyn/EbJQYm4SRIAAAAABJRU5ErkJggg==>

[image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFkAAAAfCAYAAACMCmHeAAAE40lEQVR4Xu2aa6imUxTH18QUkcuYIQ1NLiW5TJMQQ0pG5FIGkcunSXzQCLmEQs188EEKjZI6KJH4KELTKWXcPkiYyYwaUvIBUTM1xFi/s57l2e969n6e5z2cc97z9v7q33vaez23tfdea+3dEZkwYcKEkeEA1b2qu2LHGHCc6rXYuBDcr3pPdXjsGBOuVZ0aG7u4VfV9hz5XbVadXF3Txo+qNbEx4VjVh9J8BvpCdUZtOpIsUX2jWhU72rhUtUX1imqfao/qZdXziXap9ld6QrV05somp6keiI2BFapNqs+kvucOsec8qjq0Nh1Ztou9b8kPRVgCP6teiB1io7dB9beYU+4Y7J7hQNWrqmNiRwGew70Y1LNC36hzp+ov1brY0QXhgI9eHTsqWObfidnwm8IgPCvmsL4woNxrSuz6xYR/Lyv/vNBX5CDVW2IffWToc3D+72I234a+lardqk9DexseKm6PHYuEq8Xen8nZC0qTH8QuKs2qm6V2DGEh5RKxUJILNSUWa6hwPLxOS8884k7iw3McrHpHrP9XaVYPD1V9fWflIWL2X6mWh74+HKX6SKwi4fcmsXfMca5YbYstCZakW+IwsRqfagp7vrk06VjxrFyqqRNDXxYqAp9ZOW4QC/SIOjGFl6Ay4frLQ18JXgp7rit9RA42OhtVf6iuV50kVpHwXm8ndg73xvYZ1elik4mwyCDl+LIS92amsqG6ZcCihkT/hupP1QWhrwHVADHWQ0FOlG2Mcg5PiL1HVCyO9Xq5CpzFJod3+USaTrqq6ktXBddMyWBicrtTkjaHsMWMZyCBEDCtetINMvjk7CpbZz6UD8b4KTGnpTqiNs3iTkb83YUnWQa2b7lHeCJM8Y43hj5w56WVkTuJ1eLPIUzd7QYB7sEzrpC6/l0rdk2J3k6+R8yQmMxyGpZhnexJlqXGkivxiFgoAMol3pHrTvjXooZcQH+aRL1u99WIA5mVpRzAjHdbtE112YBFk15O9riC4TDLPWVYJ3uSbXsxzj22Sh1OPJyVBsY3NgxgyjlVeyp2azmYvdEWtdHLyYwqGR7D96V9aZQY1sleibStGmYQ5xtes5OQSx/jWZ5+QlGEauFjseTY5Tje6UXVT1LbtoW0Xk72ZYb6VgYRBoYB2qs6O/RFfNNScognOO6VJizq0VxNjb2HknertrTcZOBT3CkpDGiufPXJUwov8JLYdcTzIv81VDi+XFsfJt31+INiM47aNz0qdaetT9rgQrGtbXoq5s7B/rmqDQgHr4vF9RR3PDE7xe9dwhMru+DGUQSjf77qOqkzNr+3VW2zwVcEoSAH96UqYLZhh2hLRZnofY/NXFXDQQzO/0C1rGq7Rmx2E2PT812fyW9KXXbS9rhYFRXrfGbybzK4ks8UywOs0BI+mNkNVTrSOc0GljEfzMrIEZ/RplJYuFLMGWwu2I0RUu6T/E4Pp3M2zcAwc/llMC6W5uaHGf6wmA33/aX6+2kp7w3AS98pad5zTmBps8R3h/b/GzYKVBAXVX+3wYezaWFSddX6QI7AlkTX55zYwwwHRfPG8aqdkp9Z4wZJmXjdWlXMFRzmkzDGHTY5nHEcHTvmAzIuya3X0d8ihrwxr2EiQoKh1p2XZLAArJIR+D4y+9fSLJXGARI8/+7QJzFOmDBhQhf/AMenXf9LKNLvAAAAAElFTkSuQmCC>
<!-- PGNTOOLS-LC0-END -->
