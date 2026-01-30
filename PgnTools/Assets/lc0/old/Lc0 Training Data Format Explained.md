# **Lc0 Data Processing Pipeline: An Exhaustive Analysis of the lczero-training Architecture**

## **1\. Introduction to the Leela Chess Zero Data Ecosystem**

The Leela Chess Zero (Lc0) project represents a pivotal development in the history of computer chess, marking the transition from heuristic-based alpha-beta search engines to neural network-driven Monte Carlo Tree Search (MCTS) systems. While the neural network architecture—often a ResNet or, more recently, a Transformer—receives the bulk of the public attention, the efficacy of the engine is fundamentally tethered to the quality and structure of its data processing pipeline. This pipeline is a complex, distributed, and highly optimized system designed to generate, transform, and ingest billions of chess positions to train the network's policy and value heads.

Unlike traditional supervised learning tasks where a static dataset is curated once and used indefinitely, Lc0 operates on a Reinforcement Learning (RL) loop. This creates a dynamic data environment where the "ground truth" is constantly evolving. The agent generates its own training data through self-play, and as the agent improves, the data it produces becomes more sophisticated, necessitating a pipeline that is robust, scalable, and capable of handling varying data formats (V3 through V6) without pipeline fragmentation.

The lczero-training project serves as the nexus of this ecosystem. It is responsible for bridging the gap between the raw, distributed generation of chess games (in PGN or proprietary formats) and the mathematical tensors required for backpropagation on GPUs. This report provides a comprehensive technical dissection of this pipeline, exploring the lifecycle of a chess position from its genesis in a self-play game to its final conversion into the 112-plane binary input format used for training. We will examine the specific tools involved, including the lc0 engine, the lczero-client, and the trainingdata-tool, and provide a byte-level analysis of the binary chunk formats and the input/output tensor specifications.

### **1.1 The Reinforcement Learning Loop Context**

To understand the data format, one must understand the generation intent. The data pipeline is designed to support an AlphaZero-style RL loop. In this paradigm, the network ![][image1] takes a board state ![][image2] and outputs a policy vector ![][image3] and a value scalar ![][image4]. The MCTS search uses these outputs to guide simulations, resulting in a refined policy ![][image5] (based on visit counts) and a refined value ![][image6] (based on game outcome). The data pipeline's primary responsibility is to capture ![][image7] triplets efficiently.

However, Lc0 diverges from the pure AlphaZero implementation in several key areas, most notably in its decentralized nature and its handling of draw probabilities, which significantly impacts the data structure. Where AlphaZero might predict a simple ![][image8] scalar, Lc0 data structures must support a richer set of targets, including separate probabilities for winning, losing, and drawing (![][image9]), as well as auxiliary targets like "Moves Left" to guide endgame precision. These requirements have driven the evolution of the data format from a simple state-outcome tuple to the complex V6TrainingData struct used today.

## ---

**2\. Data Generation: The Distributed Client Architecture**

The genesis of all training data in the Lc0 ecosystem is the distributed client network. Thousands of volunteers run the lczero-client, which wraps the lc0 binary to coordinate self-play matches. This distributed approach solves the massive compute requirement of generating millions of games but introduces significant complexity regarding data consistency, validation, and serialization.

### **2.1 The lc0 Engine as a Data Generator**

When the lc0 binary is invoked by the client for training, it operates in a distinct mode compared to its competitive play configuration. The goal is not to win a single game with maximum certainty, but to explore the game tree and generate high-quality training signal.

#### **2.1.1 Exploration and Noise Injection**

In competitive play, determinism is often preferred to reduce blunder variance. In data generation, determinism is detrimental. If the engine always played the move with the highest policy prior, it would repeatedly traverse the same narrow path in the game tree, leading to overfitting on specific lines—a phenomenon known as "mode collapse."

To prevent this, the data generation pipeline mandates the injection of Dirichlet noise ![][image10] into the root node's policy priors ![][image11]. The formula used is:

![][image12]  
where ![][image13] and ![][image14] is the exploration fraction. This noise ensures that the MCTS explores moves that the raw policy might initially undervalue, generating data that corrects the network's "blind spots."

#### **2.1.2 Temperature Scheduling**

The "Temperature" (![][image15]) parameter controls the greediness of move selection. The probability of selecting a move ![][image16] is derived from its visit count ![][image17]:

![][image18]  
For the first 30 moves (plies) of a training game, Lc0 typically utilizes a high temperature (e.g., ![][image19]). This makes move selection proportional to visit count, ensuring diverse opening play. After the opening phase, ![][image20], making the engine greedy (selecting the move with the highest ![][image17]). The data pipeline must record not just the move selected, but the entire visit distribution ![][image5], as this distribution forms the training target for the policy head. A standard PGN records only the move played (![][image21]); the Lc0 pipeline captures the full vector ![][image5].

### **2.2 The lczero-client Protocol**

The lczero-client orchestrates the generation process. It does not simply pipe PGNs to a server; it participates in a rigorous handshake protocol to ensure data validity.

1. **Work Unit Acquisition:** The client polls the central server for a "Next Game" assignment. The server response dictates the network hash to use, the configuration parameters (e.g., node count, exploration settings), and the "Training ID."  
2. **Network Synchronization:** If the client does not possess the required network weights (e.g., weights\_run1\_62114.pb.gz), it downloads them. This ensures that the data generated correlates exactly with the network version the server intends to evaluate or improve.  
3. **The Self-Play Loop:** The client executes the lc0 binary. The engine plays a game against itself. Crucially, the engine outputs log data that includes the MCTS statistics (visit counts, Q-values) for every move.  
4. **Data Serialization:** The client aggregates this metadata. It computes a checksum of the game to prevent duplicate uploads and potentially validates the game integrity (checking for illegal moves or crashes).  
5. **Upload:** The game is uploaded to the server. The payload is not a text file but a compressed data stream containing the move sequence and the associated probability vectors.

### **2.3 The Role of the Rescorer**

A critical, often overlooked component of the Lc0 pipeline is the **Rescorer**. In the early days of chess engines, training data was often "scored" by the game result (0, 0.5, 1). However, self-play games can contain blunders, or "drawn" games might actually be tablebase wins that were adjudicated early due to move limits.

The Rescorer is a server-side (or offline) process that acts as a quality control filter. It replays the generated games and cross-references positions with Syzygy endgame tablebases.

* **Adjudication Correction:** If a game was drawn by the 50-move rule, but the tablebase indicates a forced mate in 5 moves, the Rescorer modifies the value target ![][image6] to reflect the theoretical truth rather than the empirical game result.  
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
* **V4 Format:** Introduced separate fields for root\_q, best\_q, root\_d, and best\_d. This marked the shift towards explicitly modeling draw probabilities (![][image22]) alongside win/loss values (![][image23]). In V3, draws were often implicitly handled or mashed into the ![][image23] value; V4 made them first-class training targets.  
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

The "Input" to an Lc0 network is not a raw board representation (like FEN). It is a tensor of shape ![][image24]. This tensor encodes the spatial distribution of pieces, the history of the game, and the game state metadata.

### **4.1 The Need for History**

Chess is a Markovian game in theory (the current position determines the future), but in practice, history matters for two reasons:

1. **Repetition Draw Rules:** The state of the board includes the repetition counter.  
2. **Dynamics and "Velocity":** While not strictly necessary for correctness, providing the network with the previous board states helps it infer the "direction" of the game—e.g., whether a position is static or dynamic, or whether a player is shuffling pieces (indicating a lack of constructive plan).

Lc0 typically uses a history length of 8 steps (Current position ![][image25] plus 7 previous moves ![][image26]).

### **4.2 Breakdown of the 112 Planes**

The 112 planes are decomposed into ![][image27] feature planes (104 planes) plus 8 constant planes.

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

**Total Feature Planes:** ![][image28]. These correspond to the planes array in the V6 struct.

#### **4.2.2 The 8 Meta Planes**

The final 8 planes are "broadcast" planes. They are usually filled entirely with 0s or 1s (or a normalized constant value) across the ![][image29] grid. They provide global state information to the convolutional layers, which otherwise operate only on local spatial features.

| Plane Index | Feature | Description |
| :---- | :---- | :---- |
| 105 | **Castling Us: O-O** | 1.0 if Kingside castling is legal for P1. |
| 106 | **Castling Us: O-O-O** | 1.0 if Queenside castling is legal for P1. |
| 107 | **Castling Them: O-O** | 1.0 if Kingside castling is legal for P2. |
| 108 | **Castling Them: O-O-O** | 1.0 if Queenside castling is legal for P2. |
| 109 | **Side to Move** | Often all 0s for White, all 1s for Black (or encoded via P1/P2 orientation). |
| 110 | **Rule 50 Count** | Normalized value ![][image30]. |
| 111 | **Game Progress** | Optional/Version-dependent. |
| 112 | **Bias / Constant** | All 1.0s. Provides a baseline activation. |

*Note on En Passant:* En Passant targets are sometimes encoded in the meta-planes or implicitly handled. In V6, the side\_to\_move\_or\_ep field in the struct is used to construct the relevant plane or update the input tensor during the inflation step.

### **4.3 Symmetries and Invariance**

Chess board geometry allows for certain symmetries, primarily horizontal flipping (mirroring). While the rules of chess are not perfectly symmetric (due to Kingside vs Queenside castling differences), they are *nearly* symmetric. Lc0 exploits this to augment the dataset.

During training, the pipeline can apply a horizontal flip to the board state. If the board is flipped:

1. The input planes are mirrored.  
2. The policy vector indices must be remapped (e.g., a move from a1 to a8 becomes h1 to h8).  
3. Castling rights must be swapped (Kingside becomes Queenside).

