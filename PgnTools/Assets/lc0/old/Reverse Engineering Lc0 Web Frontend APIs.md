<!-- PGNTOOLS-LC0-BEGIN -->
# **Architectural Analysis of the Lc0 Distributed Training Ecosystem: HTTP API Endpoints, Protocol Specifications, and Data Interchange Formats**

## **1\. Executive Summary and Architectural Context**

The Leela Chess Zero (Lc0) project represents a paradigmatic shift in the development of chess engines, transitioning from the handcrafted evaluation functions and search heuristics of traditional engines like Stockfish (prior to NNUE) to a deep reinforcement learning approach modeled after DeepMind's AlphaZero. This transition necessitates a computational infrastructure of immense scale, as the neural network requires millions of self-play games to learn the intricacies of chess strategy from tabula rasa. To achieve this, the Lc0 project utilizes a distributed computing model where a central server orchestrates the efforts of thousands of volunteer clients.

This report provides an exhaustive technical analysis of the communication protocols, API endpoints, and data structures that facilitate this distributed training loop. By deconstructing the source code of the lczero-client and lczero-server, alongside the configuration configurations of the training.lczero.org frontend, we identify a system built on a robust, stateless HTTP architecture. The analysis confirms that the infrastructure eschews persistent WebSocket connections in favor of a polling-based RESTful interface, a design choice that prioritizes firewall compatibility and horizontal scalability over real-time bidirectionality.

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

* **Search Exploration Control (cpuct):** The parameters cpuct and cpuct\_at\_root control the "Polynomial Upper Confidence Trees" formula, which balances exploration (looking at new moves) vs. exploitation (playing the best known move). The server specifies cpuct\_at\_root=1.414 (approx ![][image1]), a higher value than the interior nodes, forcing the engine to widen its search at the root.2 This ensures diverse training data, preventing the network from getting stuck in local optima.  
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

The V3 format was a direct implementation of the AlphaZero paper. It stored the probabilities (the policy vector ![][image2]) and the result (the game outcome ![][image3]).

* **Limitation:** The game outcome is noisy. A player might play a brilliant game and lose due to a single blunder at move 60\. Training every position in that game as a "loss" provides a noisy signal.

#### **4.2.2 V4: Search Statistics**

V4 added root\_q (Q-value at the root) and best\_q (Q-value of the best move).

* **Implication:** This allows the network to learn from the MCTS search itself. Even if the game was lost (![][image4]), the search might have calculated that the position at move 20 was winning (![][image5]). Training on ![][image6] helps smooth the learning process.

#### **4.2.3 V5: Symmetry and Invariance**

V5 introduced invariance\_info replacing the move\_count.

* **Implication:** Chess is spatially symmetric (mostly). A position reflected horizontally is strategically identical (swapping King-side/Queen-side castling rights). This field helps the training server perform "Data Augmentation"—generating 8 training samples from a single position by rotating and flipping the board—without corrupting the rules of castling or en passant.

#### **4.2.4 V6: Uncertainty and Active Learning (Current Standard)**

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

**9\. Conclusion**

The Lc0 distributed training ecosystem is a sophisticated application of volunteer computing, underpinned by a pragmatic and scalable HTTP API. By avoiding complex bidirectional protocols like WebSockets, the system maximizes compatibility and stability across a diverse global grid of GPUs. The complexity of the system is encapsulated not in the transport layer, but in the data layer—specifically, the evolving binary formats (V3-V6) that compress rich chess knowledge into compact tensors.

As the project moves toward Transformer-based architectures (BT4), the API has demonstrated the flexibility to handle radical changes in model definitions and input representations via simple version negotiation and JSON-based configuration. This architecture serves as a robust blueprint for any large-scale distributed reinforcement learning initiative.

### **Appendix: API Specification Summary**

