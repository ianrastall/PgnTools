# Leela Chess Zero: Comprehensive Technical Documentation
# A Complete Analysis of Architecture, Data Pipelines, and Distributed Training Infrastructure

## Table of Contents

- PART I: SYSTEM OVERVIEW AND FOUNDATIONS
  - Chapter 1: Introduction to Leela Chess Zero
    - Section 1.1: The Distributed Challenge
    - Section 1.2: The AlphaZero Paradigm
    - Section 1.3: Project Evolution and Scope
  - Chapter 2: Architectural Foundations and Component Ecosystem
    - Section 2.1: Component Overview
    - Section 2.2: The lc0 Engine
    - Section 2.3: The lczero-server Backend
    - Section 2.4: The lczero-client Worker
    - Section 2.5: The Training Pipeline
    - Section 2.6: Modern Backend Architecture
- PART II: DATA PIPELINE ARCHITECTURE
  - Chapter 3: Training Data Formats and Binary Structures
    - Section 3.1: Historical Evolution (V3 -> V6)
    - Section 3.2: The V6 Binary Structure
    - Section 3.3: The 112-Plane Input Representation
    - Section 3.4: Output Heads (Policy, Value, MLH)
    - Section 3.5: Search Statistics and Metadata
  - Chapter 4: Data Storage, Consistency, and Orphaned Records
    - Section 4.1: PostgreSQL Database Schema
    - Section 4.2: File Storage Topology
    - Section 4.3: The Orphan Problem
    - Section 4.4: Reconciliation and Cleanup
- PART III: CLIENT-SERVER INFRASTRUCTURE
  - Chapter 5: Client Architecture and Worker Lifecycle
    - Section 5.1: Client Initialization
    - Section 5.2: Server Connection and Authentication
    - Section 5.3: Task Acquisition Protocol
    - Section 5.4: Network Download and Caching
    - Section 5.5: Game Execution
    - Section 5.6: Data Upload Protocol
    - Section 5.7: Failure Modes and Error Handling
  - Chapter 6: Server APIs and HTTP Endpoints
    - Section 6.1: Task Distribution Endpoints
    - Section 6.2: Data Ingestion Endpoints
    - Section 6.3: Network Distribution Endpoints
    - Section 6.4: Statistics and Query Endpoints
- PART IV: TRAINING RUNS AND DIFFERENTIATION
  - Chapter 7: Training Run Architecture (Runs 1, 2, and 3)
    - Section 7.1: Run 1 - The Mainline (T60 Series)
    - Section 7.2: Run 2 - Efficiency Frontier (T70 Series)
    - Section 7.3: Run 3 - Experimental Branch (T75/T71)
    - Section 7.4: Cross-Run Comparative Analysis
  - Chapter 8: Workflow Differentiation (Tuning vs Matches)
    - Section 8.1: Match Workflow Overview
    - Section 8.2: Tuning Workflow Overview
    - Section 8.3: Execution and Resource Differences
    - Section 8.4: OpenBench SPRT Validation
- PART V: DATA INTEGRATION AND RETRIEVAL
  - Chapter 9: Match Data Mapping and PGN Retrieval
    - Section 9.1: Match Database Schema
    - Section 9.2: Run-Based Directory Structure
    - Section 9.3: URL Construction and Data Retrieval
    - Section 9.4: SQL Queries and Access Patterns
- PART VI: CONCLUSION AND FUTURE DIRECTIONS
  - Chapter 10: System Evolution and Future Directions
    - Section 10.1: Architectural Achievements
    - Section 10.2: Persistent Challenges
    - Section 10.3: Recommended Improvements
    - Section 10.4: Future Technology Directions
    - Section 10.5: Delta Analysis (2024-2025)
- APPENDICES
  - Appendix A: Complete API Reference
  - Appendix B: Database Schema Reference
  - Appendix C: Binary Format Specifications
  - Appendix D: Network Topology Reference
  - Appendix E: SQL Query Library
  - Appendix F: Index of Technical Terms
  - Appendix G: Base64 Image Assets
- Works Cited

---

## PART I: SYSTEM OVERVIEW AND FOUNDATIONS

### Chapter 1: Introduction to Leela Chess Zero

#### Section 1.1: The Distributed Challenge

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

The landscape of computer chess has undergone a radical transformation in the last decade, shifting from the brute-force calculation and handcrafted evaluation functions of engines like Deep Blue and early Stockfish to the intuition-based, neural network-driven approach pioneered by DeepMind's AlphaZero. Leela Chess Zero (Lc0) stands as the open-source implementation of this methodology, representing a distributed computing effort of massive scale. Unlike traditional software development where improvements are made via code changes, Lc0 improves through "learning"—a process that requires the generation of millions of self-play games to train its neural networks.

This report provides an exhaustive technical analysis of the lczero-client, the critical orchestrator software that enables this distributed training. By reverse-engineering the source code, analyzing protocol buffers, and dissecting communication logs, we illuminate the worker lifecycle. We explore how a decentralized fleet of volunteer hardware—ranging from consumer laptops to high-end cloud GPUs—synchronizes with a central server to perform complex reinforcement learning tasks. The analysis covers the initialization sequences, the specific HTTP protocols used for data transport, the authentication mechanisms, and the resilience strategies employed to maintain stability in a volatile volunteer grid.

#### Section 1.2: The AlphaZero Paradigm

### **1.1 The AlphaZero Paradigm and Distributed Requirements**

To understand the client's architecture, one must first understand the computational workload it manages. The AlphaZero algorithm relies on a reinforcement learning loop consisting of four distinct stages:

1. **Self-Play:** An agent plays games against itself using its current neural network to evaluate positions.  
2. **Data Generation:** The game moves and outcomes are stored as training examples.  
3. **Training:** A new neural network is trained on this data to predict the game outcomes and move probabilities.  
4. **Evaluation:** The new network is pitted against the old one; if it wins a sufficient margin, it becomes the new "best" network.

Stages 3 and 4 (Training and Evaluation) are centralized or semi-centralized. However, Stage 1 (Self-Play) is embarrassingly parallel and computationally expensive, requiring billions of inference calculations. The lczero-client is designed specifically to distribute this Stage 1 workload across the internet. It transforms the AlphaZero methodology into a client-server architecture where the "brain" (the network) is hosted centrally but executed locally.

**1\. Introduction: The Neural Paradigm of Leela Chess Zero**

To fully appreciate the architectural distinctions between Runs 1, 2, and 3, it is necessary to first establish the theoretical and structural baseline from which all three lineages emerged. Lc0 represents a departure from classical Alpha-Beta pruning engines (such as early Stockfish versions) by utilizing a Deep Convolutional Neural Network (DCNN) as its evaluation function and move ordering oracle.

### **1.1 The Residual Network (ResNet) Backbone**

The core of the Lc0 architecture during the period covered by Runs 1, 2, and 3 is the Residual Network (ResNet). Adapted from computer vision research, the ResNet architecture solves the problem of "vanishing gradients" in deep networks by introducing skip connections—pathways that allow the gradient to bypass non-linear transformation layers during backpropagation.

In the specific context of Lc0, the network is defined by a "Tower" of residual blocks. The dimensions of this tower are the primary differentiators between the training runs. The standard notation used throughout this report is ![][ds-image2], where:

* ![][ds-image3] **(Filters):** The number of feature maps (channels) in each convolutional layer. This determines the "width" of the network, or its capacity to capture parallel patterns and features at each level of abstraction.  
* ![][ds-image4] **(Blocks):** The number of residual blocks stacked sequentially. This determines the "depth" of the network, or its ability to perform sequential reasoning and compose abstract concepts from lower-level features.

Mathematically, a single residual block in Lc0 can be described as:

![][ds-image5]  
Where ![][ds-image6] is the input tensor, ![][ds-image7] and ![][ds-image8] are convolutional layers with ![][ds-image3] filters, and the addition ![][ds-image9] represents the residual skip connection.

### **1.2 The Role of Squeeze-and-Excitation (SE)**

A pivotal architectural enhancement adopted across the major runs is the Squeeze-and-Excitation (SE) block. While the original AlphaZero paper did not utilize SE layers, the Lc0 project integrated them to enhance the representational power of the network without a proportional increase in computational cost.

The SE block operates by explicitly modeling inter-dependencies between channels. It performs two operations:

1. **Squeeze:** Global information embedding, typically via global average pooling, to produce a channel descriptor.  
2. **Excitation:** An adaptive recalibration, using a simple gating mechanism with a sigmoid activation, to produce per-channel modulation weights.

This architecture allows the network to emphasize informative features (e.g., "King Safety" channels) and suppress less useful ones (e.g., "Queenside Pawn Structure" when the action is on the Kingside) dynamically on a move-by-move basis. The presence and configuration of SE layers became a standard in Run 1 and subsequently influenced the efficient designs of Run 2\.1

### **1.3 The Monte Carlo Tree Search (MCTS) Interface**

The neural network does not operate in a vacuum; it serves as the heuristic engine for a Monte Carlo Tree Search. The network receives a board state and outputs two critical signals:

1. **Policy (![][ds-image10]):** A probability distribution over all legal moves, acting as a prior for the search.  
2. **Value (![][ds-image11]):** An estimation of the game outcome (typically ![][ds-image12] or a probability distribution).

The interplay between the network's architecture and the MCTS is crucial. A larger network (Run 1\) provides a more accurate prior ![][ds-image10] and evaluation ![][ds-image11], allowing the MCTS to converge on the best move with fewer simulations. Conversely, a smaller network (Run 2\) provides a less accurate prior but allows for vastly more simulations (nodes) per second, potentially compensating for its "blind spots" through deeper brute-force verification.

## ---

# **The Lc0 Distributed Ecosystem: A Technical Analysis of Match and Tuning Workloads**

## **1\. Introduction: The Distributed Paradigm in Neural Chess**

The development of Leela Chess Zero (Lc0) represents a fundamental departure from traditional chess engine programming. Historically, engines such as Stockfish or Komodo relied on handcrafted evaluation functions—linear combinations of features like material, mobility, and pawn structure—tuned via local optimization techniques. In contrast, Lc0 is an open-source implementation of the AlphaZero algorithm, which replaces human domain knowledge with a deep neural network (DNN) and a general-purpose reinforcement learning (RL) algorithm based on Monte Carlo Tree Search (MCTS).1

This architectural shift necessitates a corresponding shift in infrastructure. The training of deep neural networks requires massive datasets of self-played games to facilitate reinforcement learning. Furthermore, the MCTS algorithm itself depends on a suite of hyperparameters—exploration constants, time management curves, and urgency factors—that cannot be learned via backpropagation and must be optimized externally. Consequently, the Lc0 project operates as a massive distributed system, orchestrating the computational resources of thousands of volunteers to perform two distinct but symbiotic tasks: **Matches** (Self-Play) and **Tuning**.1

This report provides an exhaustive technical analysis of these two task types. It dissects the server-side assignment logic that governs their distribution, the binary and serialization formats used to encode their data, the storage hierarchies that persist their outputs, and the profound impact of Tuning tasks on the operational "manifest" that defines the engine's evolution.

## **2\. System Architecture and Component Interaction**

To understand the distinction between Matches and Tuning, one must first delineate the components of the Lc0 distributed ecosystem. The system is not a monolith but a federated network of independent agents coordinating via strict API protocols.

### **2.1. The Client-Server Topology**

#### Section 1.3: Project Evolution and Scope

## **Executive Summary**

The evolution of Leela Chess Zero (Lc0) constitutes one of the most significant open-source achievements in the domain of artificial intelligence and game theory. Originating from the foundational algorithms proposed by DeepMind’s AlphaZero, the Lc0 project has diverged into a complex ecosystem of neural architectures, training methodologies, and data representations. This report provides an exhaustive, expert-level analysis of the three primary training lineages—designated as **Run 1**, **Run 2**, and **Run 3**—that have defined the project's development during the "ResNet Era" (prior to the wholesale adoption of Transformer architectures).

The analysis reveals that while **Run 1** (principally the T60 series) functioned as the "Mainline" effort dedicated to maximizing absolute playing strength via massive parameter scaling (up to ![][ds-image1]), **Run 2** (T70 series) and **Run 3** (T75/T71 series) were established to explore orthogonal objectives. Run 2 focused on computational efficiency, distillation, and accessibility, targeting high Nodes Per Second (NPS) performance on consumer hardware. Run 3 served as an experimental testbed for radical hyperparameter tuning, alternative loss functions (such as Armageddon-specific rewards), and architectural variations that were too risky for the main lineage.

This document deconstructs these runs across four critical dimensions:

1. **Network Topology:** The specific configuration of residual blocks, filter widths, and squeeze-and-excitation layers.  
2. **Input Feature Representation:** The 112-plane standard and its temporal implications.  
3. **Head Architecture:** The divergence from scalar Value heads to Win/Draw/Loss (WDL) distributions and Moves Left Heads (MLH).  
4. **Training Data Infrastructure:** The transition from V3 to V6 binary formats and the implementation of value repair mechanisms.

> *[This topic is covered in detail in Chapter 7, Section 7.4. See page XXX for complete specification.]*

### Chapter 2: Architectural Foundations and Component Ecosystem

#### Section 2.1: Component Overview

### **1.2 The Lc0 Ecosystem Components**

The Lc0 ecosystem is not a monolith but a collection of loosely coupled services and binaries. Understanding the client requires placing it within this topology:

* **The Engine (lc0):** The core C++ binary that implements Monte Carlo Tree Search (MCTS) and interfaces with hardware backends (CUDA, OpenCL, BLAS) to perform neural network inference.1  
* **The Training Server (lczero-server):** A Go-based central authority that manages the database of games, tracks user contributions, and schedules training runs.2  
* **The Client (lczero-client):** The focus of this report. A lightweight Go application that acts as the "glue" between the server and the engine.4  
* **The Network Weights:** Protocol Buffer files containing the floating-point parameters of the neural network.5

The client's role is strictly operational: it does not "know" chess. It knows only how to download a file, execute a subprocess, and upload a result. This "dumb worker" design is a strategic choice to maximize stability and minimize the need for frequent software updates on volunteer machines.

#### Official Repository Inventory (2025)

| Repository | Functional Role | Language | Criticality |
| :---- | :---- | :---- | :---- |
| **lc0** | Inference Engine | C++ | Core |
| **lczero-client** | Grid Worker | Go | Core |
| **lczero-server** | Orchestrator | Go | Core |
| **lczero-training** | Learning Pipeline | Python | Core |
| **OpenBench** | Validation | Python | High |
| **lczero-common** | Data Schema | C++ | High |
| **lczero-live** | Visualization | TypeScript | Medium |


## **2\. System Architecture and Backend Components**

The backend architecture of the Lc0 project is not a monolith but a constellation of interacting services and repositories. At its core is the lczero-server repository, which handles the HTTP interface for clients and the persistence layer for game metadata.

### **2.1 The Technology Stack: Go and Gin**

The lczero-server is implemented in **Go (Golang)**.4 This choice of language is strategic for several reasons pertinent to the system's function:

* **Concurrency:** Go's goroutines provide a lightweight mechanism to handle thousands of concurrent upload connections without the overhead of OS-level threads. This is crucial during peak times when thousands of contributors might be uploading games simultaneously.  
* **Performance:** Go offers near-C++ performance for execution while maintaining memory safety, reducing the risk of buffer overflows during the parsing of binary game data.  
* **Deployment:** Go compiles to a single static binary, simplifying deployment across different server environments, as evidenced by the prod.sh and start.sh scripts in the repository.1

The web framework utilized is **Gin** (github.com/gin-gonic/gin).1 Gin is known for its speed and minimal memory allocation. It uses a custom httprouter that is significantly faster than Go's default net/http multiplexer. In the context of Lc0, Gin handles the routing of API requests such as /get\_task, /upload\_game, and /upload\_network. The use of Gin implies a design philosophy prioritizing raw throughput—essential when the "clients" are automated bots capable of hitting endpoints continuously, unlike human users who browse at a leisurely pace.

#### Section 2.2: The lc0 Engine

* **The Engine (lc0):** The core C++ binary that implements Monte Carlo Tree Search (MCTS) and interfaces with hardware backends (CUDA, OpenCL, BLAS) to perform neural network inference.1  
* **The Training Server (lczero-server):** A Go-based central authority that manages the database of games, tracks user contributions, and schedules training runs.2  

#### DAG Search (Directed Acyclic Graph)

Recent updates introduced **dag-preview** search, a structural change from traditional tree search:

- **Tree Search:** Treats transpositions as separate nodes
- **DAG Search:** Recognizes position convergences, merging search branches

This allows evaluation sharing across different move orders, significantly improving efficiency in transposition-heavy positions.

However, Lc0 diverges from the pure AlphaZero implementation in several key areas, most notably in its decentralized nature and its handling of draw probabilities, which significantly impacts the data structure. Where AlphaZero might predict a simple ![][tdf-image8] scalar, Lc0 data structures must support a richer set of targets, including separate probabilities for winning, losing, and drawing (![][tdf-image9]), as well as auxiliary targets like "Moves Left" to guide endgame precision. These requirements have driven the evolution of the data format from a simple state-outcome tuple to the complex V6TrainingData struct used today.

## ---

**2\. Data Generation: The Distributed Client Architecture**

The genesis of all training data in the Lc0 ecosystem is the distributed client network. Thousands of volunteers run the lczero-client, which wraps the lc0 binary to coordinate self-play matches. This distributed approach solves the massive compute requirement of generating millions of games but introduces significant complexity regarding data consistency, validation, and serialization.

### **2.1 The lc0 Engine as a Data Generator**

When the lc0 binary is invoked by the client for training, it operates in a distinct mode compared to its competitive play configuration. The goal is not to win a single game with maximum certainty, but to explore the game tree and generate high-quality training signal.

#### **2.1.1 Exploration and Noise Injection**

In competitive play, determinism is often preferred to reduce blunder variance. In data generation, determinism is detrimental. If the engine always played the move with the highest policy prior, it would repeatedly traverse the same narrow path in the game tree, leading to overfitting on specific lines—a phenomenon known as "mode collapse."

To prevent this, the data generation pipeline mandates the injection of Dirichlet noise ![][tdf-image10] into the root node's policy priors ![][tdf-image11]. The formula used is:

![][tdf-image12]  
where ![][tdf-image13] and ![][tdf-image14] is the exploration fraction. This noise ensures that the MCTS explores moves that the raw policy might initially undervalue, generating data that corrects the network's "blind spots."

#### **2.1.2 Temperature Scheduling**

The "Temperature" (![][tdf-image15]) parameter controls the greediness of move selection. The probability of selecting a move ![][tdf-image16] is derived from its visit count ![][tdf-image17]:

![][tdf-image18]  

* **Client Capability:** Match tasks generally require GPU acceleration to be efficient. The server may reserve CPU-only clients for different workloads or assign them smaller networks to ensure they can complete games within reasonable timeframes.9

### **3.2. Execution: The Self-Play Loop**

Upon receiving a Match task, the client executes the lc0 binary in a specialized self-play mode. This mode differs significantly from standard chess engine analysis.

1. **Exploration vs. Exploitation:** To ensure the network sees a diverse range of positions, self-play games introduce noise into the search. The root node of the MCTS tree is perturbed with Dirichlet noise, and moves are chosen proportionally to their visit counts (soft choice) rather than greedily selecting the best move (hard choice) for the first ![][tm-image1] moves (typically 30\) of the game.10  
2. **Rescoring and Adjudication:** As the game progresses, a "Rescorer" component monitors the board state. It checks positions against Syzygy tablebases (endgame databases) to adjudicate games early if a known win/loss/draw position is reached. It also detects "intentional blunders"—moves that significantly drop the evaluation—to ensure the training data reflects high-quality play.1  
3. **No Pondering:** Unlike tournament play, where an engine "ponders" during the opponent's time, self-play engines run sequentially or in parallel batches without pondering to maximize throughput.11

### **3.3. Data Format: The V6 Training Data Standard**

The output of a Match task is not a PGN text file. Text formats are inefficient for the massive volume of data Lc0 generates. Instead, Lc0 uses a highly optimized binary format. The 2024-2025 standard is the **Version 6 (V6)** training data format.12

The V6 format is defined as a PACKED\_STRUCT in C++, ensuring exact byte alignment for direct memory mapping. A single training record is exactly **8356 bytes** in size.12

#### **3.3.1. Structural Decomposition of V6 Data**

The V6TrainingData struct encapsulates all information required to train the neural network to predict the MCTS output.

| Field | Type | Size | Description |
| :---- | :---- | :---- | :---- |
| version | uint32\_t | 4 bytes | Format version identifier (must be 6). |
| input\_format | uint32\_t | 4 bytes | Defines the encoding of planes (e.g., INPUT\_CLASSICAL\_112\_PLANE). |
| probabilities | float | 7432 bytes | The policy target: a probability distribution over all 1858 possible legal moves in chess (including promotions). |
| planes | uint64\_t | 832 bytes | The compressed input representation of the board state (see Section 3.3.2). |

#### Section 2.3: The lczero-server Backend

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


The core of the system revolves around two primary data flows: the downstream dissemination of neural network weights and training configurations (via the /next\_game endpoint) and the upstream ingestion of game records and binary training data (via the /games or /upload endpoint). These interactions are governed by strict serialization formats (V3 through V6) that encode chess positions into highly compressed bitstream tensors, allowing for the efficient transport of terabytes of training data. Furthermore, the API facilitates complex version negotiation, enabling the seamless deployment of disparate neural architectures—ranging from classic Convolutional Neural Networks (ResNets) to modern Transformer-based models (BT4)—across a heterogeneous grid of consumer hardware.

## ---

**2\. The Distributed Training Paradigm**

To fully comprehend the API surface, one must first understand the functional requirements of the distributed system it serves. The Lc0 infrastructure is not merely a game server; it is a global, asynchronous training pipeline. The server acts as a parameter server and job dispatcher, while the clients function as ephemeral generation nodes.

### **2.1 The Hub-and-Spoke Topology**

The architecture follows a strict hub-and-spoke topology where the lczero-server acts as the central authority. The clients (lczero-client) are designed to be passive consumers of instructions and active producers of data. This distinction is critical for the network protocol design. Since volunteer clients operate behind residential NATs, corporate firewalls, and university proxies, the communication model must be strictly client-initiated.

The server does not "push" jobs to clients; rather, clients "pull" jobs. This fundamentally eliminates the need for bidirectional protocols like WebSockets for the core training loop, as the latency between a client finishing a game and requesting a new one (typically seconds) is negligible compared to the duration of the game itself (minutes). The source code analysis of the Go-based client (lc0\_main.go) confirms this, utilizing standard net/http libraries to execute blocking POST requests in a continuous loop.1

### **2.2 Component Interaction Flow**

The lifecycle of a single training contribution follows a rigid sequence of API interactions:

1. **Authentication and Handshake:** The client initializes and establishes its identity and hardware capabilities with the server.  
2. **Configuration Retrieval:** The client polls the /next\_game endpoint to receive the current network hash, training hyperparameters (e.g., learning rate schedules, exploration noise), and engine settings.  
3. **Inference and Generation:** The client executes the lc0 binary, which performs Monte Carlo Tree Search (MCTS) using the specified neural network.  
4. **Data Serialization:** The engine outputs raw training data—comprising input planes (board states) and target probabilities (policy vectors)—which the client compresses into a proprietary binary format.  
5. **Payload Submission:** The client uploads the binary data and the PGN (Portable Game Notation) record to the /games endpoint.

This cycle repeats indefinitely, with the server aggregating data from thousands of such cycles to trigger the training of the next generation of the neural network.

## ---

#### Section 2.4: The lczero-client Worker

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

#### Section 2.5: The Training Pipeline


### **1.1 The Reinforcement Learning Loop Context**

To understand the data format, one must understand the generation intent. The data pipeline is designed to support an AlphaZero-style RL loop. In this paradigm, the network ![][tdf-image1] takes a board state ![][tdf-image2] and outputs a policy vector ![][tdf-image3] and a value scalar ![][tdf-image4]. The MCTS search uses these outputs to guide simulations, resulting in a refined policy ![][tdf-image5] (based on visit counts) and a refined value ![][tdf-image6] (based on game outcome). The data pipeline's primary responsibility is to capture ![][tdf-image7] triplets efficiently.

However, Lc0 diverges from the pure AlphaZero implementation in several key areas, most notably in its decentralized nature and its handling of draw probabilities, which significantly impacts the data structure. Where AlphaZero might predict a simple ![][tdf-image8] scalar, Lc0 data structures must support a richer set of targets, including separate probabilities for winning, losing, and drawing (![][tdf-image9]), as well as auxiliary targets like "Moves Left" to guide endgame precision. These requirements have driven the evolution of the data format from a simple state-outcome tuple to the complex V6TrainingData struct used today.


Run 1 implemented a "Rescorer" pipeline. Instead of relying solely on the self-play result, the system would periodically re-evaluate positions from older training data using the latest, strongest network. This effectively "repaired" the training labels. The Run 1 architecture was specifically adapted to handle this via the V6 data format (discussed in Section 6), which includes fields for orig\_q (original value) to track the divergence between the old and new evaluations.3

## ---

**3\. Run 2: The Efficiency Frontier (T70 Series)**

---



#### Section 2.6: Modern Backend Architecture

##### The ONNX Revolution

A significant evolution in the ecosystem is the adoption of **ONNX** (Open Neural Network Exchange), decoupling network architecture from the engine binary:

| Backend | Target Platform | Key Feature |
| :---- | :---- | :---- |
| **CUDA** | NVIDIA GPUs | FP16 inference via Tensor Cores |
| **OpenCL** | AMD/Older NVIDIA | Manual kernel tuning |
| **Metal** | Apple Silicon | MPS integration |
| **ONNX-TensorRT** | NVIDIA (optimized) | Layer fusion, auto-tuning |
| **ONNX-DirectML** | Windows AMD/Intel | Microsoft ML APIs |

**TensorRT Integration:** The ONNX backend enables NVIDIA's TensorRT optimizer to fuse layers and tune kernels, potentially outperforming handwritten CUDA kernels.


## PART II: DATA PIPELINE ARCHITECTURE

### Chapter 3: Training Data Formats and Binary Structures

#### Section 3.1: Historical Evolution (V3 -> V6)

4. **Data Serialization:** The client aggregates this metadata. It computes a checksum of the game to prevent duplicate uploads and potentially validates the game integrity (checking for illegal moves or crashes).  
5. **Upload:** The game is uploaded to the server. The payload is not a text file but a compressed data stream containing the move sequence and the associated probability vectors.

### **2.3 The Role of the Rescorer**

A critical, often overlooked component of the Lc0 pipeline is the **Rescorer**. In the early days of chess engines, training data was often "scored" by the game result (0, 0.5, 1). However, self-play games can contain blunders, or "drawn" games might actually be tablebase wins that were adjudicated early due to move limits.

The Rescorer is a server-side (or offline) process that acts as a quality control filter. It replays the generated games and cross-references positions with Syzygy endgame tablebases.

* **Adjudication Correction:** If a game was drawn by the 50-move rule, but the tablebase indicates a forced mate in 5 moves, the Rescorer modifies the value target ![][tdf-image6] to reflect the theoretical truth rather than the empirical game result.  
* **Blunder Detection:** If the engine plays a move that transitions a position from "Won" to "Lost" (according to tablebases), the Rescorer can flag this.  
* **Value Head Repair:** The Rescorer updates the training targets root\_q (search value) and best\_q (Q-value of best move) based on this perfect information. This "Rescorer" step ensures that the neural network is trained on the "truth" of the position rather than the potentially flawed evaluation of a previous network iteration.

## ---

**3\. Data Transformation: From PGN to Binary Chunks**

Raw game logs, even when enriched with MCTS stats, are inefficient for training deep neural networks. Text-based formats like PGN require heavy string parsing, which becomes a CPU bottleneck when training on GPUs capable of consuming thousands of positions per second. To alleviate this, Lc0 employs a dedicated transformation step using the trainingdata-tool, which converts game data into a highly optimized, flat binary format packed into "chunks."

### **3.1 The trainingdata-tool**


### **3.2 Data Ingestion: The /games and /upload Endpoints**

Upon the conclusion of a game, the client enters the submission phase. This is handled by the uploadGame function in the client source, which constructs a multipart POST request.1

* **URL:** http://api.lczero.org/games (or /upload depending on API versioning).  
* **Method:** POST  
* **Payload:** Multipart form data or JSON wrapper.

The upload payload is critical as it contains the raw material for the learning process. It consists of three distinct layers of data:

1. **Metadata:** The training\_id and network\_id are echoed back to the server to ensure the data is attributed to the correct epoch. This prevents "data poisoning" where data from an old, weak network might be accidentally mixed into the training set of a new, strong network.1  
2. **Portable Game Notation (PGN):** A text-based record of the moves. This is primarily used for the training.lczero.org frontend (allowing users to view games), for Elo verification (SPRT matches), and for debugging (detecting illegal moves or crashes). The PGN is relatively small (kilobytes).  
3. **Binary Training Data:** This is the bulk of the payload (megabytes). It is a compressed dump of the internal state of the neural network inputs and the MCTS outputs for every position in the game. This data bypasses the PGN format entirely, as parsing PGN to reconstruct bitboards is computationally expensive and lossy regarding search statistics.

### **3.3 Network Distribution: The /networks Endpoint**

Before a client can generate data, it must possess the neural network weights.

* **URL:** http://training.lczero.org/networks  
* **Method:** GET

**Table 1: Training Data Format Comparison**

| Feature | Version 3 (Legacy) | Version 6 (Current) |
| :---- | :---- | :---- |
| **Size** | Variable/Smaller | Fixed 8356 bytes |
| **Input Planes** | Basic Board State | 112 Planes (History \+ Meta) |
| **Search Stats** | Limited | root\_q, best\_q, policy\_kld |
| **Use Case** | Run 1 / Early Run 2 | Run 3 / Transformer Training |
| **Consistency Risk** | Low (Simple text) | High (Strict binary alignment) |

#### Section 3.2: The V6 Binary Structure

The trainingdata-tool is a specialized C++ utility designed to parse PGN files (often pre-processed by pgn-extract to ensure standard compliance) and emit the proprietary binary format used by the lczero-training pipeline.

**Workflow:**

1. **Input:** A .pgn file containing games. These PGNs usually contain proprietary tags (e.g., { %eval... }) or comments that encode the MCTS visit counts from the generation phase.  
2. **Parsing & Validation:** The tool reads the moves, verifying legality and reconstructing the board state for every ply. It calculates necessary metadata such as castling rights, en passant availability, and the 50-move counter.  
3. **Target Calculation:** For every position, the tool computes the training targets. It extracts the winner (for the Value head) and the move probability distribution (for the Policy head).  
4. **Chunking:** To facilitate random shuffling and efficient disk I/O, individual positions from thousands of games are aggregated into "chunks." A chunk is a collection of binary records, typically compressed (gzip or tar).  
5. **Output:** The tool produces a directory of binary files (e.g., chunk\_001.gz, chunk\_002.gz).

The command-line invocation typically looks like:

Bash

./trainingdata-tool input.pgn

This process effectively "freezes" the data. Once in chunk format, the data is agnostic to the chess rules; it is simply a sequence of tensors and floats ready for ingestion by the neural network trainer.

### **3.2 Evolution of Data Formats (V3 to V6)**

The binary format has evolved to accommodate new research ideas and network architectures. Understanding this evolution is key to understanding the current V6 specification.

* **V3 Format:** The baseline for modern Lc0 training. It contained the board planes, the policy probabilities, and the game result. It relied on a simpler set of auxiliary planes.  
* **V4 Format:** Introduced separate fields for root\_q, best\_q, root\_d, and best\_d. This marked the shift towards explicitly modeling draw probabilities (![][tdf-image22]) alongside win/loss values (![][tdf-image23]). In V3, draws were often implicitly handled or mashed into the ![][tdf-image23] value; V4 made them first-class training targets.  
* **V5 Format:** Added the input\_format field, allowing the struct to self-describe how the planes are encoded. This was crucial for experimenting with different input feature sets (e.g., changing history length) without breaking backward compatibility with the parser. It also introduced invariance\_info to better handle symmetries.  
* **V6 Format (2024-2025 Standard):** The V6 format further expanded the metadata to support "Moves Left" prediction. It added root\_m, best\_m, and plies\_left. This data allows the network to learn not just *if* a position is won, but *how quickly* it can be won, refining the engine's instinct in converting winning advantages.

### **3.3 The V6TrainingData Struct: A Byte-Level Analysis**

The V6TrainingData struct is a packed C-structure with a fixed size of **8,356 bytes**. This fixed size allows the chunkparser.py to read the file using fixed-stride offsets, which is significantly faster than parsing variable-length records.

The struct layout is as follows:

| Offset (approx) | Field Name | Data Type | Count | Description |
| :---- | :---- | :---- | :---- | :---- |
| 0 | version | uint32\_t | 1 | Version identifier (e.g., 6). |
| 4 | input\_format | uint32\_t | 1 | Describes the layout of the planes array. |
| 8 | probabilities | float | 1858 | The policy target vector. 1858 floats representing probabilities for each potential move. |
| 7440 | planes | uint64\_t | 104 | The input feature bitboards. 104 uint64 values, each representing an 8x8 bitmask. |
| 8272 | castling\_us\_ooo | uint8\_t | 1 | Castling right: Us, Queenside. |
| 8273 | castling\_us\_oo | uint8\_t | 1 | Castling right: Us, Kingside. |
| 8274 | castling\_them\_ooo | uint8\_t | 1 | Castling right: Them, Queenside. |
| 8275 | castling\_them\_oo | uint8\_t | 1 | Castling right: Them, Kingside. |
| 8276 | side\_to\_move\_or\_ep | uint8\_t | 1 | Encodes side to move and En Passant target file. |
| 8277 | rule50\_count | uint8\_t | 1 | 50-move rule counter. |
| 8278 | invariance\_info | uint8\_t | 1 | Symmetry/Transform flags. |
| 8279 | dummy | uint8\_t | 1 | Padding/Legacy field. |
| 8280 | root\_q | float | 1 | Average Q-value of the root node. |
| 8284 | best\_q | float | 1 | Q-value of the best move. |
| 8288 | root\_d | float | 1 | Draw probability (root). |
| 8292 | best\_d | float | 1 | Draw probability (best move). |
| 8296 | root\_m | float | 1 | Avg moves remaining (root). |
| 8300 | best\_m | float | 1 | Moves remaining (best move). |
| 8304 | plies\_left | float | 1 | Ground truth game length. |
| 8308 | result\_q | float | 1 | Game result (-1, 0, 1). |
| 8312 | result\_d | float | 1 | Game result (Draw binary). |
| ... | ... | ... | ... | (Additional replay/debug fields: played\_q, orig\_q, visits, policy\_kld) |

**Structural Insights:**

* **The 1858 Probabilities:** The largest component of the struct (over 7KB) is the policy vector. This consumes \~88% of the storage space. This massive footprint is due to the decision to store probabilities as full 32-bit floats. Some experimental branches have explored quantizing this to uint8 or float16 to reduce bandwidth, but standard V6 uses float32.  
* **The 104 Planes:** Note that the input planes are stored as uint64\_t bitboards, not floats. This is a compression ratio of 32:1 compared to storing them as floats. The expansion from bitboard to float tensor happens just-in-time in Python memory (RAM), saving disk I/O bandwidth.

## ---

**4\. The Neural Network Input: 112 Planes Specification**

The "Input" to an Lc0 network is not a raw board representation (like FEN). It is a tensor of shape ![][tdf-image24]. This tensor encodes the spatial distribution of pieces, the history of the game, and the game state metadata.

### **4.1 The Need for History**

Chess is a Markovian game in theory (the current position determines the future), but in practice, history matters for two reasons:

1. **Repetition Draw Rules:** The state of the board includes the repetition counter.  
2. **Dynamics and "Velocity":** While not strictly necessary for correctness, providing the network with the previous board states helps it infer the "direction" of the game—e.g., whether a position is static or dynamic, or whether a player is shuffling pieces (indicating a lack of constructive plan).

Lc0 typically uses a history length of 8 steps (Current position ![][tdf-image25] plus 7 previous moves ![][tdf-image26]).

### **4.2 Breakdown of the 112 Planes**

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
3. **Storage Calculation:** ![][tm-image2]. These are stored explicitly in the planes array.13  
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



#### V6 Value Repair Fields (2024-2025 Enhancement)

The V6 format introduced critical "Value Repair" fields that enable the training pipeline to learn from the *correction* of errors:

| Field | Type | Description |
| :---- | :---- | :---- |
| `orig_q`, `orig_d`, `orig_m` | float | Values as they were *during the game generation* |
| `played_q`, `played_d`, `played_m` | float | Values derived from the move *actually played* |

**Significance:** By storing both original evaluations and outcomes, the training pipeline calculates the error in the engine's evaluation at generation time. This allows "Value Repair," where training targets are adjusted based on known outcomes, correcting for search artifacts.

The `invariance_info` field now contains a detailed bitfield encoding side-to-move and symmetry transforms (mirror/flip), enabling data augmentation through geometric symmetries.

#### Section 3.3: The 112-Plane Input Representation

The 112 planes are decomposed into ![][tdf-image27] feature planes (104 planes) plus 8 constant planes.

#### **4.2.1 The 13 Feature Planes (Per Time Step)**

For each of the 8 time steps in the history buffer, the following 13 planes are generated:

| Plane Index | Feature Description | Type |
| :---- | :---- | :---- |
| 1 | **P1 Pawn** | Bitmask (Sparse) |
| 2 | **P1 Knight** | Bitmask (Sparse) |
| 3 | **P1 Bishop** | Bitmask (Sparse) |
| 4 | **P1 Rook** | Bitmask (Sparse) |
| 5 | **P1 Queen** | Bitmask (Sparse) |
| 6 | **P1 King** | Bitmask (Sparse) |
| 7 | **P2 Pawn** | Bitmask (Sparse) |
| 8 | **P2 Knight** | Bitmask (Sparse) |
| 9 | **P2 Bishop** | Bitmask (Sparse) |
| 10 | **P2 Rook** | Bitmask (Sparse) |
| 11 | **P2 Queen** | Bitmask (Sparse) |
| 12 | **P2 King** | Bitmask (Sparse) |
| 13 | **Repetitions** | Special Mask |

* **P1 vs P2:** "P1" always refers to the player to move, and "P2" to the opponent. The board is always oriented so that P1 is at the "bottom." If Black is to move, the board is flipped/rotated, and Black's pieces populate the P1 planes.  
* **Repetitions Plane:** This plane indicates whether the configuration of pieces at that specific history step has occurred before in the game. If a position has occurred once before, the bits corresponding to that configuration (or sometimes the entire plane) are set to 1\. This provides the network with the context needed to avoid or force 3-fold repetition.

**Total Feature Planes:** ![][tdf-image28]. These correspond to the planes array in the V6 struct.

#### **4.2.2 The 8 Meta Planes**

The final 8 planes are "broadcast" planes. They are usually filled entirely with 0s or 1s (or a normalized constant value) across the ![][tdf-image29] grid. They provide global state information to the convolutional layers, which otherwise operate only on local spatial features.

| Plane Index | Feature | Description |
| :---- | :---- | :---- |
| 105 | **Castling Us: O-O** | 1.0 if Kingside castling is legal for P1. |
| 106 | **Castling Us: O-O-O** | 1.0 if Queenside castling is legal for P1. |
| 107 | **Castling Them: O-O** | 1.0 if Kingside castling is legal for P2. |
| 108 | **Castling Them: O-O-O** | 1.0 if Queenside castling is legal for P2. |
| 109 | **Side to Move** | Often all 0s for White, all 1s for Black (or encoded via P1/P2 orientation). |
| 110 | **Rule 50 Count** | Normalized value ![][tdf-image30]. |
| 111 | **Game Progress** | Optional/Version-dependent. |
| 112 | **Bias / Constant** | All 1.0s. Provides a baseline activation. |

*Note on En Passant:* En Passant targets are sometimes encoded in the meta-planes or implicitly handled. In V6, the side\_to\_move\_or\_ep field in the struct is used to construct the relevant plane or update the input tensor during the inflation step.

### **4.3 Symmetries and Invariance**

Chess board geometry allows for certain symmetries, primarily horizontal flipping (mirroring). While the rules of chess are not perfectly symmetric (due to Kingside vs Queenside castling differences), they are *nearly* symmetric. Lc0 exploits this to augment the dataset.

During training, the pipeline can apply a horizontal flip to the board state. If the board is flipped:

    "sticky\_endgames": "true"  
  }  
}

**Second-Order Analysis of Response Parameters:**

* **Search Exploration Control (cpuct):** The parameters cpuct and cpuct\_at\_root control the "Polynomial Upper Confidence Trees" formula, which balances exploration (looking at new moves) vs. exploitation (playing the best known move). The server specifies cpuct\_at\_root=1.414 (approx ![][re-image1]), a higher value than the interior nodes, forcing the engine to widen its search at the root.2 This ensures diverse training data, preventing the network from getting stuck in local optima.  
* **First Play Urgency (fpu):** The fpu\_strategy determines how the engine evaluates moves it hasn't visited yet. The configuration reduction with fpu\_value=0.49 suggests a pessimistic approach, assuming unvisited moves are slightly worse than the parent node, unless at the root (fpu\_value\_at\_root=1.0), where optimism is encouraged to widen the search beam.2  
* **Temperature Decay (policy\_softmax\_temp):** To generate varied games, the engine plays moves stochastically based on their probability. policy\_softmax\_temp=1.45 flattens the distribution, increasing randomness. The tempdecay\_moves=60 parameter instructs the engine to switch to deterministic play (best move only) after move 60, ensuring that the endgame data is high-quality and precise, while the opening remains diverse.2  
* **Time Management Simulation (moves\_left):** Parameters like moves\_left\_slope and quadratic\_factor inject a synthetic sense of urgency into the search. Since self-play has no opponent clock, these parameters model the psychological and strategic pressure of a ticking clock, training the network to evaluate positions differently when "time" is short.2

#### **4.1.2. OpenBench (SPRT)**

OpenBench is a distributed framework for Sequential Probability Ratio Testing (SPRT). It is used to validate binary changes, such as a new neural network architecture or a patch to the C++ engine code.1

* **Mechanism:** Clients are assigned a "Match" between a **Candidate** (the new configuration) and a **Base** (the current standard). The system tracks the sequence of wins, losses, and draws to calculate a Log-Likelihood Ratio (LLR). The test continues until the LLR exceeds a threshold for acceptance (proving superiority) or rejection (proving inferiority).22

### **4.2. Server Assignment and Execution**

The assignment of Tuning tasks is driven by the active "Tests" in the system.

**Assignment Logic:**

When a client requests a task via get\_task, the server checks if the client qualifies for any active tuning runs. Qualification is often stricter for Tuning than for Matches:

1. **Client Version:** OpenBench tests often require specific, newer versions of the lc0 binary or the client to support new features (e.g., "Squeeze-Excitation" support in Run 2).6  


In the specific context of Lc0, the network is defined by a "Tower" of residual blocks. The dimensions of this tower are the primary differentiators between the training runs. The standard notation used throughout this report is ![][ds-image2], where:

* ![][ds-image3] **(Filters):** The number of feature maps (channels) in each convolutional layer. This determines the "width" of the network, or its capacity to capture parallel patterns and features at each level of abstraction.  
* ![][ds-image4] **(Blocks):** The number of residual blocks stacked sequentially. This determines the "depth" of the network, or its ability to perform sequential reasoning and compose abstract concepts from lower-level features.

#### Section 3.4: Output Heads (Policy, Value, MLH)


### **5.1 The 112-Plane Standard Specification**

The input to the ResNet tower is an ![][ds-image26] tensor. This tensor is constructed by stacking "planes" of ![][ds-image27] bitboards. The decomposition is as follows 6:

| Plane Index | Feature Description | Count |
| :---- | :---- | :---- |
| **0 \- 103** | **History Planes** (8 Time Steps) | **104** |
|  | *Per Step:* 13 Planes |  |
|  | \- Own Pieces (P, N, B, R, Q, K) | 6 |
|  | \- Opponent Pieces (P, N, B, R, Q, K) | 6 |
|  | \- Repetition Count (1 for single, 2 for double) | 1 |
| **104 \- 111** | **Meta Planes** (Game State) | **8** |
| 104 | Castling Rights: White Kingside | 1 |
| 105 | Castling Rights: White Queenside | 1 |
| 106 | Castling Rights: Black Kingside | 1 |
| 107 | Castling Rights: Black Queenside | 1 |
| 108 | Side to Move (0=Black, 1=White) | 1 |
| 109 | 50-Move Rule Counter (Normalized) | 1 |
| 110 | Constant 0 (Reserved) | 1 |
| 111 | Constant 1 (Bias/Ones) | 1 |

### **5.2 Architectural Implications of History**

The 104 history planes represent the current position (![][ds-image28]) and the 7 preceding positions (![][ds-image29]).

* **Run 1 (T60) Utilization:** The massive depth of Run 1 networks allows them to effectively utilize the full 8-step history. The first convolutional block acts as a "temporal feature extractor," detecting patterns like "has the opponent just moved their knight back and forth?" (indicating a lack of a plan) or "is this position a repetition?"  
* **Run 2/3 Variations:** In experimental branches of Run 3, developers tested reducing the history to 4 steps (reducing input size to roughly 60 planes). While this reduces the computational load of the first layer, it was generally found that the 112-plane standard was optimal. The "Repetition" plane is critical; without it, the network cannot understand 3-fold repetition rules and will blunder into unwanted draws.

### **5.3 Normalization of the 50-Move Counter**

The "50-Move Rule Counter" plane (Plane 109\) is a scalar value broadcast across the ![][ds-image27] grid.

* **Implementation:** It is typically normalized as ![][ds-image30].  
* **Run Difference:** Run 3 experiments often involved tweaking this normalization. If the network treats the 50-move rule too linearly, it may not "panic" enough when the counter reaches 98 (49 moves). Run 3 tested non-linear activations for this plane to create a "deadline pressure" effect in the network's evaluation.

## ---

**6\. Technical Deep Dive: Training Data Binary Formats**

The architecture of the network is inextricably linked to the format of the data it consumes. The Lc0 project evolved its binary training data format from **V3** to **V6** during the timeline of Runs 1, 2, and 3\. This evolution reflects a shift in what the architects believed was important for the network to learn.

### **6.1 The V3 Format (Early Run 1\)**

* **Structure:** Compressed state \+ Policy vector \+ Game Result (![][ds-image31]).  
* **Limitation:** V3 only stored the final outcome of the game. It did not store the internal evaluations of the MCTS (![][ds-image32]) at each step. This meant the network was training purely on the "ground truth" of the game end, which is a high-variance signal. A brilliant move in a lost game would be labeled as "Loss" (-1), confusing the network.

### **6.2 The V4/V5 Formats (Mid Run 1, Run 2\)**

* **Innovation:** These formats introduced **Root Statistics**.  
* **Data Fields:** stored result\_q (the average value of the nodes in the MCTS search) and result\_d (the distribution of visits).  

1. The input planes are mirrored.  
2. The policy vector indices must be remapped (e.g., a move from a1 to a8 becomes h1 to h8).  
3. Castling rights must be swapped (Kingside becomes Queenside).

The invariance\_info field in the V6 struct records if a transformation was applied during generation. The chunkparser.py can also apply random flips on-the-fly to double the effective size of the training set.

## ---

**5\. Output Targets: The 1858 Policy Vector**

Perhaps the most distinctive feature of the Lc0 data format is the policy output. While AlphaZero used a convolutional output head (![][tdf-image31]), Lc0 uses a **flat fully-connected output** of size 1858\.

### **5.1 The 1858-Move Mapping Logic**

The choice of 1858 represents a specific optimization. The set of all theoretically possible legal moves from any position is large, but limited. Lc0 enumerates these moves to create a fixed-size vocabulary of actions.

The 1858 indices map to moves as follows:

1. **Queen Moves (Sliding):** From any square to any square on the same rank, file, or diagonal.  
   * This covers Rooks and Bishops as subsets of Queen moves.  
   * Encoding: A set of "planes" (conceptually) for directions (N, NE, E, SE, S, SW, W, NW) ![][tdf-image32] distance (1..7).  
   * Instead of a sparse 3D tensor, these are flattened.  
2. **Knight Moves:** 8 possible L-shapes from a given square.  
3. **Underpromotions:** 3 specific moves (promotion to Knight, Bishop, Rook) for pawn moves landing on the 8th rank. (Queen promotion is covered by the sliding moves).

**Why Flat?**

DeepMind's AlphaZero used a convolutional policy head to enforce spatial invariance—learning that a Knight jump at e4 is "the same" action as a Knight jump at d4. Lc0's flat head forces the network to learn the meaning of "Knight jump from e4" and "Knight jump from d4" independently.

While this seemingly increases the learning difficulty, it significantly reduces the computational cost of the policy head. A ![][tdf-image33] vector is faster to compute than an ![][tdf-image31] tensor (4672 values). Empirical testing by the Lc0 team suggested that the flat head performed comparably to the convolutional head for chess, likely because the board is small enough that the network has ample capacity to memorize the spatial translations.

### **5.2 Deciphering the probabilities Array**

The probabilities array in V6TrainingData contains the float values corresponding to these 1858 indices.

* **Sparse Distribution:** In any given position, only a fraction of the 1858 moves are legal. The illegal moves should theoretically have a probability of 0\.  
* **Softmax Target:** The values in this array sum to 1.0 (or close to it). They represent the MCTS visit distribution:  
  ![][tdf-image34]  
  The training objective is to minimize the Cross-Entropy loss between the network's predicted softmax distribution and this target distribution.

## ---

**6\. Pipeline Consumption: The chunkparser.py Mechanism**

The final stage of the pipeline is the ingestion of these chunks by the training framework. This is handled by Python scripts, principally chunkparser.py in the lczero-training repository. This script acts as the DataLoader.

### **6.1 The Shuffle Buffer**

A single game of chess contains highly correlated positions. Training on a sequence of positions from the same game ![][tdf-image35] introduces bias and can lead to oscillation in the gradient descent.



#### Win/Draw/Loss (WDL) Distribution Heads

Modern Lc0 networks have transitioned from scalar value targets to **WDL probability vectors**:

- **Output Structure:** Three probabilities: $P_{win}$, $P_{draw}$, $P_{loss}$
- **Training Configuration:** YAML configs specify distinct weights (e.g., `policy_loss_weight: 1.0`, `value_loss_weight: 1.0`)
- **Contempt Implementation:** The WDL output enables "contempt" by adjusting draw utility. Configured via `WDLCalibrationElo`, the engine can steer toward complex, decisive positions even at slightly higher objective risk.

This granular understanding of draw probabilities enables sophisticated match strategies impossible with scalar evaluation.

#### Section 3.5: Search Statistics and Metadata

To mitigate this, chunkparser.py implements a **Shuffle Buffer**:

1. **Windowed Reading:** The parser opens multiple chunk files simultaneously (e.g., 20 files).  
2. **Reservoir Sampling:** It reads samples into a large in-memory buffer (e.g., holding 1,000,000 positions).  
3. **Random Yield:** When the trainer requests a batch, the parser draws random samples from this buffer.  
4. **Refill:** As samples are consumed, new samples are read from the disk chunks to replenish the buffer.

This mechanism ensures that every mini-batch sent to the GPU contains a diverse mix of opening, middlegame, and endgame positions from thousands of different games.

### **6.2 Plane Inflation and Tensor Construction**

This is the most CPU-intensive part of the training loop. The V6TrainingData struct stores planes as uint64. The GPU expects float32.

The parser performs the following bit-manipulation for every sample in the batch:

Python

\# Pseudo-code for Plane Inflation  
def inflate\_planes(v6\_sample):  
    input\_tensor \= np.zeros((112, 64), dtype=np.float32)

    \# 1\. Inflate History Planes (0-103)  
    for i in range(104):  
        bitboard \= v6\_sample.planes\[i\]  
        \# Bitwise operations to scatter bits to float array  
        input\_tensor\[i\] \= bitboard\_to\_array(bitboard)

    \# 2\. Construct Meta Planes (104-111)  
    \# Side to move  
    input\_tensor \= 1.0 if v6\_sample.side\_to\_move else 0.0  

#### **3.3.2. The 104 Input Planes**

A critical distinction exists between the *conceptual* input to the neural network and the *stored* input in the V6 format. The Lc0 architecture typically utilizes **112 input planes** (8x8 grids of binary features).13 However, the V6 struct allocates storage for only **104 planes** (uint64\_t planes).

The resolution to this discrepancy lies in data compression and reconstruction:

### Chapter 4: Data Storage, Consistency, and Orphaned Records

#### Section 4.1: PostgreSQL Database Schema


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
  * result: The outcome (1-0, 0-1, 1/2). This is the "Ground Truth" value (![][db-image1]) used in the AlphaZero loss function ![][db-image2].  
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

**Key Tables Referenced**

**Table 2: Types of Orphaned Records**

| Type | Definition | Primary Cause | System Impact |
| :---- | :---- | :---- | :---- |
| **Ghost Record** | DB Row exists, File missing | Storage failure after DB commit | Training pipeline crashes/skips; Stats inflation |
| **Zombie File** | File exists, DB Row missing | DB rollback after Storage write | Wasted storage cost; Data leakage |
| **Version Orphan** | File exists, Wrong Version | Legacy data not migrated | Unusable data; Pipeline errors |

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

#### Section 4.2: File Storage Topology


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

    \# Castling  
    input\_tensor \= v6\_sample.castling\_us\_oo  
    \#... etc

    \# Reshape to (112, 8, 8\)  
    return input\_tensor.reshape(112, 8, 8)

This inflation is typically offloaded to multiple CPU worker processes to keep the GPU fed.

## ---

**7\. Match vs. Training Data: A Critical Distinction**

Within the Lc0 ecosystem, "Data" falls into two categories which are treated differently by the pipeline.

| Feature | Training Data (Self-Play) | Match Data (Validation) |
| :---- | :---- | :---- |
| **Source** | Generated by lc0 in training mode. | Generated by lc0 in match mode vs opponents. |
| **Exploration** | **High.** Uses Dirichlet noise and Temperature \> 0\. | **None.** Temperature \= 0 (Greedy best move). |
| **Purpose** | To expand the network's knowledge and explore new lines. | To verify the network's strength (Elo) and select best nets. |
| **Format** | Uploaded as binary/compressed training stats with full policy vectors. | Usually uploaded as PGN or minimal result logs. |
| **Pipeline Usage** | Ingested by lczero-training for backpropagation. | **Excluded** from training loop. |

**Why Match Data is Excluded:**

Training on match data is dangerous for RL agents. Match games represent the "exploitation" phase of the algorithm. They follow the narrow path of what the network *already believes* is best. If the network trains on its own deterministic best games, it reinforces its existing biases without seeing the refutations of alternative moves. This leads to a feedback loop where the network becomes overconfident in a specific line, even if that line is theoretically unsound.

Therefore, the pipeline strictly segregates these data streams. The trainingdata-tool is almost exclusively applied to the noisy, high-temperature self-play games.

## ---

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

#### Section 4.3: The Orphan Problem

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


**Table 2: Types of Orphaned Records**

| Type | Definition | Primary Cause | System Impact |
| :---- | :---- | :---- | :---- |
| **Ghost Record** | DB Row exists, File missing | Storage failure after DB commit | Training pipeline crashes/skips; Stats inflation |
| **Zombie File** | File exists, DB Row missing | DB rollback after Storage write | Wasted storage cost; Data leakage |
| **Version Orphan** | File exists, Wrong Version | Legacy data not migrated | Unusable data; Pipeline errors |

> *[This topic is covered in detail in Chapter 5, Section 5.7. See page XXX for complete specification.]*

#### Section 4.4: Reconciliation and Cleanup


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

---

## PART III: CLIENT-SERVER INFRASTRUCTURE

### Chapter 5: Client Architecture and Worker Lifecycle

#### Section 5.1: Client Initialization

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

#### Section 5.2: Server Connection and Authentication

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

* **URL:** http://api.lczero.org/next\_game  
* **Method:** POST  
* **Authentication:** Stateless, via parameters in the request body.

#### **3.1.1 Request Payload and Capability Negotiation**

The client does not simply request "work"; it must advertise its capabilities. The getExtraParams function in the client source code reveals a comprehensive list of parameters sent with every request.1

| Parameter | Data Type | Description and Implications |
| :---- | :---- | :---- |
| user | String | The registered username of the contributor. Used for tracking contributions on the leaderboard. |
| password | String | The authentication token. The client code warns that this is stored in plain text, highlighting a reliance on the user to secure their local environment.1 |
| version | String | The semantic version of the lczero-client (e.g., "34"). The server uses this to enforce upgrades or deprecated clients that cannot support newer data formats (e.g., V6). |
| token | String | A unique session identifier (often a random ID) used to deduplicate active worker counts on the server stats page. |
| gpu | String | The specific GPU model (e.g., "RTX 3090"). This is crucial for scheduling. Large Transformer networks (BT4) may be inefficient or impossible to run on older hardware, allowing the server to selectively assign lighter ResNet tasks. |
| train\_only | Boolean | A flag indicating if the client is restricted to self-play training games or if it is available for "match" games (validation matches against other engines or older networks). |

#### Section 5.3: Task Acquisition Protocol

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


#### **3.1.2 Response Structure: Dynamic Engine Configuration**

The server's response is a JSON object that acts as a remote configuration file for the lc0 engine. This allows the developers to tweak search behavior globally without requiring a software update on the client side. Analysis of the "Train Params" from active runs provides a detailed view of this schema.2

JSON

{  
  "training\_id": 1234,  
  "network\_id": 900434,  
  "network\_url": "https://storage.lczero.org/files/networks/weights\_900434.pb.gz",  
  "options": {  
    "visits": "400",  
    "cpuct": "1.20",  
    "cpuct\_at\_root": "1.414",  
    "root\_has\_own\_cpuct\_params": "true",  
    "fpu\_strategy": "reduction",  
    "fpu\_value": "0.49",  
    "fpu\_strategy\_at\_root": "absolute",  
    "fpu\_value\_at\_root": "1.0",  
    "policy\_softmax\_temp": "1.45",  
    "noise\_epsilon": "0.1",  
    "noise\_alpha": "0.12",  
    "tempdecay\_moves": "60",  
    "tempdecay\_delay\_moves": "20",  
    "moves\_left\_max\_effect": "0.3",  
    "moves\_left\_slope": "0.007",  
    "moves\_left\_quadratic\_factor": "0.85",  
    "smart\_pruning\_factor": "0.0",  
    "sticky\_endgames": "true"  

#### Section 5.4: Network Download and Caching

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

#### Section 5.5: Game Execution

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

This embedding allows the server to extract the training targets (![][cs-image1] for policy, ![][cs-image2] for value) directly from the game record.

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

* **Client Capability:** Match tasks generally require GPU acceleration to be efficient. The server may reserve CPU-only clients for different workloads or assign them smaller networks to ensure they can complete games within reasonable timeframes.9

### **3.2. Execution: The Self-Play Loop**

Upon receiving a Match task, the client executes the lc0 binary in a specialized self-play mode. This mode differs significantly from standard chess engine analysis.

1. **Exploration vs. Exploitation:** To ensure the network sees a diverse range of positions, self-play games introduce noise into the search. The root node of the MCTS tree is perturbed with Dirichlet noise, and moves are chosen proportionally to their visit counts (soft choice) rather than greedily selecting the best move (hard choice) for the first ![][tm-image1] moves (typically 30\) of the game.10  
2. **Rescoring and Adjudication:** As the game progresses, a "Rescorer" component monitors the board state. It checks positions against Syzygy tablebases (endgame databases) to adjudicate games early if a known win/loss/draw position is reached. It also detects "intentional blunders"—moves that significantly drop the evaluation—to ensure the training data reflects high-quality play.1  
3. **No Pondering:** Unlike tournament play, where an engine "ponders" during the opponent's time, self-play engines run sequentially or in parallel batches without pondering to maximize throughput.11

### **3.3. Data Format: The V6 Training Data Standard**

The output of a Match task is not a PGN text file. Text formats are inefficient for the massive volume of data Lc0 generates. Instead, Lc0 uses a highly optimized binary format. The 2024-2025 standard is the **Version 6 (V6)** training data format.12

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

#### Section 5.6: Data Upload Protocol


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

  }  
}

**Second-Order Analysis of Response Parameters:**

* **Search Exploration Control (cpuct):** The parameters cpuct and cpuct\_at\_root control the "Polynomial Upper Confidence Trees" formula, which balances exploration (looking at new moves) vs. exploitation (playing the best known move). The server specifies cpuct\_at\_root=1.414 (approx ![][re-image1]), a higher value than the interior nodes, forcing the engine to widen its search at the root.2 This ensures diverse training data, preventing the network from getting stuck in local optima.  
* **First Play Urgency (fpu):** The fpu\_strategy determines how the engine evaluates moves it hasn't visited yet. The configuration reduction with fpu\_value=0.49 suggests a pessimistic approach, assuming unvisited moves are slightly worse than the parent node, unless at the root (fpu\_value\_at\_root=1.0), where optimism is encouraged to widen the search beam.2  
* **Temperature Decay (policy\_softmax\_temp):** To generate varied games, the engine plays moves stochastically based on their probability. policy\_softmax\_temp=1.45 flattens the distribution, increasing randomness. The tempdecay\_moves=60 parameter instructs the engine to switch to deterministic play (best move only) after move 60, ensuring that the endgame data is high-quality and precise, while the opening remains diverse.2  
* **Time Management Simulation (moves\_left):** Parameters like moves\_left\_slope and quadratic\_factor inject a synthetic sense of urgency into the search. Since self-play has no opponent clock, these parameters model the psychological and strategic pressure of a ticking clock, training the network to evaluate positions differently when "time" is short.2

### **3.2 Data Ingestion: The /games and /upload Endpoints**

Upon the conclusion of a game, the client enters the submission phase. This is handled by the uploadGame function in the client source, which constructs a multipart POST request.1

* **URL:** http://api.lczero.org/games (or /upload depending on API versioning).  
* **Method:** POST  
* **Payload:** Multipart form data or JSON wrapper.

The upload payload is critical as it contains the raw material for the learning process. It consists of three distinct layers of data:

1. **Metadata:** The training\_id and network\_id are echoed back to the server to ensure the data is attributed to the correct epoch. This prevents "data poisoning" where data from an old, weak network might be accidentally mixed into the training set of a new, strong network.1  
2. **Portable Game Notation (PGN):** A text-based record of the moves. This is primarily used for the training.lczero.org frontend (allowing users to view games), for Elo verification (SPRT matches), and for debugging (detecting illegal moves or crashes). The PGN is relatively small (kilobytes).  
3. **Binary Training Data:** This is the bulk of the payload (megabytes). It is a compressed dump of the internal state of the neural network inputs and the MCTS outputs for every position in the game. This data bypasses the PGN format entirely, as parsing PGN to reconstruct bitboards is computationally expensive and lossy regarding search statistics.

### **3.3 Network Distribution: The /networks Endpoint**

#### Section 5.7: Failure Modes and Error Handling

**9\. Failure Modes and Error Handling**

The distributed nature of the system means errors are the norm, not the exception. The client handles several specific failure modes.

### **9.1 Network Mismatches**

If a client has a cached network file that is corrupt, the hash check (NetworkId) will fail. The client handles this by forcing a re-download from the NetworkUrl. This self-healing property ensures that bit-rot on the client disk doesn't propagate to the engine process.

### **9.2 Engine Version Incompatibility**

As noted in the retry logic section, the server can reject clients with an "upgrade" message. This handles the lifecycle of the *engine* binary. If a new network architecture (e.g., the introduction of Squeeze-Excitation units) requires a new engine binary to parse, the server can lock out old clients until they update, preventing them from crashing when trying to load the new .pb.gz files.

### **9.3 "Phantom" Games and Memory Errors**

One of the insidious issues in distributed chess computing is "phantom" pieces or illegal moves caused by non-ECC RAM errors on consumer hardware.17 The client does not strictly validate the *chess logic* (that is the engine's job), but it does monitor the process exit code. If the engine crashes due to an illegal state, the client discards the result. The server's Rescorer provides the final line of defense against subtle corruption that doesn't cause a crash.

### Chapter 6: Server APIs and HTTP Endpoints

#### Section 6.1: Task Distribution Endpoints


#### **3.1.2 Response Structure: Dynamic Engine Configuration**

The server's response is a JSON object that acts as a remote configuration file for the lc0 engine. This allows the developers to tweak search behavior globally without requiring a software update on the client side. Analysis of the "Train Params" from active runs provides a detailed view of this schema.2

JSON

{  
  "training\_id": 1234,  
  "network\_id": 900434,  
  "network\_url": "https://storage.lczero.org/files/networks/weights\_900434.pb.gz",  
  "options": {  
    "visits": "400",  
    "cpuct": "1.20",  
    "cpuct\_at\_root": "1.414",  
    "root\_has\_own\_cpuct\_params": "true",  
    "fpu\_strategy": "reduction",  
    "fpu\_value": "0.49",  
    "fpu\_strategy\_at\_root": "absolute",  
    "fpu\_value\_at\_root": "1.0",  
    "policy\_softmax\_temp": "1.45",  
    "noise\_epsilon": "0.1",  
    "noise\_alpha": "0.12",  
    "tempdecay\_moves": "60",  
    "tempdecay\_delay\_moves": "20",  
    "moves\_left\_max\_effect": "0.3",  
    "moves\_left\_slope": "0.007",  
    "moves\_left\_quadratic\_factor": "0.85",  
    "smart\_pruning\_factor": "0.0",  
    "sticky\_endgames": "true"  
  }  
}

**Second-Order Analysis of Response Parameters:**

> *[This topic is covered in detail in Chapter 5, Section 5.3. See page XXX for complete specification.]*

#### Section 6.2: Data Ingestion Endpoints

* **Search Exploration Control (cpuct):** The parameters cpuct and cpuct\_at\_root control the "Polynomial Upper Confidence Trees" formula, which balances exploration (looking at new moves) vs. exploitation (playing the best known move). The server specifies cpuct\_at\_root=1.414 (approx ![][re-image1]), a higher value than the interior nodes, forcing the engine to widen its search at the root.2 This ensures diverse training data, preventing the network from getting stuck in local optima.  
* **First Play Urgency (fpu):** The fpu\_strategy determines how the engine evaluates moves it hasn't visited yet. The configuration reduction with fpu\_value=0.49 suggests a pessimistic approach, assuming unvisited moves are slightly worse than the parent node, unless at the root (fpu\_value\_at\_root=1.0), where optimism is encouraged to widen the search beam.2  
* **Temperature Decay (policy\_softmax\_temp):** To generate varied games, the engine plays moves stochastically based on their probability. policy\_softmax\_temp=1.45 flattens the distribution, increasing randomness. The tempdecay\_moves=60 parameter instructs the engine to switch to deterministic play (best move only) after move 60, ensuring that the endgame data is high-quality and precise, while the opening remains diverse.2  
* **Time Management Simulation (moves\_left):** Parameters like moves\_left\_slope and quadratic\_factor inject a synthetic sense of urgency into the search. Since self-play has no opponent clock, these parameters model the psychological and strategic pressure of a ticking clock, training the network to evaluate positions differently when "time" is short.2

### **3.2 Data Ingestion: The /games and /upload Endpoints**

Upon the conclusion of a game, the client enters the submission phase. This is handled by the uploadGame function in the client source, which constructs a multipart POST request.1

* **URL:** http://api.lczero.org/games (or /upload depending on API versioning).  
* **Method:** POST  
* **Payload:** Multipart form data or JSON wrapper.

The upload payload is critical as it contains the raw material for the learning process. It consists of three distinct layers of data:

1. **Metadata:** The training\_id and network\_id are echoed back to the server to ensure the data is attributed to the correct epoch. This prevents "data poisoning" where data from an old, weak network might be accidentally mixed into the training set of a new, strong network.1  
2. **Portable Game Notation (PGN):** A text-based record of the moves. This is primarily used for the training.lczero.org frontend (allowing users to view games), for Elo verification (SPRT matches), and for debugging (detecting illegal moves or crashes). The PGN is relatively small (kilobytes).  
3. **Binary Training Data:** This is the bulk of the payload (megabytes). It is a compressed dump of the internal state of the neural network inputs and the MCTS outputs for every position in the game. This data bypasses the PGN format entirely, as parsing PGN to reconstruct bitboards is computationally expensive and lossy regarding search statistics.

### **3.3 Network Distribution: The /networks Endpoint**

Before a client can generate data, it must possess the neural network weights.

* **URL:** http://training.lczero.org/networks  
* **Method:** GET

The client code includes a flag \--network-mirror, which indicates that the system is designed to support alternative content delivery networks (CDNs).1 This is a scalability feature; if the main server is saturated, the community can host mirrors of the network files (which can be 100MB+ for modern Transformer models), and users can redirect their clients to these mirrors without code changes.

## ---

**4\. Data Interchange Formats: The Logic of the Binary Payload**

The Lc0 API distinguishes itself from standard web APIs by relying heavily on custom binary serialization for the training data. This is necessary because the input to the neural network is not a list of moves, but a high-dimensional tensor representation of the board state.

### **4.1 The Input Feature Map (112 Planes)**

> *[This topic is covered in detail in Chapter 5, Section 5.6. See page XXX for complete specification.]*

#### Section 6.3: Network Distribution Endpoints


The source code in encoder.cc reveals that the board is represented as a stack of 112 "planes" (8x8 bitmaps).3 This input format, known as INPUT\_CLASSICAL\_112\_PLANE, is the standard for recent runs.

| Plane Index | Feature Description | Count | Insight |
| :---- | :---- | :---- | :---- |
| 0-5 | Own Pieces (P, N, B, R, Q, K) | 6 | One-hot encoding of current player's pieces. |
| 6-11 | Opponent Pieces (P, N, B, R, Q, K) | 6 | One-hot encoding of opponent's pieces. |
| 12-103 | History (Last 7 moves) | 96 | The previous 7 board states (12 planes \* 8 steps). This allows the network to perceive "time," detecting 3-fold repetition and irreversible moves. |
| 104-107 | Castling Rights | 4 | White/Black King/Queen-side castling availability. |
| 108 | Side to Move | 1 | All 1s for Black, 0s for White (or vice versa), handling color invariance. |
| 109 | Repetition Count | 1 | Explicit counter for draw-by-repetition. |
| 110 | Rule 50 Counter | 1 | Progress towards the 50-move rule draw. |
| 111 | Constant / Bias | 1 | A plane of all 1s, likely used for edge detection or bias in the convolution. |

This exact ordering is critical. If the API were to change this format (e.g., by adding a plane for "Knight promoted"), it would require a synchronized update of both the client (to encode it) and the training pipeline (to decode it). The "Version" field in the binary header manages this compatibility.

### **4.2 Evolution of the Binary Format (V3 to V6)**

The structure of the data uploaded to /games has evolved to capture more sophisticated training targets. The API supports multiple versions, indicated by the first bytes of the payload.5

#### **4.2.1 V3: The Baseline**

The V3 format was a direct implementation of the AlphaZero paper. It stored the probabilities (the policy vector ![][re-image2]) and the result (the game outcome ![][re-image3]).

* **Limitation:** The game outcome is noisy. A player might play a brilliant game and lose due to a single blunder at move 60\. Training every position in that game as a "loss" provides a noisy signal.

#### **4.2.2 V4: Search Statistics**

V4 added root\_q (Q-value at the root) and best\_q (Q-value of the best move).

#### Section 6.4: Statistics and Query Endpoints

* **Implication:** This allows the network to learn from the MCTS search itself. Even if the game was lost (![][re-image4]), the search might have calculated that the position at move 20 was winning (![][re-image5]). Training on ![][re-image6] helps smooth the learning process.

#### **4.2.3 V5: Symmetry and Invariance**

V5 introduced invariance\_info replacing the move\_count.

* **Implication:** Chess is spatially symmetric (mostly). A position reflected horizontally is strategically identical (swapping King-side/Queen-side castling rights). This field helps the training server perform "Data Augmentation"—generating 8 training samples from a single position by rotating and flipping the board—without corrupting the rules of castling or en passant.

#### **4.2.4 V6: Uncertainty and Active Learning (2024-2025 Standard)**

The V6 format represents a significant leap, adding policy\_kld (Kullback-Leibler Divergence) and visits.5

* **Technical Detail:** policy\_kld measures the divergence between the raw network policy (the "intuition") and the final MCTS policy (the "calculation").  
* **Strategic Insight:** A high KLD indicates the network was "surprised" by the search—it thought a move was bad, but the search found it was good. This metric allows the API to prioritize these positions for training (Active Learning), effectively telling the network: "Focus on the positions you misunderstand the most."

## ---

**5\. Protocol Verification: The Absence of WebSockets**

The original request specifically queried for the presence of WebSocket implementations. A thorough review of the lczero-client source code and the architectural constraints leads to a definitive conclusion: **The Lc0 core training loop does not use WebSockets.**

### **5.1 Evidence from Source Code**

The client's main loop is implemented in lc0\_main.go and client.go. The logic structure is synchronous and sequential:

Go

for {  
    // 1\. HTTP POST to upload result  
    uploadGame(...)   
      
    // 2\. HTTP POST to get next assignment  
    nextGame := getNextGame(...)  
      
    // 3\. Execute Engine  
    runEngine(nextGame)  
}

The getNextGame function utilizes standard HTTP clients. There are no imports of WebSocket libraries (e.g., gorilla/websocket or golang.org/x/net/websocket) in the dependency lists.6 The error handling specifically checks for HTTP status codes and JSON parsing errors.7

### **5.2 Architectural Justification**

The decision to use HTTP Polling over WebSockets is likely driven by reliability and scale:

* **Statelessness:** WebSocket connections are stateful. Maintaining open TCP connections for 10,000+ volatile clients (who may disconnect at any moment) consumes significant server resources (file descriptors, memory). A RESTful API is stateless, allowing the server to be trivially scaled horizontally using load balancers like Nginx.  
* **Network Traversal:** The distributed grid runs on volunteer hardware. WebSockets are often blocked by aggressive corporate firewalls or proxy servers. HTTP traffic (Port 80/443) is universally permitted.  
* **Latency Tolerance:** The "real-time" requirement is low. A client playing a 10-minute game does not need sub-millisecond task assignment. A 1-2 second HTTP poll overhead is acceptable.

## ---

**6\. Architecture Negotiation: ResNet vs. Transformer**

A critical function of the API is managing the heterogeneity of the neural network architectures. The Lc0 project has recently transitioned from ResNet (T60) architectures to Transformer (BT4) architectures.

### **6.1 The T60 Era (ResNet)**

In previous runs (Run 1 & 2), the network size was described by "Blocks x Filters" (e.g., 20x256 or 24x320).8 The API simply distributed these files, and the engine loaded them. The input was always the standard 112-plane tensor.

### **6.2 The BT4 Era (Transformer)**

The newer "Run 3" (BT4) introduces the "ChessFormer" architecture.10 This changes the fundamental data flow.

* **Architecture:** Instead of convolution filters, BT4 uses embedding\_size (1024), layers (15), and heads (32).11  
* **Input Tokens:** Transformers operate on sequences of tokens. Lc0 BT4 treats the 64 squares of the board as a sequence of 64 tokens.11  
* **API Adaptation:** The /next\_game endpoint must now perform a capability check. An older client binary might support ResNet but not Transformers. The version parameter in the client's handshake allows the server to segment the workforce, assigning BT4 tasks only to clients running updated, compatible binaries. This explains why the API requires explicit version reporting—it prevents a client from crashing when trying to load an incompatible neural architecture.

## ---

**7\. The Tuning Ecosystem: CLOP**

Beyond self-play training, the API supports a secondary mode of operation: **CLOP (Confident Local Optimization)**. This is used to tune the engine's search parameters (e.g., cpuct, fpu) rather than the neural network weights.

* **Mechanism:** In CLOP mode, the client does not generate training data. Instead, it plays matches against other engines or other versions of Lc0 to test specific parameter values.  
* **API Interaction:** The snippets reference a clop mode.12 While the endpoint structure (/next\_game) remains similar, the *response* changes. Instead of a single network to train, the server sends two configurations (e.g., cpuct=1.2 vs cpuct=1.25) and requests a match result (Win/Loss/Draw).  
* **Data Flow:** The result submission for CLOP is likely a lightweight JSON payload containing the game outcome and the parameters tested, feeding into a Bayesian optimization algorithm on the server that iteratively refines the parameters.

## ---

**8\. Backend Infrastructure and Security**

The server backend is robust but simple, relying on established open-source technologies.

* **Nginx:** Serves as the gateway, handling SSL termination and routing /api requests to the Go application server.14  
* **PostgreSQL:** The primary data store. The use of CREATE MATERIALIZED VIEW commands (e.g., games\_month) indicates that the system optimizes for read-heavy workloads (leaderboards) by pre-calculating aggregations, ensuring the API remains responsive under load.14  
* **Security Vulnerabilities:**  
  * **Plaintext Credentials:** The API relies on user and password parameters sent in the HTTP request.1 The client code explicitly warns users that the password is "stored in plain text" in the config file. If the API is accessed via HTTP (http://api.lczero.org), these credentials are vulnerable to interception.  
  * **Trust Model:** The system operates on a "trust but verify" model. It trusts clients to perform the MCTS correctly. While the server can verify the legality of moves in the PGN, it cannot mathematically verify that the *probability distribution* in the training data was generated by a genuine search. The policy\_kld metric in V6 data likely serves as a statistical anomaly detector to identify and ban malfunctioning or malicious clients.

## ---

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

---

## PART IV: TRAINING RUNS AND DIFFERENTIATION

### Chapter 7: Training Run Architecture (Runs 1, 2, and 3)

#### Section 7.1: Run 1 - The Mainline (T60 Series)

**2\. Run 1: The Mainline (T60 Series)**

**Run 1** constitutes the primary, longest-standing training effort of the Lc0 project during the ResNet era. It is synonymous with the **T60** network series (and the earlier **T40** series). The overriding objective of Run 1 was the maximization of playing strength (Elo) in competitive environments such as the Top Chess Engine Championship (TCEC) and the Chess.com Computer Chess Championship (CCC).

### **2.1 Architectural Evolution of T60**

The T60 architecture was not static; it underwent a significant metamorphosis during the lifespan of Run 1 to adapt to the increasing availability of powerful GPU hardware (e.g., NVIDIA V100s and A100s).

#### **2.1.1 The 320x24 Era**

Initially, Run 1 utilized a ![][ds-image13] topology. This represented a massive leap from the preceding T40 generation (![][ds-image14]). The increase in width to 320 filters required a 56% increase in parameters per layer (![][ds-image15]), significantly increasing the memory bandwidth requirements for inference.

This topology was selected to maximize the "knowledge capacity" of the network. In the context of chess, "width" often correlates with pattern recognition capabilities—the ability to simultaneously track multiple tactical motifs (pins, skewers, back-rank weakness) across the board.

#### **2.1.2 The Transition to 384x30**

As training progressed and Elo gains began to plateau, the architecture was further scaled to ![][ds-image16].2 This upgrade marked the zenith of the ResNet era.

* **Filters (384):** This width allowed for an extremely rich feature space. The 384 channels in each convolutional layer provided the network with the bandwidth to encode highly specific positional nuances, such as "minority attacks in the Carlsbad structure" or "opposite-colored bishop endgames with rook support."  
* **Blocks (30):** The increase to 30 blocks deepened the network's reasoning horizon. In deep learning, depth is often associated with the level of abstraction. A 30-block tower can compose lower-level features (piece locations) into higher-level concepts (control of the center) and finally into abstract strategies (prophylaxis) more effectively than a shallower network.

### **2.2 Hyperparameter Dynamics and Training Events**

Run 1 was characterized by a dynamic training regimen, where hyperparameters were adjusted on-the-fly to destabilize local minima and encourage continued learning. Key events in the Run 1 timeline include:

* **Temperature Decay Adjustments:** The "Temperature" parameter controls the randomness of move selection during self-play data generation. Run 1 saw multiple adjustments to this schedule. For instance, at net 60584, the start temperature was reduced to 1.15, and at 60647, it was dropped to 1.1.2 Lowering temperature forces the network to play more "seriously" during training games, generating high-quality data closer to its true strength, though at the risk of reducing exploration.  
* **Multinet Activation (Brief):** Nets 60951-60955 saw the brief activation of "Multinet," an experimental architecture where the network attempts to predict outcomes from multiple different board orientations or historical states simultaneously. This was quickly deactivated due to a degradation in game generation speed, highlighting the constant tension in Run 1 between architectural complexity and training throughput.2  
* **Learning Rate Drops:** Significant architectural "settling" points occurred when the learning rate was dropped (e.g., at net 60781 to 0.015). These drops typically result in a sharp increase in Elo as the network fine-tunes its weights into a precise local minimum.2

### **2.3 Value Repair and The Rescorer**

A critical innovation that matured during Run 1 was **Value Repair**. In pure AlphaZero-style reinforcement learning, the network learns from the outcome of self-play games ![][ds-image17]. However, self-play games can be noisy; a blunder in move 40 might turn a win into a loss, teaching the network that the opening position (move 10\) was "bad," which is incorrect.

Run 1 implemented a "Rescorer" pipeline. Instead of relying solely on the self-play result, the system would periodically re-evaluate positions from older training data using the latest, strongest network. This effectively "repaired" the training labels. The Run 1 architecture was specifically adapted to handle this via the V6 data format (discussed in Section 6), which includes fields for orig\_q (original value) to track the divergence between the old and new evaluations.3

## ---


The analysis of the lczero-server reveals a fundamental truth about distributed machine learning systems.

**Insight 1: The Database is a Map, Not the Territory.**

In many systems, the database *is* the data. In Lc0, the database is merely a *map* to the data. The neural network learns from the terrain (the files), not the map. Therefore, the system is designed to be resilient to map errors (Ghost Records). If the map says "Game here" and there is no game, the trainer just moves on. This resilience allows the server to use "looser" consistency models (like Eventual Consistency) to achieve the massive throughput required.

**Insight 2: Consistency is a Spectrum, not a Binary.**

For Lc0, data consistency is not just "Exists vs Not Exists." It includes:

* **Structural Consistency:** Is the file V6 or V5?  
* **Semantic Consistency:** Are the input planes valid chess states?  

> *[This topic is covered in detail in Chapter 9, Section 9.2. See page XXX for complete specification.]*

#### Section 7.2: Run 2 - Efficiency Frontier (T70 Series)

**3\. Run 2: The Efficiency Frontier (T70 Series)**

While Run 1 chased the ceiling of performance, **Run 2** was initiated to explore the floor of computational cost. This lineage, associated with the **T70** series (including T74, T76, T77, and T79), focused on producing networks that were "strong enough" but vastly faster and more efficient.3

### **3.1 Architectural Constraints: 192x15 and 128x10**

Run 2 networks are defined by their constrained topologies. The two most common configurations in this run are:

1. ![][ds-image18] **(Medium):** This topology is widely regarded as the "sweet spot" for consumer hardware (e.g., NVIDIA RTX 2060/3060).  
   * **Computation Reduction:** Compared to the Run 1 (![][ds-image1]) baseline, a ![][ds-image19] network has roughly 25% of the width (computational cost scales quadratically with width: ![][ds-image20]) and 50% of the depth. This results in an inference speedup of approximately ![][ds-image21].  
   * **Strategic Impact:** This speedup allows the MCTS to search 8 times as many nodes in the same time control. Run 2 explores the hypothesis that searching deeper with a slightly "dumber" network is often superior to searching shallower with a "genius" network, especially in tactical positions.  
2. ![][ds-image22] **(Small):** Used for extreme efficiency scenarios, such as running Lc0 on mobile devices or browser-based implementations.

### **3.2 Distillation and Knowledge Transfer**

A key architectural difference in Run 2 is the heavy use of **Knowledge Distillation**. Rather than learning chess from scratch (tabula rasa) like the early Run 1 nets, many Run 2 networks were trained to mimic the outputs of a larger "Teacher" network (typically a mature Run 1 T60 net).

* **Loss Function Modification:** In distillation, the loss function includes a term that minimizes the Kullback-Leibler (KL) divergence between the Student's policy distribution and the Teacher's policy distribution.  
  $$ L\_{\\text{distill}} \= \\alpha L\_{\\text{outcome}} \+ \\beta \\text{KL}(P\_{\\text{student}} |

| P\_{\\text{teacher}}) $$

* **Architectural Implication:** This allows the Run 2 architecture to "punch above its weight." Even though it lacks the parameter count to independently derive complex positional truths, it can memorize the evaluation patterns of the T60 network. This effectively compresses the knowledge of the massive Run 1 architecture into the efficient Run 2 topology.4

### **3.3 Human-Like Play and Personality**

Run 2 architectures found a secondary niche in "Human-Like" chess engines. Projects like **Maia Chess** utilize the smaller topologies (![][ds-image23]) typical of Run 2\. The rationale is that the massive Run 1 networks are *too* perfect; their move choices are often alien and incomprehensible to humans. The restricted capacity of Run 2 networks, combined with training on human game databases (rather than self-play), results in an architecture that predicts human errors and stylistic preferences with high accuracy.5

## ---

**4\. Run 3: The Experimental Testbed (T75 & T71)**

**Run 3** represents the "skunkworks" of the Lc0 project—a parallel track used to test radical ideas, new code paths, and specialized tuning configurations that were deemed too disruptive for the stable Run 1 mainline.

### **4.1 Topology Variations: T75 and T71**

Run 3 is associated with the **T75** and **T71** network series.

* **T75:** utilized the ![][ds-image19] topology, similar to Run 2, but generated its own training data rather than relying on distillation. This allowed developers to A/B test whether the "efficiency" of Run 2 came from the topology itself or the distillation process.3  
* **T71:** Experimented with unconventional sizes, such as ![][ds-image24]. This specific size was an attempt to find a middle ground between the lightweight T70s and the massive T60s, aiming for a "Heavyweight" class that could still run on high-end consumer GPUs (like the RTX 2080 Ti) without the massive latency of the full T60.

### **4.2 The Armageddon Experiment (T71.5)**

One of the most distinct architectural experiments in Run 3 was the **T71.5** series, tuned specifically for **Armageddon Chess**. In Armageddon, Black has "draw odds," meaning a draw is scored as a win for Black.

* **Loss Function Engineering:** Standard Lc0 networks are trained to predict the game theoretical result (![][ds-image25]). For T71.5, the architectural loss function was modified to skew the value targets.  
  * For White: Draw \= Loss (Target \-1.0)  
  * For Black: Draw \= Win (Target 1.0)  
* **Architectural Behavior:** This created a network with a fundamental "contempt" for draws hard-coded into its weights. Unlike standard engines that use a "Contempt Factor" during search (a heuristic adjustment), the T71.5 architecture fundamentally "believed" that a draw was equivalent to a loss/win. This demonstrates the flexibility of the Run 3 pipeline to produce specialized architectures for variant chess.3

## ---

**5\. Technical Deep Dive: Input Feature Representation**

A Neural Network's view of the world is defined by its input tensor. Across Runs 1, 2, and 3, the **112-plane** standard remained the dominant specification, but the utilization and training of these planes varied slightly based on the network's goal.

### **5.1 The 112-Plane Standard Specification**

The input to the ResNet tower is an ![][ds-image26] tensor. This tensor is constructed by stacking "planes" of ![][ds-image27] bitboards. The decomposition is as follows 6:

| Plane Index | Feature Description | Count |
| :---- | :---- | :---- |
| **0 \- 103** | **History Planes** (8 Time Steps) | **104** |
|  | *Per Step:* 13 Planes |  |
|  | \- Own Pieces (P, N, B, R, Q, K) | 6 |
|  | \- Opponent Pieces (P, N, B, R, Q, K) | 6 |
|  | \- Repetition Count (1 for single, 2 for double) | 1 |
| **104 \- 111** | **Meta Planes** (Game State) | **8** |
| 104 | Castling Rights: White Kingside | 1 |
| 105 | Castling Rights: White Queenside | 1 |
| 106 | Castling Rights: Black Kingside | 1 |
| 107 | Castling Rights: Black Queenside | 1 |
| 108 | Side to Move (0=Black, 1=White) | 1 |
| 109 | 50-Move Rule Counter (Normalized) | 1 |
| 110 | Constant 0 (Reserved) | 1 |
| 111 | Constant 1 (Bias/Ones) | 1 |

### **5.2 Architectural Implications of History**

The 104 history planes represent the current position (![][ds-image28]) and the 7 preceding positions (![][ds-image29]).

* **Run 1 (T60) Utilization:** The massive depth of Run 1 networks allows them to effectively utilize the full 8-step history. The first convolutional block acts as a "temporal feature extractor," detecting patterns like "has the opponent just moved their knight back and forth?" (indicating a lack of a plan) or "is this position a repetition?"  
* **Run 2/3 Variations:** In experimental branches of Run 3, developers tested reducing the history to 4 steps (reducing input size to roughly 60 planes). While this reduces the computational load of the first layer, it was generally found that the 112-plane standard was optimal. The "Repetition" plane is critical; without it, the network cannot understand 3-fold repetition rules and will blunder into unwanted draws.

### **5.3 Normalization of the 50-Move Counter**

#### Section 7.3: Run 3 - Experimental Branch (T75/T71)

The "50-Move Rule Counter" plane (Plane 109\) is a scalar value broadcast across the ![][ds-image27] grid.

* **Implementation:** It is typically normalized as ![][ds-image30].  
* **Run Difference:** Run 3 experiments often involved tweaking this normalization. If the network treats the 50-move rule too linearly, it may not "panic" enough when the counter reaches 98 (49 moves). Run 3 tested non-linear activations for this plane to create a "deadline pressure" effect in the network's evaluation.

## ---

**6\. Technical Deep Dive: Training Data Binary Formats**

The architecture of the network is inextricably linked to the format of the data it consumes. The Lc0 project evolved its binary training data format from **V3** to **V6** during the timeline of Runs 1, 2, and 3\. This evolution reflects a shift in what the architects believed was important for the network to learn.

### **6.1 The V3 Format (Early Run 1\)**

* **Structure:** Compressed state \+ Policy vector \+ Game Result (![][ds-image31]).  
* **Limitation:** V3 only stored the final outcome of the game. It did not store the internal evaluations of the MCTS (![][ds-image32]) at each step. This meant the network was training purely on the "ground truth" of the game end, which is a high-variance signal. A brilliant move in a lost game would be labeled as "Loss" (-1), confusing the network.

### **6.2 The V4/V5 Formats (Mid Run 1, Run 2\)**

* **Innovation:** These formats introduced **Root Statistics**.  
* **Data Fields:** stored result\_q (the average value of the nodes in the MCTS search) and result\_d (the distribution of visits).  
* **Architectural Impact:** This allowed the loss function to include a term for matching the search value:  
  ![][ds-image33]  
  This stabilized training significantly, allowing the deeper Run 1 networks to converge.

### **6.3 The V6 Format (Late Run 1, Run 3\)**

The **V6** format is the most advanced standard utilized in the ResNet era and is key to the advanced capabilities of late T60 and T75 networks.8

**V6 Struct Definition:**

C++

struct V6TrainingData {  
    float result\_q;      // MCTS Root Q (Average Value)  
    float result\_d;      // Game Result (Win/Draw/Loss)  
    float played\_q;      // Q-value of the move actually played  
    float played\_d;      // Probabilities of the move played  
    float played\_m;      // Move index  
    float orig\_q;        // Original Q-value (for Value Repair)  
    float orig\_d;        // Original Result  
    float orig\_m;        // Original Move  
    uint32\_t visits;     // Number of nodes searched  
    uint16\_t played\_idx; // Index of move in policy  
    uint16\_t best\_idx;   // Index of best move  
    float policy\_kld;    // KL Divergence (Search Complexity)  
    float best\_m;        // Best move (in plies)  
    float plies\_left;    // TARGET FOR MOVES LEFT HEAD (MLH)  
};

**Key Architectural Enablers in V6:**

1. **plies\_left:** This field is the specific target for the **Moves Left Head (MLH)**. Without V6, the MLH architecture in Run 1 could not be trained.  
2. **orig\_q:** This field enables **Value Repair**. It stores what the network *thought* the value was when the game was played. The training loop can then compare the *new* network's evaluation against this *old* evaluation to gauge progress and weight the training sample accordingly. Run 1 relied heavily on this for its "Rescorer" pipeline.  
3. **policy\_kld:** This measures the divergence between the raw network policy and the final MCTS counts. A high KLD indicates a position where the network was "surprised" by the search (i.e., the search found a move the network missed). Run 3 experiments utilized this field to perform **Prioritized Experience Replay**—training more frequently on positions with high KLD to fix the network's blind spots.

### **6.4 The "Client" and "Rescorer" Pipeline**

The training loop involves a distributed architecture.9

1. **Client (lczero-client):** Downloads the latest net, generates self-play games, and uploads them.  
2. **Server:** Receives the data.  
3. **Rescorer:** A specialized binary (often a modified lc0 engine) that processes V6 data. It recalculates the result\_q and plies\_left values using endgame tablebases (Syzygy) if the game entered a known endgame.  
   * *Note:* The Rescorer ensures that Run 1 networks have "perfect" endgame knowledge ingrained in their weights, as any tablebase position is relabeled with the exact game-theoretic outcome.3

## ---

**7\. Output Heads and Loss Architectures**

The final layer of the network, the "Head," is where the abstract features of the ResNet tower are translated into chess judgments. This is a major area of divergence between early Run 1 and later Run 1/3 architectures.

> *[This topic is covered in detail in Chapter 9, Section 9.2. See page XXX for complete specification.]*

#### Section 7.4: Cross-Run Comparative Analysis

### **7.1 The Transition to WDL (Win/Draw/Loss)**

Early networks (T40, early T60) used a scalar Value head: a single neuron with a Tanh activation outputting a value in ![][ds-image12].

* **The Draw Problem:** In high-level chess, the draw rate is extremely high (\>50%). A scalar value of 0.0 is ambiguous. It can mean:  
  1. A dead drawn opposite-colored bishop endgame (100% Draw).  
  2. A chaotic tactical mess where White wins 50% and Black wins 50% (0% Draw).  
* **WDL Architecture:** Later Run 1 (T60) and Run 3 (T75) networks adopted the **WDL Head**.  
  * **Structure:** The Value head outputs a vector of length 3 with a Softmax activation: $$.  
  * **Benefit:** This disentangles the "Draw Probability" from the "Expected Score."  
  * **Contempt Implementation:** With WDL, the engine can be configured (at search time) to calculate score as:  
    ![][ds-image34]  
    Run 3 (Armageddon) networks tuned the *training targets* of this head to intrinsically bias the network against draws.

### **7.2 The Moves Left Head (MLH)**

The **Moves Left Head** is a distinguishing feature of the mature Run 1 architecture.12

* **Purpose:** To provide a heuristic for "conversion efficiency." In a winning position, simply knowing ![][ds-image35] is insufficient; the engine should prefer the shortest path to mate.  
* **Architecture:**  
  * MLH is a secondary value head.  
  * It outputs a categorical distribution over logarithmic "buckets" of move counts (e.g., 1-10 moves, 10-20 moves, etc.).  
  * This is preferred over a scalar regression (predicting the exact number) because the uncertainty of game length grows exponentially with depth.  
* **Training (V6):** It trains on the plies\_left field in the V6 data.  
* **Run 2 Omission:** To save parameters and complexity, many Run 2 efficiency nets omit the MLH or use a simplified version, relying on the MCTS to handle time management naturally.

## ---

**8\. Comparative Analysis: Run 1 vs. Run 2 vs. Run 3**

This section synthesizes the technical details into a direct comparison of the three runs.

### **8.1 Table of Comparative Architectures**

| Feature | Run 1 (Mainline / T60) | Run 2 (Efficiency / T70) | Run 3 (Experimental / T75/T71) |
| :---- | :---- | :---- | :---- |
| **Network Series** | T60 (and T40) | T70, T74, T76, T77, T79 | T75, T71 |
| **Max Topology** | **![][ds-image36]** | **![][ds-image37]** | **![][ds-image38]** |
| **Target Hardware** | Server Class (A100, V100) | Consumer Class (RTX 3060, Mobile) | Research Clusters / Specific GPUs |
| **Primary Goal** | Maximize Elo (Absolute Strength) | Maximize Strength/Watt (Efficiency) | Test Hyperparameters / Variants |
| **Training Method** | Self-Play \+ Rescoring | Self-Play \+ Distillation | Self-Play \+ Experimental Loss |
| **Data Format** | V3 ![][ds-image39] V6 (Full) | V5 / V6 (Often Distilled) | V6 (Experimental Fields) |
| **Value Head** | WDL \+ MLH | WDL (MLH often removed) | WDL (Modified targets, e.g., Armageddon) |
| **Input Features** | 112 Planes (Full History) | 112 Planes (Standard) | 112 (Var. history length tested) |
| **Key Innovation** | Massive Scale, Value Repair | Distillation, Human-like play | Alternative Loss Functions, T71.5 |

### **8.2 The Scaling Law Trade-off**

The relationship between Run 1 and Run 2 is governed by the **Neural Scaling Laws** of chess.

* **Run 1 (Large Nets):** Exhibit superior "Static Evaluation." They can look at a position and "know" the result without searching. This is crucial for opening theory and strategic long-term planning where search horizons are insufficient.  
* **Run 2 (Small Nets):** Rely on "Dynamic Search." They have a weaker static evaluator but can search millions of nodes per second.  
* **The Intersection:** Empirical testing showed that roughly ![][ds-image19] (Run 2/3) is the point of diminishing returns for most time controls. While the T60 (![][ds-image1]) is stronger at very long time controls (TCEC), the T70/T75 is often stronger at "Blitz" or "Bullet" speeds because it can clear the search overhead faster.

### **8.3 The Legacy of Run 3**

Although Run 3 was "experimental," its contributions were vital.

1. **T71.5 (Armageddon):** Proved that neural networks could be "lobotomized" via loss function tuning to play irrational chess for specific tournament scenarios.  
2. **Hyperparameter Tuning:** The temperature schedules and learning rate drops perfected in Run 3 were back-ported to the main Run 1 training loop to squeeze the final Elo out of the T60 architecture.

## ---

**9\. Conclusion**

The architectural history of Leela Chess Zero's Runs 1, 2, and 3 is a testament to the sophistication of modern reinforcement learning pipelines. It was not merely a linear progression of "bigger is better." Instead, it was a branching evolutionary tree.

**Run 1** represents the brute-force triumph of the ResNet architecture, pushing the boundaries of parameter count (![][ds-image1]) and input history utilization to create an entity with profound chess understanding. **Run 2** represents the engineering triumph of efficiency, proving that through distillation and careful topology selection (![][ds-image19]), near-state-of-the-art performance could be democratized for consumer hardware. **Run 3** represents the scientific triumph of experimentation, using the V6 data format and flexible head architectures to test the fundamental limits of how a machine learns to play chess.

As the project moves into the Transformer era (T80 and beyond), the lessons learned from the "War of the Runs"—specifically the value of WDL heads, the necessity of V6-style data repair, and the efficiency of the ![][ds-image19] sweet spot—remain foundational to the continued dominance of Lc0 in computer chess.

#### **Works cited**

1. Weights file format \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/weights/](https://lczero.org/dev/backend/weights/)  
2. Project History \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/project-history/](https://lczero.org/dev/wiki/project-history/)  
3. Best Nets for Lc0 \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/best-nets-for-lc0/](https://lczero.org/dev/wiki/best-nets-for-lc0/)  
4. Networks \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/networks/](https://lczero.org/dev/wiki/networks/)  
5. Beyond Perfection: The Ultimate Guide to Lc0's Human-Like Personality Nets\!, accessed January 27, 2026, [https://groups.google.com/g/picochess/c/ap95BZ2JIPg](https://groups.google.com/g/picochess/c/ap95BZ2JIPg)  
6. Contrastive Sparse Autoencoders for Interpreting Planning of Chess-Playing Agents \- arXiv, accessed January 27, 2026, [https://arxiv.org/html/2406.04028v1](https://arxiv.org/html/2406.04028v1)  
7. Contrastive Sparse Autoencoders for Interpreting ... \- OpenReview, accessed January 27, 2026, [https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf](https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf)  
8. Training data format versions \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/training-data-format-versions/](https://lczero.org/dev/wiki/training-data-format-versions/)  
9. LeelaChessZero/lczero-client: The executable that communicates with the server to run selfplay games locally and upload results \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client](https://github.com/LeelaChessZero/lczero-client)  
10. Getting Started \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/getting-started/](https://lczero.org/dev/wiki/getting-started/)  
11. lczero-training/init.sh at master \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training/blob/master/init.sh](https://github.com/LeelaChessZero/lczero-training/blob/master/init.sh)  
12. Layman's question: what is the difference between Leela's networks? \- TalkChess.com, accessed January 27, 2026, [https://talkchess.com/viewtopic.php?t=76512](https://talkchess.com/viewtopic.php?t=76512)

[ds-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAAD7UlEQVR4Xu2YS6hOURTHl1BE3pHIlVcJSV4ljxLyiPIKMVAGJkYUyeRKBmQgRJnckIkYkYR0MxApIiKPQiKEERPP9bPPutbZ55zv3vvdj0udX/27p7X3OXuf/7fPWntfkZKSkpKSkpKS9qCvaoPqaKKNqg6pHmlom6jaqzqgmqfqnOpRma6qs6pucUM7E/swVCr70FE1VUJfvOCaWC6vVdOi2AjVD9VlVU8XN4POqPq4ONdXVB9VE1w8jykS+vH87lFbe5LnwyvJ94F5X1R9Uy13cXu3ey72i94SHvRONcbF+RWJoyUuPl/1RjXWxQxiDMIKL4LJXlJ9l3/L6CIfTibx2IfVSey0qpOL49uhpC3FwCSIFkdtFt/mYpj4XjXaxQwme1PVGMUNJrFdwuo4J/+W0UU+HHNx84Gv+kIU8yyT0NbPB23l3lLVuTi/kg2wwMVt4F2SzcmDVS9VDVHcIH89lZBa7DmtMXq4hDzoP+EimPMOSa+2ShT5wIqNfWCRsdiIrU1iHvrRNj1uyGOQhM7kmv4uTpG0ga9JKBbGFtUX1VwX81yX0AeqMZrCeUJCLfD1wYNhKyWkt0VRWzU8k6wPZiSKswCwSfgkwasMrM4hqqWq4xIeMivV4zeTJOQyG+yr6q1qoRRX6BmSzn3VGG2Y4XVRnLEpaHOieGuIfXgiWR9IFy0xmnfMME51V/VC9UFCUWOrU8RI1SP5PSDiwT18pwRWwp0o1hajgTzJ+GY2Jm+S7K6htcQ+bJWsD20y2sOkV0noTKWNVyk56oHqtmqzpM32Lw/cu191ysWgrUbDEQnz4EcnJfGV1RLmzvYt9qFmRgMP5UFF+0S/rzwoabPZXxrk6/uSzuVQC6P5zPdJ2CqSukhptWaPZH2oymhOMTMlu4MAOvMw/nIz8FJF+2SMvyrhHmAS/geopLxtUnPUckUX+WCmeR+aK4aTVZ8lKob1Em5g0vFWyAbgJm6G5kxhu0MfYOeyIkfrVA+TflwTy9uXV6LWObpe8n3wRpsPfntH6oyxBda0vaN6kwIINkr2M7ZfjUo+zMV2N/XIYoNUgnEaJfSLx2wJtd51VPLBr17zwR9Y8rywLXDqwFKfBPn04qJnA1DMrI0DSVzwDD67BtXjuCGiLUabyaSoPNZLdWbXS74PFo99sCM4hmO8QTse0JYCw85LONV5xkvofEPC8dSgIFAYeFF/D4PtlHBg8cXT00tCPiVdPJfwfK6JtcTwP3lgKfKBIpvnA/Ol6FOz1ri4+ZP5pxIwuVES9oz8u++wBAMq0UXCquFIjGZLtpD8b+T5MCDVI4vdQ38WGtdxZigpKSkpKSkp+Qv8BFweNUj81DxFAAAAAElFTkSuQmCC>

[ds-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAfCAYAAABeWmuGAAAC8UlEQVR4Xu2XTahNURTH/0Ip5HNA6L3MjChfJQOJImWAokxuTzIzUeSjyGNmwkRJ+UpSpgxQXgwkRQZiQD3ykYEBRSEf62+d7eyzzjn7nnPvUWewf/Xv3rvXPnuvvc5ea+8LRCKRSOS/MiR6XVO3/j7ZHo4i76PVS9F50XJ9pJwdojOiEdFv0RfRxaTN6arobWKn7vHBFrEN6ucvqH/Pk9++6LPzf6I+Fmah6LPonGiMsRG2bRX9FF03trbwA7rg7dYA9X8/1D6c/A5yCtp5sTUYLoj22cYWwAXS/6eimcbmmC16lYjfS5mMdEuVDUY46WXRLmsogf05dhUm2IaaTIP6X7bDyUroLuIOD863QPQROqAdbBXSnJskuiNa/88aZoboAfJjWgZEz2xjTZjy9L/sZY0TnYX2WWtsOTYiLTiW4953Lmy6aLzX1o1log7Kg+KCccS014V146toqTVA5+5A61/RS8/BRbMjB/QZhB5X/fIexUFhMJ6ITqBekIvg2y+qH/NEp6HreyFanTXnmQ91mA98h57Zb5BG82HatS82iT6JNie/70KPw34DQVzKH4YWS1/rRI+ha7kC3eFBWA9cuvinB/P/EbRINQWDwaAMiS6h4n2gAgw2/WfRLGIQutPZ52bWlMelC6uvPyAL6AjKi1Sv8BLFC9QUa+gDd2WYYw0erg9VCo8eHkHsNIrsgMzF+yguUr3CINwWfRBtMLZecS+Oawgdpdz9XQPi149r0KPJMRaaNvxsAhcMincGzrsi06M3/CtDCHfkBvsdgHbgFl5jbE3CesEiOstr44mzBxqYJV57XVwqcJwiOI+7slN7s+YsLl14qsw1tqbgKWKD4aCzdHBUtChrqoSfLmX/rw4hDUbhqcYUYPHcIvoG7ci7fSdpa6ryE05OJ0LBdkGpc1PluPT1INLrwY2kzdfuxMY+nCMXDDIgeoc0albMyaaYKjppGwtgUHbaxgDuml5Fx9Dlj1wkEolEIpFIpAX8AR6XyNF6l9r7AAAAAElFTkSuQmCC>

[ds-image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABQ0lEQVR4Xu2TLUsEURSGX1FBEIMfTcNiMxlkm8FgEcFks4gGf4Jg9w8YLWIwLFhNCi4YDIL/QFBRTBZBQcGP9+Xeu/fOGWcctWyYBx5295yZwz3nngVquo41evtLj+moXras013app/eZx9LbdF7nz+jQyhhmj7BPbxncoEe+k6P6IDJZVhBPJm+F9Gm+zZo2UFsccbkUlRo0wZTpugjYotqJzAH19ag/31KFzrZb1hCbHHD5LbhTh0Yof3J7xx6QYVeaDOJN+gVymeYQcc/gSv2Sg8QV0HFdcO66UpM0ge4Ym+Ii6kVUOyCDnee/gENM8wrvSVt+CXyF1KIFk83pULXdDzJjdFzZGdYStriIe1Lcr1wp9NnJebpB/It/oktuEIqqML/Iszrjk6YXCU0g1m6DLdXKnZDV31sEfGvU1NTU8gXcHZVE0kGCGMAAAAASUVORK5CYII=>

[ds-image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAeCAYAAAAsEj5rAAABSElEQVR4Xu2UPUsDQRCGR1BQjARBELGwDNhYiIKinaVWWgSsxd5Cf4edWNmks7Wz8KMRe9FGEP+AWCgYifq+mdvs7txd1la5Bx4CeefmMntzEan4EyzCB/ickDWHsKGXpRmGZ/AOTpiMjMAT+J2ZZBo+wVM4GEc9VuCnaMNRk+Vwxbs2CJiHb6INx02WY0+0cMEGAdviRy6bogtDjsrCovMjdXglWvNishzh2VgG4K1oxs/ZOC7mQPwodl3a8BLOiTZPEo7LXZsyzog+CDbeF12fvrh1YcNWHPVYhR+iNcdwKI5j3Lg8Q55lETV4If5YNqI0IBy37A0hY/BatO4LrsWxJxy33xuyJH7kezgZxx7eiXdkIRe7jHPRmg7cNFn30S/DLfgoWshF3cm+czbhEXyHr3A9uzYHf65rlJKrciO/+COoqPhX/ACVfmCVhMP+cAAAAABJRU5ErkJggg==>

[ds-image5]: <data:image/png;base64,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>

[ds-image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA4AAAAgCAYAAAAi7kmXAAABDklEQVR4Xu3RsUtCURTH8RMZGBpFQSEkOAUhUUuG4GZ/Q0t/QFtLq4OL4OLiIrQ0NjUGDQ1BQ462NNUguIYQ0RJU3/POeXp54tzyfvCBe989997jVSRNmn/NMgbuBIvIoY03lCaVQdZxjxrO8Y0ebnCJCvpYjTdolnyx4fMVPOIXd2KdXGCMPa+JUsUTNn0ebjwVa1e7ecaG10Rpuji7eMcH9v1bHTuTijlpid12hYXE2txkcSu28SyxNhM9VV9VH2kbI3zhMCySxMPopo7YDdc4xo/Y/7YV1GUk0UEBQ7GN2mLXxw/IT8vkCMVgHp2qp7/iQKzNT/9W8hp95Rcfz2RN7PY4+nvLTsdp0kzzB34xLf5j6prqAAAAAElFTkSuQmCC>

[ds-image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAdCAYAAAATksqNAAADSklEQVR4Xu2YTahNURTHl1BEIfKt5yUDIYpIoTeQTBiYIEqRTJiQR0Ympga+BjIx8i2SGCh3JJlIkQmFmCgpoeRz/d46q7vPfmfft7vvJfd1/vWvc9fae599/utjn3NFatSoUaPGP8FI5Urlo4InleuVowv/cuWR4npYAyG2KT8o/yjvK88pLyjfK18qJysfF7ZOAwHdr1wQO6owQXlVTIivyh1ld59Yu5RPxcZ0miBjlE/E9r4x8lWCbGDwW+XSyBfil3SOIOOUl5RfxPbsHFCQNWIDr0izT6SwWvlDOkOQGGT+gIKMUl4UG7g18lVhivK5DGNBupXvlB8lr9kg4DVJCzJHuVN5VjlXOaLktd805hnK+WLZCcYre8Q2O7OwDTWyBFmn/K18pZwW+VJYLHYsh5iuvKH8qbyuPFhcY8PnOCzlem6IZSin2D3lp8JOk3cg3pvC7uThlhV+f1BnKlhZguyV5saIUjvoUj4T2/TCwM41NnyMAdyDzLgsdl98u8VOMeDH+r7iN8A3VSxozHkgtob3uz1izZ4DgbJnjSpkCeIRa0j7gtwWWyN8CAc2fHHDZlMe0bisjivviB2VIXyvL6SczbPFxNoQ2KqQJYiXDJEkNXPARkPxuElqPmntGzka2F0QfDF48Ib0D5D3L+ZRZvwGXFcFI0aWILOUryW/qQI2HNZpK0HC+g/nuCCfA5sjJQigJJhHr1lU2MgYv26FLEFI19NiA7dHvhTOi23aMRhB8MVoJQilggDM9awIs6UVsgQBpDUD6fJjI1+MScqHYqXm8EgvCWyOsGQOBPZ2BQH0GOby4dml3FR2J5EtCFnCQHrJlsgXg28cBAmPxe9ic0ORHN6jGLMqsA9GENbxex6T/NeFbEEAmXFCbAI8VXb3HX10cdI0PhUQ567Y0ccR6KcJxyk2fC4gD0m9n5FmEIgyjXqicoXylphQm8VKzo/kEP66UNWUQ7AmpxB/X3Av5lC6PWJrx89SAs610vwq5G+Am2Iffnwg8fmfWgBBDym/iYnA2y/X2MIyJPouekiixkZje6o3dYvdgzJvhao1Q6aysB9IQ/4Y4v+QXrFeUBWpGP4SlYrsUIHAzJNqsWrUqFGjRo3/DH8BIkD3JiGS6/kAAAAASUVORK5CYII=>

### Chapter 8: Workflow Differentiation (Tuning vs Matches)

#### Section 8.1: Match Workflow Overview

Match tasks, colloquially known as "Self-Play," constitute the vast majority of the computational workload in the Lc0 grid. Their primary objective is the generation of training data—specifically, positions labeled with MCTS search probabilities and game outcomes—which serve as the ground truth for the next generation of the neural network.

### **3.1. Server Assignment Logic for Matches**

The server assigns Match tasks based on the needs of the reinforcement learning loop. The training pipeline requires a continuously sliding window of games played by the most recent network iterations.

**Assignment Criteria:**

* **Network Freshness:** The server prioritizes generating games for the most recently promoted network. As soon as a new network passes the "gatekeeper" (a verification match), all Match tasks are shifted to this new network ID.8  
* **Data Hunger:** If the reservoir of games for the current training window is insufficient, the server increases the probability of assigning selfplay tasks over tuning tasks.  
* **Client Capability:** Match tasks generally require GPU acceleration to be efficient. The server may reserve CPU-only clients for different workloads or assign them smaller networks to ensure they can complete games within reasonable timeframes.9

### **3.2. Execution: The Self-Play Loop**

Upon receiving a Match task, the client executes the lc0 binary in a specialized self-play mode. This mode differs significantly from standard chess engine analysis.

1. **Exploration vs. Exploitation:** To ensure the network sees a diverse range of positions, self-play games introduce noise into the search. The root node of the MCTS tree is perturbed with Dirichlet noise, and moves are chosen proportionally to their visit counts (soft choice) rather than greedily selecting the best move (hard choice) for the first ![][tm-image1] moves (typically 30\) of the game.10  
2. **Rescoring and Adjudication:** As the game progresses, a "Rescorer" component monitors the board state. It checks positions against Syzygy tablebases (endgame databases) to adjudicate games early if a known win/loss/draw position is reached. It also detects "intentional blunders"—moves that significantly drop the evaluation—to ensure the training data reflects high-quality play.1  
3. **No Pondering:** Unlike tournament play, where an engine "ponders" during the opponent's time, self-play engines run sequentially or in parallel batches without pondering to maximize throughput.11

### **3.3. Data Format: The V6 Training Data Standard**

The output of a Match task is not a PGN text file. Text formats are inefficient for the massive volume of data Lc0 generates. Instead, Lc0 uses a highly optimized binary format. The 2024-2025 standard is the **Version 6 (V6)** training data format.12

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
3. **Storage Calculation:** ![][tm-image2]. These are stored explicitly in the planes array.13  
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

> *[This topic is covered in detail in Chapter 5, Section 5.5. See page XXX for complete specification.]*

#### Section 8.2: Tuning Workflow Overview

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

#### Section 8.3: Execution and Resource Differences

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

---



#### Section 8.4: OpenBench SPRT Validation

The **OpenBench** framework provides statistical rigor for validating improvements:

##### SPRT Workflow

1. **Submission:** Developer submits a Test (patch or new network)
2. **Distribution:** Server distributes match pairs to volunteer clients
3. **Execution:** Clients run thousands of games (Test vs. Base)
4. **Analysis:** Log-Likelihood Ratio (LLR) calculated in real-time
   - LLR exceeds upper bound (e.g., 2.94) -> **Pass**
   - LLR drops below lower bound -> **Fail**

This sequential method detects clearly good or bad changes early, far more efficiently than fixed-game matches.


## PART V: DATA INTEGRATION AND RETRIEVAL

### Chapter 9: Match Data Mapping and PGN Retrieval

#### Section 9.1: Match Database Schema

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

The algorithm acts as a transformation function ![][map-image1].

**Inputs:**

* Base URL: https://storage.lczero.org/files/match\_pgns  
* Run ID: Extracted from the run column.  
* Match ID: Extracted from the id column.

**Logic:**

> *[This topic is covered in detail in Chapter 4, Section 4.1. See page XXX for complete specification.]*

#### Section 9.2: Run-Based Directory Structure

1. **Sanitize Inputs:** Ensure Run ID and Match ID are integers. Strip any whitespace.  
2. **Select Directory:** Append the Run ID as a directory path segment.  
   * Path \= Base URL \+ "/" \+ str(Run ID) \+ "/"  
3. **Construct Filename:** Append the Match ID followed by the .pgn extension.  
   * Resource \= str(Match ID) \+ ".pgn"  
4. **Final Assembly:** Combine Path and Resource.

**Formula:**

**![][map-image2]**

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

#### Section 9.3: URL Construction and Data Retrieval


1. **The Manifest Ingestor:** Responsible for acquiring and parsing the CSV.  
2. **The Job Scheduler:** Filters rows and populates the download queue.  
3. **The Async Transport Layer:** Manages HTTP connections and bandwidth.  
4. **The Persistence Manager:** Handles file writing and local indexing.

### **5.2 Module 1: Manifest Ingestor**

The CSV file at training.lczero.org is large and constantly growing. Loading the entire file into memory is inefficient and scalable only up to a point.

**Design Recommendation:**

* **Streaming Parse:** Utilize an HTTP stream to fetch the CSV line-by-line.  
* **Iterator Pattern:** Implement a generator function that yields parsed rows one by one. This ensures the memory footprint remains constant ![][map-image3] regardless of the CSV size.  
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

#### Section 9.4: SQL Queries and Access Patterns


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

---

## PART VI: CONCLUSION AND FUTURE DIRECTIONS

### Chapter 10: System Evolution and Future Directions

#### Section 10.1: Architectural Achievements

**10\. Conclusion**

The lczero-client represents a masterclass in minimalist distributed system design. By stripping the client of all domain knowledge (chess rules, neural network training logic) and reducing it to a process orchestrator and HTTP courier, the Lc0 developers created a system robust enough to scale to thousands of heterogeneous nodes.

**Key Architectural Takeaways:**

1. **Decoupling:** The separation of the client (Go), Engine (C++), and Training (Python) allows each component to iterate independently.  
2. **Stateless Protocol:** The HTTP-based "ask, play, upload" loop is effectively stateless, simplifying error recovery and scaling.  
3. **Data-Centricity:** The protocol revolves around the integrity of the data (PGN \+ Training Labels) and the definition of the model (Protobufs), ensuring that the distributed compute translates directly into neural network gains.  
4. **Resilience:** Through exponential backoff, hash verification, and strict version policing, the client mitigates the inherent unreliability of the public internet and volunteer hardware.

The relationship between the PostgreSQL records and the file storage is maintained through a combination of application-level logic (GORM code), scheduled maintenance (Materialized Views), and implicit conventions (Chunking strategies). "Orphaned" records are an inherent byproduct of this decoupled architecture—a necessary evil to allow the system to scale to billions of games.

The evolution from Run 1 to Run 3 demonstrates a clear trajectory: moving from simple file storage to complex, versioned, and compressed binary pipelines. As Lc0 moves towards Transformer-based architectures, the data requirements will only grow, likely necessitating even more advanced storage topologies (e.g., data lakes, column-oriented storage for metadata) to manage the next generation of orphans and training data.

#### Section 10.2: Persistent Challenges

> *[This topic is covered in detail in Chapter 4, Section 4.3. See page XXX for complete specification.]*
> *[This topic is covered in detail in Chapter 5, Section 5.7. See page XXX for complete specification.]*

#### Section 10.3: Recommended Improvements

### **Recommendations for Consistency**

To further mitigate the orphan problem, the Lc0 architecture could benefit from:

1. **Transactional Outbox Pattern:** Instead of writing to storage immediately, write a "Pending Upload" record to the DB. A background worker then processes these, writes to storage, and marks them "Complete." This ensures atomicity.  
2. **Content-Addressable Storage (CAS):** using the SHA-256 hash of the game file as its filename and DB key. This eliminates duplicates (Zombie files) automatically.  
3. **Active Reconciliation:** A dedicated "Janitor" service that runs continuously (not just as a cron script) to verify the existence of files referenced by the games\_month view, ensuring the leaderboard always reflects reality.

The lczero-server remains a living codebase, continuously adapting to the needs of the engine it serves, balancing the fragile reality of distributed storage against the relentless demand for more training data.

#### Section 10.4: Future Technology Directions

**11\. Future Outlook**

As the project matures, several evolutions in the client architecture are possible or underway:

* **WebSockets/gRPC:** Moving from HTTP polling to persistent connections could reduce latency for short games and allow real-time server control.  
* **Searchless Chess:** Snippet 12 hints at "Searchless Chess" using Transformers. This would fundamentally change the client's role, potentially requiring it to run different types of inference tasks (e.g., sequence prediction) rather than MCTS games.  
* **WASM Clients:** Porting the client and engine to WebAssembly could allow users to contribute compute directly from a web browser, lowering the barrier to entry even further.

The lczero-client remains the unsung hero of the ecosystem, a silent worker tirelessly orchestrating the millions of games required to solve the mysteries of chess.

[ds-image8]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAdCAYAAAATksqNAAADm0lEQVR4Xu2YTahNURTHl1AUIfKt55GBEEWkfA0kEgMTRCmSiYHIR0Ympga+BjKRgXxGEgPlZiAxEJEShZgoKaHkc/3uOuvddfe9+3q9J72n86t/nbfWOfvsvdbaa5/7REpKSkpK/gl9VfNUdwsdVi1T9S/8c1T7iuv/GgKxXvVO9Ut1U3VCdUr1VvVcNVx1r7D1FjzBrCVNbpYhqvNigfis2ljvrg66WfVQ7J7eEpDRYon9pHqk+iE2f5K7ONzXAA9x42vVrMQX8QF7Q0BIMus6qRpY2Marbout4UNha2Ch2A3n5M+ltED1TXpHQJgj60Lrgr2f6kJhZz114DwjjQ/lGKF6Ij0/IHHRiN4ROVjYVyV2aVe9Ub1XTU18zfAX5QIyQbVJdVw1UdWnzmt/05jHqKaIVScMUi0Rm+DYwtZdVqq+iB0InIyRbECWqn6qXqhGJb4cM8S6doTmdUn1XXVRtau4xobP2Su1rKGKWIUy6Rti+xo7+98heK8Ku4vGP7vwcx19uWQ5A1TXxO71MTrYVjgqYlnqCm2qx2KTnhbsXGPDxz3AO6iMs2LvxbdF7BQDP9a3F38DvpFiSeOZW2JjeL/bKtbsORDY9ozRiulSC7w32w48YxXpekCuio0RF+Fgw5c2bErVM5puK8qZDJLJiM/1qdRXMycHwVoebDnY8pw6jEMAG/AtQyYpzc7ARGPwGDz3PCXpJb0/2D0g+FJYeEUaExQbJduMv4HrZslImStWGS0/LcapXkrnmyow4bhPWwUk7v/4jAfkY7A5uYAAW4LnWBilD1SMX7fimeq+WLPPQrkeFXvJhsSXg5Jj0k53AoIvpVVA2CoEgGe9KmK15KBJ80EWGzyk27IKZc0L6PINTSZhmOqO2FZzPNMzg82JW2ZnsHc1IOBHJj8821Sr690N+M+SeHLBYNWKxFaFKuEF9JK1iS+F3zgEJA7+VezZGCTHexT3zA/27gSEcfydB6T15wKNnB91p1OHMkmaHLsOlXFIbJLoSL27evTRxSnT9FQgONfFjj6OQD9NOE6x4fMAskj2+zGpJYEsU7pDxRrfFbFArRHbcn4kR/xzoVlTdnZIbT055YJehYUuUj0Qu5l/A1yW2q9FfiGmwXAI6G6xL0OCwNcv19jiNiT76aQQFUOPSe253tQu9g62eTNYaEUax0vVaShDvv8ptz1ipdUsUyn+EZXL7N+CxEyW5sEqKSkpKSnpYfwGmA8E1Yx44+UAAAAASUVORK5CYII=>

[ds-image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACEAAAAfCAYAAABplKSyAAABVElEQVR4Xu2UvyuGURTHjzCI/CgLBpL8AyYSC0UvpRjsLAYLxWqxmowWg4nNyMQigxhMUpL4B2Qg8Tnde/O6PW/ueZVB91Of4Z7nebrf7rnnEclkMpnfs4vrcfGvySEC1YSoxVk8w1tsxhocwVO89O8kYw3Rgvt4jhM4h8e4KG7zMVzC+fBBCpYQA3iNfVH9Aw+wDqf9euHbGz9gCbGM2+KOPtAkbtMVv9aT2sD68EKgJK539wW+4nNBPXiB/VKZXnzD4fhBTDvOiOtf7AnuFdSD49gglZnEO+yK6iYs7ShiU77uQ9VYQqyJ67+OpvZeW/EkbiLKGRR3+slYQui90hA6IbqJjqGudWoC2rrDsnUSlhC6uV7WUezGK7zBKf9cA2zhkV8nYwmhPyGdpkd8wVVsw3d88LUdbAwfpGIJoeh/oQNby2qdOBTVTPSI8RJlMpl/xSeNnUxubGgXlQAAAABJRU5ErkJggg==>

[ds-image10]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABOklEQVR4Xu2UsUoDQRRFn6CFCKIQEMFC0qUSsQspRbBWsJBUIvoH+QILKzsFiVhJGj/Awj+wsbHTQhGsJChYSIjxXt+MO/PczRrSpNgDB5a9b4aZN7MrUjBy1OHTP7yFB25MJmvwGF7AT9iDH/A08Azeu+wQTvyM7EMFvooOaJqMjMEd0XzfZH9YFy2k2ybzzIvmN3DWZBHsBwvf4ZLJPH6yR/ecygJ8Fi08F91SGlwxa1pw3GS/rMIv0cI9k3km4RVsw2WTRTQkOcUVkxGudAt24YbJIrjcS9HJ7mApyDjJIjxx+Zt7l8kcfJDkJNPk1ni/pt2YTGqwIzroSPSUQmeS0v6EW3yB5TgeDPaHfeJk13Aqjgcj3GLuR5wH75RvMj+noRi6X7wnVbgpeuT+6HfdO/49CgoKcvkGllRR4tTDUpoAAAAASUVORK5CYII=>

[ds-image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAhCAYAAAA74pBqAAABSklEQVR4Xu2UMStGcRTGH8UgCilSJiwyGGSTzWBA+QbvZmbAB1BmAyWLZGG0Gt5MYpBiVEhMFsXAwHM69989/3Nd7n0n6f7qN7z3nPfpds7pAhV/gnl6n+Oc6RNa6V5Ssy6Ehkm6TT8T3+kh3aCDoSlBwlboFbT3gm7SYdskPEAb6rQ9LmWYoC+0yRcC19CwG9rrahZ5uyO65QuWOjRM3rA/LkXIjG/pkHsesQsNe6VjrhbooZcwA89jDekSpl1NkPms0hPa4WoZZpCGLbtaCz2gO7TZ1b5FNvQBDZM/WWahi/lxTpZR6LolbB/x2k/pkvn9K330Dtlbk9BCc7J00XNkb22EToWmorTRY2jYEx1AOvhCQ/f4WwuDbwh7azXogZYavGURaZgEndHuqKME4/QN6a01NKuAfJee6SNKHGgenXQdOviKiv/NF8KKUGzPSoFMAAAAAElFTkSuQmCC>

[ds-image12]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEMAAAAdCAYAAADxTtH0AAABZklEQVR4Xu2ZsUoDQRCGJ2AgRcCggRCwsbALWAiWVpaxS5cH8A0UbC3txCpdep8gVd4gFnmG9BZ2kjhzu8fODieb3F089zIf/BzsHH9m/kuO3B6AoihKfjZMd6JWB9qoObgZv7yq4BnVt2qJWh1ooE7BzHeJWvpln0e5sGdOUA9gmiubkC/VPuQi56/C6IFp9hPMV/XKLxci9Q75Vh7GLbjf69oeQ01vA/muwPcO+VYeBoc+q6wwJOQd8tUwGBoGQ8NgaBgMDYORK4wb1CSnjuF3NAxGlGHsCw2DoWEwtgmDGh7a4y5EEUYTdYEaoWbgniNewDTXcacm0EDp88a9qHHItwe+d+p7DdnelYdBVyodLktTd2rCG5ihvlGvosYhXx5clqR35WHk5cmqTKIM4wj1jhrLQkGiDGOAWqDOZaEg0YVBN0b680Z7s7R/WSbBMP7bhvAZqisXC7DThjC/+x78q4KD5gfoGaDT2ePfWwAAAABJRU5ErkJggg==>

[ds-image13]: <data:image/png;base64,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>

[ds-image14]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAAD/klEQVR4Xu2YXYhVVRTHl1gPoRJZoIIyUalEIklqJPrWJ9GLIShGBIKJhIJCPvg0Dz0UFSGRIEUMEQX6IojIIDIohOCrEQQ9GJGYaBQaTGG6frP2unefde6599yZe7k+nB/8YWatffY+57+/r0hDQ0NDQ0NDwyhYoTqoOpb0bjHdYqFqj+pp1fyQW6LarXo8xJ15qlWqcbE2Pkqx+4noA/93Aw+el/b38Hf0ZYYHVD+rnspiFNyvuqs6q3o4yy1TXUm5qJOqpe2iBZ5V/araEGLTqhey2Kio8uGOdPaBATeZ8m9m8Y2qP1WXs9gMfOT7qodCnIbdQEaw40bfVN1K+e/FerJqdI6JfcSWEJ8Qe/6NEB8FVT58KZ192J5iJ8S8cvDg85QrcCAFv5PiA/Bfyk2J9SC40XXNoeHPxOqJ9TNFr4otQ6OmyofNUvaBzjiTYodaJdtsFcs9lgcpSPDHmBAzgRzGYjD0a/Rq1TXp0MOJB2OgC0+KrYP5FK7iNdVhKXduFVU+PCFlHxgYN1JsZ7toC9omRye1GFP9q9or5akfG4B+jWZTpQ5GBdDGYunPYGeB6hvVObE6OkH928Q69/WQ60aVD52MdiNRJx+eU92W6gNFARqjov/FpoLjRv+k+kW1T2wJoOLrUtzYmGZTYvX8o/pU2mvgWrHn87rr4oZjTg7vjCkvhvhc8PU298FHfy+jJ2KiE2xuVMSxJR99bnTchV8Vexl23HUpxnEPM/2lfJ13eOZ3Ke70daHD2GDdbEx+T7WpVWIwYFj0YWBGYyBGsuPGXZj/X5Hyev6I6pJY42woEI+CkeVi8U9ioiZHxWbWSrFZxYwaJPjA+0UfBmI0vUbvUUk/6yibDkcdnmOjgNx8X6Nz6AhycQOqC+/3sdhM+kO1vpieE+4D9Ucf5mQ0U49zJGfjfBOhEUzw2w6jlQ/7MOVyqDiOXo/RcMSN9k2mX4Yxouv40Gsz5FLGnlTaDH19+03KtzQ2lh/ERif4msXaOuaFxOr4NuXoCMdPHd2MZh1nPe+HYazRdX3Ij3ecvyOYT65wvAOukLw0V+II0yS//fwtVslpKW5ui1QXUo5R5qwR2yBz8x03Ol4SejGsU0ddH/ILywd5oYQPrtJyOC32IGtSrq/EXvxIu6hcFKs8/nDihtIA09jhxTCSeIQep+14Ne+Gm3w+JhLvyOzN7scHv4JTPt8o6eyvU64AP4IQ7KZ8rXlJ9ZfqbWkf7Lk8cImg7HEp39yeEVs/acvBsFNi66HX04thXlj69YHZPCk2U3dkcWYFPzSVflSaLXw0H/SF6i3Vo8V0R14WGxWMkl1S75n7HTqWn375pvH0d92B09DQ0NDQ0NAwQO4ByxIwoy5Vlu4AAAAASUVORK5CYII=>

[ds-image15]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAALMAAAAcCAYAAAAqXo7IAAAIxklEQVR4Xu2ad6gdRRSHj6ig2LtiecYSsQQVGwZL1BgVC1iCLYhgCyIqCpZ/9Ab1DyWKiCRgC08QuyJ2kbAYwQY2LGDBKBKJoqKooGI5X2bP27Ozu3f37ns83n3sB4f37szc2Sm/OXNm9op0dHR0dHQ04XS1fdUOV3tP7S+103Ilhp911W5U21XtIrXf1T7KlegYerZRW6G2Vvp5N7VVap+PlZgeHK+2vfu8QO0/tXVcWseQc4AEL4WoAVE/JGGiB2F9tWfUTogzpgjXqt3nPu+g9q1k/e6YBmyi9qTahi5tVAYX86Fqn6rNiDOmCAeqXew+b6f2dfq3YwLYQu0CtXtSu0Sy7b6MDdTmqy1RWyTBq66dK5GHuihH3XepzZMQO/ZjM7V31FbHGTX01B6W4rZNG2ZK1g62d/pdxp5q50vRW9LHWWrrRekePzY8Z+N8doHD1P6W/nVOJfZXu1SK41sHTmqhhLGNtcI4s8B3jtINtIJm0A5jenU+O4OM2VEa8Soe8VUJHtM4RUIocKZkYj9I7RsJ5X9WOzhNBzq8NM3zi2NE7TO1vV2ah3y8a0/qRR/zgRRF+IracrXNXdodEtqFMUD+OSe7PG//qF3jynkWq/0roV5CHaBOvle2uIAD73dqc+OMKQT9+UlCP+gffxPJ76BNsB0oHlOMsHDbrOgYaOYyCTu2zR1pLKY3Ja/NMe/3g+SFxRfsQQjYoILnJX+AAW4crKPPunS2/D8lPCOGRpZNMg3kGVdIcQXXQV29OFH5Q+0cydfHZCSSifRYl2diRmjkcQi9WapDARuvxyS/KNixqiafRf+uBGcwlTlE7QwJ7U2kuj91mJhZGL9JqOcRCfVXRQEsdsbf76BWD5abD79amECPiZkDi/FrmsaBZYZLt0VBHp4baODdadpomuZhEn+UsO14ELL3/Ee4vDp2kbCAYqwvvSj93DQdW+bSGYvCYPUBR0CoQMjg4XBH3bdLfsIQxusSdkBgEW6aZU9ZEhm/mGOdVbG1hCtLnufhuey0eGvbAdfAAN8gwUOMuHQG1ybZ3wogVNLwwn5rpPwTaZ49fEu1j9PPt6RpHu6TyUNQBl4tvlfGIzYFb5/rYIq1i4XiY1OLV22CjEHFjFjpK32OyW2FKYyVH2++t5H73BT6MkfCtlsWi5bBgt8qTmxIIpMnZs5wPAuHF0M/q7x5AcIIKmJlsEIM4l+EzPbgJ6lMzIjVPLn37gadI4+gHhByT0L8bcYOsDLNr8PChjIIo36R4nXdRIjZFi39t5AJkVV5WmJDtlrfz++lGG71g4k8SUKfbMwxzhlHp/lV3CTFcWhKIpMjZsYPx8OzLERFH8TN/fq2BgrupHaq2oMSKjkyV6I/vAiwmNnuUP0hqp+YExl8YMpgURCDNwUP/pKENrDjEN8atJ1Y/0sJOwcx4wMSyr7syoH1EyG9JVkMTP2L1F6Ucu/cFib6abWzpTixfEbMX0jYceObFA70nGnKdq8mJNJ+zkzMjBPjermEywfGHmfjw0N758CzKMu5jb7hkfHYxNEjY6UjZql9KMFL4DW4lai6tophol6T8GBuKOwhkylmbi+4xWBRNYW43BYgNxReGNZ2f3NBvp0BfHuvStOoKw6REA15eMNYeG1h3HiZ1C8s4XqQnwLwbHbX5yR4f/6vFEEDEmk/Zybm+IbMHCGa49oP2DlMO5jH5iE+bJdCYSaalWEroh9MuAnZDjQwmWI+S8JWj6ibQnt5/vVSjDUJs46R4mBZWMLzDPpGPVUxM3mr1faIM1pCH6+LE0tgfDlvcBNDuPe+tPfIRiLt54xnHyfFMfKXB7azeu2Uxcw4DbzznCi9FARMRXyB65F+UIbGcHL3TJaYiTUZhF6U3g88A+3mwFi3WD0cnriu8wdJuxHxMbPHxoAXUcNOIhMzZx5/3jLh+rNM2bWuacvOW2vAI/HF2AOBxSxxPGlwvURI4r+LMCw8qTsA2rVVrkEtQJDEvk28Dm3lBQnhlAdhWrsJuYg5yw6MtlVidji0+G7UCkX0W9BtYTESi1PvSrVHJWzTfH5Kio7FQDj3S/EKsSmJtBczDodw4lYp6o2xs3ECG2d7VoyJOTfmvTRxqRS9iomZFw52qDH2U/tKim/D2ELedv+z9VKHHQo99kLBX80NCpPKi5wmXo8BXCwhtvbhEPB9vANY2IAhEg8i4YbFhxTWzzoxE1tPFD0JP5Hdy6XhmHjxw0IkXi5b3CdKmJ9BwjFPIu3FbHpaJfm4HQfIGYA8xA7+NiNJ0zwm5jFdcUggGK9qnE0C2yrbq0FDEETZFo1AP0n/t0CdOkwonrlS/tJkEDgBczqeEWdE0BYWHhMZey3ylkl2F24HOuxKK5TComZx0y/ru/XThx4e6uFmxJ/WxwMxJrcZ8YI0EDF95cDHX+YLT7ePBLEsyIoOTCLVevFw3kBfHtulX5D8dznIrkjzmEsDfZGGo4gxMfuzy5hn5ookFqZN6J0uj4Hh4MQD7pXsh0lmb6R5BhPIRPpGGnhAVla8IwwCIip7JR7DpBKDPS7FNnMVidcw706bEcJR6WePDbCdug2+g8eOFxXtonyjk3dDEOYSKV84Hp5pc2jGy5V4ngchkXoxMzaU4S2dL8MOisOID9ssMguR0KHBWDKm6CeGenBMuRs3E2fsrYh3qZx7U4sNwTx5P8NDGUwmIQzpvhPE23TA/x6kDU2v4zjsxe305t9oMtksYBag3zVmS2gzdcWC4DP1sDi8aNnWGd8RlzZe8LxVXjmGMGyehHty/rYBQaIB5owQgX7y90K13aW4qMxrxrsuIRDnkPMkGz9egiyXUB5H46/sYGGa59PRJs6G9pQyU4L3YjJY9ePZiqpg1VH/bRJ+XBKv0EFhofTixAlmR8nazMuIOu9K+bifHXkIP+ZLprMm7zN4UcKYclnAoqybh6GDVV92y9LRMXQQv5ad2Ds6hgriuCRO7OgYRuxKrqNjqOEAwY0Jp+OOjo6O6cP/aVNW8UvL3AIAAAAASUVORK5CYII=>

[ds-image16]: <data:image/png;base64,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>

[ds-image17]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAfCAYAAAAslQkwAAADsElEQVR4Xu2aS6hNURjH/0IRUojkcQ0UBqS8ItyJiSTyKCLdGJDyiKIwcctQiYlHioFEkUJ5DU6ZeBQToTBgcIsBKRTl8f377mqfvc49Z6+1z97bvrv1q//kfPusvdf6n7XW9619gEAgEAgEcmOYaL3ovOi16GY8XDm6Re9EV0T7RZPi4erxVfRX9ES0TbQoHq4cM0UbRBdFv6F9Hxq7okLMgXaySzQgHsodrhzTkc99TdtJzIDO5oN2oCqwYw9FI+xAztDUF6KaaHg81BZsdymitl04hOyfozTQ4BqK6dwC0U7o3vcRujTWkM292XZ9u6ZtFzgGnMXj7ECRDIQup0dFZ/tQWrgP1ZDNIPvA+2VpsIFt1eBv8HvReDtQBDR2o+iT6JfoBiJTORvW9SotweD/bPBd0VvRKqjZWRMM9jTYbPJMXJ6KOns/HyQ6Di1DXBkpuiYaZQcyJBjsafABRJs8xRKEM2+t6LloYnRpS/hDOQE1OU+CwcAaeBi8F411HZdYGuzDdtFt+8OMmSrqEe2wAwVQJoM5Drx+sx1woQPpDhE4s/Iqvvkss0WvoB0bHA8XQpkMJt+hSexWeOQ6PCV5CX9zCQ3+JvrgKFcuiD5DB+ARtH5sxWjRfTTez0W70JyyGczl+ZzoD9Tox/FwIwtFb0Qr7IAjNPgSojIoSa7wYJ3nsPegnbkeDzfAX/NiNN4vSavRek8rm8Enod/hXrwbeubQlGXQmUuT03IMetO84KpiEkKuNEVTJoP5ooHX84whMal9Bt206+EsOCW6DC2XXDAPmmZ594ErxQPo4XyRuBjMvnMLabUS2KQxeCUcs+j50AvPQDMylkRMYHjq9EO0JLrUCbbnm337UuYyiSvgT+iLg7FWrBlpDN4HB4M7oFnpJmh5wxvUi8thmtnId7V74JHZeVKkwdwKzB7NF+0clx5omWY+r4czy4zfcitmY9pmu8yITdum3QnRpQ0kHnRwll6FLsOEa/ppRA/XhfQGdULraIq18eR4uG2KNJgDaf/wbdXD/fAW9LCIfW9FUtv8sTTDyeB5iP8rgLN1imha3WdpYbtboH+nsR+8r4HxoUiD08Jn5GlTXiQa3J/pDy+7WZOOsT/MEBr8P/70UAjsXA3lNZirV95HqWUfg7bg3sZkxC7tygBLyiNwqE3bwORPTIyHWLFKwMHjHs43XbOQLtPPC/MWLi94DMv9nf2fa8UqxWHRF2hHeQ57Jx6uHN2IzuOZofP/4IFAIBDof/wD3sIcbAs9PmgAAAAASUVORK5CYII=>

[ds-image18]: <data:image/png;base64,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>

[ds-image19]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAADcUlEQVR4Xu2YS6hNURjH/0KR96NQJHmUyCMiogwIyQh5pjsQkpkimVwDA5KBASV1k0QxUUiSTkzIyEgpAxIhRCjJ4/v79mevvc7eZ69zz2Xfy/rVv3vv96197tr/9a3XASKRSCQSiUSqZrZolx/06CWaIjooOiKaL+qdaZGF7dn2lOiEaItoRKZF94M+9PGDCQNFO0VTUf/eo0Tbvdgv3op+JPqe/Ky5DRyGiG5C222EGkjWiL6JbkA7YfB3xm45sf6iY9D/Q+P7OrkqyfPBfReXMaInSNu7uiwanTZNWSuaJxoJNbiR0Seh+TuiQU6c5l1PcqxWY4Pos2iTEyN8gRp0cJZlU5WR50OZ0Rycj6JXogvQmW3FV4i9fCOj30HzZ/yEcByao+HG6SRG+WyGxjsQ0Lm/iOtDmdGr/UQIIUabaXlG74Pm3jgxtrNn+jlxsiiJ11D8Qi4TofsBl68yVooOoHiNbUSPMdqt3rmi16L3Tsxo1ugBorPQ9X64lzM4M9aJXopWeblQuoXRtknkGW1Lh2t0EbamfxLN8XJlmOHjvThNfiFa6sWbpRmjH4oeQ9f3PdD3YWEtSJvWE2I0P5j5c8iuq+6zIUavhw7aXnRufeZAPUJqNj9jt2jh7xadpxmjeQJzl7IV0PfiXlZIiNFmEA3nedFYLPqCMKNpDk1iO/8M2gw8AbEfk6HVxErqCkKM5kAvh55QXIaJ7kOfLdwfQozmmbcdaVUPFs0Q3RNdSeKNjLZzOI91rMBWYF+OQgeexyvuB11BiNFF0NxL0Gd5mcklxOhG2BrNdTIPXk6eimY5MZ5EeEPszPJRVUUzdh46wIdRf+Gyk1bhRhlq9FDUH9X491Xos+yEDzvzQDTJi++AVkDhNCugyjWamzc3PeafI7spsx+c6cwVbsohRrNymGf1THPiE0TPkH/T4z/npjfWi5MO0SE/WELVp46Zog/Q/DVk2/C2zFszc+4eliHEaJsWlE0NvuD+JOZ/d2Emf01yvlgZrOpQzOTbfiKhDa2bXWY095m70ALxN/PpSG/PdfCowjs+v43iVLApwbWPOXeZaE/yF5Eea9qglewfdYh92WSD44vrXKgpf/rCkufDNuT7wFnLS9hWpPsL+8S+mT8tw1FcAr0OsyrHZbL/Fxx8DmxP+eo3EolEIpHIP8tPmwAN3TcnOp0AAAAASUVORK5CYII=>

[ds-image20]: <data:image/png;base64,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>

[ds-image21]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAB8AAAAcCAYAAACZOmSXAAABiklEQVR4Xu2VPyhGURjGX6GIssifyZcsWJRkMcpokIEyKZsyKDZ9ZoOSKEkZxSolGYxmUiKZjCZShOfp3Pvdc5/u/T59rsn91W8577nnvOe855xrlpPzX2mEk3AL7sBVWBPr8Uf0wwVYK+3P8BQ2S3tmcHWb5lauzMNPOKqBrGiF17BBA2AEvsNlDWRFJ3yEYxoAE/ALjmsADMMVWK8BgTs7B2c1QLjiY3OTrFl8+1nvK9jmtYV0wAu4qwEPnqEl+AAHJFaC2XFy3w9Y8PqkwSROYIu0F+Ed7JX2RKbNTegnwK3SG5DEOTyzKAGW4gb2lHqUgSt/g3tw3eIJ8M5Xqisn5eqZBHdiG3bFeqTAiVkXGj4qffDSogQWg/ZyMIFDc1eT5+RHbJj7IAme6JfAQYn5VLVy1vPA3OBJ1MEj+AqHJOZTdc33zT0kaTD+BLs1EPCr0z5lrq5Jh6oA783dZe6CEt71Jg2YG69oFRJgp/Avpu87B7615Ppl9siQdjhj0S/VP/05OTlV8w1rmVGXtjBmBwAAAABJRU5ErkJggg==>

[ds-image22]: <data:image/png;base64,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>

[ds-image23]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAADY0lEQVR4Xu2YTahNURTHl1BEycdESD7LROSrZCSKxMBHyERRJBlQ3oCBN5CSkUShJBPKTEqSbibESPkoKZEIoRQzH+vX3vvdfdY7557zzn3uvYP9q3/vtNY+577933uvs/cRSSQSiUQikeg2i1UHbNAwTrVNdV7Vr1qiGplpMZg9qoteR1QzsumeAx9G2aCBPq8Q16fT/rrQh2+qv15//N9G3CBik+qnartqhI8tU70Td9931XIfD6xVfZVme+CaZ1yQ8s50ijwfxmdaNCF+V/VbtSWK03c8eBbFBtgqrsEUcQa3MvqR6rZqmolvluY/dyuKj1XdUZ2LYgHMZgAW2ESXyPOhyOgd4vI3JTtR6BN9JVcID21Ia6N/iMu/V82K4hNVT3yOGR/ARMw8G8ViaL/RBrtM7EOe0WHykO8zOWDSkWPAcqliNCaGpbUmijOqjC65eDSp3dzzNorFkKNNFeaIq4MTbCKH9apjUq8slRkdJg/5XSYH/Da5VTYRqGI0NRWTr0u2w0VGM6rPfeyQZF8U08WVGWZIFXgBX1PdV00yuQBLl5f0J9UGk6tKmdHBSJS3GsPk2mcTgSpGF7FOmjX6ssmNFjdA4Z9DvESuxo2GQDB8polj8kfJrrQ6lBlNuahidGH/6hrNzH4g7r5XMtgAYCbHRqMvkt2JDAVWQfxbPOegauVAi/r0rNFHpWnyXJMDZvRx1SnVDcmazb11zWaVvFTNE7cvZ+CGg541mjLAjoOaa8FkNvO0AUxdrXot7neIszevA88+I65kfVYtzaZr03NGs+dkk0+HAxg52V+PEbfnbrVXxiR2JFNtogLdmtFlL0MOcL9kmF6Gi1RvxC39GHYZj/01tZsDTisjWQnsyfNWQyu6WaPj7d1hkwPMJ9fW9g7o3FNxHbP1lWXzwl+H57ETmB0aGBrizObAU5Vu7zriA8tJkwNmMrm2Dix0jpnE3viSND8SBT30ucAJcc+zMz9ALcs7XRURTGaXk8duad/sMqMhHMExPD4HMNhXfG4QLGvq7X7VB3GN+EvtI0etDdzz+VaiLgcoH9zDS89+1Vroc1VOevC/Dyx5PuyVfB8YAD4q8Y7ZGcX5wERfcz8qdQIMYEmFmc91r38mrQL9mi+uT/3+2pbTRCKRSCQSiQ7wD/AqEjbYkNyRAAAAAElFTkSuQmCC>

[ds-image24]: <data:image/png;base64,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>

[ds-image25]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAE0AAAAcCAYAAAAk2zLiAAAB5UlEQVR4Xu2XzytEURTHv4pSRH6UJCmxUEpSbGSlycZGrNhZkOwUZWWvZC0bC/kLrGyw9A8opdiwVMpCCufbmdtcxxtmnjfdwf3Ut5nu98159327c989QCQSiUQikewYFq3YwR/QIJoT7YnWRN0f7aqiVbQu6rSGZUf0ltdr/vPUvyAlDP9BdCXqyY/VQCfFe+zmx0IyKbpD4fmpJ9GIf1ESY6JRUTs0rCxCqxUdQWvNG69DdCl6NuMh6BLNinKiNpQRmqMR2YU2CF1lSRPgajuE3qfaCBraErTOvajXeGQf6rdYIzBBQ3Oh3CJ5U92A+gPWCEzQ0E5RWmjT1ghMDC0FMbQU/OnQJqCH5XK1JWpGcYKG9t2LYBPqxxeBhztyPIqGjEcOoP6/OXL0Qf9WTdbwcIfbF9G48epFx9D7WFztUFQkNNdP8poLaOuRhN9GrRqPrcsNdHI+fu1idStNyaExLPaeyyg0r/zsh+5HXBkOrgL6FJt7NrzFcCFcQ2uROuiGzN/bht2v/VXdLOF82AtzfuxB3XNtQzNJ2o9T4/rHGWskwInloGEt4vtVxNql1P11cAM/w+f9KgtYuxJ1gzMlOsfXZ560sHYl6gaFD3QiWrBGBrjafwq3kVP8niV+7UjkH/MOhJqn0byC+dYAAAAASUVORK5CYII=>

[ds-image26]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAcCAYAAACqAXueAAADsklEQVR4Xu2ZTYhOURjH/0JRFkIkat7EFDaEUbKUkrJA+UyTlJKyUGRnbImSZiHR2Imt0iRulnaKjahhQVGkkHw//3nmeM993vt972veqfOrf2+dc+65/3Oe83XPCwQCgUAgEAgEAoF6zBbtFg2LroqGRNNiJXoD3yc99qM3fTrWio6JZtiMFOaJTtlEA9vLdrP97IeDovmxEobVohOi6Sb9o2hUNMekTwb0+BqdPjeht3xeFH0Q/RH9nviNkO5tC7SMX/5LrEQb1sF2PoAOBMIBz3e6Ojg5Y3A0XIEWtByHvpQmJht6vIfe97kRusIMQAObF+Aloq3QGXga2QHeK/oq2o/4IGfdEfTZX176OAtEz0SzbIawWfQD+uLJhh7vovd9+kTID7BPXoCvoT1Tz8azcGAinYptWYtFr6CjyLIT+sAOm5ECO/+caI3NSGApdP8oCj2y4U35LOKR0CP7qAoRmg3wCNpBtIPdDXLmxd7FQizMjPOIL4Fc75+KFnppWXDkDIremnTLctET0QWbkYHzaH26famsT3rk/p0FfdLjTJtRkAjNBni96L3ok2ibyUsNMGGDXec5/RS1vDJlYH00wZnls1L0Arq8lO001nkSnR4foppPNxjzfNYhQrMBToODneeTzGf3QTvM78DD6DxZF2UP9GW7vLQx0RlUr5NBsT6/o7pP1md9cukeQz2fjgj/J8BsgzuBJ35isaHfRNdFlxAPMo/dZWebgzODM4QGNkBfHjsAlIDPHUJ3fdLjO9Tz6ROh+wHuEz2HPpc4KNkQNshv1CrRY7Q7j0tjVbaLPkOP73U6jf44Sn2fvscmfNIj66jj0ydC9wN8H+qbn4qJvi9DOy4JftPxZdQ6k1eEJmdw1neu77MKU3EGc7XiqmW/Bnho/ued0/kW0ivk9dod6Mc1G18Gu7eRMaQsIzmwfNYg832WYaruwQwuT/f8ErEchbkWHYEer9NgPj8pltmMDNhxeafTsvslPfJTIA3nsyj0OIh8n3WI0HyA3ZbK7Yl3CTbvhkkbv/5ipUkd3hK9hN6gFL0sdx2XtpxWDTI9ph2kWmj7LMogdEDk+Ux6X1EiNBtgF1wO9tvQ/vB1EwnPurV8CJ33vI+gJ7Q+k56GC27eTKpy0UGPbLz1yVFcxWe3LjoYSN58DYjeQD3z94hoBTqvWlk303l/PQotz/MGn2c9c9tFx7cRHqhYJk1p5yksgv7tNAztzKYOGk3j+6THfvSmz0AgEAgEAoFAIDDl+As0+zHejIsuBQAAAABJRU5ErkJggg==>

[ds-image27]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADYAAAAdCAYAAADhLp8oAAACk0lEQVR4Xu2Xy8tNURjGH6HILZdcSshAYSATyphSYmKgMGJABiaKkvQZ+AcQJZKkDAwMXCYGLiUxZWhgIAMhysT9/Xn3ctZeZ++z9z59n+8r61dP55x1Oed91rv2u9aRMplMJlPPbNN+08VCvJ+ITDadkcd4yrTKNKk0IuGj6bF6xl6YDqlh0j9mqemJ6bI8xtumn6Yb8aCYDaZdKptgZb6ZdkZt48kc033TpaR9kzwpU5N2LTG9ThsLrsr7GDPeEMsr06K0w9heqEQwRtZSbppemhakHTVsNJ1UxeolzJM/J2yttmDsl+lo2mHsMW1OG+eanpu+mvbJt2Dgi2kk+tzEYtND0wUNNvdI/owMGpNyWm4MUegCbFG+j9c+jqg36Zm80qw23VPNhAGQBRaKwKvAfFdTsF7+LBHjJ3lNmGW6ZnoXjSvBjwRjiEpDBqfHgzqwXF5V0+Axxeqm7W3ZJt9FIc7vprtq2NLHVTaH1pRGdANzcWaCqbpMtoUqGMeI0d2lEQVTTOfUK+uU/C3y9DKR17VFX1dC5tgBlOlhdwBQ9dhFYWHIEuU/GDxYtP8FEz/UfxDzgIZJI+Wu1oyWsYXy7+H5nx+1U+goeBh+qqgeYOaKPJ1VnJUbu2OalvS1gWBY4WUarhIGdsjjOJZ2FGw1vZcXvD/MND1Q/QHNQCaQ8hlJXxOj+YxhCGN9h3ABi/7ZtC408HxxCBN8FStNb+WHaRfCFkyzM2xV5ADG2IG0owAffReJcAfjNh8fzmzTD/LCwsQ2jNUBTSzcODB3PemjiPB9lSWfKw6T3sivLoj3J9T+x2Esr1Qs+mF5obglXxguAsRNYmpZIU81E86b9pZ6Jw7ssPB/jCw2/h/LZDKZ/5vfQqeR5LaNrwwAAAAASUVORK5CYII=>

[ds-image28]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABgAAAAfCAYAAAD9cg1AAAABkUlEQVR4Xu2VvytFYRjHH1nkRxIlmSgDJYMkxWaRLDIog0EZjAaD3T8gGaRkMCiDxWC7mEwsbPIjJYoJi4Hvt+ece5/znHO7x71nUO6nPnXf53nP+5z3Ofc9R6TKv2IIbpXhAi8uRQ3cgd/wDT4Y3038EO7Dm2DM+LqkoBPewR4XJ9vwAw66OG+KBZZdPAYnbgR6euGr6O44z3MFB3zQ0wbP4YhPgGnRu5zziYBL2OGDnnF4Cpt9QrS/LODbE5KDjT7o4Q76fBA0wTPRApyTRNJ1qQn7zwJJ/a+YsD2fPpEF7GtOtAD/KZnTDZ9ECxy4nIWtGxbd7SachLWRGUWYEF281EFagSewC7bCPdHXR0nWRBf/gqMuZ3mWaJ47vzfjRGx7jmBdNJ2HcS5mD1r47FpMLIZtz6rLWbhYsQKx090PZ0RfCRdSKLAbxPnwGvKzFS6SqkA9PJbCoknywPHgWdolZYFy+VWLysXvjO+sTA/mI5w14zH4YsYVw+/FLVyE8/AaLkVmZABP8FQgf1f5Y/wAenBiNK8B7uoAAAAASUVORK5CYII=>

[ds-image29]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAG4AAAAfCAYAAAAGJfifAAADH0lEQVR4Xu2Zz6tNURTHl1BEISIZPMmEGAj5EWXIgAzEwB/AyJBiwEQpKQz9GFDSK8VEKQY3AwNDZSQlhSJKoSg/vl/7rnfXWXe/e9897+zeuefuT33rtde+r332WmfttdcRyWQymUwmk6kp66BrJbVTms8c6IR0P/tUdAZaIIm4Cv2FvkFvjThG/YZeQDfb47+MbZ80Hwb2Z+k8s90j3Ysv0ANoHHoNfWyPP5FEjlsGvYS2Q7OcTRdKx1oWQU+h79BmZ2sixyTsw2UpOoF/0zG00bmeP9AtP1gV56G7flDCorigD9AaZyN8mBa00I03DT77K2iHN0jINhrcMfhC7PeDVbAEegbt9QYJC+aCWhJ3zmlJGE014ij0CJrvDRKCvpfjeLwkyUi7oOcS0qVHo4mL8zCl3oFOeUMDuSchu3hsmmRWikHHrfSDVUCHrfeDbTSaDniDBMdtlLjDm8YmiRcXzEh0mGalGHzbfN2QFKbGlkx+vmVCQGuajGWlGUEXVZsF1QwN7F7F24ygaXIU7mhlsGky2T1tUGw0pYik2dBhaLUbHyZSpUlW97ehDRKKGtUKaK6ZF8VGU5WRxHL6CvROhv/ibq8BVWYlVun6f63YtYld8AvYaKoSOm4PdFaG33EtSXO+3YDuS6fPeR16A52UPtWpP3RTwKgq4zgG1EGJX4QttHPeIHP5v/umIkOKNMm1bJPQ0CbLJdwhxyZm9MA2U3nopqCs43SzLnmDg3ade65oKsAIZu9R5w7STNDfVJkmPewPH/GDFjrrUFtsYemi2GfT8bUTs6fPdB3HKNSojPFQOnN7teRs54NimorBN4EO0r04LmE+G8gXzXi/t3tQHkto5kdhhTcuncVPplg0zpNi5RPTUunOzWUdx6LmE7TbGxy0cx7nb3E2Dx3yVXrPZXP5p3Tvidcq/YGBz+/3JCYPA9N/kakMPrT/aOh1AVqsP2hT1nHDCJ/f70lMHl4H2NyuFaPkuLIwbfMDQK2g435AW70h8x8eQTynaxPYLAb4ed+eC+8LMzJEi6baOC4zdcYkFI6ZTCaTyYwU/wBp0OvRxxE+BQAAAABJRU5ErkJggg==>

[ds-image30]: <data:image/png;base64,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>

[ds-image31]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>

[ds-image32]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAfCAYAAADTNyzSAAABCElEQVR4Xu3RMUtCYRTG8RMlBNUgSeDg4hJNDlFLgdDm0hfwC0g4ObsKToaCS4SfoDVoaGtrD4KmJAqKiiDasv7nnlc7XK/VKvjAD/G558j7XkVmmeXfWUcb1zjDDoroYsnNRaljgB4KqOINnzh2c1EquMRqrN/CR/gcJYcbsV+Mp4wrZHypg1/Y9GVIBydYGBZ6kXOxhfSwDFnBBWq+zOJWbCGeDTxj15e/LTTE+tFxNIs4DQ98tvGa0EfZF3vXqfB9D09iw/q/jGUOB3jBo9grPhRbuHNzY9H7rGFefs6vx/0zeTyILZRizxKjd9JhXdLliWmhj3exBXUvdszENHGUYNkPTXu+Aa03OKImIX7yAAAAAElFTkSuQmCC>

[ds-image33]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAmwAAAA5CAYAAACLSXdIAAAO0ElEQVR4Xu2deax11xiHX1FiVlrElLZURbRBTKkYvphFkCghoVT9QWJKKKKGfIiIMeY/pNWKUHNICULYRaJogsYUQ1JiCIKQklRN+7H26669zt7nnHu/c797v37Pk7y55669zzprr7XOen/7XWvtEyEiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIrIxrtPbvXp7TG+3qdJvXr0+VPiML/Z20/bAEQzX9N7YbD3tJdfr7UBvD+/tuCHtWr1dP0/YALQ/dUbdye7xpd7OaxNFRHabs3v75YR9rT7pKOLDMa6Hl44PL/Do3n4e43q75XAMh/yX3v7T2++Hvw/q7R693XM4p/28Zca5LXzGS3o7uUp7cCy+9z29HVOd84AYl/unvd2vOr4fQIDgHPdCiF4e4/q7z/jwAm+J8flvrY4hoP7R2796+1OUfnBKb4/t7dbDOfSbts3mrM47SYGbYhDOjsX3nlMdhzOrY9hlMe5LssgJvf0kFMYisgfcrrdf9fbt3m7WHDta6aI4bZzrk8eHRny1ty/39rfYEmHAa9JasXFWlDzrc6GbSYdrR8nrt+2BKO/7VpsYpR1pT95zh+ZYgtjD8exnB/2uKNe+V/C9uGL4e9L40IiLo5zT9XajKv3xvf07Fp3766O0dwq25Bcz6UA0DtFHu7YguD/aJkZpe/rAsu/2jaP04WtKNPNwQFtQ57dvD4iI7CYPjeJU3tEeOIrpentxFOd50fjQiIO9vT8WBRt1yXtbGOg/H4vCrIt5wQbPjWnhwns41nLDKNGptlw19+3tYW3iPuP03q6KcXRwO1wYJaq5U37Y2/NiuXBHlD8hitjqYizYPhvT/eBWvf0oFoXZMsEGCFjOaeH7+8g2McrnEEnlPXN5Pqm3u7aJshLa6WCbKCKym3w8yuBzl/bAUUwXZd0ZYmHK4QIOEgE2Jdi6KO8jitVyt1gUUV1MC7Zjh7+sgcL51yBmaLs5MXNulDyf0h7oeWqUCMGRANPSq6am5+iitONO+W4UoUM9/rk5BrQvIopzpgRbCrApHhWLImpOsGWex/f2nfpAFCH5uiYtoXwf7O3q3u7fHIPXRokSy/a5IObbVkRkV7giysDD1IgUuiiOnmgYddNObaajhinBlpGVqbVPrDM6rUnrYlqwkXfSLnReJWSeFSXPqXMQa89uE/cpCA3qE9G6XbrYjGBDrE05ZyJYCJ45wfaDKO87oUpLTomt9Y7JnGDLNszIaUI/RDiwHm6OjPZO1cMfYv9HWfcr3AhN9QkRkV2DQceBZ0wXxcGdFGVtEuKsjpYR1fjQ8HpKsLEukCmvrFuMdURza166GAs2PotNALVgqyHSghggWjcHeZFnG5ljwwHToXsJQjZFEHaTKKJmSnhwI/H33u7dHliDLqaFyrqkYKO9KWc77Ugf4NicYGODSX2drEFjvRiifYpWsLF+8YyYFt1AVPyPUYTjHFw/ebZLHoiyntikHW6eFmVDBn3yQG9fiPK9ORKi/bn2l78iIrsO02kM5gz6RyLsjNuOcVdcC685uiiOjvphDdsVvd22Os4i83TeU4IN7h5jwZb2pvqkgS4Wz8PmBFs66jYSU8OCc/Kod/wSKSQitE4d7CaXRtk5e35v34/itJl+nioXkTWuYyfCq4udvS9JwZbC/YIYl5G6RCzNCTZgJ3Hbrlzv8+uTBlKwtTYn2HL9afuZNUQoyYOp0eTE2PspceqRctURQ/oAaUdCtD9vmtrvvYjIrpCDeXv3vQkYkJc5kv1MF1tOkoGZOqojVQer13OCreaJsbVWEGsjNd2QXueR6+OmWMdRc4w8/1ql8eiGqSjWXsIOSq6T6b45lomWZXRxaIKN+so2oc4pB1PNgPg9fXi9TLAlfB+eGUWgZD84dXTGYoQNEIRz157T3ssgL85h80HCzlGif3sFU8EI9TZyjShedT27BVHPqRuGOWjnLg6tf4mIrA2OgAGSxw9M8YY2YU2eEzt3svuBLsZl51py0TmPR6iF1TqCLXl1lLzazQLdkN7mUQs2HEQ6h5zmmhMHkBEMFpwDwohHP7SPmNhLEGkfiNUPkF3Vly6MxWeOYfnsszYd8dKK5ilqwYZAoxysawRudrL+1xFsNUS7yKvdLDAl2KC+do5lmfL7uwyiVZyTj4VBmLDZYDviZNOk0GzbgJuQvYj2E+3tYr22SxRsInLYQDAgHBggp9aMcGc/9YyvdblTLHey+5kuxmVHkKVjzN2hyZRgY7rrjtX/SQ7yTKUQuUtImxJs9UNSaY9cx7WOYAPOyXI/KTb3+AZ2OG6Cz/X25jZxglWCjbV8PFqjtR9Huelo06k/1s2tohZsQDly6pboUDIn2CgzAqmFtYxMSbabKeYEW71BhGvNdVPrCDZEMefQRyn3y2Jxs8NOYdp/J3DdlKleZgCkTT1nbrfhJqaL1d+nGgWbiBw2clptbjoUsbbMSa4Cp3Mo71+HFCTrGgL1uv9753K6GJc9d4QROfx6lQ5Tgq2L6eejAQP8uoKthvzy4aecx2e2jr0lr/ttsbnHN+DcDrVdT4jimOsoD/nOPbyVa5iLAi+ji0NzqK1go/9QlhfG1qYTmBNsiLI2ipRQh+sKtiSFYkZntyvcL46yDm8TIER3Wrd5nTWIUNKmHkNzOOhidT3WpGDLmygRkV0j786nBkh2pzEFeGoUh4IDwRik8/+8Syft1VHWadXUgo3oEDsDcTTkTfSNxdgJ0Q52rTHNxPF1Id8s2zq27qMhuhiLkpOirK+5MsaRFZgTbHPRSeobx7/OlGhCHZNfvid3qN3h/2dMgyAnX6Z82InXgnDC6VIvB2IxGnRyjH/6irZ5QZQpNepzqq3uHKt/PQGxRsSvBmFDPbfgGLkObjC2Sxc7FxXQCrbcLUo/qIUYdTEl2Eib+gUCYDp03SnRhHr9TfU/YoEdtHO/YpCkYKPPtG1DG/I9J2rK94n6aqfNieq9MrbKRYSZ6C+7PEmrhTfw/32HY1MwJd0KNtqX9Za585nI4It6e2BvtxjSyJfjT49x3pxDPz1lOAeyT1MOrq3eof3gKHVf10UXZRwiAjv3PazJNp+anRAR2QgPiTIoXRVl0HzG8D+GmMABkJ6Lq4FBkME+HSriCk6Pkg9wzhWxNc3BgJaih2Os20lnRjoiBxhIDw6vcRx7sYYlORBlYTjXz04+6iSvBwd9aWw9kw1HgMPC+SAoXhXFMeDMuih5fDLGIgQnR3o6xANRPgMnTDpOpJ66oz0uHI61Du68mBbbNSkA5h49kA4/p3hp44y4EtGifYHo3u+G1zizuQhbRv74TKJQLVw3u3Wp4/x5prR6t2ANDhqBt0qUTNHFzgTb43p7RZRyUYfZrvRjBPvB4TzSOPbyKOvlOPes2HpsCv/TN94ZYzH8iSh5p7ggj/o7ec7wfxr1le8hvwRRQ71xo7OMbJO5CNK5Me5ftUCmrbMPc+3fG15Tr3N1m5E/bGr6nI0v1Fd+l4j+ci59Gug7jBdJCqgcg7IdqFPy4DuI2OQ7Sr5JF+Vm5Xm9XRJbu6Szv3Od1G2+ThFOfawSYlwXZT6mPSAisily8F5ltdAABkIiDAx6DI7AwFlPY9WRplqwwftjWrAhDNM53Dj29ofnu1ish3RKXHd7Pe25OGiuu4uyVoioGOk8HuIzw+v6sR7dkLaO1Y4aEGtz09kJddm+ryYFVkL5uS6iE0zXHT+kp7CDZYINQYYj/WdMlw2hf/nwmr6DKOba3hfza8oQjlN5rUMX86JiGe13JNsVELF1H+dY21bUITAlSiTxZ0M6gh+B1T7WYyqPOWtvaBD59c3VFAidup1baM86Xz6HeqP9fxBbkWnaIvNZJtj4rtDfGTOmykbbc7NzZZSblTdGKWPegPAe3ktUnjGDfkVZ6JN1WbIdclxp+3M3WIJQ5NoSbgIoC3Cd2d+pj1VRtlbkiojsG4iyMBgyuCaPiDLo5h3rTgQbznhVhEAWwdFcHYdWd62DS8GWjnoqqlULtnbReMKU76ro3zpwY/CjWPyliXXpYl5UXFPgu8cO0FXT48ugPWn7JAUbUSaE3FQUqRZs9U1bzSWxXv9M8VP3N/r303v7SpRNF5SlXUoAjDNEYE+Mxf7cxXinNdc5J7K6GI9PqwQbS0YuahNFRPYDTFcySOW0BXDHi0NNGCxZT8JUzTLBxgCdAyvTELVz36lzPhqhDolo7ZTWwaVgw1mSby0CWB8EtWCbE0OXxeoppXUgorssQrgKyjwXubsmQR1xQ7VT5gQbYpDHmNRCKteY1YJtTtx8M8aba+ZgHKmF1Itj63mBRNfok5SlnRq/RZRoXE5lZn9mPDktFgVbvYQDGKfyerrYnmAjn1wyICKy72gXhrOtn+ke1iWxXov1LTz3ijtiIm8Mwt8YzmUA/dxw7jnDsbcPx4iycSf/qVhciC3zHBdl2rVdJL4uTDvRDh+L4iRz2o22AvL+fhRnlmuREHN/iRLtmPrcM2P6tzO3C59DX5r6PVYZw+J5phaps+3C5qFsd/rAR4bX9A0EDTC1y/T6F2PrUR60/a+jfO/bzyUKxhrAVTdffF5uQMDY5EH/IR2xx1jx6djK50FRynJ+lLIAx7iRRJghWt8dpT8jJjNf1o4mXBObG7jO10S5jiwDYxbjFa+Ztq7fl+Q6zE30cRGRXWNqVyCDNYM+x7ApJ54gMDDWoTDdVUPa3KJomQcHyg651mluimNjsU1pJ9qxhTKwA28TINTOaBNlFnZd71Z90a60d/v9p29gLZy3TlkQZoiftPdEiczeIEoejBHtmFCPNwnn5BrDdWCs2c75NVxXPoRYRERkW+CAphznkco17XoOFwiXVuDIZrF+RUREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREREdoX/AhKHoZu1uJRFAAAAAElFTkSuQmCC>

[ds-image34]: <data:image/png;base64,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>

[ds-image35]: <data:image/png;base64,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>

[ds-image36]: <data:image/png;base64,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>

[ds-image37]: <data:image/png;base64,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>

[ds-image38]: <data:image/png;base64,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>

---



#### Section 10.5: Delta Analysis (2024-2025)

##### From Scalar to Distributional Evaluation

- **Baseline:** The engine evaluated positions as a single centipawn number or winning probability.
- **Current:** The engine thinks in **WDL distributions**. This granular understanding of draw probabilities enables sophisticated match strategies (contempt) that were previously impossible.

##### From Fixed to Flexible Backends

- **Baseline:** Users were largely locked into CUDA (NVIDIA) or OpenCL (AMD). Network files were strictly Protobuf.
- **Current:** The ecosystem has embraced **ONNX**. This standard allows Lc0 to ride the wave of broader AI hardware optimization. Users can now run Lc0 on Windows via DirectML or on high-end NVIDIA clusters via TensorRT, using the exact same network topology but with vastly different runtime execution paths.

##### From Tree to Graph

- **Baseline:** MCTS was a strict tree search. Transpositions were handled via hash tables but not structurally integrated.
- **Current:** The introduction of **DAG Search** (Directed Acyclic Graph) represents a structural change in how the engine explores the state space, formally recognizing that different move orders can lead to the same state and merging those search branches.

##### From V5 to V6 Data

- **Baseline:** Training data was a simple record of "State -> Result".
- **Current:** V6 data is a forensic record of the search process itself, capturing original evaluations vs. final outcomes. This enables "Value Repair," allowing the network to learn from the *correction* of errors rather than just the final truth.


## APPENDICES

### Appendix A: Complete API Reference
#### A.1 Task Distribution Endpoints

**3\. Detailed API Endpoint Specification**

The core functional interface of the Lc0 ecosystem is hosted at api.lczero.org. While training.lczero.org serves as the human-readable frontend, the underlying API endpoints handle the high-throughput automated traffic.

### **3.1 Task Acquisition: The /next\_game Endpoint**

The /next\_game endpoint serves as the command-and-control heartbeat of the distributed grid. It is responsible not just for assigning work, but for ensuring that the work assigned is compatible with the volunteer's hardware and software version.

* **URL:** http://api.lczero.org/next\_game  
* **Method:** POST  
* **Authentication:** Stateless, via parameters in the request body.

#### **3.1.1 Request Payload and Capability Negotiation**

The client does not simply request "work"; it must advertise its capabilities. The getExtraParams function in the client source code reveals a comprehensive list of parameters sent with every request.1

| Parameter | Data Type | Description and Implications |
| :---- | :---- | :---- |
| user | String | The registered username of the contributor. Used for tracking contributions on the leaderboard. |
| password | String | The authentication token. The client code warns that this is stored in plain text, highlighting a reliance on the user to secure their local environment.1 |
| version | String | The semantic version of the lczero-client (e.g., "34"). The server uses this to enforce upgrades or deprecated clients that cannot support newer data formats (e.g., V6). |
| token | String | A unique session identifier (often a random ID) used to deduplicate active worker counts on the server stats page. |
| gpu | String | The specific GPU model (e.g., "RTX 3090"). This is crucial for scheduling. Large Transformer networks (BT4) may be inefficient or impossible to run on older hardware, allowing the server to selectively assign lighter ResNet tasks. |
| train\_only | Boolean | A flag indicating if the client is restricted to self-play training games or if it is available for "match" games (validation matches against other engines or older networks). |

#### **3.1.2 Response Structure: Dynamic Engine Configuration**

The server's response is a JSON object that acts as a remote configuration file for the lc0 engine. This allows the developers to tweak search behavior globally without requiring a software update on the client side. Analysis of the "Train Params" from active runs provides a detailed view of this schema.2

JSON

{  
  "training\_id": 1234,  
  "network\_id": 900434,  
  "network\_url": "https://storage.lczero.org/files/networks/weights\_900434.pb.gz",  
  "options": {  
    "visits": "400",  
    "cpuct": "1.20",  
    "cpuct\_at\_root": "1.414",  
    "root\_has\_own\_cpuct\_params": "true",  
    "fpu\_strategy": "reduction",  
    "fpu\_value": "0.49",  
    "fpu\_strategy\_at\_root": "absolute",  
    "fpu\_value\_at\_root": "1.0",  
    "policy\_softmax\_temp": "1.45",  
    "noise\_epsilon": "0.1",  
    "noise\_alpha": "0.12",  
    "tempdecay\_moves": "60",  
    "tempdecay\_delay\_moves": "20",  
    "moves\_left\_max\_effect": "0.3",  
    "moves\_left\_slope": "0.007",  
    "moves\_left\_quadratic\_factor": "0.85",  
    "smart\_pruning\_factor": "0.0",  
    "sticky\_endgames": "true"  
  }  
}

**Second-Order Analysis of Response Parameters:**

* **Search Exploration Control (cpuct):** The parameters cpuct and cpuct\_at\_root control the "Polynomial Upper Confidence Trees" formula, which balances exploration (looking at new moves) vs. exploitation (playing the best known move). The server specifies cpuct\_at\_root=1.414 (approx ![][re-image1]), a higher value than the interior nodes, forcing the engine to widen its search at the root.2 This ensures diverse training data, preventing the network from getting stuck in local optima.  
* **First Play Urgency (fpu):** The fpu\_strategy determines how the engine evaluates moves it hasn't visited yet. The configuration reduction with fpu\_value=0.49 suggests a pessimistic approach, assuming unvisited moves are slightly worse than the parent node, unless at the root (fpu\_value\_at\_root=1.0), where optimism is encouraged to widen the search beam.2  
* **Temperature Decay (policy\_softmax\_temp):** To generate varied games, the engine plays moves stochastically based on their probability. policy\_softmax\_temp=1.45 flattens the distribution, increasing randomness. The tempdecay\_moves=60 parameter instructs the engine to switch to deterministic play (best move only) after move 60, ensuring that the endgame data is high-quality and precise, while the opening remains diverse.2  
* **Time Management Simulation (moves\_left):** Parameters like moves\_left\_slope and quadratic\_factor inject a synthetic sense of urgency into the search. Since self-play has no opponent clock, these parameters model the psychological and strategic pressure of a ticking clock, training the network to evaluate positions differently when "time" is short.2


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


#### A.2 Data Ingestion Endpoints

### **3.2 Data Ingestion: The /games and /upload Endpoints**

Upon the conclusion of a game, the client enters the submission phase. This is handled by the uploadGame function in the client source, which constructs a multipart POST request.1

* **URL:** http://api.lczero.org/games (or /upload depending on API versioning).  
* **Method:** POST  
* **Payload:** Multipart form data or JSON wrapper.

The upload payload is critical as it contains the raw material for the learning process. It consists of three distinct layers of data:

1. **Metadata:** The training\_id and network\_id are echoed back to the server to ensure the data is attributed to the correct epoch. This prevents "data poisoning" where data from an old, weak network might be accidentally mixed into the training set of a new, strong network.1  
2. **Portable Game Notation (PGN):** A text-based record of the moves. This is primarily used for the training.lczero.org frontend (allowing users to view games), for Elo verification (SPRT matches), and for debugging (detecting illegal moves or crashes). The PGN is relatively small (kilobytes).  
3. **Binary Training Data:** This is the bulk of the payload (megabytes). It is a compressed dump of the internal state of the neural network inputs and the MCTS outputs for every position in the game. This data bypasses the PGN format entirely, as parsing PGN to reconstruct bitboards is computationally expensive and lossy regarding search statistics.

### **3.3 Network Distribution: The /networks Endpoint**

Before a client can generate data, it must possess the neural network weights.


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

This embedding allows the server to extract the training targets (![][cs-image1] for policy, ![][cs-image2] for value) directly from the game record.

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

#### A.3 Network Distribution Endpoints

3. **Binary Training Data:** This is the bulk of the payload (megabytes). It is a compressed dump of the internal state of the neural network inputs and the MCTS outputs for every position in the game. This data bypasses the PGN format entirely, as parsing PGN to reconstruct bitboards is computationally expensive and lossy regarding search statistics.

### **3.3 Network Distribution: The /networks Endpoint**

Before a client can generate data, it must possess the neural network weights.

* **URL:** http://training.lczero.org/networks  
* **Method:** GET

The client code includes a flag \--network-mirror, which indicates that the system is designed to support alternative content delivery networks (CDNs).1 This is a scalability feature; if the main server is saturated, the community can host mirrors of the network files (which can be 100MB+ for modern Transformer models), and users can redirect their clients to these mirrors without code changes.

#### A.4 Public Query and Registry Endpoints


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

### **Appendix: API Specification Summary**

| Endpoint | Method | Function | Payload Highlights |
| :---- | :---- | :---- | :---- |
| /next\_game | POST | Task Assignment | **Req:** gpu, version, token **Res:** network\_url, cpuct, fpu\_value |
| /games | POST | Data Ingestion | **Req:** pgn (text), training\_data (V6 Binary) |
| /networks | GET | Weight Distribution | **Res:** .pb.gz file (Protocol Buffer) |
| /active\_users | GET | Monitoring | **Res:** JSON stats (Games/Day, Version) |

### Appendix B: Database Schema Reference

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
  * result: The outcome (1-0, 0-1, 1/2). This is the "Ground Truth" value (![][db-image1]) used in the AlphaZero loss function ![][db-image2].  
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

The algorithm acts as a transformation function ![][map-image1].

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

**![][map-image2]**

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


### Appendix C: Binary Format Specifications
**Table 1: Training Data Format Comparison**

| Feature | Version 3 (Legacy) | Version 6 (Current) |
| :---- | :---- | :---- |
| **Size** | Variable/Smaller | Fixed 8356 bytes |
| **Input Planes** | Basic Board State | 112 Planes (History \+ Meta) |
| **Search Stats** | Limited | root\_q, best\_q, policy\_kld |
| **Use Case** | Run 1 / Early Run 2 | Run 3 / Transformer Training |
| **Consistency Risk** | Low (Simple text) | High (Strict binary alignment) |

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

### **6.3 The V6 Format (Late Run 1, Run 3\)**

The **V6** format is the most advanced standard utilized in the ResNet era and is key to the advanced capabilities of late T60 and T75 networks.8

**V6 Struct Definition:**

C++

struct V6TrainingData {  
    float result\_q;      // MCTS Root Q (Average Value)  
    float result\_d;      // Game Result (Win/Draw/Loss)  
    float played\_q;      // Q-value of the move actually played  
    float played\_d;      // Probabilities of the move played  
    float played\_m;      // Move index  
    float orig\_q;        // Original Q-value (for Value Repair)  
    float orig\_d;        // Original Result  
    float orig\_m;        // Original Move  
    uint32\_t visits;     // Number of nodes searched  
    uint16\_t played\_idx; // Index of move in policy  
    uint16\_t best\_idx;   // Index of best move  
    float policy\_kld;    // KL Divergence (Search Complexity)  
    float best\_m;        // Best move (in plies)  
    float plies\_left;    // TARGET FOR MOVES LEFT HEAD (MLH)  
};

**Key Architectural Enablers in V6:**

1. **plies\_left:** This field is the specific target for the **Moves Left Head (MLH)**. Without V6, the MLH architecture in Run 1 could not be trained.  
2. **orig\_q:** This field enables **Value Repair**. It stores what the network *thought* the value was when the game was played. The training loop can then compare the *new* network's evaluation against this *old* evaluation to gauge progress and weight the training sample accordingly. Run 1 relied heavily on this for its "Rescorer" pipeline.  
3. **policy\_kld:** This measures the divergence between the raw network policy and the final MCTS counts. A high KLD indicates a position where the network was "surprised" by the search (i.e., the search found a move the network missed). Run 3 experiments utilized this field to perform **Prioritized Experience Replay**—training more frequently on positions with high KLD to fix the network's blind spots.

### **4.2 Evolution of the Binary Format (V3 to V6)**

The structure of the data uploaded to /games has evolved to capture more sophisticated training targets. The API supports multiple versions, indicated by the first bytes of the payload.5

#### **4.2.1 V3: The Baseline**

The V3 format was a direct implementation of the AlphaZero paper. It stored the probabilities (the policy vector ![][re-image2]) and the result (the game outcome ![][re-image3]).

* **Limitation:** The game outcome is noisy. A player might play a brilliant game and lose due to a single blunder at move 60\. Training every position in that game as a "loss" provides a noisy signal.

#### **4.2.2 V4: Search Statistics**

V4 added root\_q (Q-value at the root) and best\_q (Q-value of the best move).

* **Implication:** This allows the network to learn from the MCTS search itself. Even if the game was lost (![][re-image4]), the search might have calculated that the position at move 20 was winning (![][re-image5]). Training on ![][re-image6] helps smooth the learning process.

#### **4.2.3 V5: Symmetry and Invariance**

V5 introduced invariance\_info replacing the move\_count.

* **Implication:** Chess is spatially symmetric (mostly). A position reflected horizontally is strategically identical (swapping King-side/Queen-side castling rights). This field helps the training server perform "Data Augmentation"—generating 8 training samples from a single position by rotating and flipping the board—without corrupting the rules of castling or en passant.

#### **4.2.4 V6: Uncertainty and Active Learning (Current Standard)**

The V6 format represents a significant leap, adding policy\_kld (Kullback-Leibler Divergence) and visits.5

* **Technical Detail:** policy\_kld measures the divergence between the raw network policy (the "intuition") and the final MCTS policy (the "calculation").  
* **Strategic Insight:** A high KLD indicates the network was "surprised" by the search—it thought a move was bad, but the search found it was good. This metric allows the API to prioritize these positions for training (Active Learning), effectively telling the network: "Focus on the positions you misunderstand the most."

4. **Data Serialization:** The client aggregates this metadata. It computes a checksum of the game to prevent duplicate uploads and potentially validates the game integrity (checking for illegal moves or crashes).  
5. **Upload:** The game is uploaded to the server. The payload is not a text file but a compressed data stream containing the move sequence and the associated probability vectors.

### **2.3 The Role of the Rescorer**

A critical, often overlooked component of the Lc0 pipeline is the **Rescorer**. In the early days of chess engines, training data was often "scored" by the game result (0, 0.5, 1). However, self-play games can contain blunders, or "drawn" games might actually be tablebase wins that were adjudicated early due to move limits.

The Rescorer is a server-side (or offline) process that acts as a quality control filter. It replays the generated games and cross-references positions with Syzygy endgame tablebases.

* **Adjudication Correction:** If a game was drawn by the 50-move rule, but the tablebase indicates a forced mate in 5 moves, the Rescorer modifies the value target ![][tdf-image6] to reflect the theoretical truth rather than the empirical game result.  
* **Blunder Detection:** If the engine plays a move that transitions a position from "Won" to "Lost" (according to tablebases), the Rescorer can flag this.  
* **Value Head Repair:** The Rescorer updates the training targets root\_q (search value) and best\_q (Q-value of best move) based on this perfect information. This "Rescorer" step ensures that the neural network is trained on the "truth" of the position rather than the potentially flawed evaluation of a previous network iteration.

## ---

**3\. Data Transformation: From PGN to Binary Chunks**

Raw game logs, even when enriched with MCTS stats, are inefficient for training deep neural networks. Text-based formats like PGN require heavy string parsing, which becomes a CPU bottleneck when training on GPUs capable of consuming thousands of positions per second. To alleviate this, Lc0 employs a dedicated transformation step using the trainingdata-tool, which converts game data into a highly optimized, flat binary format packed into "chunks."

### **3.1 The trainingdata-tool**

The trainingdata-tool is a specialized C++ utility designed to parse PGN files (often pre-processed by pgn-extract to ensure standard compliance) and emit the proprietary binary format used by the lczero-training pipeline.

**Workflow:**

1. **Input:** A .pgn file containing games. These PGNs usually contain proprietary tags (e.g., { %eval... }) or comments that encode the MCTS visit counts from the generation phase.  
2. **Parsing & Validation:** The tool reads the moves, verifying legality and reconstructing the board state for every ply. It calculates necessary metadata such as castling rights, en passant availability, and the 50-move counter.  
3. **Target Calculation:** For every position, the tool computes the training targets. It extracts the winner (for the Value head) and the move probability distribution (for the Policy head).  
4. **Chunking:** To facilitate random shuffling and efficient disk I/O, individual positions from thousands of games are aggregated into "chunks." A chunk is a collection of binary records, typically compressed (gzip or tar).  
5. **Output:** The tool produces a directory of binary files (e.g., chunk\_001.gz, chunk\_002.gz).

The command-line invocation typically looks like:

Bash

./trainingdata-tool input.pgn

This process effectively "freezes" the data. Once in chunk format, the data is agnostic to the chess rules; it is simply a sequence of tensors and floats ready for ingestion by the neural network trainer.

### **3.2 Evolution of Data Formats (V3 to V6)**

The binary format has evolved to accommodate new research ideas and network architectures. Understanding this evolution is key to understanding the current V6 specification.

* **V3 Format:** The baseline for modern Lc0 training. It contained the board planes, the policy probabilities, and the game result. It relied on a simpler set of auxiliary planes.  
* **V4 Format:** Introduced separate fields for root\_q, best\_q, root\_d, and best\_d. This marked the shift towards explicitly modeling draw probabilities (![][tdf-image22]) alongside win/loss values (![][tdf-image23]). In V3, draws were often implicitly handled or mashed into the ![][tdf-image23] value; V4 made them first-class training targets.  
* **V5 Format:** Added the input\_format field, allowing the struct to self-describe how the planes are encoded. This was crucial for experimenting with different input feature sets (e.g., changing history length) without breaking backward compatibility with the parser. It also introduced invariance\_info to better handle symmetries.  
* **V6 Format (Current Standard):** The V6 format further expanded the metadata to support "Moves Left" prediction. It added root\_m, best\_m, and plies\_left. This data allows the network to learn not just *if* a position is won, but *how quickly* it can be won, refining the engine's instinct in converting winning advantages.

### **3.3 The V6TrainingData Struct: A Byte-Level Analysis**

The V6TrainingData struct is a packed C-structure with a fixed size of **8,356 bytes**. This fixed size allows the chunkparser.py to read the file using fixed-stride offsets, which is significantly faster than parsing variable-length records.

The struct layout is as follows:

| Offset (approx) | Field Name | Data Type | Count | Description |
| :---- | :---- | :---- | :---- | :---- |
| 0 | version | uint32\_t | 1 | Version identifier (e.g., 6). |
| 4 | input\_format | uint32\_t | 1 | Describes the layout of the planes array. |
| 8 | probabilities | float | 1858 | The policy target vector. 1858 floats representing probabilities for each potential move. |
| 7440 | planes | uint64\_t | 104 | The input feature bitboards. 104 uint64 values, each representing an 8x8 bitmask. |
| 8272 | castling\_us\_ooo | uint8\_t | 1 | Castling right: Us, Queenside. |
| 8273 | castling\_us\_oo | uint8\_t | 1 | Castling right: Us, Kingside. |
| 8274 | castling\_them\_ooo | uint8\_t | 1 | Castling right: Them, Queenside. |
| 8275 | castling\_them\_oo | uint8\_t | 1 | Castling right: Them, Kingside. |
| 8276 | side\_to\_move\_or\_ep | uint8\_t | 1 | Encodes side to move and En Passant target file. |
| 8277 | rule50\_count | uint8\_t | 1 | 50-move rule counter. |
| 8278 | invariance\_info | uint8\_t | 1 | Symmetry/Transform flags. |
| 8279 | dummy | uint8\_t | 1 | Padding/Legacy field. |
| 8280 | root\_q | float | 1 | Average Q-value of the root node. |
| 8284 | best\_q | float | 1 | Q-value of the best move. |
| 8288 | root\_d | float | 1 | Draw probability (root). |
| 8292 | best\_d | float | 1 | Draw probability (best move). |
| 8296 | root\_m | float | 1 | Avg moves remaining (root). |
| 8300 | best\_m | float | 1 | Moves remaining (best move). |
| 8304 | plies\_left | float | 1 | Ground truth game length. |
| 8308 | result\_q | float | 1 | Game result (-1, 0, 1). |
| 8312 | result\_d | float | 1 | Game result (Draw binary). |
| ... | ... | ... | ... | (Additional replay/debug fields: played\_q, orig\_q, visits, policy\_kld) |

**Structural Insights:**

* **The 1858 Probabilities:** The largest component of the struct (over 7KB) is the policy vector. This consumes \~88% of the storage space. This massive footprint is due to the decision to store probabilities as full 32-bit floats. Some experimental branches have explored quantizing this to uint8 or float16 to reduce bandwidth, but standard V6 uses float32.  
* **The 104 Planes:** Note that the input planes are stored as uint64\_t bitboards, not floats. This is a compression ratio of 32:1 compared to storing them as floats. The expansion from bitboard to float tensor happens just-in-time in Python memory (RAM), saving disk I/O bandwidth.

## ---

**4\. The Neural Network Input: 112 Planes Specification**

The "Input" to an Lc0 network is not a raw board representation (like FEN). It is a tensor of shape ![][tdf-image24]. This tensor encodes the spatial distribution of pieces, the history of the game, and the game state metadata.

### **4.1 The Need for History**

Chess is a Markovian game in theory (the current position determines the future), but in practice, history matters for two reasons:

1. **Repetition Draw Rules:** The state of the board includes the repetition counter.  
2. **Dynamics and "Velocity":** While not strictly necessary for correctness, providing the network with the previous board states helps it infer the "direction" of the game—e.g., whether a position is static or dynamic, or whether a player is shuffling pieces (indicating a lack of constructive plan).

Lc0 typically uses a history length of 8 steps (Current position ![][tdf-image25] plus 7 previous moves ![][tdf-image26]).

### **4.2 Breakdown of the 112 Planes**


#### V6 Value Repair Fields (2024-2025 Enhancement)

The V6 format introduced critical "Value Repair" fields that enable the training pipeline to learn from the *correction* of errors:

| Field | Type | Description |
| :---- | :---- | :---- |
| `orig_q`, `orig_d`, `orig_m` | float | Values as they were *during the game generation* |
| `played_q`, `played_d`, `played_m` | float | Values derived from the move *actually played* |

**Significance:** By storing both original evaluations and outcomes, the training pipeline calculates the error in the engine's evaluation at generation time. This allows "Value Repair," where training targets are adjusted based on known outcomes, correcting for search artifacts.

The `invariance_info` field now contains a detailed bitfield encoding side-to-move and symmetry transforms (mirror/flip), enabling data augmentation through geometric symmetries.

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
3. **Storage Calculation:** ![][tm-image2]. These are stored explicitly in the planes array.13  
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


The 112 planes are decomposed into ![][tdf-image27] feature planes (104 planes) plus 8 constant planes.

#### **4.2.1 The 13 Feature Planes (Per Time Step)**

For each of the 8 time steps in the history buffer, the following 13 planes are generated:

| Plane Index | Feature Description | Type |
| :---- | :---- | :---- |
| 1 | **P1 Pawn** | Bitmask (Sparse) |
| 2 | **P1 Knight** | Bitmask (Sparse) |
| 3 | **P1 Bishop** | Bitmask (Sparse) |
| 4 | **P1 Rook** | Bitmask (Sparse) |
| 5 | **P1 Queen** | Bitmask (Sparse) |
| 6 | **P1 King** | Bitmask (Sparse) |
| 7 | **P2 Pawn** | Bitmask (Sparse) |
| 8 | **P2 Knight** | Bitmask (Sparse) |
| 9 | **P2 Bishop** | Bitmask (Sparse) |
| 10 | **P2 Rook** | Bitmask (Sparse) |
| 11 | **P2 Queen** | Bitmask (Sparse) |
| 12 | **P2 King** | Bitmask (Sparse) |
| 13 | **Repetitions** | Special Mask |

* **P1 vs P2:** "P1" always refers to the player to move, and "P2" to the opponent. The board is always oriented so that P1 is at the "bottom." If Black is to move, the board is flipped/rotated, and Black's pieces populate the P1 planes.  
* **Repetitions Plane:** This plane indicates whether the configuration of pieces at that specific history step has occurred before in the game. If a position has occurred once before, the bits corresponding to that configuration (or sometimes the entire plane) are set to 1\. This provides the network with the context needed to avoid or force 3-fold repetition.

**Total Feature Planes:** ![][tdf-image28]. These correspond to the planes array in the V6 struct.

#### **4.2.2 The 8 Meta Planes**

The final 8 planes are "broadcast" planes. They are usually filled entirely with 0s or 1s (or a normalized constant value) across the ![][tdf-image29] grid. They provide global state information to the convolutional layers, which otherwise operate only on local spatial features.

| Plane Index | Feature | Description |
| :---- | :---- | :---- |
| 105 | **Castling Us: O-O** | 1.0 if Kingside castling is legal for P1. |
| 106 | **Castling Us: O-O-O** | 1.0 if Queenside castling is legal for P1. |
| 107 | **Castling Them: O-O** | 1.0 if Kingside castling is legal for P2. |
| 108 | **Castling Them: O-O-O** | 1.0 if Queenside castling is legal for P2. |
| 109 | **Side to Move** | Often all 0s for White, all 1s for Black (or encoded via P1/P2 orientation). |
| 110 | **Rule 50 Count** | Normalized value ![][tdf-image30]. |
| 111 | **Game Progress** | Optional/Version-dependent. |
| 112 | **Bias / Constant** | All 1.0s. Provides a baseline activation. |

*Note on En Passant:* En Passant targets are sometimes encoded in the meta-planes or implicitly handled. In V6, the side\_to\_move\_or\_ep field in the struct is used to construct the relevant plane or update the input tensor during the inflation step.

### **4.3 Symmetries and Invariance**

Chess board geometry allows for certain symmetries, primarily horizontal flipping (mirroring). While the rules of chess are not perfectly symmetric (due to Kingside vs Queenside castling differences), they are *nearly* symmetric. Lc0 exploits this to augment the dataset.

During training, the pipeline can apply a horizontal flip to the board state. If the board is flipped:


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

### Appendix D: Network Topology Reference
**1\. Introduction: The Neural Paradigm of Leela Chess Zero**

To fully appreciate the architectural distinctions between Runs 1, 2, and 3, it is necessary to first establish the theoretical and structural baseline from which all three lineages emerged. Lc0 represents a departure from classical Alpha-Beta pruning engines (such as early Stockfish versions) by utilizing a Deep Convolutional Neural Network (DCNN) as its evaluation function and move ordering oracle.

### **1.1 The Residual Network (ResNet) Backbone**

The core of the Lc0 architecture during the period covered by Runs 1, 2, and 3 is the Residual Network (ResNet). Adapted from computer vision research, the ResNet architecture solves the problem of "vanishing gradients" in deep networks by introducing skip connections—pathways that allow the gradient to bypass non-linear transformation layers during backpropagation.

In the specific context of Lc0, the network is defined by a "Tower" of residual blocks. The dimensions of this tower are the primary differentiators between the training runs. The standard notation used throughout this report is ![][ds-image2], where:

* ![][ds-image3] **(Filters):** The number of feature maps (channels) in each convolutional layer. This determines the "width" of the network, or its capacity to capture parallel patterns and features at each level of abstraction.  
* ![][ds-image4] **(Blocks):** The number of residual blocks stacked sequentially. This determines the "depth" of the network, or its ability to perform sequential reasoning and compose abstract concepts from lower-level features.

Mathematically, a single residual block in Lc0 can be described as:

![][ds-image5]  
Where ![][ds-image6] is the input tensor, ![][ds-image7] and ![][ds-image8] are convolutional layers with ![][ds-image3] filters, and the addition ![][ds-image9] represents the residual skip connection.

### **1.2 The Role of Squeeze-and-Excitation (SE)**

A pivotal architectural enhancement adopted across the major runs is the Squeeze-and-Excitation (SE) block. While the original AlphaZero paper did not utilize SE layers, the Lc0 project integrated them to enhance the representational power of the network without a proportional increase in computational cost.

The SE block operates by explicitly modeling inter-dependencies between channels. It performs two operations:

1. **Squeeze:** Global information embedding, typically via global average pooling, to produce a channel descriptor.  
2. **Excitation:** An adaptive recalibration, using a simple gating mechanism with a sigmoid activation, to produce per-channel modulation weights.

This architecture allows the network to emphasize informative features (e.g., "King Safety" channels) and suppress less useful ones (e.g., "Queenside Pawn Structure" when the action is on the Kingside) dynamically on a move-by-move basis. The presence and configuration of SE layers became a standard in Run 1 and subsequently influenced the efficient designs of Run 2\.1

### **1.3 The Monte Carlo Tree Search (MCTS) Interface**

The neural network does not operate in a vacuum; it serves as the heuristic engine for a Monte Carlo Tree Search. The network receives a board state and outputs two critical signals:

1. **Policy (![][ds-image10]):** A probability distribution over all legal moves, acting as a prior for the search.  
2. **Value (![][ds-image11]):** An estimation of the game outcome (typically ![][ds-image12] or a probability distribution).

The interplay between the network's architecture and the MCTS is crucial. A larger network (Run 1\) provides a more accurate prior ![][ds-image10] and evaluation ![][ds-image11], allowing the MCTS to converge on the best move with fewer simulations. Conversely, a smaller network (Run 2\) provides a less accurate prior but allows for vastly more simulations (nodes) per second, potentially compensating for its "blind spots" through deeper brute-force verification.

## ---

**2\. Run 1: The Mainline (T60 Series)**

**Run 1** constitutes the primary, longest-standing training effort of the Lc0 project during the ResNet era. It is synonymous with the **T60** network series (and the earlier **T40** series). The overriding objective of Run 1 was the maximization of playing strength (Elo) in competitive environments such as the Top Chess Engine Championship (TCEC) and the Chess.com Computer Chess Championship (CCC).

### **2.1 Architectural Evolution of T60**

The T60 architecture was not static; it underwent a significant metamorphosis during the lifespan of Run 1 to adapt to the increasing availability of powerful GPU hardware (e.g., NVIDIA V100s and A100s).

#### **2.1.1 The 320x24 Era**

Initially, Run 1 utilized a ![][ds-image13] topology. This represented a massive leap from the preceding T40 generation (![][ds-image14]). The increase in width to 320 filters required a 56% increase in parameters per layer (![][ds-image15]), significantly increasing the memory bandwidth requirements for inference.

This topology was selected to maximize the "knowledge capacity" of the network. In the context of chess, "width" often correlates with pattern recognition capabilities—the ability to simultaneously track multiple tactical motifs (pins, skewers, back-rank weakness) across the board.

#### **2.1.2 The Transition to 384x30**

As training progressed and Elo gains began to plateau, the architecture was further scaled to ![][ds-image16].2 This upgrade marked the zenith of the ResNet era.

* **Filters (384):** This width allowed for an extremely rich feature space. The 384 channels in each convolutional layer provided the network with the bandwidth to encode highly specific positional nuances, such as "minority attacks in the Carlsbad structure" or "opposite-colored bishop endgames with rook support."  
* **Blocks (30):** The increase to 30 blocks deepened the network's reasoning horizon. In deep learning, depth is often associated with the level of abstraction. A 30-block tower can compose lower-level features (piece locations) into higher-level concepts (control of the center) and finally into abstract strategies (prophylaxis) more effectively than a shallower network.

### **2.2 Hyperparameter Dynamics and Training Events**

Run 1 was characterized by a dynamic training regimen, where hyperparameters were adjusted on-the-fly to destabilize local minima and encourage continued learning. Key events in the Run 1 timeline include:

* **Temperature Decay Adjustments:** The "Temperature" parameter controls the randomness of move selection during self-play data generation. Run 1 saw multiple adjustments to this schedule. For instance, at net 60584, the start temperature was reduced to 1.15, and at 60647, it was dropped to 1.1.2 Lowering temperature forces the network to play more "seriously" during training games, generating high-quality data closer to its true strength, though at the risk of reducing exploration.  
* **Multinet Activation (Brief):** Nets 60951-60955 saw the brief activation of "Multinet," an experimental architecture where the network attempts to predict outcomes from multiple different board orientations or historical states simultaneously. This was quickly deactivated due to a degradation in game generation speed, highlighting the constant tension in Run 1 between architectural complexity and training throughput.2  
* **Learning Rate Drops:** Significant architectural "settling" points occurred when the learning rate was dropped (e.g., at net 60781 to 0.015). These drops typically result in a sharp increase in Elo as the network fine-tunes its weights into a precise local minimum.2

### **2.3 Value Repair and The Rescorer**

A critical innovation that matured during Run 1 was **Value Repair**. In pure AlphaZero-style reinforcement learning, the network learns from the outcome of self-play games ![][ds-image17]. However, self-play games can be noisy; a blunder in move 40 might turn a win into a loss, teaching the network that the opening position (move 10\) was "bad," which is incorrect.

Run 1 implemented a "Rescorer" pipeline. Instead of relying solely on the self-play result, the system would periodically re-evaluate positions from older training data using the latest, strongest network. This effectively "repaired" the training labels. The Run 1 architecture was specifically adapted to handle this via the V6 data format (discussed in Section 6), which includes fields for orig\_q (original value) to track the divergence between the old and new evaluations.3

## ---

**3\. Run 2: The Efficiency Frontier (T70 Series)**

While Run 1 chased the ceiling of performance, **Run 2** was initiated to explore the floor of computational cost. This lineage, associated with the **T70** series (including T74, T76, T77, and T79), focused on producing networks that were "strong enough" but vastly faster and more efficient.3

### **3.1 Architectural Constraints: 192x15 and 128x10**

Run 2 networks are defined by their constrained topologies. The two most common configurations in this run are:

1. ![][ds-image18] **(Medium):** This topology is widely regarded as the "sweet spot" for consumer hardware (e.g., NVIDIA RTX 2060/3060).  
   * **Computation Reduction:** Compared to the Run 1 (![][ds-image1]) baseline, a ![][ds-image19] network has roughly 25% of the width (computational cost scales quadratically with width: ![][ds-image20]) and 50% of the depth. This results in an inference speedup of approximately ![][ds-image21].  
   * **Strategic Impact:** This speedup allows the MCTS to search 8 times as many nodes in the same time control. Run 2 explores the hypothesis that searching deeper with a slightly "dumber" network is often superior to searching shallower with a "genius" network, especially in tactical positions.  
2. ![][ds-image22] **(Small):** Used for extreme efficiency scenarios, such as running Lc0 on mobile devices or browser-based implementations.

### **3.2 Distillation and Knowledge Transfer**

A key architectural difference in Run 2 is the heavy use of **Knowledge Distillation**. Rather than learning chess from scratch (tabula rasa) like the early Run 1 nets, many Run 2 networks were trained to mimic the outputs of a larger "Teacher" network (typically a mature Run 1 T60 net).

* **Loss Function Modification:** In distillation, the loss function includes a term that minimizes the Kullback-Leibler (KL) divergence between the Student's policy distribution and the Teacher's policy distribution.  
  $$ L\_{\\text{distill}} \= \\alpha L\_{\\text{outcome}} \+ \\beta \\text{KL}(P\_{\\text{student}} |

| P\_{\\text{teacher}}) $$

* **Architectural Implication:** This allows the Run 2 architecture to "punch above its weight." Even though it lacks the parameter count to independently derive complex positional truths, it can memorize the evaluation patterns of the T60 network. This effectively compresses the knowledge of the massive Run 1 architecture into the efficient Run 2 topology.4

### **3.3 Human-Like Play and Personality**

Run 2 architectures found a secondary niche in "Human-Like" chess engines. Projects like **Maia Chess** utilize the smaller topologies (![][ds-image23]) typical of Run 2\. The rationale is that the massive Run 1 networks are *too* perfect; their move choices are often alien and incomprehensible to humans. The restricted capacity of Run 2 networks, combined with training on human game databases (rather than self-play), results in an architecture that predicts human errors and stylistic preferences with high accuracy.5

## ---

**4\. Run 3: The Experimental Testbed (T75 & T71)**

**Run 3** represents the "skunkworks" of the Lc0 project—a parallel track used to test radical ideas, new code paths, and specialized tuning configurations that were deemed too disruptive for the stable Run 1 mainline.

### **4.1 Topology Variations: T75 and T71**

Run 3 is associated with the **T75** and **T71** network series.

* **T75:** utilized the ![][ds-image19] topology, similar to Run 2, but generated its own training data rather than relying on distillation. This allowed developers to A/B test whether the "efficiency" of Run 2 came from the topology itself or the distillation process.3  
* **T71:** Experimented with unconventional sizes, such as ![][ds-image24]. This specific size was an attempt to find a middle ground between the lightweight T70s and the massive T60s, aiming for a "Heavyweight" class that could still run on high-end consumer GPUs (like the RTX 2080 Ti) without the massive latency of the full T60.

### **4.2 The Armageddon Experiment (T71.5)**

One of the most distinct architectural experiments in Run 3 was the **T71.5** series, tuned specifically for **Armageddon Chess**. In Armageddon, Black has "draw odds," meaning a draw is scored as a win for Black.

* **Loss Function Engineering:** Standard Lc0 networks are trained to predict the game theoretical result (![][ds-image25]). For T71.5, the architectural loss function was modified to skew the value targets.  
  * For White: Draw \= Loss (Target \-1.0)  
  * For Black: Draw \= Win (Target 1.0)  
* **Architectural Behavior:** This created a network with a fundamental "contempt" for draws hard-coded into its weights. Unlike standard engines that use a "Contempt Factor" during search (a heuristic adjustment), the T71.5 architecture fundamentally "believed" that a draw was equivalent to a loss/win. This demonstrates the flexibility of the Run 3 pipeline to produce specialized architectures for variant chess.3

## ---

**5\. Technical Deep Dive: Input Feature Representation**

A Neural Network's view of the world is defined by its input tensor. Across Runs 1, 2, and 3, the **112-plane** standard remained the dominant specification, but the utilization and training of these planes varied slightly based on the network's goal.

### **5.1 The 112-Plane Standard Specification**

The input to the ResNet tower is an ![][ds-image26] tensor. This tensor is constructed by stacking "planes" of ![][ds-image27] bitboards. The decomposition is as follows 6:

| Plane Index | Feature Description | Count |
| :---- | :---- | :---- |
| **0 \- 103** | **History Planes** (8 Time Steps) | **104** |
|  | *Per Step:* 13 Planes |  |
|  | \- Own Pieces (P, N, B, R, Q, K) | 6 |
|  | \- Opponent Pieces (P, N, B, R, Q, K) | 6 |
|  | \- Repetition Count (1 for single, 2 for double) | 1 |
| **104 \- 111** | **Meta Planes** (Game State) | **8** |
| 104 | Castling Rights: White Kingside | 1 |
| 105 | Castling Rights: White Queenside | 1 |
| 106 | Castling Rights: Black Kingside | 1 |
| 107 | Castling Rights: Black Queenside | 1 |
| 108 | Side to Move (0=Black, 1=White) | 1 |
| 109 | 50-Move Rule Counter (Normalized) | 1 |
| 110 | Constant 0 (Reserved) | 1 |
| 111 | Constant 1 (Bias/Ones) | 1 |

### **5.2 Architectural Implications of History**

The 104 history planes represent the current position (![][ds-image28]) and the 7 preceding positions (![][ds-image29]).

* **Run 1 (T60) Utilization:** The massive depth of Run 1 networks allows them to effectively utilize the full 8-step history. The first convolutional block acts as a "temporal feature extractor," detecting patterns like "has the opponent just moved their knight back and forth?" (indicating a lack of a plan) or "is this position a repetition?"  
* **Run 2/3 Variations:** In experimental branches of Run 3, developers tested reducing the history to 4 steps (reducing input size to roughly 60 planes). While this reduces the computational load of the first layer, it was generally found that the 112-plane standard was optimal. The "Repetition" plane is critical; without it, the network cannot understand 3-fold repetition rules and will blunder into unwanted draws.

### **5.3 Normalization of the 50-Move Counter**

The "50-Move Rule Counter" plane (Plane 109\) is a scalar value broadcast across the ![][ds-image27] grid.

* **Implementation:** It is typically normalized as ![][ds-image30].  
* **Run Difference:** Run 3 experiments often involved tweaking this normalization. If the network treats the 50-move rule too linearly, it may not "panic" enough when the counter reaches 98 (49 moves). Run 3 tested non-linear activations for this plane to create a "deadline pressure" effect in the network's evaluation.

## ---

**6\. Technical Deep Dive: Training Data Binary Formats**

The architecture of the network is inextricably linked to the format of the data it consumes. The Lc0 project evolved its binary training data format from **V3** to **V6** during the timeline of Runs 1, 2, and 3\. This evolution reflects a shift in what the architects believed was important for the network to learn.

### **6.1 The V3 Format (Early Run 1\)**

* **Structure:** Compressed state \+ Policy vector \+ Game Result (![][ds-image31]).  
* **Limitation:** V3 only stored the final outcome of the game. It did not store the internal evaluations of the MCTS (![][ds-image32]) at each step. This meant the network was training purely on the "ground truth" of the game end, which is a high-variance signal. A brilliant move in a lost game would be labeled as "Loss" (-1), confusing the network.

### **6.2 The V4/V5 Formats (Mid Run 1, Run 2\)**

* **Innovation:** These formats introduced **Root Statistics**.  
* **Data Fields:** stored result\_q (the average value of the nodes in the MCTS search) and result\_d (the distribution of visits).  
* **Architectural Impact:** This allowed the loss function to include a term for matching the search value:  
  ![][ds-image33]  
  This stabilized training significantly, allowing the deeper Run 1 networks to converge.

### **6.3 The V6 Format (Late Run 1, Run 3\)**

The **V6** format is the most advanced standard utilized in the ResNet era and is key to the advanced capabilities of late T60 and T75 networks.8

**V6 Struct Definition:**

C++

struct V6TrainingData {  
    float result\_q;      // MCTS Root Q (Average Value)  
    float result\_d;      // Game Result (Win/Draw/Loss)  
    float played\_q;      // Q-value of the move actually played  
    float played\_d;      // Probabilities of the move played  
    float played\_m;      // Move index  
    float orig\_q;        // Original Q-value (for Value Repair)  
    float orig\_d;        // Original Result  
    float orig\_m;        // Original Move  
    uint32\_t visits;     // Number of nodes searched  
    uint16\_t played\_idx; // Index of move in policy  
    uint16\_t best\_idx;   // Index of best move  
    float policy\_kld;    // KL Divergence (Search Complexity)  
    float best\_m;        // Best move (in plies)  
    float plies\_left;    // TARGET FOR MOVES LEFT HEAD (MLH)  
};

**Key Architectural Enablers in V6:**

1. **plies\_left:** This field is the specific target for the **Moves Left Head (MLH)**. Without V6, the MLH architecture in Run 1 could not be trained.  
2. **orig\_q:** This field enables **Value Repair**. It stores what the network *thought* the value was when the game was played. The training loop can then compare the *new* network's evaluation against this *old* evaluation to gauge progress and weight the training sample accordingly. Run 1 relied heavily on this for its "Rescorer" pipeline.  
3. **policy\_kld:** This measures the divergence between the raw network policy and the final MCTS counts. A high KLD indicates a position where the network was "surprised" by the search (i.e., the search found a move the network missed). Run 3 experiments utilized this field to perform **Prioritized Experience Replay**—training more frequently on positions with high KLD to fix the network's blind spots.

### **6.4 The "Client" and "Rescorer" Pipeline**

The training loop involves a distributed architecture.9

1. **Client (lczero-client):** Downloads the latest net, generates self-play games, and uploads them.  
2. **Server:** Receives the data.  
3. **Rescorer:** A specialized binary (often a modified lc0 engine) that processes V6 data. It recalculates the result\_q and plies\_left values using endgame tablebases (Syzygy) if the game entered a known endgame.  
   * *Note:* The Rescorer ensures that Run 1 networks have "perfect" endgame knowledge ingrained in their weights, as any tablebase position is relabeled with the exact game-theoretic outcome.3

## ---

**7\. Output Heads and Loss Architectures**

The final layer of the network, the "Head," is where the abstract features of the ResNet tower are translated into chess judgments. This is a major area of divergence between early Run 1 and later Run 1/3 architectures.

### **7.1 The Transition to WDL (Win/Draw/Loss)**

Early networks (T40, early T60) used a scalar Value head: a single neuron with a Tanh activation outputting a value in ![][ds-image12].

* **The Draw Problem:** In high-level chess, the draw rate is extremely high (\>50%). A scalar value of 0.0 is ambiguous. It can mean:  
  1. A dead drawn opposite-colored bishop endgame (100% Draw).  
  2. A chaotic tactical mess where White wins 50% and Black wins 50% (0% Draw).  
* **WDL Architecture:** Later Run 1 (T60) and Run 3 (T75) networks adopted the **WDL Head**.  
  * **Structure:** The Value head outputs a vector of length 3 with a Softmax activation: $$.  
  * **Benefit:** This disentangles the "Draw Probability" from the "Expected Score."  
  * **Contempt Implementation:** With WDL, the engine can be configured (at search time) to calculate score as:  
    ![][ds-image34]  
    Run 3 (Armageddon) networks tuned the *training targets* of this head to intrinsically bias the network against draws.

### **7.2 The Moves Left Head (MLH)**

The **Moves Left Head** is a distinguishing feature of the mature Run 1 architecture.12

* **Purpose:** To provide a heuristic for "conversion efficiency." In a winning position, simply knowing ![][ds-image35] is insufficient; the engine should prefer the shortest path to mate.  
* **Architecture:**  
  * MLH is a secondary value head.  
  * It outputs a categorical distribution over logarithmic "buckets" of move counts (e.g., 1-10 moves, 10-20 moves, etc.).  
  * This is preferred over a scalar regression (predicting the exact number) because the uncertainty of game length grows exponentially with depth.  
* **Training (V6):** It trains on the plies\_left field in the V6 data.  
* **Run 2 Omission:** To save parameters and complexity, many Run 2 efficiency nets omit the MLH or use a simplified version, relying on the MCTS to handle time management naturally.

## ---

**8\. Comparative Analysis: Run 1 vs. Run 2 vs. Run 3**

This section synthesizes the technical details into a direct comparison of the three runs.

### **8.1 Table of Comparative Architectures**

| Feature | Run 1 (Mainline / T60) | Run 2 (Efficiency / T70) | Run 3 (Experimental / T75/T71) |
| :---- | :---- | :---- | :---- |
| **Network Series** | T60 (and T40) | T70, T74, T76, T77, T79 | T75, T71 |
| **Max Topology** | **![][ds-image36]** | **![][ds-image37]** | **![][ds-image38]** |
| **Target Hardware** | Server Class (A100, V100) | Consumer Class (RTX 3060, Mobile) | Research Clusters / Specific GPUs |
| **Primary Goal** | Maximize Elo (Absolute Strength) | Maximize Strength/Watt (Efficiency) | Test Hyperparameters / Variants |
| **Training Method** | Self-Play \+ Rescoring | Self-Play \+ Distillation | Self-Play \+ Experimental Loss |
| **Data Format** | V3 ![][ds-image39] V6 (Full) | V5 / V6 (Often Distilled) | V6 (Experimental Fields) |
| **Value Head** | WDL \+ MLH | WDL (MLH often removed) | WDL (Modified targets, e.g., Armageddon) |
| **Input Features** | 112 Planes (Full History) | 112 Planes (Standard) | 112 (Var. history length tested) |
| **Key Innovation** | Massive Scale, Value Repair | Distillation, Human-like play | Alternative Loss Functions, T71.5 |

### **8.2 The Scaling Law Trade-off**

The relationship between Run 1 and Run 2 is governed by the **Neural Scaling Laws** of chess.

* **Run 1 (Large Nets):** Exhibit superior "Static Evaluation." They can look at a position and "know" the result without searching. This is crucial for opening theory and strategic long-term planning where search horizons are insufficient.  
* **Run 2 (Small Nets):** Rely on "Dynamic Search." They have a weaker static evaluator but can search millions of nodes per second.  
* **The Intersection:** Empirical testing showed that roughly ![][ds-image19] (Run 2/3) is the point of diminishing returns for most time controls. While the T60 (![][ds-image1]) is stronger at very long time controls (TCEC), the T70/T75 is often stronger at "Blitz" or "Bullet" speeds because it can clear the search overhead faster.

### **8.3 The Legacy of Run 3**

Although Run 3 was "experimental," its contributions were vital.

1. **T71.5 (Armageddon):** Proved that neural networks could be "lobotomized" via loss function tuning to play irrational chess for specific tournament scenarios.  
2. **Hyperparameter Tuning:** The temperature schedules and learning rate drops perfected in Run 3 were back-ported to the main Run 1 training loop to squeeze the final Elo out of the T60 architecture.

## ---

**9\. Conclusion**

The architectural history of Leela Chess Zero's Runs 1, 2, and 3 is a testament to the sophistication of modern reinforcement learning pipelines. It was not merely a linear progression of "bigger is better." Instead, it was a branching evolutionary tree.

**Run 1** represents the brute-force triumph of the ResNet architecture, pushing the boundaries of parameter count (![][ds-image1]) and input history utilization to create an entity with profound chess understanding. **Run 2** represents the engineering triumph of efficiency, proving that through distillation and careful topology selection (![][ds-image19]), near-state-of-the-art performance could be democratized for consumer hardware. **Run 3** represents the scientific triumph of experimentation, using the V6 data format and flexible head architectures to test the fundamental limits of how a machine learns to play chess.

As the project moves into the Transformer era (T80 and beyond), the lessons learned from the "War of the Runs"—specifically the value of WDL heads, the necessity of V6-style data repair, and the efficiency of the ![][ds-image19] sweet spot—remain foundational to the continued dominance of Lc0 in computer chess.

#### **Works cited**

1. Weights file format \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/weights/](https://lczero.org/dev/backend/weights/)  
2. Project History \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/project-history/](https://lczero.org/dev/wiki/project-history/)  
3. Best Nets for Lc0 \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/best-nets-for-lc0/](https://lczero.org/dev/wiki/best-nets-for-lc0/)  
4. Networks \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/networks/](https://lczero.org/dev/wiki/networks/)  
5. Beyond Perfection: The Ultimate Guide to Lc0's Human-Like Personality Nets\!, accessed January 27, 2026, [https://groups.google.com/g/picochess/c/ap95BZ2JIPg](https://groups.google.com/g/picochess/c/ap95BZ2JIPg)  
6. Contrastive Sparse Autoencoders for Interpreting Planning of Chess-Playing Agents \- arXiv, accessed January 27, 2026, [https://arxiv.org/html/2406.04028v1](https://arxiv.org/html/2406.04028v1)  
7. Contrastive Sparse Autoencoders for Interpreting ... \- OpenReview, accessed January 27, 2026, [https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf](https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf)  
8. Training data format versions \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/training-data-format-versions/](https://lczero.org/dev/wiki/training-data-format-versions/)  
9. LeelaChessZero/lczero-client: The executable that communicates with the server to run selfplay games locally and upload results \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client](https://github.com/LeelaChessZero/lczero-client)  
10. Getting Started \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/getting-started/](https://lczero.org/dev/wiki/getting-started/)  
11. lczero-training/init.sh at master \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training/blob/master/init.sh](https://github.com/LeelaChessZero/lczero-training/blob/master/init.sh)  
12. Layman's question: what is the difference between Leela's networks? \- TalkChess.com, accessed January 27, 2026, [https://talkchess.com/viewtopic.php?t=76512](https://talkchess.com/viewtopic.php?t=76512)

[ds-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAAD7UlEQVR4Xu2YS6hOURTHl1BE3pHIlVcJSV4ljxLyiPIKMVAGJkYUyeRKBmQgRJnckIkYkYR0MxApIiKPQiKEERPP9bPPutbZ55zv3vvdj0udX/27p7X3OXuf/7fPWntfkZKSkpKSkpKS9qCvaoPqaKKNqg6pHmlom6jaqzqgmqfqnOpRma6qs6pucUM7E/swVCr70FE1VUJfvOCaWC6vVdOi2AjVD9VlVU8XN4POqPq4ONdXVB9VE1w8jykS+vH87lFbe5LnwyvJ94F5X1R9Uy13cXu3ey72i94SHvRONcbF+RWJoyUuPl/1RjXWxQxiDMIKL4LJXlJ9l3/L6CIfTibx2IfVSey0qpOL49uhpC3FwCSIFkdtFt/mYpj4XjXaxQwme1PVGMUNJrFdwuo4J/+W0UU+HHNx84Gv+kIU8yyT0NbPB23l3lLVuTi/kg2wwMVt4F2SzcmDVS9VDVHcIH89lZBa7DmtMXq4hDzoP+EimPMOSa+2ShT5wIqNfWCRsdiIrU1iHvrRNj1uyGOQhM7kmv4uTpG0ga9JKBbGFtUX1VwX81yX0AeqMZrCeUJCLfD1wYNhKyWkt0VRWzU8k6wPZiSKswCwSfgkwasMrM4hqqWq4xIeMivV4zeTJOQyG+yr6q1qoRRX6BmSzn3VGG2Y4XVRnLEpaHOieGuIfXgiWR9IFy0xmnfMME51V/VC9UFCUWOrU8RI1SP5PSDiwT18pwRWwp0o1hajgTzJ+GY2Jm+S7K6htcQ+bJWsD20y2sOkV0noTKWNVyk56oHqtmqzpM32Lw/cu191ysWgrUbDEQnz4EcnJfGV1RLmzvYt9qFmRgMP5UFF+0S/rzwoabPZXxrk6/uSzuVQC6P5zPdJ2CqSukhptWaPZH2oymhOMTMlu4MAOvMw/nIz8FJF+2SMvyrhHmAS/geopLxtUnPUckUX+WCmeR+aK4aTVZ8lKob1Em5g0vFWyAbgJm6G5kxhu0MfYOeyIkfrVA+TflwTy9uXV6LWObpe8n3wRpsPfntH6oyxBda0vaN6kwIINkr2M7ZfjUo+zMV2N/XIYoNUgnEaJfSLx2wJtd51VPLBr17zwR9Y8rywLXDqwFKfBPn04qJnA1DMrI0DSVzwDD67BtXjuCGiLUabyaSoPNZLdWbXS74PFo99sCM4hmO8QTse0JYCw85LONV5xkvofEPC8dSgIFAYeFF/D4PtlHBg8cXT00tCPiVdPJfwfK6JtcTwP3lgKfKBIpvnA/Ol6FOz1ri4+ZP5pxIwuVES9oz8u++wBAMq0UXCquFIjGZLtpD8b+T5MCDVI4vdQ38WGtdxZigpKSkpKSkp+Qv8BFweNUj81DxFAAAAAElFTkSuQmCC>

[ds-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAfCAYAAABeWmuGAAAC8UlEQVR4Xu2XTahNURTH/0Ip5HNA6L3MjChfJQOJImWAokxuTzIzUeSjyGNmwkRJ+UpSpgxQXgwkRQZiQD3ykYEBRSEf62+d7eyzzjn7nnPvUWewf/Xv3rvXPnuvvc5ea+8LRCKRSOS/MiR6XVO3/j7ZHo4i76PVS9F50XJ9pJwdojOiEdFv0RfRxaTN6arobWKn7vHBFrEN6ucvqH/Pk9++6LPzf6I+Fmah6LPonGiMsRG2bRX9FF03trbwA7rg7dYA9X8/1D6c/A5yCtp5sTUYLoj22cYWwAXS/6eimcbmmC16lYjfS5mMdEuVDUY46WXRLmsogf05dhUm2IaaTIP6X7bDyUroLuIOD863QPQROqAdbBXSnJskuiNa/88aZoboAfJjWgZEz2xjTZjy9L/sZY0TnYX2WWtsOTYiLTiW4953Lmy6aLzX1o1log7Kg+KCccS014V146toqTVA5+5A61/RS8/BRbMjB/QZhB5X/fIexUFhMJ6ITqBekIvg2y+qH/NEp6HreyFanTXnmQ91mA98h57Zb5BG82HatS82iT6JNie/70KPw34DQVzKH4YWS1/rRI+ha7kC3eFBWA9cuvinB/P/EbRINQWDwaAMiS6h4n2gAgw2/WfRLGIQutPZ52bWlMelC6uvPyAL6AjKi1Sv8BLFC9QUa+gDd2WYYw0erg9VCo8eHkHsNIrsgMzF+yguUr3CINwWfRBtMLZecS+Oawgdpdz9XQPi149r0KPJMRaaNvxsAhcMincGzrsi06M3/CtDCHfkBvsdgHbgFl5jbE3CesEiOstr44mzBxqYJV57XVwqcJwiOI+7slN7s+YsLl14qsw1tqbgKWKD4aCzdHBUtChrqoSfLmX/rw4hDUbhqcYUYPHcIvoG7ci7fSdpa6ryE05OJ0LBdkGpc1PluPT1INLrwY2kzdfuxMY+nCMXDDIgeoc0albMyaaYKjppGwtgUHbaxgDuml5Fx9Dlj1wkEolEIpFIpAX8AR6XyNF6l9r7AAAAAElFTkSuQmCC>

[ds-image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABQ0lEQVR4Xu2TLUsEURSGX1FBEIMfTcNiMxlkm8FgEcFks4gGf4Jg9w8YLWIwLFhNCi4YDIL/QFBRTBZBQcGP9+Xeu/fOGWcctWyYBx5295yZwz3nngVquo41evtLj+moXras013app/eZx9LbdF7nz+jQyhhmj7BPbxncoEe+k6P6IDJZVhBPJm+F9Gm+zZo2UFsccbkUlRo0wZTpugjYotqJzAH19ag/31KFzrZb1hCbHHD5LbhTh0Yof3J7xx6QYVeaDOJN+gVymeYQcc/gSv2Sg8QV0HFdcO66UpM0ge4Ym+Ii6kVUOyCDnee/gENM8wrvSVt+CXyF1KIFk83pULXdDzJjdFzZGdYStriIe1Lcr1wp9NnJebpB/It/oktuEIqqML/Iszrjk6YXCU0g1m6DLdXKnZDV31sEfGvU1NTU8gXcHZVE0kGCGMAAAAASUVORK5CYII=>

[ds-image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAeCAYAAAAsEj5rAAABSElEQVR4Xu2UPUsDQRCGR1BQjARBELGwDNhYiIKinaVWWgSsxd5Cf4edWNmks7Wz8KMRe9FGEP+AWCgYifq+mdvs7txd1la5Bx4CeefmMntzEan4EyzCB/ickDWHsKGXpRmGZ/AOTpiMjMAT+J2ZZBo+wVM4GEc9VuCnaMNRk+Vwxbs2CJiHb6INx02WY0+0cMEGAdviRy6bogtDjsrCovMjdXglWvNishzh2VgG4K1oxs/ZOC7mQPwodl3a8BLOiTZPEo7LXZsyzog+CDbeF12fvrh1YcNWHPVYhR+iNcdwKI5j3Lg8Q55lETV4If5YNqI0IBy37A0hY/BatO4LrsWxJxy33xuyJH7kezgZxx7eiXdkIRe7jHPRmg7cNFn30S/DLfgoWshF3cm+czbhEXyHr3A9uzYHf65rlJKrciO/+COoqPhX/ACVfmCVhMP+cAAAAABJRU5ErkJggg==>

[ds-image5]: <data:image/png;base64,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>

[ds-image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA4AAAAgCAYAAAAi7kmXAAABDklEQVR4Xu3RsUtCURTH8RMZGBpFQSEkOAUhUUuG4GZ/Q0t/QFtLq4OL4OLiIrQ0NjUGDQ1BQ462NNUguIYQ0RJU3/POeXp54tzyfvCBe989997jVSRNmn/NMgbuBIvIoY03lCaVQdZxjxrO8Y0ebnCJCvpYjTdolnyx4fMVPOIXd2KdXGCMPa+JUsUTNn0ebjwVa1e7ecaG10Rpuji7eMcH9v1bHTuTijlpid12hYXE2txkcSu28SyxNhM9VV9VH2kbI3zhMCySxMPopo7YDdc4xo/Y/7YV1GUk0UEBQ7GN2mLXxw/IT8vkCMVgHp2qp7/iQKzNT/9W8hp95Rcfz2RN7PY4+nvLTsdp0kzzB34xLf5j6prqAAAAAElFTkSuQmCC>

[ds-image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAdCAYAAAATksqNAAADSklEQVR4Xu2YTahNURTHl1BEIfKt5yUDIYpIoTeQTBiYIEqRTJiQR0Ympga+BjIx8i2SGCh3JJlIkQmFmCgpoeRz/d46q7vPfmfft7vvJfd1/vWvc9fae599/utjn3NFatSoUaPGP8FI5Urlo4InleuVowv/cuWR4npYAyG2KT8o/yjvK88pLyjfK18qJysfF7ZOAwHdr1wQO6owQXlVTIivyh1ld59Yu5RPxcZ0miBjlE/E9r4x8lWCbGDwW+XSyBfil3SOIOOUl5RfxPbsHFCQNWIDr0izT6SwWvlDOkOQGGT+gIKMUl4UG7g18lVhivK5DGNBupXvlB8lr9kg4DVJCzJHuVN5VjlXOaLktd805hnK+WLZCcYre8Q2O7OwDTWyBFmn/K18pZwW+VJYLHYsh5iuvKH8qbyuPFhcY8PnOCzlem6IZSin2D3lp8JOk3cg3pvC7uThlhV+f1BnKlhZguyV5saIUjvoUj4T2/TCwM41NnyMAdyDzLgsdl98u8VOMeDH+r7iN8A3VSxozHkgtob3uz1izZ4DgbJnjSpkCeIRa0j7gtwWWyN8CAc2fHHDZlMe0bisjivviB2VIXyvL6SczbPFxNoQ2KqQJYiXDJEkNXPARkPxuElqPmntGzka2F0QfDF48Ib0D5D3L+ZRZvwGXFcFI0aWILOUryW/qQI2HNZpK0HC+g/nuCCfA5sjJQigJJhHr1lU2MgYv26FLEFI19NiA7dHvhTOi23aMRhB8MVoJQilggDM9awIs6UVsgQBpDUD6fJjI1+MScqHYqXm8EgvCWyOsGQOBPZ2BQH0GOby4dml3FR2J5EtCFnCQHrJlsgXg28cBAmPxe9ic0ORHN6jGLMqsA9GENbxex6T/NeFbEEAmXFCbAI8VXb3HX10cdI0PhUQ567Y0ccR6KcJxyk2fC4gD0m9n5FmEIgyjXqicoXylphQm8VKzo/kEP66UNWUQ7AmpxB/X3Av5lC6PWJrx89SAs610vwq5G+Am2Iffnwg8fmfWgBBDym/iYnA2y/X2MIyJPouekiixkZje6o3dYvdgzJvhao1Q6aysB9IQ/4Y4v+QXrFeUBWpGP4SlYrsUIHAzJNqsWrUqFGjRo3/DH8BIkD3JiGS6/kAAAAASUVORK5CYII=>


### Appendix E: SQL Query Library

#### E.1 Materialized Views

SQL

CREATE MATERIALIZED VIEW games\_month AS  
SELECT user\_id, username, count(\*)  
FROM training\_games  
LEFT JOIN users ON users.id \= training\_games.user\_id  
WHERE training\_games.created\_at \>= now() \- INTERVAL '1 month'  
GROUP BY user\_id, username  
ORDER BY count DESC;

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

### Appendix F: Index of Technical Terms

- AlphaZero Paradigm — Chapter 1, Section 1.2
- Binary Formats (V3-V6) — Chapter 3, Sections 3.1–3.2
- Client Lifecycle — Chapter 5
- Data Orphans (Ghost/Zombie/Version) — Chapter 4, Section 4.3
- MCTS Parameters (cpuct, fpu) — Chapter 6, Section 6.1
- Match PGN Retrieval — Chapter 9
- Network Topologies (T60/T70/T75) — Chapter 7
- Rescorer / Value Repair — Chapters 3 and 7
- Storage Chunking — Chapter 4, Section 4.2
- Tuning Workflows (CLOP/SPSA/SPRT) — Chapter 8

### Appendix G: Base64 Image Assets

[db-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>
[db-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFMAAAAfCAYAAACbKPEXAAADTUlEQVR4Xu2YT6gNURzHf0KR/5Q/Rd5DSSlJbFhSLEgoC5GNWFiIIqv3ShaskBXqJclGSQphcaMkyp8SJQqJEAuhJPH7dGaaM787c+e67757p5n51Lf35pyZ6dzv/H7n/M4RqaioKDGTVedsY0E5rhptGw0TVDtUc1V9qj+qa7E7UuDBm6r9tqOgnBIXOGNsh8du1fjg/2GqQ6q/UXcyI8W9/IZqrOkrKlNVT8X9bn5/EmdVe73rpaqf0vgDyC7VN9US21Fw1ql+qDbajoD1qlXeNf5wf2rA9areqU6KC+UywZx5XVyEEqlZbJGMNH+uqkkDtwvONHEe3LMdHgQZa8lL1QLTFwOnD9vGEoFRA6rftsODaeCOqse014GZa2xjydgp6emLkZRDlI1ABo+KuiPGqb5IRugGbFU9Vl2S6AvNCa5nBtfdZLi48TEHLjd9lHyszJR/SawQF5kjbIdyXuLP4VXi2oIJr8TNG42gdODLheIZXkp6HJSUl3cQTGABZeVlMfmomu/1h+NOy0CC4oNqkmnnN35SvfX0NXaHx1rVRUn+Ij5HzTVRsEc127R3A8Z+QdxKG5YuyC/zzkhjM0ndmgyyNMRMwv9/2aB6aBu7xAzVfXFRxI4F02oSr05Wqt4H9yTRFTNJ582qZ5I+sDT8VGlWTCerebhJKG8wE1N9MPOu1KdxSMfNxMjtqgeqefGuptjUghhfuDduhl9Sn+JwQFyqp9FxM/na1Fp5WLnTICr52DYCr4rbOqbRFjMXiTOIEikNIhIjwzrLh50DKZQHMAQziUIfxt9v2izMu2+Cvy1DSZT1EopWzvFOiJu/JoorWvn/imSfCXYKVvUkM5epZpk2S1gFNDwNygJTOFLiaCmJxarPqtOqRxKvNREDzROM6bZERTZzO2mfBdMdzw4aXkKNZiHiLovb4JMq01W3JDKSciRvHBE3tu/iTsI4VtwWuyMZzibaYiYr4IDU72LY9TCn+gen/E+09kj9/XmBuXNhoMQ9tCFcfF7Hm1uDApw5kQPiMtIvyeVUS5DOLCSk7RTTV3R6xU0Hx6SNmUbqsoHfZzsKDOZhIuVd22tnSiBOTvK2Qg8VQ/p7w+3iE9NeVF5IfjYcFRUV+eUf5tOrclUhOmEAAAAASUVORK5CYII=>
[cs-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAgCAYAAAAmG5mqAAABJElEQVR4Xu2RP0uCURTGn6CgKIggkKClMRSapEWaQmxp7SME0dSQHyD8BtHQIg7S0uLg1tYi1uDS2BJNijQlRFA9z3uO1+trUK3hAz9e7nP+vPecC0w11Y8qkluyQ2bIAjkiHf/qHLTtgRZ5IwekTRpki7ySG7Ks5FlyRY5JjXw6p7A/SefuKQcb5J5skqYHqmTOk6Wy+4qjBPvdEnkmH2Q3pJoqsIK7lJ+YKl5M+Y8eu45NzSJT3dJ6h8V0taCMm3ux6ZLfI9nYLJA+bPhYWqUKzjDaWqIT8kJysQl7k/AGQ+n+GkidLjFaqbalJut+DsqTAew6StY8a2Q+Top1COu+kg58Jw1ShxX8SqvkAX8o6MKSxRPZHw9PSlsZcoHJd/h3+gKDYjz3nz0vXQAAAABJRU5ErkJggg==>
[cs-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>
[ds-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAAD7UlEQVR4Xu2YS6hOURTHl1BE3pHIlVcJSV4ljxLyiPIKMVAGJkYUyeRKBmQgRJnckIkYkYR0MxApIiKPQiKEERPP9bPPutbZ55zv3vvdj0udX/27p7X3OXuf/7fPWntfkZKSkpKSkpKS9qCvaoPqaKKNqg6pHmlom6jaqzqgmqfqnOpRma6qs6pucUM7E/swVCr70FE1VUJfvOCaWC6vVdOi2AjVD9VlVU8XN4POqPq4ONdXVB9VE1w8jykS+vH87lFbe5LnwyvJ94F5X1R9Uy13cXu3ey72i94SHvRONcbF+RWJoyUuPl/1RjXWxQxiDMIKL4LJXlJ9l3/L6CIfTibx2IfVSey0qpOL49uhpC3FwCSIFkdtFt/mYpj4XjXaxQwme1PVGMUNJrFdwuo4J/+W0UU+HHNx84Gv+kIU8yyT0NbPB23l3lLVuTi/kg2wwMVt4F2SzcmDVS9VDVHcIH89lZBa7DmtMXq4hDzoP+EimPMOSa+2ShT5wIqNfWCRsdiIrU1iHvrRNj1uyGOQhM7kmv4uTpG0ga9JKBbGFtUX1VwX81yX0AeqMZrCeUJCLfD1wYNhKyWkt0VRWzU8k6wPZiSKswCwSfgkwasMrM4hqqWq4xIeMivV4zeTJOQyG+yr6q1qoRRX6BmSzn3VGG2Y4XVRnLEpaHOieGuIfXgiWR9IFy0xmnfMME51V/VC9UFCUWOrU8RI1SP5PSDiwT18pwRWwp0o1hajgTzJ+GY2Jm+S7K6htcQ+bJWsD20y2sOkV0noTKWNVyk56oHqtmqzpM32Lw/cu191ysWgrUbDEQnz4EcnJfGV1RLmzvYt9qFmRgMP5UFF+0S/rzwoabPZXxrk6/uSzuVQC6P5zPdJ2CqSukhptWaPZH2oymhOMTMlu4MAOvMw/nIz8FJF+2SMvyrhHmAS/geopLxtUnPUckUX+WCmeR+aK4aTVZ8lKob1Em5g0vFWyAbgJm6G5kxhu0MfYOeyIkfrVA+TflwTy9uXV6LWObpe8n3wRpsPfntH6oyxBda0vaN6kwIINkr2M7ZfjUo+zMV2N/XIYoNUgnEaJfSLx2wJtd51VPLBr17zwR9Y8rywLXDqwFKfBPn04qJnA1DMrI0DSVzwDD67BtXjuCGiLUabyaSoPNZLdWbXS74PFo99sCM4hmO8QTse0JYCw85LONV5xkvofEPC8dSgIFAYeFF/D4PtlHBg8cXT00tCPiVdPJfwfK6JtcTwP3lgKfKBIpvnA/Ol6FOz1ri4+ZP5pxIwuVES9oz8u++wBAMq0UXCquFIjGZLtpD8b+T5MCDVI4vdQ38WGtdxZigpKSkpKSkp+Qv8BFweNUj81DxFAAAAAElFTkSuQmCC>
[ds-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAfCAYAAABeWmuGAAAC8UlEQVR4Xu2XTahNURTH/0Ip5HNA6L3MjChfJQOJImWAokxuTzIzUeSjyGNmwkRJ+UpSpgxQXgwkRQZiQD3ykYEBRSEf62+d7eyzzjn7nnPvUWewf/Xv3rvXPnuvvc5ea+8LRCKRSOS/MiR6XVO3/j7ZHo4i76PVS9F50XJ9pJwdojOiEdFv0RfRxaTN6arobWKn7vHBFrEN6ucvqH/Pk9++6LPzf6I+Fmah6LPonGiMsRG2bRX9FF03trbwA7rg7dYA9X8/1D6c/A5yCtp5sTUYLoj22cYWwAXS/6eimcbmmC16lYjfS5mMdEuVDUY46WXRLmsogf05dhUm2IaaTIP6X7bDyUroLuIOD863QPQROqAdbBXSnJskuiNa/88aZoboAfJjWgZEz2xjTZjy9L/sZY0TnYX2WWtsOTYiLTiW4953Lmy6aLzX1o1log7Kg+KCccS014V146toqTVA5+5A61/RS8/BRbMjB/QZhB5X/fIexUFhMJ6ITqBekIvg2y+qH/NEp6HreyFanTXnmQ91mA98h57Zb5BG82HatS82iT6JNie/70KPw34DQVzKH4YWS1/rRI+ha7kC3eFBWA9cuvinB/P/EbRINQWDwaAMiS6h4n2gAgw2/WfRLGIQutPZ52bWlMelC6uvPyAL6AjKi1Sv8BLFC9QUa+gDd2WYYw0erg9VCo8eHkHsNIrsgMzF+yguUr3CINwWfRBtMLZecS+Oawgdpdz9XQPi149r0KPJMRaaNvxsAhcMincGzrsi06M3/CtDCHfkBvsdgHbgFl5jbE3CesEiOstr44mzBxqYJV57XVwqcJwiOI+7slN7s+YsLl14qsw1tqbgKWKD4aCzdHBUtChrqoSfLmX/rw4hDUbhqcYUYPHcIvoG7ci7fSdpa6ryE05OJ0LBdkGpc1PluPT1INLrwY2kzdfuxMY+nCMXDDIgeoc0albMyaaYKjppGwtgUHbaxgDuml5Fx9Dlj1wkEolEIpFIpAX8AR6XyNF6l9r7AAAAAElFTkSuQmCC>
[ds-image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABQ0lEQVR4Xu2TLUsEURSGX1FBEIMfTcNiMxlkm8FgEcFks4gGf4Jg9w8YLWIwLFhNCi4YDIL/QFBRTBZBQcGP9+Xeu/fOGWcctWyYBx5295yZwz3nngVquo41evtLj+moXras013app/eZx9LbdF7nz+jQyhhmj7BPbxncoEe+k6P6IDJZVhBPJm+F9Gm+zZo2UFsccbkUlRo0wZTpugjYotqJzAH19ag/31KFzrZb1hCbHHD5LbhTh0Yof3J7xx6QYVeaDOJN+gVymeYQcc/gSv2Sg8QV0HFdcO66UpM0ge4Ym+Ii6kVUOyCDnee/gENM8wrvSVt+CXyF1KIFk83pULXdDzJjdFzZGdYStriIe1Lcr1wp9NnJebpB/It/oktuEIqqML/Iszrjk6YXCU0g1m6DLdXKnZDV31sEfGvU1NTU8gXcHZVE0kGCGMAAAAASUVORK5CYII=>
[ds-image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAeCAYAAAAsEj5rAAABSElEQVR4Xu2UPUsDQRCGR1BQjARBELGwDNhYiIKinaVWWgSsxd5Cf4edWNmks7Wz8KMRe9FGEP+AWCgYifq+mdvs7txd1la5Bx4CeefmMntzEan4EyzCB/ickDWHsKGXpRmGZ/AOTpiMjMAT+J2ZZBo+wVM4GEc9VuCnaMNRk+Vwxbs2CJiHb6INx02WY0+0cMEGAdviRy6bogtDjsrCovMjdXglWvNishzh2VgG4K1oxs/ZOC7mQPwodl3a8BLOiTZPEo7LXZsyzog+CDbeF12fvrh1YcNWHPVYhR+iNcdwKI5j3Lg8Q55lETV4If5YNqI0IBy37A0hY/BatO4LrsWxJxy33xuyJH7kezgZxx7eiXdkIRe7jHPRmg7cNFn30S/DLfgoWshF3cm+czbhEXyHr3A9uzYHf65rlJKrciO/+COoqPhX/ACVfmCVhMP+cAAAAABJRU5ErkJggg==>
[ds-image5]: <data:image/png;base64,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>
[ds-image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA4AAAAgCAYAAAAi7kmXAAABDklEQVR4Xu3RsUtCURTH8RMZGBpFQSEkOAUhUUuG4GZ/Q0t/QFtLq4OL4OLiIrQ0NjUGDQ1BQ462NNUguIYQ0RJU3/POeXp54tzyfvCBe989997jVSRNmn/NMgbuBIvIoY03lCaVQdZxjxrO8Y0ebnCJCvpYjTdolnyx4fMVPOIXd2KdXGCMPa+JUsUTNn0ebjwVa1e7ecaG10Rpuji7eMcH9v1bHTuTijlpid12hYXE2txkcSu28SyxNhM9VV9VH2kbI3zhMCySxMPopo7YDdc4xo/Y/7YV1GUk0UEBQ7GN2mLXxw/IT8vkCMVgHp2qp7/iQKzNT/9W8hp95Rcfz2RN7PY4+nvLTsdp0kzzB34xLf5j6prqAAAAAElFTkSuQmCC>
[ds-image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAdCAYAAAATksqNAAADSklEQVR4Xu2YTahNURTHl1BEIfKt5yUDIYpIoTeQTBiYIEqRTJiQR0Ympga+BjIx8i2SGCh3JJlIkQmFmCgpoeRz/d46q7vPfmfft7vvJfd1/vWvc9fae599/utjn3NFatSoUaPGP8FI5Urlo4InleuVowv/cuWR4npYAyG2KT8o/yjvK88pLyjfK18qJysfF7ZOAwHdr1wQO6owQXlVTIivyh1ld59Yu5RPxcZ0miBjlE/E9r4x8lWCbGDwW+XSyBfil3SOIOOUl5RfxPbsHFCQNWIDr0izT6SwWvlDOkOQGGT+gIKMUl4UG7g18lVhivK5DGNBupXvlB8lr9kg4DVJCzJHuVN5VjlXOaLktd805hnK+WLZCcYre8Q2O7OwDTWyBFmn/K18pZwW+VJYLHYsh5iuvKH8qbyuPFhcY8PnOCzlem6IZSin2D3lp8JOk3cg3pvC7uThlhV+f1BnKlhZguyV5saIUjvoUj4T2/TCwM41NnyMAdyDzLgsdl98u8VOMeDH+r7iN8A3VSxozHkgtob3uz1izZ4DgbJnjSpkCeIRa0j7gtwWWyN8CAc2fHHDZlMe0bisjivviB2VIXyvL6SczbPFxNoQ2KqQJYiXDJEkNXPARkPxuElqPmntGzka2F0QfDF48Ib0D5D3L+ZRZvwGXFcFI0aWILOUryW/qQI2HNZpK0HC+g/nuCCfA5sjJQigJJhHr1lU2MgYv26FLEFI19NiA7dHvhTOi23aMRhB8MVoJQilggDM9awIs6UVsgQBpDUD6fJjI1+MScqHYqXm8EgvCWyOsGQOBPZ2BQH0GOby4dml3FR2J5EtCFnCQHrJlsgXg28cBAmPxe9ic0ORHN6jGLMqsA9GENbxex6T/NeFbEEAmXFCbAI8VXb3HX10cdI0PhUQ567Y0ccR6KcJxyk2fC4gD0m9n5FmEIgyjXqicoXylphQm8VKzo/kEP66UNWUQ7AmpxB/X3Av5lC6PWJrx89SAs610vwq5G+Am2Iffnwg8fmfWgBBDym/iYnA2y/X2MIyJPouekiixkZje6o3dYvdgzJvhao1Q6aysB9IQ/4Y4v+QXrFeUBWpGP4SlYrsUIHAzJNqsWrUqFGjRo3/DH8BIkD3JiGS6/kAAAAASUVORK5CYII=>
[ds-image8]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEQAAAAdCAYAAAATksqNAAADm0lEQVR4Xu2YTahNURTHl1AUIfKt55GBEEWkfA0kEgMTRCmSiYHIR0Ympga+BjKRgXxGEgPlZiAxEJEShZgoKaHkc/3uOuvddfe9+3q9J72n86t/nbfWOfvsvdbaa5/7REpKSkpK/gl9VfNUdwsdVi1T9S/8c1T7iuv/GgKxXvVO9Ut1U3VCdUr1VvVcNVx1r7D1FjzBrCVNbpYhqvNigfis2ljvrg66WfVQ7J7eEpDRYon9pHqk+iE2f5K7ONzXAA9x42vVrMQX8QF7Q0BIMus6qRpY2Marbout4UNha2Ch2A3n5M+ltED1TXpHQJgj60Lrgr2f6kJhZz114DwjjQ/lGKF6Ij0/IHHRiN4ROVjYVyV2aVe9Ub1XTU18zfAX5QIyQbVJdVw1UdWnzmt/05jHqKaIVScMUi0Rm+DYwtZdVqq+iB0InIyRbECWqn6qXqhGJb4cM8S6doTmdUn1XXVRtau4xobP2Su1rKGKWIUy6Rti+xo7+98heK8Ku4vGP7vwcx19uWQ5A1TXxO71MTrYVjgqYlnqCm2qx2KTnhbsXGPDxz3AO6iMs2LvxbdF7BQDP9a3F38DvpFiSeOZW2JjeL/bKtbsORDY9ozRiulSC7w32w48YxXpekCuio0RF+Fgw5c2bErVM5puK8qZDJLJiM/1qdRXMycHwVoebDnY8pw6jEMAG/AtQyYpzc7ARGPwGDz3PCXpJb0/2D0g+FJYeEUaExQbJduMv4HrZslImStWGS0/LcapXkrnmyow4bhPWwUk7v/4jAfkY7A5uYAAW4LnWBilD1SMX7fimeq+WLPPQrkeFXvJhsSXg5Jj0k53AoIvpVVA2CoEgGe9KmK15KBJ80EWGzyk27IKZc0L6PINTSZhmOqO2FZzPNMzg82JW2ZnsHc1IOBHJj8821Sr690N+M+SeHLBYNWKxFaFKuEF9JK1iS+F3zgEJA7+VezZGCTHexT3zA/27gSEcfydB6T15wKNnB91p1OHMkmaHLsOlXFIbJLoSL27evTRxSnT9FQgONfFjj6OQD9NOE6x4fMAskj2+zGpJYEsU7pDxRrfFbFArRHbcn4kR/xzoVlTdnZIbT055YJehYUuUj0Qu5l/A1yW2q9FfiGmwXAI6G6xL0OCwNcv19jiNiT76aQQFUOPSe253tQu9g62eTNYaEUax0vVaShDvv8ptz1ipdUsUyn+EZXL7N+CxEyW5sEqKSkpKSnpYfwGmA8E1Yx44+UAAAAASUVORK5CYII=>
[ds-image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACEAAAAfCAYAAABplKSyAAABVElEQVR4Xu2UvyuGURTHjzCI/CgLBpL8AyYSC0UvpRjsLAYLxWqxmowWg4nNyMQigxhMUpL4B2Qg8Tnde/O6PW/ueZVB91Of4Z7nebrf7rnnEclkMpnfs4vrcfGvySEC1YSoxVk8w1tsxhocwVO89O8kYw3Rgvt4jhM4h8e4KG7zMVzC+fBBCpYQA3iNfVH9Aw+wDqf9euHbGz9gCbGM2+KOPtAkbtMVv9aT2sD68EKgJK539wW+4nNBPXiB/VKZXnzD4fhBTDvOiOtf7AnuFdSD49gglZnEO+yK6iYs7ShiU77uQ9VYQqyJ67+OpvZeW/EkbiLKGRR3+slYQui90hA6IbqJjqGudWoC2rrDsnUSlhC6uV7WUezGK7zBKf9cA2zhkV8nYwmhPyGdpkd8wVVsw3d88LUdbAwfpGIJoeh/oQNby2qdOBTVTPSI8RJlMpl/xSeNnUxubGgXlQAAAABJRU5ErkJggg==>
[ds-image10]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABOklEQVR4Xu2UsUoDQRRFn6CFCKIQEMFC0qUSsQspRbBWsJBUIvoH+QILKzsFiVhJGj/Awj+wsbHTQhGsJChYSIjxXt+MO/PczRrSpNgDB5a9b4aZN7MrUjBy1OHTP7yFB25MJmvwGF7AT9iDH/A08Azeu+wQTvyM7EMFvooOaJqMjMEd0XzfZH9YFy2k2ybzzIvmN3DWZBHsBwvf4ZLJPH6yR/ecygJ8Fi08F91SGlwxa1pw3GS/rMIv0cI9k3km4RVsw2WTRTQkOcUVkxGudAt24YbJIrjcS9HJ7mApyDjJIjxx+Zt7l8kcfJDkJNPk1ni/pt2YTGqwIzroSPSUQmeS0v6EW3yB5TgeDPaHfeJk13Aqjgcj3GLuR5wH75RvMj+noRi6X7wnVbgpeuT+6HfdO/49CgoKcvkGllRR4tTDUpoAAAAASUVORK5CYII=>
[ds-image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAhCAYAAAA74pBqAAABSklEQVR4Xu2UMStGcRTGH8UgCilSJiwyGGSTzWBA+QbvZmbAB1BmAyWLZGG0Gt5MYpBiVEhMFsXAwHM69989/3Nd7n0n6f7qN7z3nPfpds7pAhV/gnl6n+Oc6RNa6V5Ssy6Ehkm6TT8T3+kh3aCDoSlBwlboFbT3gm7SYdskPEAb6rQ9LmWYoC+0yRcC19CwG9rrahZ5uyO65QuWOjRM3rA/LkXIjG/pkHsesQsNe6VjrhbooZcwA89jDekSpl1NkPms0hPa4WoZZpCGLbtaCz2gO7TZ1b5FNvQBDZM/WWahi/lxTpZR6LolbB/x2k/pkvn9K330Dtlbk9BCc7J00XNkb22EToWmorTRY2jYEx1AOvhCQ/f4WwuDbwh7azXogZYavGURaZgEndHuqKME4/QN6a01NKuAfJee6SNKHGgenXQdOviKiv/NF8KKUGzPSoFMAAAAAElFTkSuQmCC>
[ds-image12]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEMAAAAdCAYAAADxTtH0AAABZklEQVR4Xu2ZsUoDQRCGJ2AgRcCggRCwsbALWAiWVpaxS5cH8A0UbC3txCpdep8gVd4gFnmG9BZ2kjhzu8fODieb3F089zIf/BzsHH9m/kuO3B6AoihKfjZMd6JWB9qoObgZv7yq4BnVt2qJWh1ooE7BzHeJWvpln0e5sGdOUA9gmiubkC/VPuQi56/C6IFp9hPMV/XKLxci9Q75Vh7GLbjf69oeQ01vA/muwPcO+VYeBoc+q6wwJOQd8tUwGBoGQ8NgaBgMDYORK4wb1CSnjuF3NAxGlGHsCw2DoWEwtgmDGh7a4y5EEUYTdYEaoWbgniNewDTXcacm0EDp88a9qHHItwe+d+p7DdnelYdBVyodLktTd2rCG5ihvlGvosYhXx5clqR35WHk5cmqTKIM4wj1jhrLQkGiDGOAWqDOZaEg0YVBN0b680Z7s7R/WSbBMP7bhvAZqisXC7DThjC/+x78q4KD5gfoGaDT2ePfWwAAAABJRU5ErkJggg==>
[ds-image13]: <data:image/png;base64,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>
[ds-image14]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAAD/klEQVR4Xu2YXYhVVRTHl1gPoRJZoIIyUalEIklqJPrWJ9GLIShGBIKJhIJCPvg0Dz0UFSGRIEUMEQX6IojIIDIohOCrEQQ9GJGYaBQaTGG6frP2unefde6599yZe7k+nB/8YWatffY+57+/r0hDQ0NDQ0NDwyhYoTqoOpb0bjHdYqFqj+pp1fyQW6LarXo8xJ15qlWqcbE2Pkqx+4noA/93Aw+el/b38Hf0ZYYHVD+rnspiFNyvuqs6q3o4yy1TXUm5qJOqpe2iBZ5V/araEGLTqhey2Kio8uGOdPaBATeZ8m9m8Y2qP1WXs9gMfOT7qodCnIbdQEaw40bfVN1K+e/FerJqdI6JfcSWEJ8Qe/6NEB8FVT58KZ192J5iJ8S8cvDg85QrcCAFv5PiA/Bfyk2J9SC40XXNoeHPxOqJ9TNFr4otQ6OmyofNUvaBzjiTYodaJdtsFcs9lgcpSPDHmBAzgRzGYjD0a/Rq1TXp0MOJB2OgC0+KrYP5FK7iNdVhKXduFVU+PCFlHxgYN1JsZ7toC9omRye1GFP9q9or5akfG4B+jWZTpQ5GBdDGYunPYGeB6hvVObE6OkH928Q69/WQ60aVD52MdiNRJx+eU92W6gNFARqjov/FpoLjRv+k+kW1T2wJoOLrUtzYmGZTYvX8o/pU2mvgWrHn87rr4oZjTg7vjCkvhvhc8PU298FHfy+jJ2KiE2xuVMSxJR99bnTchV8Vexl23HUpxnEPM/2lfJ13eOZ3Ke70daHD2GDdbEx+T7WpVWIwYFj0YWBGYyBGsuPGXZj/X5Hyev6I6pJY42woEI+CkeVi8U9ioiZHxWbWSrFZxYwaJPjA+0UfBmI0vUbvUUk/6yibDkcdnmOjgNx8X6Nz6AhycQOqC+/3sdhM+kO1vpieE+4D9Ucf5mQ0U49zJGfjfBOhEUzw2w6jlQ/7MOVyqDiOXo/RcMSN9k2mX4Yxouv40Gsz5FLGnlTaDH19+03KtzQ2lh/ERif4msXaOuaFxOr4NuXoCMdPHd2MZh1nPe+HYazRdX3Ij3ecvyOYT65wvAOukLw0V+II0yS//fwtVslpKW5ui1QXUo5R5qwR2yBz8x03Ol4SejGsU0ddH/ILywd5oYQPrtJyOC32IGtSrq/EXvxIu6hcFKs8/nDihtIA09jhxTCSeIQep+14Ne+Gm3w+JhLvyOzN7scHv4JTPt8o6eyvU64AP4IQ7KZ8rXlJ9ZfqbWkf7Lk8cImg7HEp39yeEVs/acvBsFNi66HX04thXlj69YHZPCk2U3dkcWYFPzSVflSaLXw0H/SF6i3Vo8V0R14WGxWMkl1S75n7HTqWn375pvH0d92B09DQ0NDQ0NAwQO4ByxIwoy5Vlu4AAAAASUVORK5CYII=>
[ds-image15]: <data:image/png;base64,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>
[ds-image16]: <data:image/png;base64,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>
[ds-image17]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAfCAYAAAAslQkwAAADsElEQVR4Xu2aS6hNURjH/0IRUojkcQ0UBqS8ItyJiSTyKCLdGJDyiKIwcctQiYlHioFEkUJ5DU6ZeBQToTBgcIsBKRTl8f377mqfvc49Z6+1z97bvrv1q//kfPusvdf6n7XW9619gEAgEAgEcmOYaL3ovOi16GY8XDm6Re9EV0T7RZPi4erxVfRX9ES0TbQoHq4cM0UbRBdFv6F9Hxq7okLMgXaySzQgHsodrhzTkc99TdtJzIDO5oN2oCqwYw9FI+xAztDUF6KaaHg81BZsdymitl04hOyfozTQ4BqK6dwC0U7o3vcRujTWkM292XZ9u6ZtFzgGnMXj7ECRDIQup0dFZ/tQWrgP1ZDNIPvA+2VpsIFt1eBv8HvReDtQBDR2o+iT6JfoBiJTORvW9SotweD/bPBd0VvRKqjZWRMM9jTYbPJMXJ6KOns/HyQ6Di1DXBkpuiYaZQcyJBjsafABRJs8xRKEM2+t6LloYnRpS/hDOQE1OU+CwcAaeBi8F411HZdYGuzDdtFt+8OMmSrqEe2wAwVQJoM5Drx+sx1woQPpDhE4s/Iqvvkss0WvoB0bHA8XQpkMJt+hSexWeOQ6PCV5CX9zCQ3+JvrgKFcuiD5DB+ARtH5sxWjRfTTez0W70JyyGczl+ZzoD9Tox/FwIwtFb0Qr7IAjNPgSojIoSa7wYJ3nsPegnbkeDzfAX/NiNN4vSavRek8rm8Enod/hXrwbeubQlGXQmUuT03IMetO84KpiEkKuNEVTJoP5ooHX84whMal9Bt206+EsOCW6DC2XXDAPmmZ594ErxQPo4XyRuBjMvnMLabUS2KQxeCUcs+j50AvPQDMylkRMYHjq9EO0JLrUCbbnm337UuYyiSvgT+iLg7FWrBlpDN4HB4M7oFnpJmh5wxvUi8thmtnId7V74JHZeVKkwdwKzB7NF+0clx5omWY+r4czy4zfcitmY9pmu8yITdum3QnRpQ0kHnRwll6FLsOEa/ppRA/XhfQGdULraIq18eR4uG2KNJgDaf/wbdXD/fAW9LCIfW9FUtv8sTTDyeB5iP8rgLN1imha3WdpYbtboH+nsR+8r4HxoUiD08Jn5GlTXiQa3J/pDy+7WZOOsT/MEBr8P/70UAjsXA3lNZirV95HqWUfg7bg3sZkxC7tygBLyiNwqE3bwORPTIyHWLFKwMHjHs43XbOQLtPPC/MWLi94DMv9nf2fa8UqxWHRF2hHeQ57Jx6uHN2IzuOZofP/4IFAIBDof/wD3sIcbAs9PmgAAAAASUVORK5CYII=>
[ds-image18]: <data:image/png;base64,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>
[ds-image19]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAADcUlEQVR4Xu2YS6hNURjH/0KR96NQJHmUyCMiogwIyQh5pjsQkpkimVwDA5KBASV1k0QxUUiSTkzIyEgpAxIhRCjJ4/v79mevvc7eZ69zz2Xfy/rVv3vv96197tr/9a3XASKRSCQSiUSqZrZolx/06CWaIjooOiKaL+qdaZGF7dn2lOiEaItoRKZF94M+9PGDCQNFO0VTUf/eo0Tbvdgv3op+JPqe/Ky5DRyGiG5C222EGkjWiL6JbkA7YfB3xm45sf6iY9D/Q+P7OrkqyfPBfReXMaInSNu7uiwanTZNWSuaJxoJNbiR0Seh+TuiQU6c5l1PcqxWY4Pos2iTEyN8gRp0cJZlU5WR50OZ0Rycj6JXogvQmW3FV4i9fCOj30HzZ/yEcByao+HG6SRG+WyGxjsQ0Lm/iOtDmdGr/UQIIUabaXlG74Pm3jgxtrNn+jlxsiiJ11D8Qi4TofsBl68yVooOoHiNbUSPMdqt3rmi16L3Tsxo1ugBorPQ9X64lzM4M9aJXopWeblQuoXRtknkGW1Lh2t0EbamfxLN8XJlmOHjvThNfiFa6sWbpRmjH4oeQ9f3PdD3YWEtSJvWE2I0P5j5c8iuq+6zIUavhw7aXnRufeZAPUJqNj9jt2jh7xadpxmjeQJzl7IV0PfiXlZIiNFmEA3nedFYLPqCMKNpDk1iO/8M2gw8AbEfk6HVxErqCkKM5kAvh55QXIaJ7kOfLdwfQozmmbcdaVUPFs0Q3RNdSeKNjLZzOI91rMBWYF+OQgeexyvuB11BiNFF0NxL0Gd5mcklxOhG2BrNdTIPXk6eimY5MZ5EeEPszPJRVUUzdh46wIdRf+Gyk1bhRhlq9FDUH9X491Xos+yEDzvzQDTJi++AVkDhNCugyjWamzc3PeafI7spsx+c6cwVbsohRrNymGf1THPiE0TPkH/T4z/npjfWi5MO0SE/WELVp46Zog/Q/DVk2/C2zFszc+4eliHEaJsWlE0NvuD+JOZ/d2Emf01yvlgZrOpQzOTbfiKhDa2bXWY095m70ALxN/PpSG/PdfCowjs+v43iVLApwbWPOXeZaE/yF5Eea9qglewfdYh92WSD44vrXKgpf/rCkufDNuT7wFnLS9hWpPsL+8S+mT8tw1FcAr0OsyrHZbL/Fxx8DmxP+eo3EolEIpHIP8tPmwAN3TcnOp0AAAAASUVORK5CYII=>
[ds-image20]: <data:image/png;base64,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>
[ds-image21]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAB8AAAAcCAYAAACZOmSXAAABiklEQVR4Xu2VPyhGURjGX6GIssifyZcsWJRkMcpokIEyKZsyKDZ9ZoOSKEkZxSolGYxmUiKZjCZShOfp3Pvdc5/u/T59rsn91W8577nnvOe855xrlpPzX2mEk3AL7sBVWBPr8Uf0wwVYK+3P8BQ2S3tmcHWb5lauzMNPOKqBrGiF17BBA2AEvsNlDWRFJ3yEYxoAE/ALjmsADMMVWK8BgTs7B2c1QLjiY3OTrFl8+1nvK9jmtYV0wAu4qwEPnqEl+AAHJFaC2XFy3w9Y8PqkwSROYIu0F+Ed7JX2RKbNTegnwK3SG5DEOTyzKAGW4gb2lHqUgSt/g3tw3eIJ8M5Xqisn5eqZBHdiG3bFeqTAiVkXGj4qffDSogQWg/ZyMIFDc1eT5+RHbJj7IAme6JfAQYn5VLVy1vPA3OBJ1MEj+AqHJOZTdc33zT0kaTD+BLs1EPCr0z5lrq5Jh6oA783dZe6CEt71Jg2YG69oFRJgp/Avpu87B7615Ppl9siQdjhj0S/VP/05OTlV8w1rmVGXtjBmBwAAAABJRU5ErkJggg==>
[ds-image22]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAOQAAAAcCAYAAABxlhP5AAAIoUlEQVR4Xu2aa6ilUxjHH6HI3ZG7xj0ybjE0zIeRe0Ic05hIatySLyjXLzNJck3CuJUkUcanaUrInPhAfJjIIJdmiBFCKcrd+vW8z+xnP3u9l32Oc/bRrF897b3X877rXe961n9dt0ihUCgUCoVCoVAoFAqFQqEwg4wlW5rs7WRLkm3d797ENsn2SnZGsvOT7drvLswStheN05xkZyfbtt+dxWK7r/TiOxNYOc+V7mWdMscmuzYmBrZLtijZo8mWJzsu2ZZ9VwzCdU+I3sO95DEMWyQ7L9kvydYm25DsH9F8x5NNSL84L6n8GPdQxsLw0B62iokBYn+iaHzvqb63tQfjKenF6QvRRt+Gj63Fdybwz+xa1qF5QHoP+bv6nPAXOEwQi0UFAvOSfSl630/JTqjSgWseTPauSwN6FgJBfl2gQTwv+oybq7THkn2c7Mpk31a++ZXPQ3nrBHl4sk8l79tc+VEG2wOjWA7SX0n2l2inaNAGaAsfJNvdpTfBqDNsIyduFt+ZgnJOqyDpzajA3USF2CRIpomrk+0T0i+UXvBWufS5omI5y6UZe4jmxfSjDV6cCiB/KiRysujIa52Ep0mQC0TLl/Ntrlwkg+2hTpAXi/pXSv8oShwernzLXHoTRZABKn1CmgX5s6j/q2QHuPRdREdBfL5yrhYV6mkuzSCAXV+qTZBNNAmS0bbOt7nj20NOkMxyXhb126zFQyeNb1101FAEGegiSF4cfxQZ4qKXxIcZBIrfL7g0YwfR5+SCHZkOQTKVYkqV881WDhJdo+0UHRnYdLhd2td/dbQJkun+D6J+1nQRno/vj+iooQgy0EWQK0TFiMB8o6gTJKK1qew8lw6sH68JaXU0CXJn6e1+5XZSc4LkupdE84u+HGxA0cAQw00yuLNru34LRRsqU7a9k+1ffffgZ5eYtTtTvv2SndR3RT2U49lkr0v+XY1FolPxc6JjCNoEaYLLxQRMLL49NOEFSZ3RYbKDSidUt0HUJsixZBdIL27H97sH4DnkybVsbHJ9jPVkBEm+MeZsljXSRZB1sEY04bFZ40EojEQWPDYAqMCuYuRljk72tej9l4lWBOVFCOR/b+XLCSsnSO4nH+75NdmZVRqNwAf/vuoa3slvcf+W7PtkR4hez33WYaxP9qrolrwPHB0Ya/C4VX6Y5Kd8TZgwqVcPDflyyS8RhqVNkDb7wf4rQXLt58mOcul0VmwQxY0jqBMkdf2aDNYD9ZbLh9+k8xzrHP1g4tt0FCTxv1R6164RjT3pFnOm9h5ivjGkDTBZQfLQN0Tv+0RUIBHWmBY8MyqsK00jJFhgugoSrGJzPsOCNDek22zgIZf2TJWGWOeLioMgMaugl7UyxhGT38MKEmyn2uqbfK5L9s2mK6bGKATJ9PaU6BBt8ORjnaCREyR1zfEL18e6BuKJ+REKcXC9HyQWisaf9PtdehQkz7tDtGynS/8zrXzPuTTgmpg2wGQFyfDOPYjx4OCDPUXFd32yj6QXRMwfkTQxCkHS4PFT9nhmymYVPjay6GzABOnTPFZGjmniNIhp7GQgH+r0kGQ3ijaKrnXaxigEWReLA0U7GvJa5tJzgqQzpFOkM8wxIZoPx2i2vuY3+fMcA9EwM0NkflYTBcnx24eSj6GVD2HHmB8Zfg8wWUHyMBohw3RkjqhQMeDFEPDvos+hMeXui4xCkAQHP6M/RwHe7q58FhQwQTJ65jZSqF/O7KwRf5bscem+fqyDaTWN7ztpXyMNw2wSJEcw62SwbeYEeZv04pqDkQk/U2OO3mz29qboRmMbXpCM1n8mO7Xvih5NMa9bF29iWEHSE3OI7JVPrzJWfbdduNWSP2vkPp5FQ25jFIIkrWv5wATZdj353pnsfelNiVbJ4NqyC6McIds2deaJrs/xd6FJkL4sCMnICdLiUCdI83OExwhI27I2n3vPiLUbjM6QwYbvTJPjKGggvhhzrDHmwwjyGNHNC0Y7Dz3ZO9V3mzo0NVDWDPRYCLmJmRIkZbX1HL0n/roOJdImSN4B0XgIIFNYaxzDMOo1pHW4+G8IPrD6HebYIxcnIBYI0eJh5ARpI3fdc7kfv42QjIr8XifaftvwIyQxHRcVGbOUxe46sJj7+rOYc09jzLsKkgbwnmjwo5CoIObT9p2KWin5KRzgp+doYxSC5N3wb5DBfyYBZWJ6xLELtAmSZ/AXwwh1kytfE7Nhl5UOwf4YkIuhrbNp6F1oEqQXP+3OyAnS0rg2B8srfPyTyNovvxk8GEQiMc5RkMRvRZWW23Qi5vGd7JgwpvfRRZCIkSGaSn5SdJj29lblA9vyZUcrN42iMuoCEJkOQdq2tvkYBVdL/yG39crMBHznw/dbpX9joIsgeVbsxIDGltsUyGFiZG3LhlmE/BklpyrKNkGC/XUOYfrpF2V4uvJZB9cGcSUe/MPHY3VNXnRAHDEZOUFyPf+f5vrcFJJnROHYtJMyx3voAFZJ7/2iIGFMdGZIuj89sPKxCxuhnWRjTmUjGLZ8N4pmyifrEh7op2s8DH+T0agN8kWQft4PzKmXymBDz8Hzr5Jer/eI6IExFUfZKSc9GBXNJ7+9j3Tsruq3NS4Ca+ejBJxp+Frp/0sgi287A7tFekGh7MwS5kjvHNJ6Xj45jMbnseBwr1/Q0zDoYetmER4T43T+MYCYx/ZwheTbA3XJpgX1u8Slj4vWWRRQEzR0ZlfrRc+FrV1QX+SFiGzTKsYd87FlMHhRVJjUGZAfMSaesbMiX/LnXRHKjqLXUw7EagPKXNH2x3XEknZJW6Ne1lTpGOtrRGoxp/wx5gi6S8ynBQq9XHQU5eyOyreKGjWUjbVE2zoRP0FfIO2dSA7elx1l7iVYBDE28P8rvNOhovElznyfTB0ZVkfUdd3o3IXJ1LXFmfum8g5gMYdhy1EoFAqFQqFQKBQKhUKhUCgUCoXO/AvP8OHr0w791AAAAABJRU5ErkJggg==>
[ds-image23]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAADY0lEQVR4Xu2YTahNURTHl1BEycdESD7LROSrZCSKxMBHyERRJBlQ3oCBN5CSkUShJBPKTEqSbibESPkoKZEIoRQzH+vX3vvdfdY7557zzn3uvYP9q3/vtNY+577933uvs/cRSSQSiUQikeg2i1UHbNAwTrVNdV7Vr1qiGplpMZg9qoteR1QzsumeAx9G2aCBPq8Q16fT/rrQh2+qv15//N9G3CBik+qnartqhI8tU70Td9931XIfD6xVfZVme+CaZ1yQ8s50ijwfxmdaNCF+V/VbtSWK03c8eBbFBtgqrsEUcQa3MvqR6rZqmolvluY/dyuKj1XdUZ2LYgHMZgAW2ESXyPOhyOgd4vI3JTtR6BN9JVcID21Ia6N/iMu/V82K4hNVT3yOGR/ARMw8G8ViaL/RBrtM7EOe0WHykO8zOWDSkWPAcqliNCaGpbUmijOqjC65eDSp3dzzNorFkKNNFeaIq4MTbCKH9apjUq8slRkdJg/5XSYH/Da5VTYRqGI0NRWTr0u2w0VGM6rPfeyQZF8U08WVGWZIFXgBX1PdV00yuQBLl5f0J9UGk6tKmdHBSJS3GsPk2mcTgSpGF7FOmjX6ssmNFjdA4Z9DvESuxo2GQDB8polj8kfJrrQ6lBlNuahidGH/6hrNzH4g7r5XMtgAYCbHRqMvkt2JDAVWQfxbPOegauVAi/r0rNFHpWnyXJMDZvRx1SnVDcmazb11zWaVvFTNE7cvZ+CGg541mjLAjoOaa8FkNvO0AUxdrXot7neIszevA88+I65kfVYtzaZr03NGs+dkk0+HAxg52V+PEbfnbrVXxiR2JFNtogLdmtFlL0MOcL9kmF6Gi1RvxC39GHYZj/01tZsDTisjWQnsyfNWQyu6WaPj7d1hkwPMJ9fW9g7o3FNxHbP1lWXzwl+H57ETmB0aGBrizObAU5Vu7zriA8tJkwNmMrm2Dix0jpnE3viSND8SBT30ucAJcc+zMz9ALcs7XRURTGaXk8duad/sMqMhHMExPD4HMNhXfG4QLGvq7X7VB3GN+EvtI0etDdzz+VaiLgcoH9zDS89+1Vroc1VOevC/Dyx5PuyVfB8YAD4q8Y7ZGcX5wERfcz8qdQIMYEmFmc91r38mrQL9mi+uT/3+2pbTRCKRSCQSiQ7wD/AqEjbYkNyRAAAAAElFTkSuQmCC>
[ds-image24]: <data:image/png;base64,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>
[ds-image25]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAE0AAAAcCAYAAAAk2zLiAAAB5UlEQVR4Xu2XzytEURTHv4pSRH6UJCmxUEpSbGSlycZGrNhZkOwUZWWvZC0bC/kLrGyw9A8opdiwVMpCCufbmdtcxxtmnjfdwf3Ut5nu98159327c989QCQSiUQikewYFq3YwR/QIJoT7YnWRN0f7aqiVbQu6rSGZUf0ltdr/vPUvyAlDP9BdCXqyY/VQCfFe+zmx0IyKbpD4fmpJ9GIf1ESY6JRUTs0rCxCqxUdQWvNG69DdCl6NuMh6BLNinKiNpQRmqMR2YU2CF1lSRPgajuE3qfaCBraErTOvajXeGQf6rdYIzBBQ3Oh3CJ5U92A+gPWCEzQ0E5RWmjT1ghMDC0FMbQU/OnQJqCH5XK1JWpGcYKG9t2LYBPqxxeBhztyPIqGjEcOoP6/OXL0Qf9WTdbwcIfbF9G48epFx9D7WFztUFQkNNdP8poLaOuRhN9GrRqPrcsNdHI+fu1idStNyaExLPaeyyg0r/zsh+5HXBkOrgL6FJt7NrzFcCFcQ2uROuiGzN/bht2v/VXdLOF82AtzfuxB3XNtQzNJ2o9T4/rHGWskwInloGEt4vtVxNql1P11cAM/w+f9KgtYuxJ1gzMlOsfXZ560sHYl6gaFD3QiWrBGBrjafwq3kVP8niV+7UjkH/MOhJqn0byC+dYAAAAASUVORK5CYII=>
[ds-image26]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAcCAYAAACqAXueAAADsklEQVR4Xu2ZTYhOURjH/0JRFkIkat7EFDaEUbKUkrJA+UyTlJKyUGRnbImSZiHR2Imt0iRulnaKjahhQVGkkHw//3nmeM993vt972veqfOrf2+dc+65/3Oe83XPCwQCgUAgEAgEAoF6zBbtFg2LroqGRNNiJXoD3yc99qM3fTrWio6JZtiMFOaJTtlEA9vLdrP97IeDovmxEobVohOi6Sb9o2hUNMekTwb0+BqdPjeht3xeFH0Q/RH9nviNkO5tC7SMX/5LrEQb1sF2PoAOBMIBz3e6Ojg5Y3A0XIEWtByHvpQmJht6vIfe97kRusIMQAObF+Aloq3QGXga2QHeK/oq2o/4IGfdEfTZX176OAtEz0SzbIawWfQD+uLJhh7vovd9+kTID7BPXoCvoT1Tz8azcGAinYptWYtFr6CjyLIT+sAOm5ECO/+caI3NSGApdP8oCj2y4U35LOKR0CP7qAoRmg3wCNpBtIPdDXLmxd7FQizMjPOIL4Fc75+KFnppWXDkDIremnTLctET0QWbkYHzaH26famsT3rk/p0FfdLjTJtRkAjNBni96L3ok2ibyUsNMGGDXec5/RS1vDJlYH00wZnls1L0Arq8lO001nkSnR4foppPNxjzfNYhQrMBToODneeTzGf3QTvM78DD6DxZF2UP9GW7vLQx0RlUr5NBsT6/o7pP1md9cukeQz2fjgj/J8BsgzuBJ35isaHfRNdFlxAPMo/dZWebgzODM4QGNkBfHjsAlIDPHUJ3fdLjO9Tz6ROh+wHuEz2HPpc4KNkQNshv1CrRY7Q7j0tjVbaLPkOP73U6jf44Sn2fvscmfNIj66jj0ydC9wN8H+qbn4qJvi9DOy4JftPxZdQ6k1eEJmdw1neu77MKU3EGc7XiqmW/Bnho/ued0/kW0ivk9dod6Mc1G18Gu7eRMaQsIzmwfNYg832WYaruwQwuT/f8ErEchbkWHYEer9NgPj8pltmMDNhxeafTsvslPfJTIA3nsyj0OIh8n3WI0HyA3ZbK7Yl3CTbvhkkbv/5ipUkd3hK9hN6gFL0sdx2XtpxWDTI9ph2kWmj7LMogdEDk+Ux6X1EiNBtgF1wO9tvQ/vB1EwnPurV8CJ33vI+gJ7Q+k56GC27eTKpy0UGPbLz1yVFcxWe3LjoYSN58DYjeQD3z94hoBTqvWlk303l/PQotz/MGn2c9c9tFx7cRHqhYJk1p5yksgv7tNAztzKYOGk3j+6THfvSmz0AgEAgEAoFAIDDl+As0+zHejIsuBQAAAABJRU5ErkJggg==>
[ds-image27]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADYAAAAdCAYAAADhLp8oAAACk0lEQVR4Xu2Xy8tNURjGH6HILZdcSshAYSATyphSYmKgMGJABiaKkvQZ+AcQJZKkDAwMXCYGLiUxZWhgIAMhysT9/Xn3ctZeZ++z9z59n+8r61dP55x1Oed91rv2u9aRMplMJlPPbNN+08VCvJ+ITDadkcd4yrTKNKk0IuGj6bF6xl6YDqlh0j9mqemJ6bI8xtumn6Yb8aCYDaZdKptgZb6ZdkZt48kc033TpaR9kzwpU5N2LTG9ThsLrsr7GDPeEMsr06K0w9heqEQwRtZSbppemhakHTVsNJ1UxeolzJM/J2yttmDsl+lo2mHsMW1OG+eanpu+mvbJt2Dgi2kk+tzEYtND0wUNNvdI/owMGpNyWm4MUegCbFG+j9c+jqg36Zm80qw23VPNhAGQBRaKwKvAfFdTsF7+LBHjJ3lNmGW6ZnoXjSvBjwRjiEpDBqfHgzqwXF5V0+Axxeqm7W3ZJt9FIc7vprtq2NLHVTaH1pRGdANzcWaCqbpMtoUqGMeI0d2lEQVTTOfUK+uU/C3y9DKR17VFX1dC5tgBlOlhdwBQ9dhFYWHIEuU/GDxYtP8FEz/UfxDzgIZJI+Wu1oyWsYXy7+H5nx+1U+goeBh+qqgeYOaKPJ1VnJUbu2OalvS1gWBY4WUarhIGdsjjOJZ2FGw1vZcXvD/MND1Q/QHNQCaQ8hlJXxOj+YxhCGN9h3ABi/7ZtC408HxxCBN8FStNb+WHaRfCFkyzM2xV5ADG2IG0owAffReJcAfjNh8fzmzTD/LCwsQ2jNUBTSzcODB3PemjiPB9lSWfKw6T3sivLoj3J9T+x2Esr1Qs+mF5obglXxguAsRNYmpZIU81E86b9pZ6Jw7ssPB/jCw2/h/LZDKZ/5vfQqeR5LaNrwwAAAAASUVORK5CYII=>
[ds-image28]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABgAAAAfCAYAAAD9cg1AAAABkUlEQVR4Xu2VvytFYRjHH1nkRxIlmSgDJYMkxWaRLDIog0EZjAaD3T8gGaRkMCiDxWC7mEwsbPIjJYoJi4Hvt+ece5/znHO7x71nUO6nPnXf53nP+5z3Ofc9R6TKv2IIbpXhAi8uRQ3cgd/wDT4Y3038EO7Dm2DM+LqkoBPewR4XJ9vwAw66OG+KBZZdPAYnbgR6euGr6O44z3MFB3zQ0wbP4YhPgGnRu5zziYBL2OGDnnF4Cpt9QrS/LODbE5KDjT7o4Q76fBA0wTPRApyTRNJ1qQn7zwJJ/a+YsD2fPpEF7GtOtAD/KZnTDZ9ECxy4nIWtGxbd7SachLWRGUWYEF281EFagSewC7bCPdHXR0nWRBf/gqMuZ3mWaJ47vzfjRGx7jmBdNJ2HcS5mD1r47FpMLIZtz6rLWbhYsQKx090PZ0RfCRdSKLAbxPnwGvKzFS6SqkA9PJbCoknywPHgWdolZYFy+VWLysXvjO+sTA/mI5w14zH4YsYVw+/FLVyE8/AaLkVmZABP8FQgf1f5Y/wAenBiNK8B7uoAAAAASUVORK5CYII=>
[ds-image29]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAG4AAAAfCAYAAAAGJfifAAADH0lEQVR4Xu2Zz6tNURTHl1BEISIZPMmEGAj5EWXIgAzEwB/AyJBiwEQpKQz9GFDSK8VEKQY3AwNDZSQlhSJKoSg/vl/7rnfXWXe/e9897+zeuefuT33rtde+r332WmfttdcRyWQymUwmk6kp66BrJbVTms8c6IR0P/tUdAZaIIm4Cv2FvkFvjThG/YZeQDfb47+MbZ80Hwb2Z+k8s90j3Ysv0ANoHHoNfWyPP5FEjlsGvYS2Q7OcTRdKx1oWQU+h79BmZ2sixyTsw2UpOoF/0zG00bmeP9AtP1gV56G7flDCorigD9AaZyN8mBa00I03DT77K2iHN0jINhrcMfhC7PeDVbAEegbt9QYJC+aCWhJ3zmlJGE014ij0CJrvDRKCvpfjeLwkyUi7oOcS0qVHo4mL8zCl3oFOeUMDuSchu3hsmmRWikHHrfSDVUCHrfeDbTSaDniDBMdtlLjDm8YmiRcXzEh0mGalGHzbfN2QFKbGlkx+vmVCQGuajGWlGUEXVZsF1QwN7F7F24ygaXIU7mhlsGky2T1tUGw0pYik2dBhaLUbHyZSpUlW97ehDRKKGtUKaK6ZF8VGU5WRxHL6CvROhv/ibq8BVWYlVun6f63YtYld8AvYaKoSOm4PdFaG33EtSXO+3YDuS6fPeR16A52UPtWpP3RTwKgq4zgG1EGJX4QttHPeIHP5v/umIkOKNMm1bJPQ0CbLJdwhxyZm9MA2U3nopqCs43SzLnmDg3ade65oKsAIZu9R5w7STNDfVJkmPewPH/GDFjrrUFtsYemi2GfT8bUTs6fPdB3HKNSojPFQOnN7teRs54NimorBN4EO0r04LmE+G8gXzXi/t3tQHkto5kdhhTcuncVPplg0zpNi5RPTUunOzWUdx6LmE7TbGxy0cx7nb3E2Dx3yVXrPZXP5p3Tvidcq/YGBz+/3JCYPA9N/kakMPrT/aOh1AVqsP2hT1nHDCJ/f70lMHl4H2NyuFaPkuLIwbfMDQK2g435AW70h8x8eQTynaxPYLAb4ed+eC+8LMzJEi6baOC4zdcYkFI6ZTCaTyYwU/wBp0OvRxxE+BQAAAABJRU5ErkJggg==>
[ds-image30]: <data:image/png;base64,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>
[ds-image31]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>
[ds-image32]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAfCAYAAADTNyzSAAABCElEQVR4Xu3RMUtCYRTG8RMlBNUgSeDg4hJNDlFLgdDm0hfwC0g4ObsKToaCS4SfoDVoaGtrD4KmJAqKiiDasv7nnlc7XK/VKvjAD/G558j7XkVmmeXfWUcb1zjDDoroYsnNRaljgB4KqOINnzh2c1EquMRqrN/CR/gcJYcbsV+Mp4wrZHypg1/Y9GVIBydYGBZ6kXOxhfSwDFnBBWq+zOJWbCGeDTxj15e/LTTE+tFxNIs4DQ98tvGa0EfZF3vXqfB9D09iw/q/jGUOB3jBo9grPhRbuHNzY9H7rGFefs6vx/0zeTyILZRizxKjd9JhXdLliWmhj3exBXUvdszENHGUYNkPTXu+Aa03OKImIX7yAAAAAElFTkSuQmCC>
[ds-image33]: <data:image/png;base64,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>
[ds-image34]: <data:image/png;base64,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>
[ds-image35]: <data:image/png;base64,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>
[ds-image36]: <data:image/png;base64,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>
[ds-image37]: <data:image/png;base64,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>
[ds-image38]: <data:image/png;base64,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>
[ds-image39]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABUAAAAZCAYAAADe1WXtAAAAkElEQVR4XmNgGAWjYBSQDZqAOBZdkFKQDsRr0AUpBTpAfB5dkFLAAsRzgJgRXYIa4BYQG6ALUgpygPgtEEfBBLyB+C4QP6IQ/wLi/wxQIALEAUAcQgEGpYKXQHyHgYrgFBDPAGJOdAlyQQYQ86MLUgLEgPgcuiClwAOID6ELUgpagXgSuiClQAGIJdEFhwYAAEVpJCmFYRwXAAAAAElFTkSuQmCC>
[tdf-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADQAAAAfCAYAAACoE+4eAAADeklEQVR4Xu2YS6iNURTHl6IIIfIIiQEpRl4pI6EUBiiKiQxMmDBQRtfAkERRUpREYqgUgyMDrxERkVzyyAATFPJYv7vOPva37v6eZ3LT+dW/e+639v32XnutvdY+V6RHj/+aYarNquHeUJF1qlH+YRNGq9a0xeembFI98A9rMEJ1Trpbg6xVfVbdVr1SPcmaK7NU9V61wRtq8kh1Ssy5RrxTXVOtV/1Q/cmaKzFZbCGnpXm6BdiQr2LRbgQOHFIdbn/+nTVXok8sygvc8yZwhthgNoiNqsV81TfVEm+owW6xjeg21WKmiKX+HdU4ZyuE88O5meYNFRmjaomdnTlZU1dQLc+ofqpWOFsh+1U3pHlVIcIfpbt35LFLLPKssZCFqm2qY6qXbZ1Q7RSLWJ0+sFFsUt5VBru+THVR9Vr1VDUpMyILkSFCl73Bc0TshW9Uv1Sf2r+js6qRnZHlHBBzaK83JNgnVkWPixWPVap7qonxoAhSmFS+7w15UAgoCKRNU1pSLc+JznfV8ugZbeKDal70LCacT0p4JUg7drco7CxkrtgNIkVLbMJF7rmHxTHXebEKBpy5xZ0Rg6ntEL2HSfIaIWfppFiz3KOakTUP0JJqDjEHcwXRs+h7RdRyiHNyVfJvBVw7uH5QOvnM+FS1aUk1hwAnYqdQapMCtRwizR5LvkNbxPI7dH5Sj3ThZ0xLqjtEr6Mw3BUrRmXFpJZDlGde+MIb2nD92Rr9zmJSDpGOvIfynSJcYxjjmzcRp9fkwXiaPiqFnWESGqKHfPedn4qY6jXhPal0hLAoxsRnlTS+pJodPfMQdaKTWmOGkD5MklokVYh+EXoT+iJWFT30EqKZih6ECF1xzw6KlfsiKOt5a8wwVnVLbHBqkexM/BJ2lm6d6lfTVf1izW9C1tSBv3so1shDM+fiuTIelCBU4dJLb+jA3MFSi2RnYkdJCxaSKu88uyDlhYHokX5ovLOlCAWhX2zTBjFL9UzsEIa859qfgq4fFhdShn6UR5i80feXHPqkZJPCzRUnSJ/nqpmZEf/gDOEUu8r461L+nWS12JnY7g0NCBlxVNLncgDyEId2iDW4sq+3/KMDx2m+U50tBalHFDkbRc2yCjhS+h7KJIfsrVjTzPW8DWlUJddjiCIllhtGN3C++YfLkIBo3pTyDcsDR2gFPXoMFf4CdyjHIspqldUAAAAASUVORK5CYII=>
[tdf-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAsAAAAgCAYAAADEx4LTAAAAyElEQVR4Xu3QMQ4BQRTG8RFRERqJSCRUEo3EAXQKiVJE4wRahdYFXEBFp3cRBaVGJwqR0CjwfzOzk2fjABL7Jb9ivn27szPGJPnz5DHBFkd0kfqY8Kli5w3QwA0jPRRlgwNqqntgpdYhd6yR9uscXpiHCRV5cEEPGd+NkQ0TKgvjXtDksF8j23ewxMm4YTlDSc3YoT4qqpPrGuKMuurtXT4x1SUpY4+iLmVItmzpkrQxi3X2y9dY1zTufwuxPkS2FXKg6OqS/HLeqO8hszKLbi8AAAAASUVORK5CYII=>
[tdf-image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAfCAYAAAA4AJfRAAAA30lEQVR4XmNgGAWjYBRgBbpAfAmIXwHxfygOBmJfIN6BJPYLiOcDsTpEGwQYA/FXJEUg/A9K3wDihUD8EUnuLxAzgnUiAVcGhIJHQOzEgFAkBMSHkeRBalEAyAUwyXI0ORAQAeKrDBB5kEtB6uGAkGYeID7AgFADChM4GLyaKfKzBwMiCkHxz4ksiawZhEFxWwPEaUB8B0kclFgw4hlZczsDRON7JDGQptlALAPTgAxwOVsSiAWQ+FgBLs1EAbI0swKxOBC7MyA0NzNAnAuKW7xAAYgnAvEsLNgToWwwAQBxvV0xyRBntwAAAABJRU5ErkJggg==>
[tdf-image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA0AAAAfCAYAAAA89UfsAAAA2UlEQVR4Xu2SMQ4BQRSGn4hEIRoSEkToJDqdGyiUWr0LEAdwBI1LqCQqUYkodETjAhqVQhR8LzMrs7NbaiT7JV82+977Z2dnVyQh4afk8YJzLHu9Ho4w5RareMAhvnCJWdsr4gmvWLI1aeAemzjBN24xFwxATbxQH1diVl6LCWnYJ5iJ8MQ7tv0GzPxCQNzWFL3Xw4igDQ3FrahPrvhFpYMPe3Up4MarfdHT80P6Xca4cGohMmK2NxUzrA7wiHVnLsJOTPBmPWMrNBGDrq7v0BXzl6TD7YR/4gM2wyJK+8BxYgAAAABJRU5ErkJggg==>
[tdf-image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAeCAYAAADzXER0AAAA0ElEQVR4XmNgGAWjYEgAAyD+TyTuheoBAzMgfg/EK4F4FhCfAeLnQDwXiA8A8Wsgng+VA2EJsC4g4Icq6IPyGRkgCluhfBC9FCqOAfyBeCIQs0L50kB8F4j9oPytQFwFZeMFLEC8nAHiEh6o2Dcg9oUpwAd0GCB+hzkZBEABlI7ExwkagPgfELsgiYE0E+Xs60D8gAHibxgAaV7DAPESTiDMgN2JIJeAxCPQxFGAPhA/AWJFNHGQa/4yIEIfKwDFozi6IBAIMEBchTWeR8HwBwC2Ky3l227XQgAAAABJRU5ErkJggg==>
[tdf-image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>
[tdf-image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAE8AAAAfCAYAAACmupBxAAADwElEQVR4Xu2ZXahNQRTHl1DE9Z2PkHslJfKRKOHNA4nkI4qH++hBUYrkxYsXpSRPKCRFKckD4eHGA1GifJQoJJIuUZTkY/3MHmfOurPP3uecfUr2+dW/zp6ZPTN7Zs2aNXNE2rRp8x8xSnXaJpaEDtUpm5iX4aprql02o0QsVC2ziVkMVB1VXVUNNXll44O4QczNVtVn1XybUUIwoOviVmImXarXqiOqfiavjCxX/RBnUJk8UfVIe7mGbFP9Um2yGRYK7beJJQf39UV1XjXA5FXB4K2wiSVnjOqR6oVqYnVWBWKbXtUMmxFhmGqn6r7qleqKNO8jl6jWR7TZPK/xLzQI0QT9vqDqDNJ5nhQ8e7A2rO67uD5G4cXnqnE2I8LDRBvEDfZ21ZaqEvUxXpzV51Wj+DDM18P30n/S90i6AeDKKM9ERlklOda1OB9wVtU/eWZz6VEd9AXqgM4eUt0I0ujoiSQPjgd5zXJAqi2Xb9ihehqkxWBsGLzUUwcFUjMDKPdRtVLcjMFi1ZC/JfIzWdyETUmeiaduS2WGqXN38rsVrFXdU02zGYbCBm+RVC+hW+LioSKg7jdSCdBxJfSraLDqjarHks/HFzZ4WJv1Qc34oRCW7F3VyOSZQWzF4HWLayfL4jyFDR5wYD6peieVwcuz0dSC9wnSQ39Hn4oePOq+KfGdNY3MwZsjrtIOm5HA0vwpfa1sguqluHjIMldcfh4IdzhT0w8P/i4czBi0wW6fBXVwWrhsM8SFLLVuUOgH353qf5l5BiHtY30FbBYhS1XfTBrMEleWkGasyYtBHeGSBRw6AWpsYsC3Qb+y2lgn7pxKfzCEEapBye9nqsGVon3A4mgj9QBBRV9VC2xGAo18kuoKZouLlbh5sFCOBrHWWrMKhEeUPWzSea/W+74NlFYG5qneq46J28n9O17WIEJ8KNYrGZsLFaUFgmwUe8XNHqcK7rr4zQdz4rAQdhAPUibLb2FtWB67bQirIZbu8W0wwGltYFEXxV3s+uXPZPuBu6OanqTHmKp6KzkuTOholo/BQlnafJiP82pxRtI/LAQHHmuX5ZUF76W1QR/xo2Ff+Y01dkq8zZDVkuHvPPiY3PdXOeE6P9dlYhNgma1oo0vc/SannKyT1x8TvyTOlEebvEZghouciBi0cc4mFsQ+cb5ypklPBXPGn3Fr0gwsB/xMKyzC49vAulsBcWzd48C2jpOs688PAx/W7PVRFr6NegLevDDp3MDk8etV0Klu1QOTXhYYMAaukcuONm3a/Lv8Bp0dxrloSO1FAAAAAElFTkSuQmCC>
[tdf-image8]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEMAAAAdCAYAAADxTtH0AAABZklEQVR4Xu2ZsUoDQRCGJ2AgRcCggRCwsbALWAiWVpaxS5cH8A0UbC3txCpdep8gVd4gFnmG9BZ2kjhzu8fODieb3F089zIf/BzsHH9m/kuO3B6AoihKfjZMd6JWB9qoObgZv7yq4BnVt2qJWh1ooE7BzHeJWvpln0e5sGdOUA9gmiubkC/VPuQi56/C6IFp9hPMV/XKLxci9Q75Vh7GLbjf69oeQ01vA/muwPcO+VYeBoc+q6wwJOQd8tUwGBoGQ8NgaBgMDYORK4wb1CSnjuF3NAxGlGHsCw2DoWEwtgmDGh7a4y5EEUYTdYEaoWbgniNewDTXcacm0EDp88a9qHHItwe+d+p7DdnelYdBVyodLktTd2rCG5ihvlGvosYhXx5clqR35WHk5cmqTKIM4wj1jhrLQkGiDGOAWqDOZaEg0YVBN0b680Z7s7R/WSbBMP7bhvAZqisXC7DThjC/+x78q4KD5gfoGaDT2ePfWwAAAABJRU5ErkJggg==>
[tdf-image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFUAAAAfCAYAAACWNoFQAAAD9UlEQVR4Xu2YXagNURTHl1CEJCJFNxIhJEkK6fpIiQc8IB48iAdPbj7zcEpehXwluteDFF4klJd740XcEpEiD+QjSUpRvq3/WbPu7Flnz5yZc2bq3tv86l/nzFoz++y191przyEqKSkpKSnpA6xmvYkRGMLq8NigbtaUwG8O65nHZ3lgb5RW1iuqfS70hDUrdG2aG1Q7hk8dJPMaWL3LwxLWbda/QD9ZV1mnA/tg1nbWBdaHwOd18H0Xa2jgN5l1KrDB5x7rKGtCYG+UBSS/RceGnrLOsSqskT2ezYP54Ll3KBwL363U9og1tXqnh3msbySON0l2p48jJD5rrMFhPusza4A1NAkWCWMjuFjAItlNYeB8zCAJKOwvWC1RszCeojtsRNRcBTv2ConPPmNTsDgoAVoS8kQnedAacmYMyRwwVlfUVINusi/WANwHIbgIsmUF6w/FB1WD7rPlAcb9S83X6Hq4WXvC2CzI2NgdPZxkVeKCOpp1n6SQxw2GoD9kjbWGHMDvw7hvqfkaXY8dFAZqrbFZUgf1K0knd9nJus5aSeJzMWqu3o/ivs5czwvUUIybVO/zAH2gnWSsNLU7MagAgYIRWx8poEwkKcqLg+vwuUTRRqRB15NA3mDHYNyiSoti6yk2SxL4PYlB1aILaXefy3pMYeNBWbADIrjnWYOC73mjWdTbUt9dgF/G1oMbdQ2qbTwaVBzGxwXXZlIx3V5BCiIVi0590E4yvzSpv4gkmPBHcL24ZzMNpG08o0js2sy04xeJpn69o9Q21mV7MSNZUr9CYbzw2YtbdBFUPHRLxCPswrqS2vGLRMtS0lEKtfwWa6M1ZESPUhgzCWTpcxLflyR9x4sbVByZEFC7WsNI7Bh8KauTagOfJ+6pJKmeoolikpOsISM6/3r1FI0Zfji3rze2CO6hF0Lz8aH2HySTyQJ20gZKf0rQ1Id8oEluIvm/YpWxTWO1Ufp6j8aDcbqodjO5TCfxO0NS/hKBM97ZccMnkgbkww161o6v9x6jdP8NIGPgjzcpH/tJdgt2qTZOgB2L04LOJQ1oPPD3vdgABBBZiY13KPheF/f9/zDFT7pe0JPQoLqnBwvGxW5Gir0n8f8eXFNhxx8PbL5A2KyLo4XCZ14j8cU/a3assyTv97A/qN6ZEg0qVj2pfuHBByg+6Em8I9lZH0kyw4ce29IKZWhh9c4Q7KKTJDv8t7G5IFj2eT7hd8MXi5Vp3jgD7mEtswZDhZr7DxNBu0vJC5cnRb+F9QoqVJuuRYHa2m0v9jdQh/Ha20g9zgrSFA0R6td0svZSxrrUIFtJFrDFGvobmynlUaRJsGitrNnWUFJSUlLSd/gPWfRCMIZcs9MAAAAASUVORK5CYII=>
[tdf-image10]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAeCAYAAAAl+Z4RAAABAklEQVR4Xu3SsUoDQRDG8U/QQhREExALEcEUQYu0oo9gIVhaWOYBLGx9Bks72xBSpxAR0qVObLQRxEYULCwEjf7HOcLsck8g98GPcDuT4W53pSpV/mUO8I4HnKCBD4ywEfr2MBue/zKDb9xgJayf4QcdzBVrXZUMOMIztrL1Jl7xiV35H4+TDrKOe5xn65Y6xvK3aGMHm7FhAddFw3IsFFnErbzew11SJWt4lDfYPuSJA8xpUlU6oCxxgP3ac5L4jWWZR19ev8pq02zjRekJ2JEdyo/Pdn2CAZZwEfqmacnvwVPhC0P5vtiwS/lbvCm9VElq2JffwPxbbZDVV7P1KlX0C0+7Mb7jcV+jAAAAAElFTkSuQmCC>
[tdf-image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEkAAAAfCAYAAACrpOA2AAAD/ElEQVR4Xu2ZX6hMXxTHl1Dkf0RCIiURiYhISeKBBxSFJ4n8SSjKE8WDBymJkhIlJR6kX/3Cw5SSPEn+JQqJJJSikD/rc/dZzZk155w559y519B86ts0e++z79lrr73W2nNF2rRp8w/RU7VbtcN3tAADVGd9Y3fTQ7VHdV01yPW1CrNUi3xjI9arXjbQXdUh1YTomTRWqt6opvuOFuODBGPlZrHqhOq86qvqs+qc6lRMT1W/Ih1W9e54sp53qr2+sQW5prohJbx9kuq96rTvkHCMNqh+SjDU5truDnqpHqlG+I4WZInqhySvIxOOEwaY5jsiRqpeSBjDZxyMeFw1w7W3MtskrGWN70ijj+o/CQ8NcX0GxvskYcwz1zdK9VzSn21F2FBCyyUJp6Aho1WvJBgAr0hirVTj0gXXR7bgKP5NDFM9kLC5bHJDbJEYIIm+qv8l9H+U+uy1L+rLA5swW0LWfKw6KOGFOwvvSDBmzu3R9/41I2rBe/Ci76p5ri8RMhKLxP2SWC0h0CHSfBwWTWbMYyTGUmh+U02RsDkc8zuqobFxRSHpkDSILxwj5sRgryU7kVgc5pRkwiTEGDtKSSLtD7QHHBbQqY+ywEBnJJQZc6K2ZRLmf6uaGLUVYbjqvoQ51rk+Kmva08IH2N9vWIXjargcg49KWHRcg6tDEzEj+YznwfUrEv4Ongf9VDtVM6PvRbET4EsPrh83o74schtpl4SBxKTC5brkNxIxgIBv3klsOyLl4xFGwTjMhYfGPcZqPjY/i1xGsuDFQI7L+NruXOQ1EnAVwDhmKPMCsmtRlkp1Dh9TlkftZK8schnJ0iADCXS4f1GKGAkYT/AmCdgi8eai2FGjdvMFsAVkHCCLXEbaJNUXZWfKgGEx8BffEcPKBwyJkQxbKO8Rh83bKqFUSMOerUhtquc0cCostR+T9PukzZHW30Fnj5rBfY950sA49J+UanXLJfmihCJ2XNQG8ZoMb0vDaruKVI3EnFzIeZZ5Ocb0Wzb14EGJDkKAm6taJdX4wOfGqK0M5pFpsOjLUltGHJCw277u8iVJGhiG2zy1nXkci8Y4lBlmpKsSDO+xbEuAJ9DXEL+oJqkMdg9KuwPxEvckeAYvjwjYC6W+juH7FgkFZ6PshBFuSXVejhbpn5hnm5/2u5Edy4pkV+ZNg99lbkv2HYjFU1Vb/ZWHFb4hgfi8cajvfFscy4CZ8ajZjJF01y4Dhudn4K6AGIjnEUvTvL/LIMPN940l4ZpxxTc2if0SfkWd7Nq7BQIp6uwZJ448iT67Au6KxK0/wlgJAZn/mPiAXISpqgW+sUlwjCkT0n6n7xbIZA+lPrW3AlZHlblZtGnTpvn8BhvEChbOcUzBAAAAAElFTkSuQmCC>
[tdf-image12]: <data:image/png;base64,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>
[tdf-image13]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHIAAAAfCAYAAAA7t5n5AAAFK0lEQVR4Xu2aSahdRRCGS4yixJEIIaig4kAcSESSoAiKiANGBYcgRLJwo2BWgcSYRUhQV+4EBxxIXIgoDogEXYg+FFR0oQkRxYFEEUVERUHB2fpSp7yden2me0+G9+gPft6jq8+553R1VVf3vSKFQqFQKMwYHlYtj41jcoJqSWws7HuOVT2qOiwaJuBb1UWxcaZzueqrDnpXtVl1puqQPVfmmae6WrUgGsYA5+FE7jkk61Q/qpZGw0xmsdhgoU9U/6p2JW3oicSGXlCdxMUZdor1+U51VrD15VbVn7FxAIjyN1WvVf/POq4Vc8KGaKg4W/WeWJ/vg835VUYOJzLH5WTVZ6pno2EgrlL9o7orGmYD7si2lztd9Y3YIA+5djnzVR+r3lcdH2xDslrsffk7q+jqSHhAbEYzs4fmFrHnuC8aBuYCsQwyFdpnPH0cSdqk79OqOcE2CRRSW8TufV2wDQ3RTtT/EA3OJaoPVStUh1ZtVHEUDF+oHpT6YuFA0seRi8T67ladWLUtFCuOKCJ2qM6r2tdX7c+Ivf8dqsfEqmH+pmORDi73a+NI1U2q58TG917pXjEzaZ4Se49pUNJ+qnpdrMOqSlRxV6rWqv4WKxbOqa45WOjjSAaLvlSVF1dtbGVoQ6QsUhe8LfbObsOpDLiP0XYZbTFwHk7E4ayVdeAEDgl+FtsX3i6WkpkcfPY1VT+yxaUymmwRCrtpjjxNLBJ5GGYIHX5SnZ/04cZuY53pAlsEHwQXg3NK0ifH3dKvchzHkYjrHF93Ukc6T4r1Z+CBcXlIdYWM9qb+DNtUR1RtOZgYFFwUXike0dyDIgbnvqE6Ku2U4J83NzZOiV30UdUhDkr6QWuCLQdphwqOB+ZlebkbVJ9L/RYAmDCkLVJgV8Z1ZDpZujhyKrSnEFn0oW8djAV9NoV2xz+HyfCqNFel/s51jpbfZO+04/iL5mw5GNStsVE5Riy/3yz5k5ZlYi/RZ8Pbx5FkHfr+IntPli6ObHISn93WZ5Pk7+88LqMJ84HYnrSOVkdiJCo5pE1ZWdna1gDg5s+rzo2GCvZwbAHuF1v0HWYsUUxR0Yc+jvT1kKO7dLLsa0cynoxr0x7T78Ey1zYGjY4kQjCyFqalOe1bamw5SF9E3dHRkEAh9YdYBtiqulNs/XhF+kUj9HGkD1ZMW5M6kmWjqQ/RTxYg6urwZ0NtY9DoyCWST5190yqOPzw2NsBWp26WdqGLI3kmDp3pd2OwwaSOZOyYlHURR0FJsZQWWBGvRL+Mhgzu9Cws2JOm1QNBmyNJ3xvF+hD1ODUyqSMp7r6ulNtrU+y9KPlnZCLfJpahujqSnUPWkZ4+UXxR2rioS1rdX7C/YkNNmb5N7Pleqtpc2B4R20php2K+jIsDFECs16zbvnZfL1bVMomZ3D6RuW9uw882gAOFpqzle/V064ET3xFbs5eLTQQie5lYvZD7Jib9rGmQa6fEXj7SZ9uxv/AobBOz/C0xB6SFVUq6NrmITCIjtqNcVAFnrNjJbHUwmcgKHr1/iU0aHEoAsXZjx5n8H4MKPPqZYFkoUvxoLoVFummmFYwLVb9Lc+bCMfPExvIMyRcrtB0XGxP8q6yuBzP/wyxrerjCCKpXBjlWxUNxqlg09j70pwJrSxeFEaTvl8W+XB4aovkemX582gkW9pJW+0Exw3lqbn2bBIolfrPDNqr3vanc0lP+Qjf4FiO3X50EKlW+gRnr1w0UP02n+YV6dslwv3jDeWyl6irvQqFQKAzPfzRyfcXKkHvQAAAAAElFTkSuQmCC>
[tdf-image14]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAeCAYAAAAVdY8wAAAAlUlEQVR4XmNgGAXDDGgB8S4g/g/Ef4H4CRBvAWIBZEWMQPwLiL8CcQwQsyJLIoNgBogic3QJZKDIALFmCgPEZKyABYjXMEDctRSIZ6FhuEYeID4AVQhi4wTICgmCOQwQhRzoEugAFBTTGSBu5EMSB3nSBYkPBsxA/IoBEpaPoOwvDKga4QCkWBKI7YFYjgFPoI8C8gAAS4sa3MpGkmoAAAAASUVORK5CYII=>
[tdf-image15]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA0AAAAhCAYAAAAChSExAAAAqElEQVR4XmNgGAWjYOCBJxD/JwKnwzSYAfF7IF4IxLOAeAGUvwvKh+FKIOYGaRAD4otA3ADiQIEOEN8HYlMkMRQQCcQTgZgVSQzkhCdALIMkhhewAPEaIN7DAHUKMUAJiJ8D8Rx0CXzAjwEtlAgBRiCeAsT/gNgFTQ4nEAHiq0D8Fog10eRwggYGiNNAAQEKEKIAKHJBmnLQJfABSSC2AmJmdIlRMEIAAO1aItc5UAhHAAAAAElFTkSuQmCC>
[tdf-image16]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA0AAAAgCAYAAADJ2fKUAAAA8UlEQVR4XmNgGAWjgCZAHYgnAvENIN4CxFpAzAzErMiKkEENEP8D4nlArA/EqUB8H4i3A3ERkjo4AJn0H4grgZgRSbwcKh6NJAYHGUB8CoiF0cSNgfgrEJuiiTPIAvFtIM5BlwCCIAaITSLIgtxAvAcqIYgsAQQ8QHyAASKHAiSB+CE2CSBQAuLnQPwbXQKfJk8GiPhdBkhA2QGxEEgCn6ZWBoj4GiDWBOITQCwNkuAA4q1QSWRgBsTvoeJVDJAgXw7ELDAFfkD8lwHiBFAcuQLxEwaEC0CaQIHlAdOADEBOFWOAJBsYABkkjiY2CoYrAACpMCy1od5BjgAAAABJRU5ErkJggg==>
[tdf-image17]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADYAAAAfCAYAAACs5j4jAAADaklEQVR4Xu2XS6iNURTHl1CEJK+EbqREykCUUsqjDJCYKDPySEYGlNEdMEdKyWMgkQyJYnDKxGMg5RXJI48yUYpCHutn3e3ss87+9vnO67rl/Oo/OHuv7ztr7b32WvsT6dHjv2WO6oIfbJK1qtF+8F/Sp7qv2uQnmmSk6qxqjJ/IsVX1OtJz1YoaCwMnb0utLWI3Un84XnVDdVw1ws21wgPVCbEgS7FO7IFzqq+qX6rzUu/MZNVBVUXMBp1SbVANq5r9gd+HVc9UM91cq6xXfZYWdn+e6q2Ywx9VC2qn/zJWLLgzbjxmqdgi7fETbcAZuya2c1PcXJajA3ojFtxj1dQaC2OxWLqyEClYEBaGwP1Otgv+4NctsVRvSNiFLapjUk21zZFNAJuK2DMp9os9i12nYaFYsO+qZW4uyWzVQ9VCqaZR0Vk7KbazRVxRfRJ7VzfYKeYbC9gQDuYd1QSp5nLqrDF/V7UxGvOQyizSJD+RYK7qiOqJ6rJqvmq45CsfO8WOXfITKQ6J7USAFAzp2B+Nc65wuuh8wRexMp9qATE4/1N1Wmx3t6teqK6q9kZ2HrLrvdgCZ8HwkWqRGydYf9ZCgcmBPc/m2CVmN9GN4wMlnQJVRKgH2GVZpbon9akTnzVSc5zqpjQuCo3yn75Gf8POQ4oz7n2JKR3YAbHm7EtzfNYIkPR7Ko2LQqPA6G3YpBwjG5jzBSumVGCjxKoYlSZFfNbo+OQ1BSRHLjDOHecPG39GgsOpnYwpFdgMsYpU1BNCQwzpGBeYInKBTVO9ErPx7wpFgYqXo1RgOEDZzG19uEnwIl9gUuB0KrUhDow7akwoVvhD2nO7mF5jYYR3oELYBc5YDoKmUZftTTTniqRvJiH1fWBLxBaPcfyhQKUuBxAqJyldA/1jpWqb2Iv49NgxMFbUGNdI+bsfC0CTJs1TcBn4IdX3rRazDztJYDjNf6ZgQbCrazt9qndSLQpBuYsl40UFxoPDubscwexWfVN9ECv9y8X84uZO0LnvrpCyLNCgEippUQEJkJZ8fnCFChAMBSseiwmF46Wkz1/XIShWnjTrJP1Svoh1BXaCtLooxSnVLLPEziJf5mXOetcIn/KdOgsERE8tKkqDBqu6T8wZCkO70LxpC0MC0rBfdd2NNwsBcVnv0WOo8xv1GNmc3dChjwAAAABJRU5ErkJggg==>
[tdf-image18]: <data:image/png;base64,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>
[tdf-image19]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADoAAAAfCAYAAAC22t6tAAABOUlEQVR4Xu2WsU4CQRCGhyiJRgolhsRoZYehIzZgZ0WBWlhobWFj4SPwCjYWRAp7H4GCWisLaxNjQmEsqSzwnxwHy4geQyiGc77kK25n73L/3e3cEjmO4zhzZxXuwowspIk9+Ag7MDdeWny24QVswf7ADqUwaMg9edB0oQpah6eBZ+L4BG4OZ9ti6qA1Gi3ov7yMTzBGYtACfIaNYKwEX+F+MKahCm9hcwp5HnfO+Ks5hFnSkxj0HN7Q+MX5rb3DnWDMOolBJcvwAbbhmqhZRh2Ut1BdeCcLxlEHPSLbTec3VEH51/ECP2FR1DSswK0ZXafZUAU9gF/wCW6ImnVUQRsUTeZmxE3JOnl4TNFv6Y2ie/+A14Mx3vRPJH4qV7JglDLs0c9NTSzv9CbCa6QCl2TBcRzHcZz/yzcadFdjAEXdxgAAAABJRU5ErkJggg==>
[tdf-image20]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAD8AAAAfCAYAAABQ8xXpAAACA0lEQVR4Xu2XTShEURTHj1CKfERKKSWUKEoUsZEFCxIWlIWdEivFdjb2WErJQja2dspkh5UiSx+JFcpCPhL/f3ceM8eM98HMaLq/+jXNPW+ae97cc84bEYvFYrFkBMWwE9bCPBXLaOrgO7yGd/AFzsVckaEw8TNYE3mfBSfgG8yNrGUk7fAZnugA6INhWKDW/wWsy1XYpQM+WBBz3MNqnbTAB9ikA/+FZTEJBIE3b1tM8hsqRpg0Y0M6QPrhSJSj6v0gLPu8Ojn0wj1YpAMeKIGHYhJcVzFSISY2rwOsBwbcnHQ+kCRYj7twXAc8wOQuxD35z1g5PIIhZwE0iumWrVFrqYTdmWOpUgdc8J38GFyS2BHAX/dK/H/5X8L9nMNuMTfDC76T1+TALbgD81XMKzPw8g/kXOZm98XMbjd+nXw1vBEzcoJSJbGNMqhP8AB2wGxxp0zMfE+UoJN8wmkyIKlpbG40i6l7L0k78KTyxHL/PL0aZ9TFzc25c7ewXsVSBet7Ea7pgEecHy/REx7Xmec3+A/oVcys5MxMBw1iNsiJE4RSMaXCR1xNKGJcQvJ1ZNj40sE03JTfff+wmGYZ/aDUBu/lhwnGJsHkuYF04EyauDXpA5bOFDyOvLKMHuFp9EUadkOvnTUZcNPsNYU6EJBZuCLm/0KPpC8vi8VisVgsKeYD2a2CwOuQfyYAAAAASUVORK5CYII=>
[tdf-image21]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEkAAAAfCAYAAACrpOA2AAAD20lEQVR4Xu2YW6hWVRSFh6iQKeSlC2HeUowgKSiDSAzBpB6MqCRB6MHQIgy6B9VDPUQQmGQJIqgURKQGQYlEPRwMtCzEhwQpoRJJVFQIFSq0xufci73+9e//UP4HFM4eMDjnrLHW3HPNNedcex+pRYsWLVq0aNGiRYsWLS473GS+ax4wvzBHmlebo/NJwxmvmefNTeat5gpzh/mP+Vw2b9jiSXOPOakYP2OeNecW48MOU8yfzVWloMii/YqSG0qMVxzIiFK4RKCtXGuOK4UEgkMwbi8Fxfg2c1Qp9IE/FHa3m1cU2qXAVoU/8P5Cu4Cx5teKCRMKjagy/kQx3i9uNI+Yr5RCH/hK3a3i/wBf8AnfunC9+ZsiGCVY8Lc5rxT6xELzr+rnUIBs7CcrqRKqZUA9ym2wIL2putRuNr81J2c6/WS6+aCaXxGuMxeb89Wps6GmTeX2mkofPWn0EDaE/0vMpYrn5c8p7bEmx1XmIvMe80/zgU65RjqFMkh3mqdUl8Qy82PVvQln1lVjj5ifmmMqDcyuNJx/VVH3SSetOYAcOMoF8Z7C3lp1NnXs7a405nxoLjc3mL+YHynWzEwL1G3vnWoc33nd+V6xr9/NE4pE6AkieE6xGMfuNQ8rMowgEXH61n1pQfX7LkUfI3D5Dcg71nHVm7xGcRCcNGgqtdPmo9nftyj6JbjDPGa+ZM5Q+Jae16vUyODS3icKn7DDbT690vZqkFJLYOFT5kmFMxjgZKcpgkdGcWJ5Kr+sSNGVioY5pxonWz5XbAQQGDLuRdVBKxskVy+lzGEwZ5a5pdJwfCDTsf+CeXel36D6WQnY+1Hd9h4271L4/XqabPyqyLT/BOqbB+S1yybLMUC2EcB0dZItgCziiidb2Njbin6QAtR08ly7ZNIhRca+odgYIOP4CijLMyFdAjmwh09N9ghGeRkN2o8uFvSfnxTBA5Rcek2Yq3hDJ91zXKkINJuCZOrjlfaQejdqPofOqFunFMkqAk4gyHIOhMzCHmtKsGZAMT+VMq3iG3OiYv3UarxvUI6bVTfi21SfFClOBuUnT+Bo4pQlm2YuDpH6gD5DA00gOJws9gnOUXNBpj1mrlcEhN7EswjMM5We+lZCskfZv6/O/kMJfqBYg82yt100nlXcDPQpHvBdp3whaPsUOgFbo/rkuEG+NFers8cdVNjaaP5gPl+Ns0H6JZtG32k+rQggGqXEWn7m9ii50h46gSVzeLX5zHxLsZ7/eHCjDymIOD2Mb7AmpG+hspeBpnE2QP8j29h8ifS8Jq2XD73sld9prB/0ZmvRokWLywX/AtvYv7ESclJMAAAAAElFTkSuQmCC>
[tdf-image22]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABUAAAAfCAYAAAAIjIbwAAABRklEQVR4Xu2Ur0sEQRzFn6igUTgQ0XBGk10Og2gwaBCjZrtFEIPFYDWaRBAMVv8Fg2bRIqholCtnEPHHe/fd4Xa+3uxuFfYDn3LvzezM7NwCNf+WTfpc4gM9oYt0sDuqhHl6RM/oB32np/Q45w39ybyiTQ2swgx9gz2gH030Jr+LozQHsAGzPnCE3rQPPCP0ElYec5lnDdZb9oFnir7AymWswHrbPvDorX6j2qRhpTs+8Kigot58GbuosNJx2D1UUdeqiAa9hXV1W5K06CesuOUyT7476rIIbUMllTWoiH1Yt/Dsh+gFrKQj0FGkUKZLr+61yyLyZ6TJ9ZAUG7DeF111WYTOsMrWw99T34Fhl/0hbP2RTsZRF32RlmCdPRRMOEDn6Dptwwbcw7an34KH9DXLF7JxSSboE3pvMWWHnsMWUFNT049fCEFe1A68p94AAAAASUVORK5CYII=>
[tdf-image23]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABvElEQVR4Xu2UzysFURTHj7AQvZKyUo/YvNhZyM6CopBi4dfCzp6SJDs7KyvJgpWSP0D+ArFSSokFyYIoCwvKj+/3nZl5d+7MvDsjdu9bn6a533vPnTnnnitS0X+pCoyACTAG2kF1aEYK5cASeAQP4A68gG9wC4ZKU5PFLxkGr+ADLFveAHgSDcr3RNWCVdGJl6AQtgP1gDcwbhu+GGhDNNAVyIftkGrAPrgAzZZXFPPDQNyRO7s0Izp/1DYaPOMetFlekrpF1+zZxqRnrNtGGcUGqwNH4B30moZDPHuRYKzYMzgDjabh0IpoMD4D+YncMQcdYjUPJaYA/g4L5qBDLBKLFSkYk85gzEEa+Tnmmk7LK35RlmBsp0/RNZF26gdfYN4a7xDNZ58x1gRORQMdG+OBOOFctD2YWO62KHpTsOz0OMZ22xYNxDbKc3GcZqXURl3gBLR6HjfjHbYlGohHqMXzYsVdOfEGrEn4IPKreA7pb4J6wysrTmQhuIi/xOeURNuMG/CqyixeAkw6f5dioDmw671nkn+jXIv+Pp+s/qA5Ka1YYQYzORDN869kBnJW0yUWgQWZFm2niv5APzhlYlu0e/VEAAAAAElFTkSuQmCC>
[tdf-image24]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAcCAYAAACqAXueAAADwUlEQVR4Xu2ZW6hNQRjH/0IRRY5IlJNb4YVcTskjSsqDS24n7byQlAd1JC+O8oIoiZKcTp6UV6WTOHn0pkiJwgPhQQrJ/fs3e+xZ3zEz66y91t5bza/+7ZpZe+3/zDeXb2YDiUQikUgkEolEojyWiw7qwgB9olm6UDFG1C+6Irok6hV1ZZ7oDCaKtsF4pNdFMN47DeuTHtmvQZ/nRL/r+lX/HHYfcFgneo3s859FK9yHHCaLhkR3nTKas79Jg+OdunbySnRYNNYpWyP6ANMGtqXdLEXc5wh6RKtF02ECGwrwbJiRswFmBh5FOMA7RV9Eu1U5O2tY9FO0PlvVFjj6b8MMPs0hmIHMwd1O6PEi4j692E4PBVgTC/BVNGa7Zg9M+QACy0uL4OC+JZqgK4S1ou8wbW0n9PgYcZ9eqgjwIBoB1qZoyP5WnuVvvui0aIqu+AcbRcdF43SFB+YQbAdXJs0WGJ+bdYUHtvOkLvQwB/H8xcLnXiLu00sVAV4pei/6qCsw+gBPEl2H2c+nqToLV4LtoreiTaouBINiB+IZNJZAm0M8Es2ol8WghxrMvhhigegh8ucg9MjZ6/q0uD69VBFgH+xA7iVFvmsDPVeVs2PfoPheeQSNzrO6J+p2nhkNNZiBzZnlslj0THQC+YNrYRu1zx/I6bOVAd4BkxDwiFVk/+UAeYpGkPkOJhmxWROC79gF02G2876J9iGbseaF72M72T9b62XLRC9Ex1DsnaSwz1YFmEFhcPg7QUMRLoueiBbCjGpuBc2wV/RVdE10HtlZ0sxxjjOYM5nBfofig5rwe65P16P16aUVAWaCdAfmeMQZ1wzs8LMwKwE7jvt9UdhxekVZgmzncRAVhfnAJ5h3NBNc+nN90uMDZH16qTrAHF08pHOZsjBx6EKxRpc5gy/Av3f3wLQxbzs1Zc1gegydx61Pr8cqA8zZxoyRmaPLftFN5D/OWMrcg7lN3IC/DfRGj7ywWaXqQpS9B9NjqK+tT6/HqgJslxae+TQDolO6MEIVWfQgzLHNB+v57nm6IkAN5WbR9MCLjJhPr8cqAmyDS2NcorX4Xc7ivNjg3tcVdWooFmReqfoSqW7Rc5hbubwrTWywuUHOCz3aRCrkc4RHBpZ30QfQ+COBn9zbeHvi3kDxxTPrdbyTHoLZF3jo5jv4/NS/T5uliQmVmwS4Cu0pmiovOtgu+ulH9p6Xqw4Hk7sdxKCHGuLbBber0Vx08DkGN+YzEYCDtxed/3eh9UmPXCE71WcikUgkEolEIvEf8Qd7+R75vYCemQAAAABJRU5ErkJggg==>
[tdf-image25]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABgAAAAfCAYAAAD9cg1AAAABkUlEQVR4Xu2VvytFYRjHH1nkRxIlmSgDJYMkxWaRLDIog0EZjAaD3T8gGaRkMCiDxWC7mEwsbPIjJYoJi4Hvt+ece5/znHO7x71nUO6nPnXf53nP+5z3Ofc9R6TKv2IIbpXhAi8uRQ3cgd/wDT4Y3038EO7Dm2DM+LqkoBPewR4XJ9vwAw66OG+KBZZdPAYnbgR6euGr6O44z3MFB3zQ0wbP4YhPgGnRu5zziYBL2OGDnnF4Cpt9QrS/LODbE5KDjT7o4Q76fBA0wTPRApyTRNJ1qQn7zwJJ/a+YsD2fPpEF7GtOtAD/KZnTDZ9ECxy4nIWtGxbd7SachLWRGUWYEF281EFagSewC7bCPdHXR0nWRBf/gqMuZ3mWaJ47vzfjRGx7jmBdNJ2HcS5mD1r47FpMLIZtz6rLWbhYsQKx090PZ0RfCRdSKLAbxPnwGvKzFS6SqkA9PJbCoknywPHgWdolZYFy+VWLysXvjO+sTA/mI5w14zH4YsYVw+/FLVyE8/AaLkVmZABP8FQgf1f5Y/wAenBiNK8B7uoAAAAASUVORK5CYII=>
[tdf-image26]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAG4AAAAfCAYAAAAGJfifAAADH0lEQVR4Xu2Zz6tNURTHl1BEISIZPMmEGAj5EWXIgAzEwB/AyJBiwEQpKQz9GFDSK8VEKQY3AwNDZSQlhSJKoSg/vl/7rnfXWXe/e9897+zeuefuT33rtde+r332WmfttdcRyWQymUwmk6kp66BrJbVTms8c6IR0P/tUdAZaIIm4Cv2FvkFvjThG/YZeQDfb47+MbZ80Hwb2Z+k8s90j3Ysv0ANoHHoNfWyPP5FEjlsGvYS2Q7OcTRdKx1oWQU+h79BmZ2sixyTsw2UpOoF/0zG00bmeP9AtP1gV56G7flDCorigD9AaZyN8mBa00I03DT77K2iHN0jINhrcMfhC7PeDVbAEegbt9QYJC+aCWhJ3zmlJGE014ij0CJrvDRKCvpfjeLwkyUi7oOcS0qVHo4mL8zCl3oFOeUMDuSchu3hsmmRWikHHrfSDVUCHrfeDbTSaDniDBMdtlLjDm8YmiRcXzEh0mGalGHzbfN2QFKbGlkx+vmVCQGuajGWlGUEXVZsF1QwN7F7F24ygaXIU7mhlsGky2T1tUGw0pYik2dBhaLUbHyZSpUlW97ehDRKKGtUKaK6ZF8VGU5WRxHL6CvROhv/ibq8BVWYlVun6f63YtYld8AvYaKoSOm4PdFaG33EtSXO+3YDuS6fPeR16A52UPtWpP3RTwKgq4zgG1EGJX4QttHPeIHP5v/umIkOKNMm1bJPQ0CbLJdwhxyZm9MA2U3nopqCs43SzLnmDg3ade65oKsAIZu9R5w7STNDfVJkmPewPH/GDFjrrUFtsYemi2GfT8bUTs6fPdB3HKNSojPFQOnN7teRs54NimorBN4EO0r04LmE+G8gXzXi/t3tQHkto5kdhhTcuncVPplg0zpNi5RPTUunOzWUdx6LmE7TbGxy0cx7nb3E2Dx3yVXrPZXP5p3Tvidcq/YGBz+/3JCYPA9N/kakMPrT/aOh1AVqsP2hT1nHDCJ/f70lMHl4H2NyuFaPkuLIwbfMDQK2g435AW70h8x8eQTynaxPYLAb4ed+eC+8LMzJEi6baOC4zdcYkFI6ZTCaTyYwU/wBp0OvRxxE+BQAAAABJRU5ErkJggg==>
[tdf-image27]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEIAAAAcCAYAAADV0GlvAAACsElEQVR4Xu2XT6hNURTGP6GIgSJSL14ywYT8S8/gyr+BGPgTYvAyVoqQlwHKiFKSgUSMkCEpo5ehmSIlCgMGBlLIf77vrnPe3Wfdc+4797yro+xffb1aa79z9/72OnvtA0QikUgkUpXJ1HbqInWJOkmNy4z4t9DcdvlgDhOp82itaVkSy2URdYAa7+LvqfvUVBevgxvUR+o39Sv5ey0zIssS6h11JIjJgFuw/10RxJvI2QuwivDsh/3oOp+ogQasYrVpc6hX6GzEadiCpZCtSewqXMXPoJ5Qk8JgwmrqO3XUJ2pmNkY3Qrk8IzYnsWG4Sk8fuiEMJqTubfGJAmTmKWqxT+TQBzuPqlDGiAHqA/XZxffA1nTZxZuTvwtLnkH2FdH58JiaGcQ6oVIbpN66uGc+9Yg66xMlKWNEHpqfXgmtdb3LNdGAtIxS/aD6gzHdoOdpN1RRIQuo59QJdDi5S9CNEZpLgzoOm9M36nA4wLMbtvjQjH1o7yRl2Ul9orYFsZfUMVR/Zko3RkyhXlNvqJ/UdWphZkSAXPtCXaHOIWuG+m/V3VNFaBdkynJYK9NvjZVujAjRK/kMtq62LqmJaYLhJOXYQ7TMOJTEq7AJ1v+1G70wQVQ1QqyBdcK2DdatS3eFPFbCylta6nJlqLMitPtqldNdXC1zGLbBI9cCvas3YQvNYwJ1G9aCtJBuqPOMWEV9hS32qcuFRgyFCT1MpVKE8mqH83yiA9r1OrvGQbRea7/Js6gXSU53ihH04aJg3sT6Yf+ky4eqowwyYRDF1/JemDGaEWFF3CvItd2PNJn0y8yfpA9gp+xcFy8iNeFvXKimwQxowO4BWow+DXbA5qedTo1N16SWuTaJCbXSOzCDNgbxDHrQXrQ+w3t1sNWNDstwTarIsZ5RkUgkEvlv+QOC8qaehAdfHgAAAABJRU5ErkJggg==>
[tdf-image28]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAIYAAAAdCAYAAABv2BQRAAAElElEQVR4Xu2ZXchlUxjH/0KNiBlqhtDMxVDIVz6mSBgfU4oUNYoL5YLcTUKjuZnkxoWEKClfN0ymFDck8zYXEleKSKOQCA2Zosb38+vZT3ud9e6z9zl79unsi/Wrf+85a+39nvXx38961tpSoVAoFAqFQqFQKIyVi03354UZx5puND1l2m26tCobG0eZLjE9L28rbR5jO5s42fRQXphB/86Wz0Gf/h1nest0fF4RHG063/SK6T/TykTtJJeZDlR6zPSp/B6+X51ct2wY2L2m3+TGeLn6TDvpw1jZIDcEbf09q0thUp8w/Wt617RLdf8uSq5r4wH53J2QVwD/nErEj7QZg2jys7zhx1RlOHSP/L5fTZdX5cskngSMgUECPr8vb+fYuF6r52GaMRj75+TXMBdEDtho+lI+R+dVZdNgnhiHqcYIqFxRuzF46qLxfA5uTspXkvJlQVg9aDonrzDWmT5Wx2AsmYfVboxntHoOAqLhH/L+N4GJdpreM72tgYxxpTxc8cO3JuV3qjbGC0l5FzvygimcJF/qZiUM/KhWr7lnmL5T/ZSNkS5jfCavZynPudB0SF7fxBbTV/LoH+N0xMZoggF+UX7fP6YbJqtbIXxuV/skEf7fkZtjVu5VbdQPsjrW1b+ysrHRZYyYeK7LOc30jaYb40P5GMBCjYED6QD3se7lT2gXP5juVrM5TjXtN72aV3SwUb7WhjlYh8m87zH9qXpgxkqXMSIH6TJGPqZ8J/ciB4PBjcEPnGK6Rr60MNgPan5TAFGDZInJSjuSmmLqdqoFttD83zDH36afTDdp9YC1sVY+2H3Vhy5jRJ+6jJFP+FWaTEoHNwYT9ZrpW/nywTb3XM034ClXmH5RnWGTB5Agkq+Eu/twliYjB2IwTkwvamGN6SV5P/uItbwPizDGetMnyXcY3Bg518rXbe7lzKAv7L+/Vr8lKSW2YmTe5CYY4WlNGqR1MJbM0MYg7yNh3VR9DxZujA3yp4N7iSB9GcIYGIEEi/ZsmqzSBabP5e28JasbE13GmCfHIPoSKbanF1UMZozN8jML8ouU9F7UB3ICcoCd8tNUIk8fc5CzMHCcZTSBcfareVDHQpcx2nYlsR2PeUjPmLrU9P9mMsZheT1PHQ0IjtQYQ+YYd6ilkxWcuzySFzYw1hwjzjGazox4NxS7RDjddHuD7jJ9UV3HZ8qaDgRnMkZMPD9MA4J0KZnXGEPvSuIJaTr8CbgGc8zCGHclcfL5hupXE0FEzGknn0E6341LCYUka/eZvpdfyF8yejrGUxMQ3qm7TvUkMmlxtEpHtlXls7CoA67b5LkOxkojG9Fnt8Z5wMWyyZjz5PJSjPFkgpkb5gGDBum7kl3yl6AQSXfbuxL+D79DlIhchM+UTRgk3DlNPF0BJmDtpvxH0+vyvIDvH8m3rPOwqCNxjIbhOGOhbRj6Tfl5C4O2tb50NBCB87FPRaKYkr5d3Wd6Uv6q4oDa365GwtmktuV3Jkg+cdmz8ryAtSlcOyaIdkQljPG43BB9Etoxc6Z8GaZ/WzTOeSgUCoVCoVAoFAqFGfkfaX6XGQJHr9YAAAAASUVORK5CYII=>
[tdf-image29]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADYAAAAdCAYAAADhLp8oAAACk0lEQVR4Xu2Xy8tNURjGH6HILZdcSshAYSATyphSYmKgMGJABiaKkvQZ+AcQJZKkDAwMXCYGLiUxZWhgIAMhysT9/Xn3ctZeZ++z9z59n+8r61dP55x1Oed91rv2u9aRMplMJlPPbNN+08VCvJ+ITDadkcd4yrTKNKk0IuGj6bF6xl6YDqlh0j9mqemJ6bI8xtumn6Yb8aCYDaZdKptgZb6ZdkZt48kc033TpaR9kzwpU5N2LTG9ThsLrsr7GDPeEMsr06K0w9heqEQwRtZSbppemhakHTVsNJ1UxeolzJM/J2yttmDsl+lo2mHsMW1OG+eanpu+mvbJt2Dgi2kk+tzEYtND0wUNNvdI/owMGpNyWm4MUegCbFG+j9c+jqg36Zm80qw23VPNhAGQBRaKwKvAfFdTsF7+LBHjJ3lNmGW6ZnoXjSvBjwRjiEpDBqfHgzqwXF5V0+Axxeqm7W3ZJt9FIc7vprtq2NLHVTaH1pRGdANzcWaCqbpMtoUqGMeI0d2lEQVTTOfUK+uU/C3y9DKR17VFX1dC5tgBlOlhdwBQ9dhFYWHIEuU/GDxYtP8FEz/UfxDzgIZJI+Wu1oyWsYXy7+H5nx+1U+goeBh+qqgeYOaKPJ1VnJUbu2OalvS1gWBY4WUarhIGdsjjOJZ2FGw1vZcXvD/MND1Q/QHNQCaQ8hlJXxOj+YxhCGN9h3ABi/7ZtC408HxxCBN8FStNb+WHaRfCFkyzM2xV5ADG2IG0owAffReJcAfjNh8fzmzTD/LCwsQ2jNUBTSzcODB3PemjiPB9lSWfKw6T3sivLoj3J9T+x2Esr1Qs+mF5obglXxguAsRNYmpZIU81E86b9pZ6Jw7ssPB/jCw2/h/LZDKZ/5vfQqeR5LaNrwwAAAAASUVORK5CYII=>
[tdf-image30]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFEAAAAbCAYAAAAETGM8AAABJ0lEQVR4Xu2WMUuCURSGj1AgGFQUhJu2BYKE0CA41ZBDU6N/wMHBrV/Q2B9wcRX39kCItmjRqSFx1a0x6z2cK1wOnyZOfvg+8MDHey/fcDj3nitCCCGEEEIIIWSHuIYFmIky/S7DqsuJQ4vTggP4CztwP6w1QzaHtyEjCdzDD5iHX/AbVsLak1gR1YeQrUKLfyb2r008kRR2/A18hcfwUaxYb/DQ7fmB9SgjEVl4FL5HYkXUox2jXTmE5y4nCei9N4UXLm/AF3jgcuLQe0i78FmsO+O8K+vdhzvPqSQPjyJ8hyWXL6MGP+F4Q3swJylFJ6oW8c7lOrUvXUaWsCdWRD26izdiAbYXG8h69MUKOYMTsSGTujfbNqDPnSuxOzAeMIQQQsi//AHe0zcDJNkz0gAAAABJRU5ErkJggg==>
[tdf-image31]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAGwAAAAcCAYAAACERFoMAAAEOUlEQVR4Xu2ZXahNQRTHl1DkK5KPIjdfhUL5ingRSuLBR4gkEkkURT4eUFIoJRGJ8CClPOBFHsSLeCJSolwpSZJCvlm/O3s6s2fvOXfv49zOLfOrf7dm7bPPrFlr1qw5VyQSiUQikUgkEmmNrqrFqhOq06p9qg6pJ9oHPVVrxcwRDU6bG85T1UZVP1VHz1YN/MAf1n+lqn/anGaMaotkv+CD6qaquzfeKN6rFkg6kZaqfqlOqjo5443iTwF9Tp5lvsz7kWpEMgYzVN/EPNvZGW8B54+L2WE+m1S/VbN8QwNgfszT3/V2/gRzlGdrBATjglQqgKtrYpJre/LsUNUbMYF5nIxBD9XdZHyCM95CX9UTVRffoExX/VDt8A0NgGAc8wcTVohxbr5vaAAH/IEEEo6AnZPKriEYBNjddUBFu52MZ3waqGpWzfENykIxH6IMFYGg71eN9w05DBJTr4uCc8yT8u2zS4zDmWwMMEx1yB8MMFe12x8M0E3MmvlQBdhVnG9DnHGCSACpYted8d6qB2LWfpwz3gKLfEOM8bCkSyPnF1uVA7QITGy1mG1ejeGqh6ojvqEKVALm+F21WSrnLYFnIcjevLKeBwt7UdXHNzjgyxLVW9U8zxaCz+RVqtli1mSybwjg7rxcn/gieyBa/VQ1Oc+Ugfd9lGy2Udaeq/ZKzmFaAA5of57npfrCV4OgkZBu1rtJV4+zGz/vqXr5Bg8CvUzMJsGv+6qRqSc8losJkrsYayTbORaF7o0sWeSMvVTtlNrfyecof+4c34nJYL8ZKQLZe0b1TCpBo9EiWNPsQ/8Ic+OdrUFyvFJ9Sv6uElMJcsHZr6qzqqOSXhC6m1p2A7DD2GkEb5KYOl7Lwlr2iCmJB1WXJT3PWt+Nb+xcyiqtNQlQtHS1hm00ynSvJCVJjU8ELoM9EF2HR4vZknYxtiXjtUD9J2toZ2tZUAsLyzvsjuVdM6UyR2xFmyMf3s15yuE/0bP9C1PF3KdoIsrAfK6K8SuTPLTKTDSPKWLKWpkOzKVeO8w2RqFM5f340Cym6y1DW+0we11i0UOwAymZrK1/TNDO89mUTzxEaXHvAC7cxK+ovohZ8DLU8wzjwObgDgXDtsGvxXSNRWnLM8zeYUMBY20vSbiK2YBlfKLL4sUhsOMAt/Ki1LtLtBfJ0BysnaCVKT9t2SXay3woYP1VL6TyDOvssjUZz/hEK4khbwGbxLyULCz6O511OORsrUHbK+GSakt3mV9k6MDuqAb4BqlP0JhLtYC5O4xGyk1u17bBGW+BRaMT3CfZSxoOueWiNVxHqzFcyl+cKYs0FmslXVLHikmqW8kzRWiri7MLO6ZawICzkh/YT4n5L4SF77M//gZ/eGeLrpTKv1dC2dxImE+Tar2033+vWEgKzut1viEHEpDGA39oAjm/iiZfJBKJRCKRSOS/5i+CsAWDy5I5uwAAAABJRU5ErkJggg==>
[tdf-image32]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAdCAYAAABIWle8AAAAgklEQVR4XmNgGAWjYBQgQBMQG6ALYgGMQBwCxJnoEsggAYifALEJmjgyABmUAMTPgdgMVQoVgBTmMOBWSLRBMADSUAzErxlQNZBsEAxgMzCBgQyDkAHM0L9AbIUmRzKgmmHIXjVnoMCLVAszbAbBxBMYSDAwgYGKiZaq2WkUjAIoAAD6FR4oHD9AQgAAAABJRU5ErkJggg==>
[tdf-image33]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAADqElEQVR4Xu2YW4hOURTHl1CUiBSKDDHl8kBuuTyJSeTFJUSapEhTiiKeZsgL8oCQRLwpnqQk8eXRE0XKpZAIIYXcWT/722Z9++wzc8wczZT9q3/TrH05+/zPPnut84kkEolEIpFIdDWTVZvCYDeENfYKgwE9VHtVx1UHVQ2q3jU9WlmuGhDE6DtbtTqIe/qqlombv0VVL+6aUd6oflb1o/q3Yjt0Aw5Idp0VVT/Tx4IBF1Tng/gg1VVxmynEz231QbVSsuZNUD1RbVb1NPFZqreqyyb2B57IdNVgcYvvjkbPkOw6UZ7RC1QvVBPDBnExdnfId9Vzcff/RXVT1b+mhwPTD6suiXugIU3iNkIuLLoi3dNoi18nyjMaI1+rxoUNykCJj43FYvCg76guqvoEbTBH9TUMWjprNBfdpZoUNkQYrjoSBgtSxOjT4u5jd9gg7tqnJHscVCR/Pssw1WNxx0pD0AZLxF07l84azcIbxb1+bTFGdUu1P2woSBGjN0jrOVtX2yRbVfODGFQkfz4LG4rd7OffZ9oYz/l828QydNZoD4a/E/dkLbzGD1TNkp/5i1DEaJiqeiWthnxTvVQttJ0MrPmp6oy4BIgYd0yyZzH3yAPzc/v5r0n2wWYoy2hYIe7VWmpij1Q7pDZLd4SiRsNYqTUDcazEIBna9QIJjzFHJVtKYvYqcQb7uUmi66SdeyzTaGBHs0swfZpqm2TPxY5Q1GjeoLuqLVK7s9FI089DaRauj/qZ/p9VM02cfmtVn1Qnq32sqKtzKdtoWKR6L263hDfRUYoYTRlIPXul+j9l2iFpNYJzNG+sxVcQjPElIffBpqGE85tnvOpGtZ9XLmUb3VU7ep44E2K1MvD1xz1uNzGSI/kjrJimiDsCvSdcj3mZn+vEoOZnDGOjlGl0V57RPolZI0No32n+59wm1mxiwAb5KK7N181npW0jOcvPiRsbpSyju7rqWCzuHvaEDQba7e8XJ8QlMsZafE2MmqoxHgrHCcdKHvQZHQY9ZRiNyY2S/1qVYXZ7Ro8SV6bdk3jS47r3VSNMjLcg9hXJpzZ+kFT50AH/xpDwYvdQp3oo2Srl95cOyWOj6pm4SfhLaURb7DMzhjf5X3ywYGi4TrRe4uvkyCIBXzcxoB5ukWwZR5zEOdTEuB/m4JggqXswF5Pxiblsjc3D4Jo85P8KzPc/k/J3rsR3IWBsvTjz/M+e7eWTIao14vqT7BlfRsJPJBKJRCKR+Et+ATyUA9d7rBcEAAAAAElFTkSuQmCC>
[tdf-image34]: <data:image/png;base64,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>
[tdf-image35]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAJQAAAAfCAYAAAABS+TPAAAFb0lEQVR4Xu2bXchlUxjHH6F8y1eTqNdICeOjhIiaRHHhIxSFUpNobiaETEmSOwm5kEiSRC5cIEU6RRJuKCVSL5ELoYSSz+f3rvdp1n7etfZe55y1z9nTu3/1b2b2s88+/73Os9Z69lp7REZGRkZGRjY9+6t2+4Mjm5aDVM+q9vGBEkimR1Rv+8DIpuZc1fUyZVJx8j2q71QnudghqgdUT8+gnap9pQ6jj+Xxu+paf7CNi1R/qG73AeUS1b+qfyQk3Lfr4u//ret71cuq11Q/R8efkXqYD67rfeDN+yBmXvryEbdJ7AP17WORPKf6UrXiAynocRPVR6qjmqE1npDQUEyJMVskNNLnqqOj44x2963H7o2Oz4v5uMMdx8fXEr4v9gF46dNH3CaxD9okxtqkpo9Fsk31i4QOsZ+LbeAt1Q+qE31AuVD1hWqrDyh3Smi8G31AOUX1lepsH5iREh8oRU0fUOIj1yY1fSyayySMzE9KRz31p+pVSWfew5K+AOfyGebWVCOdo/pMdawPzEiXD35EvKSo6QNKfOTapKaPRXOcanVd/D1Lbko4QvWBhJrBY8P7xxLO81wjYRplOp2XEh/cA15STKSOD8BLiY9cm9TysQys0zBKpdpgDU7KnUB9QE/ztRMwBf0loVjzvRXoiaf6gzNS4oMfEi8pavkAPJT4yLXJ3s5uCffIn0koYn9VnekDHTCi5WqFRWI+lu1lKD76hlGWe3zRBwx6vn9K68LmUi6cGtoXRewjN80silUZho++IV+oEZnekzDVTWS6uT0e3pdJ7GPZ6ztD8dE3llDf+IBxhUyfUPHw3sZhql0SVoj7qB+mnWZOUN3gD1ai1Mf9EtrjQdm4I7E3UD2h4sdjaq8cK6pPVbeqDpCwysqeUC28j7YakO2OdyUsRj7vYjXAS4kP7p8dCc55Q8Jn7pJ0AT9UOhOKtZGJlCeUr1tS0MAM/fHaFutILKAeaCfNiffRVrdcrbpAwtZIHwmFly4f3DfbMLaPx44EOxO5Nauh0plQx0t+lTxFyTRDXcZSRLy2xUiY+8zJEnrqNFNAiY8YplwaoS2hZvEBJU+8LDU8Ls2p3z6XWgP0nKXa4Q8mYLTjRy8pMTiXa07zQGa/IyNskkMlbAozUnXB1GVDddvwbl+aSijfeFtlzybzjy7WRomPmJKEin2c5mI5rE1KfRg2ZTMNb2+GNnC+hN0MvDGytbFdwjU/VB3eDG3gUgnXnEj5DHWbhM+wn5mlrXcdqbpKdZ3qbtlzY/zJvznO01b8Skaq5+USyoZQYihH7AOlfKDcqyElCRX7YF8uR+zD2sT7oE3aOE/C91GgpxZKY6ztUNdMYj8499o1SnGPnMsSwBYXy0Ep05Yva7RlHE9FdjM5TaSZ4Wa0JKFoTPbGmCL/drGYEh8o19NKEir2kWsP8N+Z0sROTsDI8Y7qBdXBLpaC81+X4M1q0hzHSHhBkjchuop9yh17a6LrXGA2e0/1k4SN7iwM9W0F5bSkkid1zNMWm5eShDLwkd1amBM60FOqR6XeA8qi2CbhFRameKb6LDbt0ENLMrULewKLezlDcVtmU0t94g9WpDShzEfO5zzQtn40oF3Yzhg6jJLUZSW12doNPib1HmG53kPS/PJXVC9Jesi270d9YQnFHlSu08Q+cufMCte7RZpvmtqbnF311hC4ScJuwJU+kMPWRfjhu4rEEkikNyW8Bnu56n0Jc3aKmyUsgq74QCX4EakVrL75TXV644xAnz4soX2tNc2SzTJhYbrkAaIBax2rEv6zQg144mLEa3v6ouderDrDB5bAUHwMDZKIh4jOqS4FSUU2jowAHZ4BhmWbkZGRkZGRzc7/+VK4O3QL2zUAAAAASUVORK5CYII=>
[tm-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABYAAAAfCAYAAADjuz3zAAABeklEQVR4Xu2UvytGURjHH0URkiKLesumlEEGZcJgIbEoG8WfoEwG/gGZRCYxWJXxLYsfg8mPlEVYTQYT32/PPe99zrnvvefOup/6LOc8995zvs85V6TiX7EK34yvcMqrUGrwRvxaegY7TV2DOXgAT+AP/IWnsNUWgX64A+uiNfQILsCWtCzLMPwQfeALjvjTDbpEX34cjOeyl/gu+vInOOBVKOOicXEhUdwqVuC+pFtdNjUO1tRFn4kyBB/gKJyQ4qwPRXdWinl4C3thB7yU5llz/g4umrFCdkVX4mAELo5tM85cubNS+TKGRzgWjPNjYdauwaWYgfewLxi3WTOabngl2rxSbIlejvCQ26z5AW7/RbTBUdrhBdwIJxJs1kuijWMDowzCZzgZTiTwgvCiuDhsgwvZhOeSPasWHjceu2/JNjgXroIZF8GP8qLwmIUN9miD03BNdIv89a0nY5xrxqzoTydssEcNfkraFOc17DF1Fo7nNbiioiLGHyEOWWRG7jTXAAAAAElFTkSuQmCC>
[tm-image2]: <data:image/png;base64,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>
[map-image1]: <data:image/png;base64,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>
[map-image2]: <data:image/png;base64,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>
[map-image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADIAAAAfCAYAAAClDZ5ZAAADFElEQVR4Xu2YTahNURTH/0IRJVEGSEpiKF+RJKXIx8BHDMwMSEyJDJRM3pBCUjKQfGQiZSDtjMSUFEqJZyBkwIB8rP9bZ7937nrr3LPuvef1UvdX/153r333PWuvtdde5wF9+vzXTBNdFG2zhiCzRXfsYDdMF20Q7S7Ez1FmiG6LLosmG1sn7BKttYNRJoqeiv6KvojeiQaLz7dE80amuvDB6QDXmGVsFkZtiWiCNRRwnM+wyhrqWAx9gLuihcZ2AerMJ9EKYyuzX/RLtMkaDOtFz0UJ7aP9WPQQGuVa6Pke0U/R9+KzhZE6D3XmlWhBq3mY19DIeSm1WnRYdBO6DpXQ3pHNoj+i49ZgYR7+hi7K3WzHJOgB5Nz3xkbmiJ6JZlqDQ0LMEXIEOpd/K8n5f0801dg8TkLnc5cs+0Rn7WAFCXFHlkMzJaFiLsPPxRiRupzOMMQ5LcowHa+KdpjxKhLijjDCjPRn0VJjG4IPz8UeIBYNcgW+I/nH3B9ySIg7wk26Dp2/09iG8v0GArlXYoroPnxH6AB3jOckQkLcEZJTmn9b2FIYooeTrIOWVn7vo7FtL8bpbISEzhzJ67MUt5BznSHzyq0HD3KOBqNZ5mAxHiWhO0eSGR92hDkfgSnzEvodVizW9zJ5vSgJDTtSe9EUsLTmaHiletwcyakQrfu5//oKv/dhNRkXR9i7PIHe0LavsvD25yKn4bceZKXoB+KFI6EzR3LEz1kD4eVF4wn4B55jnMNb9RSqnSDsirkpdd1xJqEzR+gA5zOTRsEHpZE3+wFoY5hhC56bxDfwHS3DlpylkSW6Ct41+f2Gm8O1B0WHirG5I1NbyGuz9Feuv1f0DbroB9E10SOocxw/itEHuwqeN3fHCsrtjSeeA48c7RfQN8dKmDIbRQPQl6Jj0N0rRyjCGmh3zK6hSXIr756PsYI/GG15IrAQMRq8gJveoLbwjuHL1Xxr6JIz0JK/zBrGmkXQA8wHqCsQEfjOzlRvYq2O2QrthHn/9ALvul7/E9MzdOYt/C4gAh/+EuIVs0+fpvgHR8fRsTwiLQMAAAAASUVORK5CYII=>
[map-image4]: <data:image/png;base64,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>
[re-image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAdCAYAAADLnm6HAAAB/UlEQVR4Xu2Wv0scQRTHn0iiotHOaCRICgUrhWCMokEsUiSooFjZ+QdYWCS1hSTYKCkEo+ipiIWVgkkjYimkEn9EQgQVksJCsNAijfl+fbm7uXfr3u2xbkD8wIeD9/Zm3szszI5IuFTC2oCGyhRcCWionMCBgIZGByy2wagogks2GCVt8IcNRkUhXIAfbQLkwWH4GU7APrmFZWqB+7DGxNnRLFyHW/APvILnooWFQgGMwXETfyA66g0nVgoXRYvodeI581B0hJxay3vRjqhlRDTebxNBaYY7sNEmJNkJ5Tviwo4ZZ5E5wymeEZ1mL+rhL3hpE5JFAU/hSxs0NImOnr9B+SRaQJdNkHY4CddM3MLRc/2D8hh+h7uw3OSumRetjlumzuRc2AC3X1DiL2erTZBRWCXaOB/6DZ+kPKG8gK9sMAPVoiflBXxrcglK/v26W2gwmU4wLboFs6VM9DDil9Jrx6TxDB6KFrBncmzgwMQywZ3C9twl5WDjA/bkg3gfJHxBl03MD27XbdElcOGJOGRiKfCqdCapBTwXXcfXTswPnvfvYIVNgG+w0wYtPExYQAw2wJ/wjfuAD2w8/vHx8lj0/uhLPvwCT+EYXIWPUp64mTlJ79R1UzK8A3F4zeIfjiSLKbsNeNViAV9FP6f/BRbQY4NR0i16mNxzN/kLW9l3FNVwB6YAAAAASUVORK5CYII=>
[re-image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAeCAYAAADzXER0AAAA0ElEQVR4XmNgGAWjYEgAAyD+TyTuheoBAzMgfg/EK4F4FhCfAeLnQDwXiA8A8Wsgng+VA2EJsC4g4Icq6IPyGRkgCluhfBC9FCqOAfyBeCIQs0L50kB8F4j9oPytQFwFZeMFLEC8nAHiEh6o2Dcg9oUpwAd0GCB+hzkZBEABlI7ExwkagPgfELsgiYE0E+Xs60D8gAHibxgAaV7DAPESTiDMgN2JIJeAxCPQxFGAPhA/AWJFNHGQa/4yIEIfKwDFozi6IBAIMEBchTWeR8HwBwC2Ky3l227XQgAAAABJRU5ErkJggg==>
[re-image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>
[re-image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEwAAAAfCAYAAABNjStyAAABcklEQVR4Xu2WPUvEQBCGX1FB21MQQfAK4bAWFcGvws5GOLHR4sBasPJHCFaCqJ2VYGVjZWkpaKUW2mjjP7ATfYdJ2Nzq4W4QEnLzwFNksinyZnYzgGEYhmF0OcO07heN39mg7/TMv2EofXSZ7tFL+pVogQXyCgssCgsskqjAeugivaG3dCmpyz4/oNvJdZWJCkwOvvTQEz9pL23SezrmllaWqMB2oV2W5QUaWCiz9IieBrpP1zPK80USFZjPOG3hZ4hVJndgk/QR5Q1riF7TtxzuoDO5Apujz3TVv1Ei5GydR/t2DnGNjqIz0YGtQDtLQutGogK7oxNeTb7kIT2Hjhd/0U9HoF8xjwMoluDAZqCLT+gWdISQl5c/2QddcEsrTVBg8jd8opv0Cu2zmCjzWVkP//9ARpn0jJPZU975AZqH1GpuqXbRBXTbCYP0GC6sFnRbVhnpJr9Jsk65pRrYNDSoFOmmOm1kaoZhGIZhGIZRGN9kf2u7sz4wKwAAAABJRU5ErkJggg==>
[re-image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAF4AAAAfCAYAAABu1nqnAAADiklEQVR4Xu2ZS6hNURjHP6GI6x0p8sgjJgaihBQGJgw8ojxSBoQSAybKNVAYeOVRiGRCzCQGBhclMZAiCgNSii7RpZDH9+tb65591t7nse/Duedav/p32mutve45/7X2931rX5FIJBKJRCKRTqKnapRqnmqku24Pc1QTVX3CjkiBlarPqh+qN+7zpapHclCVjFXdU71Xtaj+qM6ohiTGRBy/VGdVfd11P9VF1bLWEdUxRvVMdcRds3DzxRbzlh/UXVmsalL1D9pL0ag6FjY62K27wsYSTFE1q7aEHUqD6q5U/53qkjzGD1Y9lNLmYjw7lSegEtNVX1XHJR2i+C5N7rPbksd4v0uXhh0OjH8rlnQr4Y0nbG0L+saJzVOWyaqjqueqm66NTH9Cqlv5WpPHeCqPn2L3ZIHxmImplRiqeix2D/KL1Vt1WvXBXWeyW/VbdU41TbVV7CaffOqBPMYzFpPKGV+uP4RkjFfcQ2WEfxfEKhySbCabVA/EVi7JN6cZQXsWs1UnxRarkhi3QbXcaYHY7mgvtTQeBohVRP5e/9SsSg7yjFa9EFuhEG58qhoWdnRRamk8OYNy8o5qp9iu93PwJKTAcDqzYhntV1W9wo4aQey8LVYbZ+mj2I8kmYV9aK0U6Ejj+V7e9IGubarqkRTmIcm2QsKkZKKD8ipJg2vfEbTXEsIRYcmHqFAHxAqDNRl9iNcBnoViOa2Usfx2ki9JuBKNYnMtCto5lB0Sm2tjsoMv8tp1hPDoVPuHuwp5Qg0FxBfV6rDDgSfNYj6Uw58HSoVkogXFSVGBUs74fZIvzPBCiPnaokHSMeQxnoPOeSl/cg37SJ7rxcKyPygRGTiZEmpGuLYQNm/Rjses65I2fqbqk5Q+1XVV8hgPc1X3pRCXPVx/V80K2q+JeRX2Nbp2QlwIC8T7m6IYD0vEEpIv56g5KfiZiDhYT+Q1nt/M76QK8TuYT65PSfppfyWFZJnMDcNVT8RKR/z0MP92156CP7RZrCKg2Ke0PCw2eTXH5a5EXuPhklhivCJ2trjhrrNO6vhEmciY8CnBK14J4xsHp8tiUYPx6xLjUhBrWTn+CUB8Z4J6e5HfFuPZeMTsg2KHu72qCUUjqgfvmMsfEldI9gJmMl71TtJxvx4g+U0KG+sFYhSmY37kH0Chz+muRQoJhDKJ8BPpRPZL+mXWHskXLyORSCQS+a/5Cwl05f9m7Q2AAAAAAElFTkSuQmCC>
[re-image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAfCAYAAADTNyzSAAABCElEQVR4Xu3RMUtCYRTG8RMlBNUgSeDg4hJNDlFLgdDm0hfwC0g4ObsKToaCS4SfoDVoaGtrD4KmJAqKiiDasv7nnlc7XK/VKvjAD/G558j7XkVmmeXfWUcb1zjDDoroYsnNRaljgB4KqOINnzh2c1EquMRqrN/CR/gcJYcbsV+Mp4wrZHypg1/Y9GVIBydYGBZ6kXOxhfSwDFnBBWq+zOJWbCGeDTxj15e/LTTE+tFxNIs4DQ98tvGa0EfZF3vXqfB9D09iw/q/jGUOB3jBo9grPhRbuHNzY9H7rGFefs6vx/0zeTyILZRizxKjd9JhXdLliWmhj3exBXUvdszENHGUYNkPTXu+Aa03OKImIX7yAAAAAElFTkSuQmCC>

## Works Cited
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
17. Overview | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/overview/](https://lczero.org/dev/overview/)
18. lczero-common/proto/net.proto at master \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-common/blob/master/proto/net.proto](https://github.com/LeelaChessZero/lczero-common/blob/master/proto/net.proto)
19. lczero-client command \- github.com/LeelaChessZero/lczero-client \- Go Packages, accessed January 27, 2026, [https://pkg.go.dev/github.com/LeelaChessZero/lczero-client](https://pkg.go.dev/github.com/LeelaChessZero/lczero-client)
20. lczero-client/lc0\_main.go at release \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client/blob/release/lc0\_main.go](https://github.com/LeelaChessZero/lczero-client/blob/release/lc0_main.go)
21. LeelaChessZero/lczero: A chess adaption of GCP's Leela Zero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero](https://github.com/LeelaChessZero/lczero)
22. Run Leela Chess Zero client on a Tesla T4 GPU for free (Google Colaboratory), accessed January 27, 2026, [https://lczero.org/dev/wiki/run-leela-chess-zero-client-on-a-tesla-t4-gpu-for-free-google-colaboratory/](https://lczero.org/dev/wiki/run-leela-chess-zero-client-on-a-tesla-t4-gpu-for-free-google-colaboratory/)
23. Getting Started \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/getting-started/](https://lczero.org/dev/wiki/getting-started/)
24. google-deepmind/searchless\_chess: Grandmaster-Level Chess Without Search \- GitHub, accessed January 27, 2026, [https://github.com/google-deepmind/searchless\_chess](https://github.com/google-deepmind/searchless_chess)
25. Unsorted docs from GitHub wiki \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/](https://lczero.org/dev/wiki/)
26. New Neural Network From Scratch \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/HnZ004HssWY](https://groups.google.com/g/lczero/c/HnZ004HssWY)
27. Debug and test procedures \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/debug-and-test-procedures/](https://lczero.org/dev/wiki/debug-and-test-procedures/)
28. linrock/lc0-data-converter \- stockfish nnue \- GitHub, accessed January 27, 2026, [https://github.com/linrock/lc0-data-converter](https://github.com/linrock/lc0-data-converter)
29. ChessQA: Evaluating Large Language Models for Chess Understanding \- arXiv, accessed January 27, 2026, [https://arxiv.org/pdf/2510.23948](https://arxiv.org/pdf/2510.23948)
30. Monte-Carlo Tree Search \- Chessprogramming wiki, accessed January 27, 2026, [https://www.chessprogramming.org/Monte-Carlo\_Tree\_Search](https://www.chessprogramming.org/Monte-Carlo_Tree_Search)
31. Weights file format \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/weights/](https://lczero.org/dev/backend/weights/)
32. Project History \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/project-history/](https://lczero.org/dev/wiki/project-history/)
33. Contrastive Sparse Autoencoders for Interpreting Planning of Chess-Playing Agents \- arXiv, accessed January 27, 2026, [https://arxiv.org/html/2406.04028v1](https://arxiv.org/html/2406.04028v1)
34. Contrastive Sparse Autoencoders for Interpreting ... \- OpenReview, accessed January 27, 2026, [https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf](https://openreview.net/pdf/b5e91729cc50cec948a0203c6f123c2b6f7b4380.pdf)
35. lczero-training/init.sh at master \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training/blob/master/init.sh](https://github.com/LeelaChessZero/lczero-training/blob/master/init.sh)
36. Layman's question: what is the difference between Leela's networks? \- TalkChess.com, accessed January 27, 2026, [https://talkchess.com/viewtopic.php?t=76512](https://talkchess.com/viewtopic.php?t=76512)
37. Backend \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/](https://lczero.org/dev/backend/)
38. Leela Chess Zero \- Chessprogramming wiki, accessed January 27, 2026, [https://www.chessprogramming.org/Leela\_Chess\_Zero](https://www.chessprogramming.org/Leela_Chess_Zero)
39. LCZero, accessed January 27, 2026, [http://api.lczero.org/](http://api.lczero.org/)
40. Which settings to use for Lc0 to run optimally? : r/chess \- Reddit, accessed January 27, 2026, [https://www.reddit.com/r/chess/comments/192c95c/which\_settings\_to\_use\_for\_lc0\_to\_run\_optimally/](https://www.reddit.com/r/chess/comments/192c95c/which_settings_to_use_for_lc0_to_run_optimally/)
41. Technical Explanation of Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/technical-explanation-of-leela-chess-zero/](https://lczero.org/dev/wiki/technical-explanation-of-leela-chess-zero/)
42. Optimal Engine Parameters for an Engine Match between Lc0 and SF12 \- Reddit, accessed January 27, 2026, [https://www.reddit.com/r/ComputerChess/comments/ja75jn/optimal\_engine\_parameters\_for\_an\_engine\_match/](https://www.reddit.com/r/ComputerChess/comments/ja75jn/optimal_engine_parameters_for_an_engine_match/)
43. Learning to Play \- LIACS, accessed January 27, 2026, [https://liacs.leidenuniv.nl/\~plaata1/papers/ptl2.pdf](https://liacs.leidenuniv.nl/~plaata1/papers/ptl2.pdf)
44. lczero-training/tf/chunkparser.py at master · LeelaChessZero/lczero, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-training/blob/master/tf/chunkparser.py](https://github.com/LeelaChessZero/lczero-training/blob/master/tf/chunkparser.py)
45. Koivulehto Arvi | PDF | Kernel (Operating System) \- Scribd, accessed January 27, 2026, [https://www.scribd.com/document/911325618/Koivulehto-Arvi](https://www.scribd.com/document/911325618/Koivulehto-Arvi)
46. README.md \- LeelaChessZero/lczero \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero/blob/release/README.md](https://github.com/LeelaChessZero/lczero/blob/release/README.md)
47. CLOP tuning \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/clop-tuning/](https://lczero.org/dev/wiki/clop-tuning/)
48. LeelaChessZero repositories \- GitHub, accessed January 27, 2026, [https://github.com/orgs/LeelaChessZero/repositories](https://github.com/orgs/LeelaChessZero/repositories)
49. OpenBench, accessed January 27, 2026, [https://bench.lczero.org/](https://bench.lczero.org/)
50. Running a benchmark \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/running-a-benchmark/](https://lczero.org/dev/wiki/running-a-benchmark/)
51. Tuning Search Parameters \- TalkChess.com, accessed January 27, 2026, [https://talkchess.com/viewtopic.php?t=78896](https://talkchess.com/viewtopic.php?t=78896)
52. A Layman's Guide to Configuring lc0 | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/blog/2020/04/a-laymans-guide-to-configuring-lc0/](https://lczero.org/blog/2020/04/a-laymans-guide-to-configuring-lc0/)
53. Creating yet another Lc0 benchmark on gpu/cpu \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/1sJcvkttLfA](https://groups.google.com/g/lczero/c/1sJcvkttLfA)
54. GPU set up/ tuning GPU \- Google Groups, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/UKmBXeJH72c](https://groups.google.com/g/lczero/c/UKmBXeJH72c)
55. Training runs \- Leela Chess Zero, accessed January 26, 2026, [https://lczero.org/dev/wiki/training-runs/](https://lczero.org/dev/wiki/training-runs/)
56. Unexpected exit from client · Issue \#39 · LeelaChessZero/lczero-client \- GitHub, accessed January 26, 2026, [https://github.com/LeelaChessZero/lczero-client/issues/39](https://github.com/LeelaChessZero/lczero-client/issues/39)
57. Testing guide \- Leela Chess Zero, accessed January 26, 2026, [https://lczero.org/dev/wiki/testing-guide/](https://lczero.org/dev/wiki/testing-guide/)
58. Training Runs \- LCZero, accessed January 27, 2026, [https://training.lczero.org/training\_runs](https://training.lczero.org/training_runs)
59. Neural Networks Chess | PDF | Function (Mathematics) \- Scribd, accessed January 27, 2026, [https://www.scribd.com/document/724676477/neural-networks-chess](https://www.scribd.com/document/724676477/neural-networks-chess)
60. Bad JSON \- you must upgrade to newer version, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/8SQzKe5AFcg](https://groups.google.com/g/lczero/c/8SQzKe5AFcg)
61. Leela Chess Zero \- Chessprogramming wiki, accessed January 27, 2026, [https://www.chessprogramming.org/index.php?title=Leela\_Chess\_Zero\&mobileaction=toggle\_view\_desktop](https://www.chessprogramming.org/index.php?title=Leela_Chess_Zero&mobileaction=toggle_view_desktop)
62. What are the T40 and T60 in reference to Leela Chess Zero (Lc0) chess engine? \- Quora, accessed January 27, 2026, [https://www.quora.com/What-are-the-T40-and-T60-in-reference-to-Leela-Chess-Zero-Lc0-chess-engine](https://www.quora.com/What-are-the-T40-and-T60-in-reference-to-Leela-Chess-Zero-Lc0-chess-engine)
63. Mastering Chess with a Transformer Model \- arXiv, accessed January 27, 2026, [https://arxiv.org/html/2409.12272v1](https://arxiv.org/html/2409.12272v1)
64. Transformer Progress | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/blog/2024/02/transformer-progress/](https://lczero.org/blog/2024/02/transformer-progress/)
65. The Lc0 v0.30.0 WDL rescale/contempt implementation | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/blog/2023/07/the-lc0-v0.30.0-wdl-rescale/contempt-implementation/](https://lczero.org/blog/2023/07/the-lc0-v0.30.0-wdl-rescale/contempt-implementation/)
66. Releases · LeelaChessZero/lc0 \- GitHub, accessed January 28, 2026, [https://github.com/leelachesszero/lc0/releases](https://github.com/leelachesszero/lc0/releases)
67. OpenBench is a Distributed SPRT Testing Framework for Chess Engines \- GitHub, accessed January 28, 2026, [https://github.com/AndyGrant/OpenBench](https://github.com/AndyGrant/OpenBench)
68. LeelaChessZero/lc0: Open source neural network chess engine with GPU acceleration and broad hardware support. \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lc0](https://github.com/LeelaChessZero/lc0)
69. Weights file format | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/dev/old/weights/](https://lczero.org/dev/old/weights/)
70. lc0/changelog.txt at master · LeelaChessZero/lc0 \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lc0/blob/master/changelog.txt](https://github.com/LeelaChessZero/lc0/blob/master/changelog.txt)
71. High level architecture | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/dev/lc0/architecture/](https://lczero.org/dev/lc0/architecture/)
72. Download Lc0 | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/play/download/](https://lczero.org/play/download/)
73. FAQ | Leela Chess Zero, accessed January 28, 2026, [https://lczero.org/play/faq/](https://lczero.org/play/faq/)
74. Releases · LeelaChessZero/lczero-client \- GitHub, accessed January 28, 2026, [https://github.com/LeelaChessZero/lczero-client/releases](https://github.com/LeelaChessZero/lczero-client/releases)
75. OpenBench Testing Framework, accessed January 28, 2026, [https://bench.lczero.org/test/303/](https://bench.lczero.org/test/303/)