The invariance\_info field in the V6 struct records if a transformation was applied during generation. The chunkparser.py can also apply random flips on-the-fly to double the effective size of the training set.

## ---

**5\. Output Targets: The 1858 Policy Vector**

Perhaps the most distinctive feature of the Lc0 data format is the policy output. While AlphaZero used a convolutional output head (![][image31]), Lc0 uses a **flat fully-connected output** of size 1858\.

### **5.1 The 1858-Move Mapping Logic**

The choice of 1858 represents a specific optimization. The set of all theoretically possible legal moves from any position is large, but limited. Lc0 enumerates these moves to create a fixed-size vocabulary of actions.

The 1858 indices map to moves as follows:

1. **Queen Moves (Sliding):** From any square to any square on the same rank, file, or diagonal.  
   * This covers Rooks and Bishops as subsets of Queen moves.  
   * Encoding: A set of "planes" (conceptually) for directions (N, NE, E, SE, S, SW, W, NW) ![][image32] distance (1..7).  
   * Instead of a sparse 3D tensor, these are flattened.  
2. **Knight Moves:** 8 possible L-shapes from a given square.  
3. **Underpromotions:** 3 specific moves (promotion to Knight, Bishop, Rook) for pawn moves landing on the 8th rank. (Queen promotion is covered by the sliding moves).

**Why Flat?**

DeepMind's AlphaZero used a convolutional policy head to enforce spatial invariance—learning that a Knight jump at e4 is "the same" action as a Knight jump at d4. Lc0's flat head forces the network to learn the meaning of "Knight jump from e4" and "Knight jump from d4" independently.

While this seemingly increases the learning difficulty, it significantly reduces the computational cost of the policy head. A ![][image33] vector is faster to compute than an ![][image31] tensor (4672 values). Empirical testing by the Lc0 team suggested that the flat head performed comparably to the convolutional head for chess, likely because the board is small enough that the network has ample capacity to memorize the spatial translations.

### **5.2 Deciphering the probabilities Array**

The probabilities array in V6TrainingData contains the float values corresponding to these 1858 indices.

* **Sparse Distribution:** In any given position, only a fraction of the 1858 moves are legal. The illegal moves should theoretically have a probability of 0\.  
* **Softmax Target:** The values in this array sum to 1.0 (or close to it). They represent the MCTS visit distribution:  
  ![][image34]  
  The training objective is to minimize the Cross-Entropy loss between the network's predicted softmax distribution and this target distribution.

## ---

**6\. Pipeline Consumption: The chunkparser.py Mechanism**

The final stage of the pipeline is the ingestion of these chunks by the training framework. This is handled by Python scripts, principally chunkparser.py in the lczero-training repository. This script acts as the DataLoader.

### **6.1 The Shuffle Buffer**

A single game of chess contains highly correlated positions. Training on a sequence of positions from the same game ![][image35] introduces bias and can lead to oscillation in the gradient descent.

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

**8\. Conclusion**

The Lc0 data processing pipeline is a masterclass in specialized systems engineering for Reinforcement Learning. It addresses specific challenges of chess (draw probabilities, game history, symmetries) and computational efficiency (bit-packed structs, shuffling buffers).

The transformation from PGN to the V6TrainingData struct via trainingdata-tool compresses the complex reality of a chess game into a fixed-size, 8356-byte record. The subsequent expansion of this record into 112 input planes and an 1858-dimensional policy vector by chunkparser.py provides the neural network with a rich, historical, and rule-aware view of the board.

For researchers and developers looking to interact with or modify Lc0, understanding the V6TrainingData struct is the prerequisite. It is the contract between the distributed generation cloud and the centralized training brain, ensuring that every pawn push and castling right is accurately preserved across the digital void.

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADQAAAAfCAYAAACoE+4eAAADeklEQVR4Xu2YS6iNURTHl6IIIfIIiQEpRl4pI6EUBiiKiQxMmDBQRtfAkERRUpREYqgUgyMDrxERkVzyyAATFPJYv7vOPva37v6eZ3LT+dW/e+639v32XnutvdY+V6RHj/+aYarNquHeUJF1qlH+YRNGq9a0xeembFI98A9rMEJ1Trpbg6xVfVbdVr1SPcmaK7NU9V61wRtq8kh1Ssy5RrxTXVOtV/1Q/cmaKzFZbCGnpXm6BdiQr2LRbgQOHFIdbn/+nTVXok8sygvc8yZwhthgNoiNqsV81TfVEm+owW6xjeg21WKmiKX+HdU4ZyuE88O5meYNFRmjaomdnTlZU1dQLc+ofqpWOFsh+1U3pHlVIcIfpbt35LFLLPKssZCFqm2qY6qXbZ1Q7RSLWJ0+sFFsUt5VBru+THVR9Vr1VDUpMyILkSFCl73Bc0TshW9Uv1Sf2r+js6qRnZHlHBBzaK83JNgnVkWPixWPVap7qonxoAhSmFS+7w15UAgoCKRNU1pSLc+JznfV8ugZbeKDal70LCacT0p4JUg7drco7CxkrtgNIkVLbMJF7rmHxTHXebEKBpy5xZ0Rg6ntEL2HSfIaIWfppFiz3KOakTUP0JJqDjEHcwXRs+h7RdRyiHNyVfJvBVw7uH5QOvnM+FS1aUk1hwAnYqdQapMCtRwizR5LvkNbxPI7dH5Sj3ThZ0xLqjtEr6Mw3BUrRmXFpJZDlGde+MIb2nD92Rr9zmJSDpGOvIfynSJcYxjjmzcRp9fkwXiaPiqFnWESGqKHfPedn4qY6jXhPal0hLAoxsRnlTS+pJodPfMQdaKTWmOGkD5MklokVYh+EXoT+iJWFT30EqKZih6ECF1xzw6KlfsiKOt5a8wwVnVLbHBqkexM/BJ2lm6d6lfTVf1izW9C1tSBv3so1shDM+fiuTIelCBU4dJLb+jA3MFSi2RnYkdJCxaSKu88uyDlhYHokX5ovLOlCAWhX2zTBjFL9UzsEIa859qfgq4fFhdShn6UR5i80feXHPqkZJPCzRUnSJ/nqpmZEf/gDOEUu8r461L+nWS12JnY7g0NCBlxVNLncgDyEId2iDW4sq+3/KMDx2m+U50tBalHFDkbRc2yCjhS+h7KJIfsrVjTzPW8DWlUJddjiCIllhtGN3C++YfLkIBo3pTyDcsDR2gFPXoMFf4CdyjHIspqldUAAAAASUVORK5CYII=>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAsAAAAgCAYAAADEx4LTAAAAyElEQVR4Xu3QMQ4BQRTG8RFRERqJSCRUEo3EAXQKiVJE4wRahdYFXEBFp3cRBaVGJwqR0CjwfzOzk2fjABL7Jb9ivn27szPGJPnz5DHBFkd0kfqY8Kli5w3QwA0jPRRlgwNqqntgpdYhd6yR9uscXpiHCRV5cEEPGd+NkQ0TKgvjXtDksF8j23ewxMm4YTlDSc3YoT4qqpPrGuKMuurtXT4x1SUpY4+iLmVItmzpkrQxi3X2y9dY1zTufwuxPkS2FXKg6OqS/HLeqO8hszKLbi8AAAAASUVORK5CYII=>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAfCAYAAAA4AJfRAAAA30lEQVR4XmNgGAWjYBRgBbpAfAmIXwHxfygOBmJfIN6BJPYLiOcDsTpEGwQYA/FXJEUg/A9K3wDihUD8EUnuLxAzgnUiAVcGhIJHQOzEgFAkBMSHkeRBalEAyAUwyXI0ORAQAeKrDBB5kEtB6uGAkGYeID7AgFADChM4GLyaKfKzBwMiCkHxz4ksiawZhEFxWwPEaUB8B0kclFgw4hlZczsDRON7JDGQptlALAPTgAxwOVsSiAWQ+FgBLs1EAbI0swKxOBC7MyA0NzNAnAuKW7xAAYgnAvEsLNgToWwwAQBxvV0xyRBntwAAAABJRU5ErkJggg==>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA0AAAAfCAYAAAA89UfsAAAA2UlEQVR4Xu2SMQ4BQRSGn4hEIRoSEkToJDqdGyiUWr0LEAdwBI1LqCQqUYkodETjAhqVQhR8LzMrs7NbaiT7JV82+977Z2dnVyQh4afk8YJzLHu9Ho4w5RareMAhvnCJWdsr4gmvWLI1aeAemzjBN24xFwxATbxQH1diVl6LCWnYJ5iJ8MQ7tv0GzPxCQNzWFL3Xw4igDQ3FrahPrvhFpYMPe3Up4MarfdHT80P6Xca4cGohMmK2NxUzrA7wiHVnLsJOTPBmPWMrNBGDrq7v0BXzl6TD7YR/4gM2wyJK+8BxYgAAAABJRU5ErkJggg==>