| Endpoint | Method | Function | Payload Highlights |
| :---- | :---- | :---- | :---- |
| /next\_game | POST | Task Assignment | **Req:** gpu, version, token **Res:** network\_url, cpuct, fpu\_value |
| /games | POST | Data Ingestion | **Req:** pgn (text), training\_data (V6 Binary) |
| /networks | GET | Weight Distribution | **Res:** .pb.gz file (Protocol Buffer) |
| /active\_users | GET | Monitoring | **Res:** JSON stats (Games/Day, Version) |

#### **Works cited**

1. lczero-client/lc0\_main.go at release \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-client/blob/release/lc0\_main.go](https://github.com/LeelaChessZero/lczero-client/blob/release/lc0_main.go)  
2. Training Runs \- LCZero, accessed January 27, 2026, [https://training.lczero.org/training\_runs](https://training.lczero.org/training_runs)  
3. C++ interface \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/backend/interface/](https://lczero.org/dev/backend/interface/)  
4. Neural Networks Chess | PDF | Function (Mathematics) \- Scribd, accessed January 27, 2026, [https://www.scribd.com/document/724676477/neural-networks-chess](https://www.scribd.com/document/724676477/neural-networks-chess)  
5. Training data format versions \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/training-data-format-versions/](https://lczero.org/dev/wiki/training-data-format-versions/)  
6. lczero-client command \- github.com/LeelaChessZero/lczero-client \- Go Packages, accessed January 27, 2026, [https://pkg.go.dev/github.com/LeelaChessZero/lczero-client](https://pkg.go.dev/github.com/LeelaChessZero/lczero-client)  
7. Bad JSON \- you must upgrade to newer version, accessed January 27, 2026, [https://groups.google.com/g/lczero/c/8SQzKe5AFcg](https://groups.google.com/g/lczero/c/8SQzKe5AFcg)  
8. Leela Chess Zero \- Chessprogramming wiki, accessed January 27, 2026, [https://www.chessprogramming.org/index.php?title=Leela\_Chess\_Zero\&mobileaction=toggle\_view\_desktop](https://www.chessprogramming.org/index.php?title=Leela_Chess_Zero&mobileaction=toggle_view_desktop)  
9. What are the T40 and T60 in reference to Leela Chess Zero (Lc0) chess engine? \- Quora, accessed January 27, 2026, [https://www.quora.com/What-are-the-T40-and-T60-in-reference-to-Leela-Chess-Zero-Lc0-chess-engine](https://www.quora.com/What-are-the-T40-and-T60-in-reference-to-Leela-Chess-Zero-Lc0-chess-engine)  
10. Mastering Chess with a Transformer Model \- arXiv, accessed January 27, 2026, [https://arxiv.org/html/2409.12272v1](https://arxiv.org/html/2409.12272v1)  
11. Transformer Progress | Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/blog/2024/02/transformer-progress/](https://lczero.org/blog/2024/02/transformer-progress/)  
12. Unsorted docs from GitHub wiki \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/](https://lczero.org/dev/wiki/)  
13. CLOP tuning \- Leela Chess Zero, accessed January 27, 2026, [https://lczero.org/dev/wiki/clop-tuning/](https://lczero.org/dev/wiki/clop-tuning/)  
14. LeelaChessZero/lczero-server: The code running the website, as well as distributing and collecting training games \- GitHub, accessed January 27, 2026, [https://github.com/LeelaChessZero/lczero-server](https://github.com/LeelaChessZero/lczero-server)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAdCAYAAADLnm6HAAAB/UlEQVR4Xu2Wv0scQRTHn0iiotHOaCRICgUrhWCMokEsUiSooFjZ+QdYWCS1hSTYKCkEo+ipiIWVgkkjYimkEn9EQgQVksJCsNAijfl+fbm7uXfr3u2xbkD8wIeD9/Zm3szszI5IuFTC2oCGyhRcCWionMCBgIZGByy2wagogks2GCVt8IcNRkUhXIAfbQLkwWH4GU7APrmFZWqB+7DGxNnRLFyHW/APvILnooWFQgGMwXETfyA66g0nVgoXRYvodeI581B0hJxay3vRjqhlRDTebxNBaYY7sNEmJNkJ5Tviwo4ZZ5E5wymeEZ1mL+rhL3hpE5JFAU/hSxs0NImOnr9B+SRaQJdNkHY4CddM3MLRc/2D8hh+h7uw3OSumRetjlumzuRc2AC3X1DiL2erTZBRWCXaOB/6DZ+kPKG8gK9sMAPVoiflBXxrcglK/v26W2gwmU4wLboFs6VM9DDil9Jrx6TxDB6KFrBncmzgwMQywZ3C9twl5WDjA/bkg3gfJHxBl03MD27XbdElcOGJOGRiKfCqdCapBTwXXcfXTswPnvfvYIVNgG+w0wYtPExYQAw2wJ/wjfuAD2w8/vHx8lj0/uhLPvwCT+EYXIWPUp64mTlJ79R1UzK8A3F4zeIfjiSLKbsNeNViAV9FP6f/BRbQY4NR0i16mNxzN/kLW9l3FNVwB6YAAAAASUVORK5CYII=>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA8AAAAeCAYAAADzXER0AAAA0ElEQVR4XmNgGAWjYEgAAyD+TyTuheoBAzMgfg/EK4F4FhCfAeLnQDwXiA8A8Wsgng+VA2EJsC4g4Icq6IPyGRkgCluhfBC9FCqOAfyBeCIQs0L50kB8F4j9oPytQFwFZeMFLEC8nAHiEh6o2Dcg9oUpwAd0GCB+hzkZBEABlI7ExwkagPgfELsgiYE0E+Xs60D8gAHibxgAaV7DAPESTiDMgN2JIJeAxCPQxFGAPhA/AWJFNHGQa/4yIEIfKwDFozi6IBAIMEBchTWeR8HwBwC2Ky3l227XQgAAAABJRU5ErkJggg==>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAhCAYAAADtR0oPAAAAv0lEQVR4XmNgGAWjgDZgFhD/R8J3gVgTiOcDcSUQMyKUQsA/ID7AANG4B4h/MUA0gtj8CGUIEIDGZwbiAiCWRxPHCYKA+By6IDYAcms4EF9jgPgBLwApTgDi00CsgiqFCUCKc4D4MBDLoMlhBcVAvAuIhdDEzRmweBxk+nYGTMUgcB2IXZAFDIH4NRBHA/F5BtTIA2EzhFIGBk4g3gjEZVC+BAMkomCKT0HF4YAViPWhNLIYyFYFBizJYRQMQQAAFbsg0kJlTh0AAAAASUVORK5CYII=>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEwAAAAfCAYAAABNjStyAAABcklEQVR4Xu2WPUvEQBCGX1FB21MQQfAK4bAWFcGvws5GOLHR4sBasPJHCFaCqJ2VYGVjZWkpaKUW2mjjP7ATfYdJ2Nzq4W4QEnLzwFNksinyZnYzgGEYhmF0OcO07heN39mg7/TMv2EofXSZ7tFL+pVogQXyCgssCgsskqjAeugivaG3dCmpyz4/oNvJdZWJCkwOvvTQEz9pL23SezrmllaWqMB2oV2W5QUaWCiz9IieBrpP1zPK80USFZjPOG3hZ4hVJndgk/QR5Q1riF7TtxzuoDO5Apujz3TVv1Ei5GydR/t2DnGNjqIz0YGtQDtLQutGogK7oxNeTb7kIT2Hjhd/0U9HoF8xjwMoluDAZqCLT+gWdISQl5c/2QddcEsrTVBg8jd8opv0Cu2zmCjzWVkP//9ARpn0jJPZU975AZqH1GpuqXbRBXTbCYP0GC6sFnRbVhnpJr9Jsk65pRrYNDSoFOmmOm1kaoZhGIZhGIZRGN9kf2u7sz4wKwAAAABJRU5ErkJggg==>

[image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAF4AAAAfCAYAAABu1nqnAAADiklEQVR4Xu2ZS6hNURjHP6GI6x0p8sgjJgaihBQGJgw8ojxSBoQSAybKNVAYeOVRiGRCzCQGBhclMZAiCgNSii7RpZDH9+tb65591t7nse/Duedav/p32mutve45/7X2931rX5FIJBKJRCKRTqKnapRqnmqku24Pc1QTVX3CjkiBlarPqh+qN+7zpapHclCVjFXdU71Xtaj+qM6ohiTGRBy/VGdVfd11P9VF1bLWEdUxRvVMdcRds3DzxRbzlh/UXVmsalL1D9pL0ag6FjY62K27wsYSTFE1q7aEHUqD6q5U/53qkjzGD1Y9lNLmYjw7lSegEtNVX1XHJR2i+C5N7rPbksd4v0uXhh0OjH8rlnQr4Y0nbG0L+saJzVOWyaqjqueqm66NTH9Cqlv5WpPHeCqPn2L3ZIHxmImplRiqeix2D/KL1Vt1WvXBXWeyW/VbdU41TbVV7CaffOqBPMYzFpPKGV+uP4RkjFfcQ2WEfxfEKhySbCabVA/EVi7JN6cZQXsWs1UnxRarkhi3QbXcaYHY7mgvtTQeBohVRP5e/9SsSg7yjFa9EFuhEG58qhoWdnRRamk8OYNy8o5qp9iu93PwJKTAcDqzYhntV1W9wo4aQey8LVYbZ+mj2I8kmYV9aK0U6Ejj+V7e9IGubarqkRTmIcm2QsKkZKKD8ipJg2vfEbTXEsIRYcmHqFAHxAqDNRl9iNcBnoViOa2Usfx2ki9JuBKNYnMtCto5lB0Sm2tjsoMv8tp1hPDoVPuHuwp5Qg0FxBfV6rDDgSfNYj6Uw58HSoVkogXFSVGBUs74fZIvzPBCiPnaokHSMeQxnoPOeSl/cg37SJ7rxcKyPygRGTiZEmpGuLYQNm/Rjses65I2fqbqk5Q+1XVV8hgPc1X3pRCXPVx/V80K2q+JeRX2Nbp2QlwIC8T7m6IYD0vEEpIv56g5KfiZiDhYT+Q1nt/M76QK8TuYT65PSfppfyWFZJnMDcNVT8RKR/z0MP92156CP7RZrCKg2Ke0PCw2eTXH5a5EXuPhklhivCJ2trjhrrNO6vhEmciY8CnBK14J4xsHp8tiUYPx6xLjUhBrWTn+CUB8Z4J6e5HfFuPZeMTsg2KHu72qCUUjqgfvmMsfEldI9gJmMl71TtJxvx4g+U0KG+sFYhSmY37kH0Chz+muRQoJhDKJ8BPpRPZL+mXWHskXLyORSCQS+a/5Cwl05f9m7Q2AAAAAAElFTkSuQmCC>

[image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAfCAYAAADTNyzSAAABCElEQVR4Xu3RMUtCYRTG8RMlBNUgSeDg4hJNDlFLgdDm0hfwC0g4ObsKToaCS4SfoDVoaGtrD4KmJAqKiiDasv7nnlc7XK/VKvjAD/G558j7XkVmmeXfWUcb1zjDDoroYsnNRaljgB4KqOINnzh2c1EquMRqrN/CR/gcJYcbsV+Mp4wrZHypg1/Y9GVIBydYGBZ6kXOxhfSwDFnBBWq+zOJWbCGeDTxj15e/LTTE+tFxNIs4DQ98tvGa0EfZF3vXqfB9D09iw/q/jGUOB3jBo9grPhRbuHNzY9H7rGFefs6vx/0zeTyILZRizxKjd9JhXdLliWmhj3exBXUvdszENHGUYNkPTXu+Aa03OKImIX7yAAAAAElFTkSuQmCC>
<!-- PGNTOOLS-LC0-END -->