[image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAeCAYAAADzXER0AAAA0ElEQVR4XmNgGAWjYEgAAyD+TyTuheoBAzMgfg/EK4F4FhCfAeLnQDwXiA8A8Wsgng+VA2EJsC4g4Icq6IPyGRkgCluhfBC9FCqOAfyBeCIQs0L50kB8F4j9oPytQFwFZeMFLEC8nAHiEh6o2Dcg9oUpwAd0GCB+hzkZBEABlI7ExwkagPgfELsgiYE0E+Xs60D8gAHibxgAaV7DAPESTiDMgN2JIJeAxCPQxFGAPhA/AWJFNHGQa/4yIEIfKwDFozi6IBAIMEBchTWeR8HwBwC2Ky3l227XQgAAAABJRU5ErkJggg==>

[image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>

[image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAE8AAAAfCAYAAACmupBxAAADwElEQVR4Xu2ZXahNQRTHl1DE9Z2PkHslJfKRKOHNA4nkI4qH++hBUYrkxYsXpSRPKCRFKckD4eHGA1GifJQoJJIuUZTkY/3MHmfOurPP3uecfUr2+dW/zp6ZPTN7Zs2aNXNE2rRp8x8xSnXaJpaEDtUpm5iX4aprql02o0QsVC2ziVkMVB1VXVUNNXll44O4QczNVtVn1XybUUIwoOviVmImXarXqiOqfiavjCxX/RBnUJk8UfVIe7mGbFP9Um2yGRYK7beJJQf39UV1XjXA5FXB4K2wiSVnjOqR6oVqYnVWBWKbXtUMmxFhmGqn6r7qleqKNO8jl6jWR7TZPK/xLzQI0QT9vqDqDNJ5nhQ8e7A2rO67uD5G4cXnqnE2I8LDRBvEDfZ21ZaqEvUxXpzV51Wj+DDM18P30n/S90i6AeDKKM9ERlklOda1OB9wVtU/eWZz6VEd9AXqgM4eUt0I0ujoiSQPjgd5zXJAqi2Xb9ihehqkxWBsGLzUUwcFUjMDKPdRtVLcjMFi1ZC/JfIzWdyETUmeiaduS2WGqXN38rsVrFXdU02zGYbCBm+RVC+hW+LioSKg7jdSCdBxJfSraLDqjarHks/HFzZ4WJv1Qc34oRCW7F3VyOSZQWzF4HWLayfL4jyFDR5wYD6peieVwcuz0dSC9wnSQ39Hn4oePOq+KfGdNY3MwZsjrtIOm5HA0vwpfa1sguqluHjIMldcfh4IdzhT0w8P/i4czBi0wW6fBXVwWrhsM8SFLLVuUOgH353qf5l5BiHtY30FbBYhS1XfTBrMEleWkGasyYtBHeGSBRw6AWpsYsC3Qb+y2lgn7pxKfzCEEapBye9nqsGVon3A4mgj9QBBRV9VC2xGAo18kuoKZouLlbh5sFCOBrHWWrMKhEeUPWzSea/W+74NlFYG5qneq46J28n9O17WIEJ8KNYrGZsLFaUFgmwUe8XNHqcK7rr4zQdz4rAQdhAPUibLb2FtWB67bQirIZbu8W0wwGltYFEXxV3s+uXPZPuBu6OanqTHmKp6KzkuTOholo/BQlnafJiP82pxRtI/LAQHHmuX5ZUF76W1QR/xo2Ff+Y01dkq8zZDVkuHvPPiY3PdXOeE6P9dlYhNgma1oo0vc/SannKyT1x8TvyTOlEebvEZghouciBi0cc4mFsQ+cb5ypklPBXPGn3Fr0gwsB/xMKyzC49vAulsBcWzd48C2jpOs688PAx/W7PVRFr6NegLevDDp3MDk8etV0Klu1QOTXhYYMAaukcuONm3a/Lv8Bp0dxrloSO1FAAAAAElFTkSuQmCC>

[image8]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEMAAAAdCAYAAADxTtH0AAABZklEQVR4Xu2ZsUoDQRCGJ2AgRcCggRCwsbALWAiWVpaxS5cH8A0UbC3txCpdep8gVd4gFnmG9BZ2kjhzu8fODieb3F089zIf/BzsHH9m/kuO3B6AoihKfjZMd6JWB9qoObgZv7yq4BnVt2qJWh1ooE7BzHeJWvpln0e5sGdOUA9gmiubkC/VPuQi56/C6IFp9hPMV/XKLxci9Q75Vh7GLbjf69oeQ01vA/muwPcO+VYeBoc+q6wwJOQd8tUwGBoGQ8NgaBgMDYORK4wb1CSnjuF3NAxGlGHsCw2DoWEwtgmDGh7a4y5EEUYTdYEaoWbgniNewDTXcacm0EDp88a9qHHItwe+d+p7DdnelYdBVyodLktTd2rCG5ihvlGvosYhXx5clqR35WHk5cmqTKIM4wj1jhrLQkGiDGOAWqDOZaEg0YVBN0b680Z7s7R/WSbBMP7bhvAZqisXC7DThjC/+x78q4KD5gfoGaDT2ePfWwAAAABJRU5ErkJggg==>

[image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFUAAAAfCAYAAACWNoFQAAAD9UlEQVR4Xu2YXagNURTHl1CEJCJFNxIhJEkK6fpIiQc8IB48iAdPbj7zcEpehXwluteDFF4klJd740XcEpEiD+QjSUpRvq3/WbPu7Flnz5yZc2bq3tv86l/nzFoz++y191przyEqKSkpKSnpA6xmvYkRGMLq8NigbtaUwG8O65nHZ3lgb5RW1iuqfS70hDUrdG2aG1Q7hk8dJPMaWL3LwxLWbda/QD9ZV1mnA/tg1nbWBdaHwOd18H0Xa2jgN5l1KrDB5x7rKGtCYG+UBSS/RceGnrLOsSqskT2ezYP54Ll3KBwL363U9og1tXqnh3msbySON0l2p48jJD5rrMFhPusza4A1NAkWCWMjuFjAItlNYeB8zCAJKOwvWC1RszCeojtsRNRcBTv2ConPPmNTsDgoAVoS8kQnedAacmYMyRwwVlfUVINusi/WANwHIbgIsmUF6w/FB1WD7rPlAcb9S83X6Hq4WXvC2CzI2NgdPZxkVeKCOpp1n6SQxw2GoD9kjbWGHMDvw7hvqfkaXY8dFAZqrbFZUgf1K0knd9nJus5aSeJzMWqu3o/ivs5czwvUUIybVO/zAH2gnWSsNLU7MagAgYIRWx8poEwkKcqLg+vwuUTRRqRB15NA3mDHYNyiSoti6yk2SxL4PYlB1aILaXefy3pMYeNBWbADIrjnWYOC73mjWdTbUt9dgF/G1oMbdQ2qbTwaVBzGxwXXZlIx3V5BCiIVi0590E4yvzSpv4gkmPBHcL24ZzMNpG08o0js2sy04xeJpn69o9Q21mV7MSNZUr9CYbzw2YtbdBFUPHRLxCPswrqS2vGLRMtS0lEKtfwWa6M1ZESPUhgzCWTpcxLflyR9x4sbVByZEFC7WsNI7Bh8KauTagOfJ+6pJKmeoolikpOsISM6/3r1FI0Zfji3rze2CO6hF0Lz8aH2HySTyQJ20gZKf0rQ1Id8oEluIvm/YpWxTWO1Ufp6j8aDcbqodjO5TCfxO0NS/hKBM97ZccMnkgbkww161o6v9x6jdP8NIGPgjzcpH/tJdgt2qTZOgB2L04LOJQ1oPPD3vdgABBBZiY13KPheF/f9/zDFT7pe0JPQoLqnBwvGxW5Gir0n8f8eXFNhxx8PbL5A2KyLo4XCZ14j8cU/a3assyTv97A/qN6ZEg0qVj2pfuHBByg+6Em8I9lZH0kyw4ce29IKZWhh9c4Q7KKTJDv8t7G5IFj2eT7hd8MXi5Vp3jgD7mEtswZDhZr7DxNBu0vJC5cnRb+F9QoqVJuuRYHa2m0v9jdQh/Ha20g9zgrSFA0R6td0svZSxrrUIFtJFrDFGvobmynlUaRJsGitrNnWUFJSUlLSd/gPWfRCMIZcs9MAAAAASUVORK5CYII=>

[image10]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAeCAYAAAAl+Z4RAAABAklEQVR4Xu3SsUoDQRDG8U/QQhREExALEcEUQYu0oo9gIVhaWOYBLGx9Bks72xBSpxAR0qVObLQRxEYULCwEjf7HOcLsck8g98GPcDuT4W53pSpV/mUO8I4HnKCBD4ywEfr2MBue/zKDb9xgJayf4QcdzBVrXZUMOMIztrL1Jl7xiV35H4+TDrKOe5xn65Y6xvK3aGMHm7FhAddFw3IsFFnErbzew11SJWt4lDfYPuSJA8xpUlU6oCxxgP3ac5L4jWWZR19ev8pq02zjRekJ2JEdyo/Pdn2CAZZwEfqmacnvwVPhC0P5vtiwS/lbvCm9VElq2JffwPxbbZDVV7P1KlX0C0+7Mb7jcV+jAAAAAElFTkSuQmCC>

[image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEkAAAAfCAYAAACrpOA2AAAD/ElEQVR4Xu2ZX6hMXxTHl1Dkf0RCIiURiYhISeKBBxSFJ4n8SSjKE8WDBymJkhIlJR6kX/3Cw5SSPEn+JQqJJJSikD/rc/dZzZk155w559y519B86ts0e++z79lrr73W2nNF2rRp8w/RU7VbtcN3tAADVGd9Y3fTQ7VHdV01yPW1CrNUi3xjI9arXjbQXdUh1YTomTRWqt6opvuOFuODBGPlZrHqhOq86qvqs+qc6lRMT1W/Ih1W9e54sp53qr2+sQW5prohJbx9kuq96rTvkHCMNqh+SjDU5truDnqpHqlG+I4WZInqhySvIxOOEwaY5jsiRqpeSBjDZxyMeFw1w7W3MtskrGWN70ijj+o/CQ8NcX0GxvskYcwz1zdK9VzSn21F2FBCyyUJp6Aho1WvJBgAr0hirVTj0gXXR7bgKP5NDFM9kLC5bHJDbJEYIIm+qv8l9H+U+uy1L+rLA5swW0LWfKw6KOGFOwvvSDBmzu3R9/41I2rBe/Ci76p5ri8RMhKLxP2SWC0h0CHSfBwWTWbMYyTGUmh+U02RsDkc8zuqobFxRSHpkDSILxwj5sRgryU7kVgc5pRkwiTEGDtKSSLtD7QHHBbQqY+ywEBnJJQZc6K2ZRLmf6uaGLUVYbjqvoQ51rk+Kmva08IH2N9vWIXjargcg49KWHRcg6tDEzEj+YznwfUrEv4Ongf9VDtVM6PvRbET4EsPrh83o74schtpl4SBxKTC5brkNxIxgIBv3klsOyLl4xFGwTjMhYfGPcZqPjY/i1xGsuDFQI7L+NruXOQ1EnAVwDhmKPMCsmtRlkp1Dh9TlkftZK8schnJ0iADCXS4f1GKGAkYT/AmCdgi8eai2FGjdvMFsAVkHCCLXEbaJNUXZWfKgGEx8BffEcPKBwyJkQxbKO8Rh83bKqFUSMOerUhtquc0cCostR+T9PukzZHW30Fnj5rBfY950sA49J+UanXLJfmihCJ2XNQG8ZoMb0vDaruKVI3EnFzIeZZ5Ocb0Wzb14EGJDkKAm6taJdX4wOfGqK0M5pFpsOjLUltGHJCw277u8iVJGhiG2zy1nXkci8Y4lBlmpKsSDO+xbEuAJ9DXEL+oJqkMdg9KuwPxEvckeAYvjwjYC6W+juH7FgkFZ6PshBFuSXVejhbpn5hnm5/2u5Edy4pkV+ZNg99lbkv2HYjFU1Vb/ZWHFb4hgfi8cajvfFscy4CZ8ajZjJF01y4Dhudn4K6AGIjnEUvTvL/LIMPN940l4ZpxxTc2if0SfkWd7Nq7BQIp6uwZJ448iT67Au6KxK0/wlgJAZn/mPiAXISpqgW+sUlwjCkT0n6n7xbIZA+lPrW3AlZHlblZtGnTpvn8BhvEChbOcUzBAAAAAElFTkSuQmCC>

[image12]: <data:image/png;base64,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>

[image13]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHIAAAAfCAYAAAA7t5n5AAAFK0lEQVR4Xu2aSahdRRCGS4yixJEIIaig4kAcSESSoAiKiANGBYcgRLJwo2BWgcSYRUhQV+4EBxxIXIgoDogEXYg+FFR0oQkRxYFEEUVERUHB2fpSp7yden2me0+G9+gPft6jq8+553R1VVf3vSKFQqFQKMwYHlYtj41jcoJqSWws7HuOVT2qOiwaJuBb1UWxcaZzueqrDnpXtVl1puqQPVfmmae6WrUgGsYA5+FE7jkk61Q/qpZGw0xmsdhgoU9U/6p2JW3oicSGXlCdxMUZdor1+U51VrD15VbVn7FxAIjyN1WvVf/POq4Vc8KGaKg4W/WeWJ/vg835VUYOJzLH5WTVZ6pno2EgrlL9o7orGmYD7si2lztd9Y3YIA+5djnzVR+r3lcdH2xDslrsffk7q+jqSHhAbEYzs4fmFrHnuC8aBuYCsQwyFdpnPH0cSdqk79OqOcE2CRRSW8TufV2wDQ3RTtT/EA3OJaoPVStUh1ZtVHEUDF+oHpT6YuFA0seRi8T67ladWLUtFCuOKCJ2qM6r2tdX7c+Ivf8dqsfEqmH+pmORDi73a+NI1U2q58TG917pXjEzaZ4Se49pUNJ+qnpdrMOqSlRxV6rWqv4WKxbOqa45WOjjSAaLvlSVF1dtbGVoQ6QsUhe8LfbObsOpDLiP0XYZbTFwHk7E4ayVdeAEDgl+FtsX3i6WkpkcfPY1VT+yxaUymmwRCrtpjjxNLBJ5GGYIHX5SnZ/04cZuY53pAlsEHwQXg3NK0ifH3dKvchzHkYjrHF93Ukc6T4r1Z+CBcXlIdYWM9qb+DNtUR1RtOZgYFFwUXike0dyDIgbnvqE6Ku2U4J83NzZOiV30UdUhDkr6QWuCLQdphwqOB+ZlebkbVJ9L/RYAmDCkLVJgV8Z1ZDpZujhyKrSnEFn0oW8djAV9NoV2xz+HyfCqNFel/s51jpbfZO+04/iL5mw5GNStsVE5Riy/3yz5k5ZlYi/RZ8Pbx5FkHfr+IntPli6ObHISn93WZ5Pk7+88LqMJ84HYnrSOVkdiJCo5pE1ZWdna1gDg5s+rzo2GCvZwbAHuF1v0HWYsUUxR0Yc+jvT1kKO7dLLsa0cynoxr0x7T78Ey1zYGjY4kQjCyFqalOe1bamw5SF9E3dHRkEAh9YdYBtiqulNs/XhF+kUj9HGkD1ZMW5M6kmWjqQ/RTxYg6urwZ0NtY9DoyCWST5190yqOPzw2NsBWp26WdqGLI3kmDp3pd2OwwaSOZOyYlHURR0FJsZQWWBGvRL+Mhgzu9Cws2JOm1QNBmyNJ3xvF+hD1ODUyqSMp7r6ulNtrU+y9KPlnZCLfJpahujqSnUPWkZ4+UXxR2rioS1rdX7C/YkNNmb5N7Pleqtpc2B4R20php2K+jIsDFECs16zbvnZfL1bVMomZ3D6RuW9uw882gAOFpqzle/V064ET3xFbs5eLTQQie5lYvZD7Jib9rGmQa6fEXj7SZ9uxv/AobBOz/C0xB6SFVUq6NrmITCIjtqNcVAFnrNjJbHUwmcgKHr1/iU0aHEoAsXZjx5n8H4MKPPqZYFkoUvxoLoVFummmFYwLVb9Lc+bCMfPExvIMyRcrtB0XGxP8q6yuBzP/wyxrerjCCKpXBjlWxUNxqlg09j70pwJrSxeFEaTvl8W+XB4aovkemX582gkW9pJW+0Exw3lqbn2bBIolfrPDNqr3vanc0lP+Qjf4FiO3X50EKlW+gRnr1w0UP02n+YV6dslwv3jDeWyl6irvQqFQKAzPfzRyfcXKkHvQAAAAAElFTkSuQmCC>

[image14]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAeCAYAAAAVdY8wAAAAlUlEQVR4XmNgGAXDDGgB8S4g/g/Ef4H4CRBvAWIBZEWMQPwLiL8CcQwQsyJLIoNgBogic3QJZKDIALFmCgPEZKyABYjXMEDctRSIZ6FhuEYeID4AVQhi4wTICgmCOQwQhRzoEugAFBTTGSBu5EMSB3nSBYkPBsxA/IoBEpaPoOwvDKga4QCkWBKI7YFYjgFPoI8C8gAAS4sa3MpGkmoAAAAASUVORK5CYII=>

[image15]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA0AAAAhCAYAAAAChSExAAAAqElEQVR4XmNgGAWjYOCBJxD/JwKnwzSYAfF7IF4IxLOAeAGUvwvKh+FKIOYGaRAD4otA3ADiQIEOEN8HYlMkMRQQCcQTgZgVSQzkhCdALIMkhhewAPEaIN7DAHUKMUAJiJ8D8Rx0CXzAjwEtlAgBRiCeAsT/gNgFTQ4nEAHiq0D8Fog10eRwggYGiNNAAQEKEKIAKHJBmnLQJfABSSC2AmJmdIlRMEIAAO1aItc5UAhHAAAAAElFTkSuQmCC>

[image16]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA0AAAAgCAYAAADJ2fKUAAAA8UlEQVR4XmNgGAWjgCZAHYgnAvENIN4CxFpAzAzErMiKkEENEP8D4nlArA/EqUB8H4i3A3ERkjo4AJn0H4grgZgRSbwcKh6NJAYHGUB8CoiF0cSNgfgrEJuiiTPIAvFtIM5BlwCCIAaITSLIgtxAvAcqIYgsAQQ8QHyAASKHAiSB+CE2CSBQAuLnQPwbXQKfJk8GiPhdBkhA2QGxEEgCn6ZWBoj4GiDWBOITQCwNkuAA4q1QSWRgBsTvoeJVDJAgXw7ELDAFfkD8lwHiBFAcuQLxEwaEC0CaQIHlAdOADEBOFWOAJBsYABkkjiY2CoYrAACpMCy1od5BjgAAAABJRU5ErkJggg==>

[image17]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADYAAAAfCAYAAACs5j4jAAADaklEQVR4Xu2XS6iNURTHl1CEJK+EbqREykCUUsqjDJCYKDPySEYGlNEdMEdKyWMgkQyJYnDKxGMg5RXJI48yUYpCHutn3e3ss87+9vnO67rl/Oo/OHuv7ztr7b32WvsT6dHjv2WO6oIfbJK1qtF+8F/Sp7qv2uQnmmSk6qxqjJ/IsVX1OtJz1YoaCwMnb0utLWI3Un84XnVDdVw1ws21wgPVCbEgS7FO7IFzqq+qX6rzUu/MZNVBVUXMBp1SbVANq5r9gd+HVc9UM91cq6xXfZYWdn+e6q2Ywx9VC2qn/zJWLLgzbjxmqdgi7fETbcAZuya2c1PcXJajA3ojFtxj1dQaC2OxWLqyEClYEBaGwP1Otgv+4NctsVRvSNiFLapjUk21zZFNAJuK2DMp9os9i12nYaFYsO+qZW4uyWzVQ9VCqaZR0Vk7KbazRVxRfRJ7VzfYKeYbC9gQDuYd1QSp5nLqrDF/V7UxGvOQyizSJD+RYK7qiOqJ6rJqvmq45CsfO8WOXfITKQ6J7USAFAzp2B+Nc65wuuh8wRexMp9qATE4/1N1Wmx3t6teqK6q9kZ2HrLrvdgCZ8HwkWqRGydYf9ZCgcmBPc/m2CVmN9GN4wMlnQJVRKgH2GVZpbon9akTnzVSc5zqpjQuCo3yn75Gf8POQ4oz7n2JKR3YAbHm7EtzfNYIkPR7Ko2LQqPA6G3YpBwjG5jzBSumVGCjxKoYlSZFfNbo+OQ1BSRHLjDOHecPG39GgsOpnYwpFdgMsYpU1BNCQwzpGBeYInKBTVO9ErPx7wpFgYqXo1RgOEDZzG19uEnwIl9gUuB0KrUhDow7akwoVvhD2nO7mF5jYYR3oELYBc5YDoKmUZftTTTniqRvJiH1fWBLxBaPcfyhQKUuBxAqJyldA/1jpWqb2Iv49NgxMFbUGNdI+bsfC0CTJs1TcBn4IdX3rRazDztJYDjNf6ZgQbCrazt9qndSLQpBuYsl40UFxoPDubscwexWfVN9ECv9y8X84uZO0LnvrpCyLNCgEippUQEJkJZ8fnCFChAMBSseiwmF46Wkz1/XIShWnjTrJP1Svoh1BXaCtLooxSnVLLPEziJf5mXOetcIn/KdOgsERE8tKkqDBqu6T8wZCkO70LxpC0MC0rBfdd2NNwsBcVnv0WOo8xv1GNmc3dChjwAAAABJRU5ErkJggg==>

[image18]: <data:image/png;base64,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>

[image19]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADoAAAAfCAYAAAC22t6tAAABOUlEQVR4Xu2WsU4CQRCGhyiJRgolhsRoZYehIzZgZ0WBWlhobWFj4SPwCjYWRAp7H4GCWisLaxNjQmEsqSzwnxwHy4geQyiGc77kK25n73L/3e3cEjmO4zhzZxXuwowspIk9+Ag7MDdeWny24QVswf7ADqUwaMg9edB0oQpah6eBZ+L4BG4OZ9ti6qA1Gi3ov7yMTzBGYtACfIaNYKwEX+F+MKahCm9hcwp5HnfO+Ks5hFnSkxj0HN7Q+MX5rb3DnWDMOolBJcvwAbbhmqhZRh2Ut1BdeCcLxlEHPSLbTec3VEH51/ECP2FR1DSswK0ZXafZUAU9gF/wCW6ImnVUQRsUTeZmxE3JOnl4TNFv6Y2ie/+A14Mx3vRPJH4qV7JglDLs0c9NTSzv9CbCa6QCl2TBcRzHcZz/yzcadFdjAEXdxgAAAABJRU5ErkJggg==>

[image20]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAD8AAAAfCAYAAABQ8xXpAAACA0lEQVR4Xu2XTShEURTHj1CKfERKKSWUKEoUsZEFCxIWlIWdEivFdjb2WErJQja2dspkh5UiSx+JFcpCPhL/f3ceM8eM98HMaLq/+jXNPW+ae97cc84bEYvFYrFkBMWwE9bCPBXLaOrgO7yGd/AFzsVckaEw8TNYE3mfBSfgG8yNrGUk7fAZnugA6INhWKDW/wWsy1XYpQM+WBBz3MNqnbTAB9ikA/+FZTEJBIE3b1tM8hsqRpg0Y0M6QPrhSJSj6v0gLPu8Ojn0wj1YpAMeKIGHYhJcVzFSISY2rwOsBwbcnHQ+kCRYj7twXAc8wOQuxD35z1g5PIIhZwE0iumWrVFrqYTdmWOpUgdc8J38GFyS2BHAX/dK/H/5X8L9nMNuMTfDC76T1+TALbgD81XMKzPw8g/kXOZm98XMbjd+nXw1vBEzcoJSJbGNMqhP8AB2wGxxp0zMfE+UoJN8wmkyIKlpbG40i6l7L0k78KTyxHL/PL0aZ9TFzc25c7ewXsVSBet7Ea7pgEecHy/REx7Xmec3+A/oVcys5MxMBw1iNsiJE4RSMaXCR1xNKGJcQvJ1ZNj40sE03JTfff+wmGYZ/aDUBu/lhwnGJsHkuYF04EyauDXpA5bOFDyOvLKMHuFp9EUadkOvnTUZcNPsNYU6EJBZuCLm/0KPpC8vi8VisVgsKeYD2a2CwOuQfyYAAAAASUVORK5CYII=>

[image21]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEkAAAAfCAYAAACrpOA2AAAD20lEQVR4Xu2YW6hWVRSFh6iQKeSlC2HeUowgKSiDSAzBpB6MqCRB6MHQIgy6B9VDPUQQmGQJIqgURKQGQYlEPRwMtCzEhwQpoRJJVFQIFSq0xufci73+9e//UP4HFM4eMDjnrLHW3HPNNedcex+pRYsWLVq0aNGiRYsWLS473GS+ax4wvzBHmlebo/NJwxmvmefNTeat5gpzh/mP+Vw2b9jiSXOPOakYP2OeNecW48MOU8yfzVWloMii/YqSG0qMVxzIiFK4RKCtXGuOK4UEgkMwbi8Fxfg2c1Qp9IE/FHa3m1cU2qXAVoU/8P5Cu4Cx5teKCRMKjagy/kQx3i9uNI+Yr5RCH/hK3a3i/wBf8AnfunC9+ZsiGCVY8Lc5rxT6xELzr+rnUIBs7CcrqRKqZUA9ym2wIL2putRuNr81J2c6/WS6+aCaXxGuMxeb89Wps6GmTeX2mkofPWn0EDaE/0vMpYrn5c8p7bEmx1XmIvMe80/zgU65RjqFMkh3mqdUl8Qy82PVvQln1lVjj5ifmmMqDcyuNJx/VVH3SSetOYAcOMoF8Z7C3lp1NnXs7a405nxoLjc3mL+YHynWzEwL1G3vnWoc33nd+V6xr9/NE4pE6AkieE6xGMfuNQ8rMowgEXH61n1pQfX7LkUfI3D5Dcg71nHVm7xGcRCcNGgqtdPmo9nftyj6JbjDPGa+ZM5Q+Jae16vUyODS3icKn7DDbT690vZqkFJLYOFT5kmFMxjgZKcpgkdGcWJ5Kr+sSNGVioY5pxonWz5XbAQQGDLuRdVBKxskVy+lzGEwZ5a5pdJwfCDTsf+CeXel36D6WQnY+1Hd9h4271L4/XqabPyqyLT/BOqbB+S1yybLMUC2EcB0dZItgCziiidb2Njbin6QAtR08ly7ZNIhRca+odgYIOP4CijLMyFdAjmwh09N9ghGeRkN2o8uFvSfnxTBA5Rcek2Yq3hDJ91zXKkINJuCZOrjlfaQejdqPofOqFunFMkqAk4gyHIOhMzCHmtKsGZAMT+VMq3iG3OiYv3UarxvUI6bVTfi21SfFClOBuUnT+Bo4pQlm2YuDpH6gD5DA00gOJws9gnOUXNBpj1mrlcEhN7EswjMM5We+lZCskfZv6/O/kMJfqBYg82yt100nlXcDPQpHvBdp3whaPsUOgFbo/rkuEG+NFers8cdVNjaaP5gPl+Ns0H6JZtG32k+rQggGqXEWn7m9ii50h46gSVzeLX5zHxLsZ7/eHCjDymIOD2Mb7AmpG+hspeBpnE2QP8j29h8ifS8Jq2XD73sld9prB/0ZmvRokWLywX/AtvYv7ESclJMAAAAAElFTkSuQmCC>

[image22]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABUAAAAfCAYAAAAIjIbwAAABRklEQVR4Xu2Ur0sEQRzFn6igUTgQ0XBGk10Og2gwaBCjZrtFEIPFYDWaRBAMVv8Fg2bRIqholCtnEPHHe/fd4Xa+3uxuFfYDn3LvzezM7NwCNf+WTfpc4gM9oYt0sDuqhHl6RM/oB32np/Q45w39ybyiTQ2swgx9gz2gH030Jr+LozQHsAGzPnCE3rQPPCP0ElYec5lnDdZb9oFnir7AymWswHrbPvDorX6j2qRhpTs+8Kigot58GbuosNJx2D1UUdeqiAa9hXV1W5K06CesuOUyT7476rIIbUMllTWoiH1Yt/Dsh+gFrKQj0FGkUKZLr+61yyLyZ6TJ9ZAUG7DeF111WYTOsMrWw99T34Fhl/0hbP2RTsZRF32RlmCdPRRMOEDn6Dptwwbcw7an34KH9DXLF7JxSSboE3pvMWWHnsMWUFNT049fCEFe1A68p94AAAAASUVORK5CYII=>

[image23]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAgCAYAAADwvkPPAAABvElEQVR4Xu2UzysFURTHj7AQvZKyUo/YvNhZyM6CopBi4dfCzp6SJDs7KyvJgpWSP0D+ArFSSokFyYIoCwvKj+/3nZl5d+7MvDsjdu9bn6a533vPnTnnnitS0X+pCoyACTAG2kF1aEYK5cASeAQP4A68gG9wC4ZKU5PFLxkGr+ADLFveAHgSDcr3RNWCVdGJl6AQtgP1gDcwbhu+GGhDNNAVyIftkGrAPrgAzZZXFPPDQNyRO7s0Izp/1DYaPOMetFlekrpF1+zZxqRnrNtGGcUGqwNH4B30moZDPHuRYKzYMzgDjabh0IpoMD4D+YncMQcdYjUPJaYA/g4L5qBDLBKLFSkYk85gzEEa+Tnmmk7LK35RlmBsp0/RNZF26gdfYN4a7xDNZ58x1gRORQMdG+OBOOFctD2YWO62KHpTsOz0OMZ22xYNxDbKc3GcZqXURl3gBLR6HjfjHbYlGohHqMXzYsVdOfEGrEn4IPKreA7pb4J6wysrTmQhuIi/xOeURNuMG/CqyixeAkw6f5dioDmw671nkn+jXIv+Pp+s/qA5Ka1YYQYzORDN869kBnJW0yUWgQWZFm2niv5APzhlYlu0e/VEAAAAAElFTkSuQmCC>

[image24]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAHgAAAAcCAYAAACqAXueAAADwUlEQVR4Xu2ZW6hNQRjH/0IRRY5IlJNb4YVcTskjSsqDS24n7byQlAd1JC+O8oIoiZKcTp6UV6WTOHn0pkiJwgPhQQrJ/fs3e+xZ3zEz66y91t5bza/+7ZpZe+3/zDeXb2YDiUQikUgkEolEojyWiw7qwgB9olm6UDFG1C+6Irok6hV1ZZ7oDCaKtsF4pNdFMN47DeuTHtmvQZ/nRL/r+lX/HHYfcFgneo3s859FK9yHHCaLhkR3nTKas79Jg+OdunbySnRYNNYpWyP6ANMGtqXdLEXc5wh6RKtF02ECGwrwbJiRswFmBh5FOMA7RV9Eu1U5O2tY9FO0PlvVFjj6b8MMPs0hmIHMwd1O6PEi4j692E4PBVgTC/BVNGa7Zg9M+QACy0uL4OC+JZqgK4S1ou8wbW0n9PgYcZ9eqgjwIBoB1qZoyP5WnuVvvui0aIqu+AcbRcdF43SFB+YQbAdXJs0WGJ+bdYUHtvOkLvQwB/H8xcLnXiLu00sVAV4pei/6qCsw+gBPEl2H2c+nqToLV4LtoreiTaouBINiB+IZNJZAm0M8Es2ol8WghxrMvhhigegh8ucg9MjZ6/q0uD69VBFgH+xA7iVFvmsDPVeVs2PfoPheeQSNzrO6J+p2nhkNNZiBzZnlslj0THQC+YNrYRu1zx/I6bOVAd4BkxDwiFVk/+UAeYpGkPkOJhmxWROC79gF02G2876J9iGbseaF72M72T9b62XLRC9Ex1DsnaSwz1YFmEFhcPg7QUMRLoueiBbCjGpuBc2wV/RVdE10HtlZ0sxxjjOYM5nBfofig5rwe65P16P16aUVAWaCdAfmeMQZ1wzs8LMwKwE7jvt9UdhxekVZgmzncRAVhfnAJ5h3NBNc+nN90uMDZH16qTrAHF08pHOZsjBx6EKxRpc5gy/Av3f3wLQxbzs1Zc1gegydx61Pr8cqA8zZxoyRmaPLftFN5D/OWMrcg7lN3IC/DfRGj7ywWaXqQpS9B9NjqK+tT6/HqgJslxae+TQDolO6MEIVWfQgzLHNB+v57nm6IkAN5WbR9MCLjJhPr8cqAmyDS2NcorX4Xc7ivNjg3tcVdWooFmReqfoSqW7Rc5hbubwrTWywuUHOCz3aRCrkc4RHBpZ30QfQ+COBn9zbeHvi3kDxxTPrdbyTHoLZF3jo5jv4/NS/T5uliQmVmwS4Cu0pmiovOtgu+ulH9p6Xqw4Hk7sdxKCHGuLbBber0Vx08DkGN+YzEYCDtxed/3eh9UmPXCE71WcikUgkEolEIvEf8Qd7+R75vYCemQAAAABJRU5ErkJggg==>

[image25]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABgAAAAfCAYAAAD9cg1AAAABkUlEQVR4Xu2VvytFYRjHH1nkRxIlmSgDJYMkxWaRLDIog0EZjAaD3T8gGaRkMCiDxWC7mEwsbPIjJYoJi4Hvt+ece5/znHO7x71nUO6nPnXf53nP+5z3Ofc9R6TKv2IIbpXhAi8uRQ3cgd/wDT4Y3038EO7Dm2DM+LqkoBPewR4XJ9vwAw66OG+KBZZdPAYnbgR6euGr6O44z3MFB3zQ0wbP4YhPgGnRu5zziYBL2OGDnnF4Cpt9QrS/LODbE5KDjT7o4Q76fBA0wTPRApyTRNJ1qQn7zwJJ/a+YsD2fPpEF7GtOtAD/KZnTDZ9ECxy4nIWtGxbd7SachLWRGUWYEF281EFagSewC7bCPdHXR0nWRBf/gqMuZ3mWaJ47vzfjRGx7jmBdNJ2HcS5mD1r47FpMLIZtz6rLWbhYsQKx090PZ0RfCRdSKLAbxPnwGvKzFS6SqkA9PJbCoknywPHgWdolZYFy+VWLysXvjO+sTA/mI5w14zH4YsYVw+/FLVyE8/AaLkVmZABP8FQgf1f5Y/wAenBiNK8B7uoAAAAASUVORK5CYII=>

[image26]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAG4AAAAfCAYAAAAGJfifAAADH0lEQVR4Xu2Zz6tNURTHl1BEISIZPMmEGAj5EWXIgAzEwB/AyJBiwEQpKQz9GFDSK8VEKQY3AwNDZSQlhSJKoSg/vl/7rnfXWXe/e9897+zeuefuT33rtde+r332WmfttdcRyWQymUwmk6kp66BrJbVTms8c6IR0P/tUdAZaIIm4Cv2FvkFvjThG/YZeQDfb47+MbZ80Hwb2Z+k8s90j3Ysv0ANoHHoNfWyPP5FEjlsGvYS2Q7OcTRdKx1oWQU+h79BmZ2sixyTsw2UpOoF/0zG00bmeP9AtP1gV56G7flDCorigD9AaZyN8mBa00I03DT77K2iHN0jINhrcMfhC7PeDVbAEegbt9QYJC+aCWhJ3zmlJGE014ij0CJrvDRKCvpfjeLwkyUi7oOcS0qVHo4mL8zCl3oFOeUMDuSchu3hsmmRWikHHrfSDVUCHrfeDbTSaDniDBMdtlLjDm8YmiRcXzEh0mGalGHzbfN2QFKbGlkx+vmVCQGuajGWlGUEXVZsF1QwN7F7F24ygaXIU7mhlsGky2T1tUGw0pYik2dBhaLUbHyZSpUlW97ehDRKKGtUKaK6ZF8VGU5WRxHL6CvROhv/ibq8BVWYlVun6f63YtYld8AvYaKoSOm4PdFaG33EtSXO+3YDuS6fPeR16A52UPtWpP3RTwKgq4zgG1EGJX4QttHPeIHP5v/umIkOKNMm1bJPQ0CbLJdwhxyZm9MA2U3nopqCs43SzLnmDg3ade65oKsAIZu9R5w7STNDfVJkmPewPH/GDFjrrUFtsYemi2GfT8bUTs6fPdB3HKNSojPFQOnN7teRs54NimorBN4EO0r04LmE+G8gXzXi/t3tQHkto5kdhhTcuncVPplg0zpNi5RPTUunOzWUdx6LmE7TbGxy0cx7nb3E2Dx3yVXrPZXP5p3Tvidcq/YGBz+/3JCYPA9N/kakMPrT/aOh1AVqsP2hT1nHDCJ/f70lMHl4H2NyuFaPkuLIwbfMDQK2g435AW70h8x8eQTynaxPYLAb4ed+eC+8LMzJEi6baOC4zdcYkFI6ZTCaTyYwU/wBp0OvRxxE+BQAAAABJRU5ErkJggg==>

[image27]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEIAAAAcCAYAAADV0GlvAAACsElEQVR4Xu2XT6hNURTGP6GIgSJSL14ywYT8S8/gyr+BGPgTYvAyVoqQlwHKiFKSgUSMkCEpo5ehmSIlCgMGBlLIf77vrnPe3Wfdc+4797yro+xffb1aa79z9/72OnvtA0QikUgkUpXJ1HbqInWJOkmNy4z4t9DcdvlgDhOp82itaVkSy2URdYAa7+LvqfvUVBevgxvUR+o39Sv5ey0zIssS6h11JIjJgFuw/10RxJvI2QuwivDsh/3oOp+ogQasYrVpc6hX6GzEadiCpZCtSewqXMXPoJ5Qk8JgwmrqO3XUJ2pmNkY3Qrk8IzYnsWG4Sk8fuiEMJqTubfGJAmTmKWqxT+TQBzuPqlDGiAHqA/XZxffA1nTZxZuTvwtLnkH2FdH58JiaGcQ6oVIbpN66uGc+9Yg66xMlKWNEHpqfXgmtdb3LNdGAtIxS/aD6gzHdoOdpN1RRIQuo59QJdDi5S9CNEZpLgzoOm9M36nA4wLMbtvjQjH1o7yRl2Ul9orYFsZfUMVR/Zko3RkyhXlNvqJ/UdWphZkSAXPtCXaHOIWuG+m/V3VNFaBdkynJYK9NvjZVujAjRK/kMtq62LqmJaYLhJOXYQ7TMOJTEq7AJ1v+1G70wQVQ1QqyBdcK2DdatS3eFPFbCylta6nJlqLMitPtqldNdXC1zGLbBI9cCvas3YQvNYwJ1G9aCtJBuqPOMWEV9hS32qcuFRgyFCT1MpVKE8mqH83yiA9r1OrvGQbRea7/Js6gXSU53ihH04aJg3sT6Yf+ky4eqowwyYRDF1/JemDGaEWFF3CvItd2PNJn0y8yfpA9gp+xcFy8iNeFvXKimwQxowO4BWow+DXbA5qedTo1N16SWuTaJCbXSOzCDNgbxDHrQXrQ+w3t1sNWNDstwTarIsZ5RkUgkEvlv+QOC8qaehAdfHgAAAABJRU5ErkJggg==>

[image28]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAIYAAAAdCAYAAABv2BQRAAAElElEQVR4Xu2ZXchlUxjH/0KNiBlqhtDMxVDIVz6mSBgfU4oUNYoL5YLcTUKjuZnkxoWEKClfN0ymFDck8zYXEleKSKOQCA2Zosb38+vZT3ud9e6z9zl79unsi/Wrf+85a+39nvXx38961tpSoVAoFAqFQqFQKIyVi03354UZx5puND1l2m26tCobG0eZLjE9L28rbR5jO5s42fRQXphB/86Wz0Gf/h1nest0fF4RHG063/SK6T/TykTtJJeZDlR6zPSp/B6+X51ct2wY2L2m3+TGeLn6TDvpw1jZIDcEbf09q0thUp8w/Wt617RLdf8uSq5r4wH53J2QVwD/nErEj7QZg2jys7zhx1RlOHSP/L5fTZdX5cskngSMgUECPr8vb+fYuF6r52GaMRj75+TXMBdEDtho+lI+R+dVZdNgnhiHqcYIqFxRuzF46qLxfA5uTspXkvJlQVg9aDonrzDWmT5Wx2AsmYfVboxntHoOAqLhH/L+N4GJdpreM72tgYxxpTxc8cO3JuV3qjbGC0l5FzvygimcJF/qZiUM/KhWr7lnmL5T/ZSNkS5jfCavZynPudB0SF7fxBbTV/LoH+N0xMZoggF+UX7fP6YbJqtbIXxuV/skEf7fkZtjVu5VbdQPsjrW1b+ysrHRZYyYeK7LOc30jaYb40P5GMBCjYED6QD3se7lT2gXP5juVrM5TjXtN72aV3SwUb7WhjlYh8m87zH9qXpgxkqXMSIH6TJGPqZ8J/ciB4PBjcEPnGK6Rr60MNgPan5TAFGDZInJSjuSmmLqdqoFttD83zDH36afTDdp9YC1sVY+2H3Vhy5jRJ+6jJFP+FWaTEoHNwYT9ZrpW/nywTb3XM034ClXmH5RnWGTB5Agkq+Eu/twliYjB2IwTkwvamGN6SV5P/uItbwPizDGetMnyXcY3Bg518rXbe7lzKAv7L+/Vr8lKSW2YmTe5CYY4WlNGqR1MJbM0MYg7yNh3VR9DxZujA3yp4N7iSB9GcIYGIEEi/ZsmqzSBabP5e28JasbE13GmCfHIPoSKbanF1UMZozN8jML8ouU9F7UB3ICcoCd8tNUIk8fc5CzMHCcZTSBcfareVDHQpcx2nYlsR2PeUjPmLrU9P9mMsZheT1PHQ0IjtQYQ+YYd6ilkxWcuzySFzYw1hwjzjGazox4NxS7RDjddHuD7jJ9UV3HZ8qaDgRnMkZMPD9MA4J0KZnXGEPvSuIJaTr8CbgGc8zCGHclcfL5hupXE0FEzGknn0E6341LCYUka/eZvpdfyF8yejrGUxMQ3qm7TvUkMmlxtEpHtlXls7CoA67b5LkOxkojG9Fnt8Z5wMWyyZjz5PJSjPFkgpkb5gGDBum7kl3yl6AQSXfbuxL+D79DlIhchM+UTRgk3DlNPF0BJmDtpvxH0+vyvIDvH8m3rPOwqCNxjIbhOGOhbRj6Tfl5C4O2tb50NBCB87FPRaKYkr5d3Wd6Uv6q4oDa365GwtmktuV3Jkg+cdmz8ryAtSlcOyaIdkQljPG43BB9Etoxc6Z8GaZ/WzTOeSgUCoVCoVAoFAqFGfkfaX6XGQJHr9YAAAAASUVORK5CYII=>

[image29]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADYAAAAdCAYAAADhLp8oAAACk0lEQVR4Xu2Xy8tNURjGH6HILZdcSshAYSATyphSYmKgMGJABiaKkvQZ+AcQJZKkDAwMXCYGLiUxZWhgIAMhysT9/Xn3ctZeZ++z9z59n+8r61dP55x1Oed91rv2u9aRMplMJlPPbNN+08VCvJ+ITDadkcd4yrTKNKk0IuGj6bF6xl6YDqlh0j9mqemJ6bI8xtumn6Yb8aCYDaZdKptgZb6ZdkZt48kc033TpaR9kzwpU5N2LTG9ThsLrsr7GDPeEMsr06K0w9heqEQwRtZSbppemhakHTVsNJ1UxeolzJM/J2yttmDsl+lo2mHsMW1OG+eanpu+mvbJt2Dgi2kk+tzEYtND0wUNNvdI/owMGpNyWm4MUegCbFG+j9c+jqg36Zm80qw23VPNhAGQBRaKwKvAfFdTsF7+LBHjJ3lNmGW6ZnoXjSvBjwRjiEpDBqfHgzqwXF5V0+Axxeqm7W3ZJt9FIc7vprtq2NLHVTaH1pRGdANzcWaCqbpMtoUqGMeI0d2lEQVTTOfUK+uU/C3y9DKR17VFX1dC5tgBlOlhdwBQ9dhFYWHIEuU/GDxYtP8FEz/UfxDzgIZJI+Wu1oyWsYXy7+H5nx+1U+goeBh+qqgeYOaKPJ1VnJUbu2OalvS1gWBY4WUarhIGdsjjOJZ2FGw1vZcXvD/MND1Q/QHNQCaQ8hlJXxOj+YxhCGN9h3ABi/7ZtC408HxxCBN8FStNb+WHaRfCFkyzM2xV5ADG2IG0owAffReJcAfjNh8fzmzTD/LCwsQ2jNUBTSzcODB3PemjiPB9lSWfKw6T3sivLoj3J9T+x2Esr1Qs+mF5obglXxguAsRNYmpZIU81E86b9pZ6Jw7ssPB/jCw2/h/LZDKZ/5vfQqeR5LaNrwwAAAAASUVORK5CYII=>

[image30]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFEAAAAbCAYAAAAETGM8AAABJ0lEQVR4Xu2WMUuCURSGj1AgGFQUhJu2BYKE0CA41ZBDU6N/wMHBrV/Q2B9wcRX39kCItmjRqSFx1a0x6z2cK1wOnyZOfvg+8MDHey/fcDj3nitCCCGEEEIIIWSHuIYFmIky/S7DqsuJQ4vTggP4CztwP6w1QzaHtyEjCdzDD5iHX/AbVsLak1gR1YeQrUKLfyb2r008kRR2/A18hcfwUaxYb/DQ7fmB9SgjEVl4FL5HYkXUox2jXTmE5y4nCei9N4UXLm/AF3jgcuLQe0i78FmsO+O8K+vdhzvPqSQPjyJ8hyWXL6MGP+F4Q3swJylFJ6oW8c7lOrUvXUaWsCdWRD26izdiAbYXG8h69MUKOYMTsSGTujfbNqDPnSuxOzAeMIQQQsi//AHe0zcDJNkz0gAAAABJRU5ErkJggg==>

[image31]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAGwAAAAcCAYAAACERFoMAAAEOUlEQVR4Xu2ZXahNQRTHl1DkK5KPIjdfhUL5ingRSuLBR4gkEkkURT4eUFIoJRGJ8CClPOBFHsSLeCJSolwpSZJCvlm/O3s6s2fvOXfv49zOLfOrf7dm7bPPrFlr1qw5VyQSiUQikUgkEmmNrqrFqhOq06p9qg6pJ9oHPVVrxcwRDU6bG85T1UZVP1VHz1YN/MAf1n+lqn/anGaMaotkv+CD6qaquzfeKN6rFkg6kZaqfqlOqjo5443iTwF9Tp5lvsz7kWpEMgYzVN/EPNvZGW8B54+L2WE+m1S/VbN8QwNgfszT3/V2/gRzlGdrBATjglQqgKtrYpJre/LsUNUbMYF5nIxBD9XdZHyCM95CX9UTVRffoExX/VDt8A0NgGAc8wcTVohxbr5vaAAH/IEEEo6AnZPKriEYBNjddUBFu52MZ3waqGpWzfENykIxH6IMFYGg71eN9w05DBJTr4uCc8yT8u2zS4zDmWwMMEx1yB8MMFe12x8M0E3MmvlQBdhVnG9DnHGCSACpYted8d6qB2LWfpwz3gKLfEOM8bCkSyPnF1uVA7QITGy1mG1ejeGqh6ojvqEKVALm+F21WSrnLYFnIcjevLKeBwt7UdXHNzjgyxLVW9U8zxaCz+RVqtli1mSybwjg7rxcn/gieyBa/VQ1Oc+Ugfd9lGy2Udaeq/ZKzmFaAA5of57npfrCV4OgkZBu1rtJV4+zGz/vqXr5Bg8CvUzMJsGv+6qRqSc8losJkrsYayTbORaF7o0sWeSMvVTtlNrfyecof+4c34nJYL8ZKQLZe0b1TCpBo9EiWNPsQ/8Ic+OdrUFyvFJ9Sv6uElMJcsHZr6qzqqOSXhC6m1p2A7DD2GkEb5KYOl7Lwlr2iCmJB1WXJT3PWt+Nb+xcyiqtNQlQtHS1hm00ynSvJCVJjU8ELoM9EF2HR4vZknYxtiXjtUD9J2toZ2tZUAsLyzvsjuVdM6UyR2xFmyMf3s15yuE/0bP9C1PF3KdoIsrAfK6K8SuTPLTKTDSPKWLKWpkOzKVeO8w2RqFM5f340Cym6y1DW+0we11i0UOwAymZrK1/TNDO89mUTzxEaXHvAC7cxK+ovohZ8DLU8wzjwObgDgXDtsGvxXSNRWnLM8zeYUMBY20vSbiK2YBlfKLL4sUhsOMAt/Ki1LtLtBfJ0BysnaCVKT9t2SXay3woYP1VL6TyDOvssjUZz/hEK4khbwGbxLyULCz6O511OORsrUHbK+GSakt3mV9k6MDuqAb4BqlP0JhLtYC5O4xGyk1u17bBGW+BRaMT3CfZSxoOueWiNVxHqzFcyl+cKYs0FmslXVLHikmqW8kzRWiri7MLO6ZawICzkh/YT4n5L4SF77M//gZ/eGeLrpTKv1dC2dxImE+Tar2033+vWEgKzut1viEHEpDGA39oAjm/iiZfJBKJRCKRSOS/5i+CsAWDy5I5uwAAAABJRU5ErkJggg==>

[image32]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAdCAYAAABIWle8AAAAgklEQVR4XmNgGAWjYBQgQBMQG6ALYgGMQBwCxJnoEsggAYifALEJmjgyABmUAMTPgdgMVQoVgBTmMOBWSLRBMADSUAzErxlQNZBsEAxgMzCBgQyDkAHM0L9AbIUmRzKgmmHIXjVnoMCLVAszbAbBxBMYSDAwgYGKiZaq2WkUjAIoAAD6FR4oHD9AQgAAAABJRU5ErkJggg==>

[image33]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFoAAAAcCAYAAADhqahzAAADqElEQVR4Xu2YW4hOURTHl1CUiBSKDDHl8kBuuTyJSeTFJUSapEhTiiKeZsgL8oCQRLwpnqQk8eXRE0XKpZAIIYXcWT/722Z9++wzc8wczZT9q3/TrH05+/zPPnut84kkEolEIpFIdDWTVZvCYDeENfYKgwE9VHtVx1UHVQ2q3jU9WlmuGhDE6DtbtTqIe/qqlombv0VVL+6aUd6oflb1o/q3Yjt0Aw5Idp0VVT/Tx4IBF1Tng/gg1VVxmynEz231QbVSsuZNUD1RbVb1NPFZqreqyyb2B57IdNVgcYvvjkbPkOw6UZ7RC1QvVBPDBnExdnfId9Vzcff/RXVT1b+mhwPTD6suiXugIU3iNkIuLLoi3dNoi18nyjMaI1+rxoUNykCJj43FYvCg76guqvoEbTBH9TUMWjprNBfdpZoUNkQYrjoSBgtSxOjT4u5jd9gg7tqnJHscVCR/Pssw1WNxx0pD0AZLxF07l84azcIbxb1+bTFGdUu1P2woSBGjN0jrOVtX2yRbVfODGFQkfz4LG4rd7OffZ9oYz/l828QydNZoD4a/E/dkLbzGD1TNkp/5i1DEaJiqeiWthnxTvVQttJ0MrPmp6oy4BIgYd0yyZzH3yAPzc/v5r0n2wWYoy2hYIe7VWmpij1Q7pDZLd4SiRsNYqTUDcazEIBna9QIJjzFHJVtKYvYqcQb7uUmi66SdeyzTaGBHs0swfZpqm2TPxY5Q1GjeoLuqLVK7s9FI089DaRauj/qZ/p9VM02cfmtVn1Qnq32sqKtzKdtoWKR6L263hDfRUYoYTRlIPXul+j9l2iFpNYJzNG+sxVcQjPElIffBpqGE85tnvOpGtZ9XLmUb3VU7ep44E2K1MvD1xz1uNzGSI/kjrJimiDsCvSdcj3mZn+vEoOZnDGOjlGl0V57RPolZI0No32n+59wm1mxiwAb5KK7N181npW0jOcvPiRsbpSyju7rqWCzuHvaEDQba7e8XJ8QlMsZafE2MmqoxHgrHCcdKHvQZHQY9ZRiNyY2S/1qVYXZ7Ro8SV6bdk3jS47r3VSNMjLcg9hXJpzZ+kFT50AH/xpDwYvdQp3oo2Srl95cOyWOj6pm4SfhLaURb7DMzhjf5X3ywYGi4TrRe4uvkyCIBXzcxoB5ukWwZR5zEOdTEuB/m4JggqXswF5Pxiblsjc3D4Jo85P8KzPc/k/J3rsR3IWBsvTjz/M+e7eWTIao14vqT7BlfRsJPJBKJRCKR+Et+ATyUA9d7rBcEAAAAAElFTkSuQmCC>

[image34]: <data:image/png;base64,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>

[image35]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAJQAAAAfCAYAAAABS+TPAAAFb0lEQVR4Xu2bXchlUxjHH6F8y1eTqNdICeOjhIiaRHHhIxSFUpNobiaETEmSOwm5kEiSRC5cIEU6RRJuKCVSL5ELoYSSz+f3rvdp1n7etfZe55y1z9nTu3/1b2b2s88+/73Os9Z69lp7REZGRkZGRjY9+6t2+4Mjm5aDVM+q9vGBEkimR1Rv+8DIpuZc1fUyZVJx8j2q71QnudghqgdUT8+gnap9pQ6jj+Xxu+paf7CNi1R/qG73AeUS1b+qfyQk3Lfr4u//ret71cuq11Q/R8efkXqYD67rfeDN+yBmXvryEbdJ7AP17WORPKf6UrXiAynocRPVR6qjmqE1npDQUEyJMVskNNLnqqOj44x2963H7o2Oz4v5uMMdx8fXEr4v9gF46dNH3CaxD9okxtqkpo9Fsk31i4QOsZ+LbeAt1Q+qE31AuVD1hWqrDyh3Smi8G31AOUX1lepsH5iREh8oRU0fUOIj1yY1fSyayySMzE9KRz31p+pVSWfew5K+AOfyGebWVCOdo/pMdawPzEiXD35EvKSo6QNKfOTapKaPRXOcanVd/D1Lbko4QvWBhJrBY8P7xxLO81wjYRplOp2XEh/cA15STKSOD8BLiY9cm9TysQys0zBKpdpgDU7KnUB9QE/ztRMwBf0loVjzvRXoiaf6gzNS4oMfEi8pavkAPJT4yLXJ3s5uCffIn0koYn9VnekDHTCi5WqFRWI+lu1lKD76hlGWe3zRBwx6vn9K68LmUi6cGtoXRewjN80silUZho++IV+oEZnekzDVTWS6uT0e3pdJ7GPZ6ztD8dE3llDf+IBxhUyfUPHw3sZhql0SVoj7qB+mnWZOUN3gD1ai1Mf9EtrjQdm4I7E3UD2h4sdjaq8cK6pPVbeqDpCwysqeUC28j7YakO2OdyUsRj7vYjXAS4kP7p8dCc55Q8Jn7pJ0AT9UOhOKtZGJlCeUr1tS0MAM/fHaFutILKAeaCfNiffRVrdcrbpAwtZIHwmFly4f3DfbMLaPx44EOxO5Nauh0plQx0t+lTxFyTRDXcZSRLy2xUiY+8zJEnrqNFNAiY8YplwaoS2hZvEBJU+8LDU8Ls2p3z6XWgP0nKXa4Q8mYLTjRy8pMTiXa07zQGa/IyNskkMlbAozUnXB1GVDddvwbl+aSijfeFtlzybzjy7WRomPmJKEin2c5mI5rE1KfRg2ZTMNb2+GNnC+hN0MvDGytbFdwjU/VB3eDG3gUgnXnEj5DHWbhM+wn5mlrXcdqbpKdZ3qbtlzY/zJvznO01b8Skaq5+USyoZQYihH7AOlfKDcqyElCRX7YF8uR+zD2sT7oE3aOE/C91GgpxZKY6ztUNdMYj8499o1SnGPnMsSwBYXy0Ep05Yva7RlHE9FdjM5TaSZ4Wa0JKFoTPbGmCL/drGYEh8o19NKEir2kWsP8N+Z0sROTsDI8Y7qBdXBLpaC81+X4M1q0hzHSHhBkjchuop9yh17a6LrXGA2e0/1k4SN7iwM9W0F5bSkkid1zNMWm5eShDLwkd1amBM60FOqR6XeA8qi2CbhFRameKb6LDbt0ENLMrULewKLezlDcVtmU0t94g9WpDShzEfO5zzQtn40oF3Yzhg6jJLUZSW12doNPib1HmG53kPS/PJXVC9Jesi270d9YQnFHlSu08Q+cufMCte7RZpvmtqbnF311hC4ScJuwJU+kMPWRfjhu4rEEkikNyW8Bnu56n0Jc3aKmyUsgq74QCX4EakVrL75TXV644xAnz4soX2tNc2SzTJhYbrkAaIBax2rEv6zQg144mLEa3v6ouderDrDB5bAUHwMDZKIh4jOqS4FSUU2jowAHZ4BhmWbkZGRkZGRzc7/+VK4O3QL2zUAAAAASUVORK5CYII=>