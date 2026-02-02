# PgnTools Specification: Elegance Scoring Subsystem (V1.2)

**Version:** 1.2 (Revised)
**Date:** February 2026
**Architect:** Senior .NET 10 Architect
**Status:** Approved for Implementation

---

## 1. Executive Summary

This document defines the technical implementation for the **Elegance Scoring Subsystem** within **PgnTools**. The objective is to calculate a heuristic "Elegance" score (0-100) for chess games at a throughput of **1,000,000 games per hour** on standard desktop hardware.

To achieve this throughput without utilizing heavy UCI engines (like Stockfish), we utilize a "Shannon-Lite" evaluation function optimized for modern hardware. The architecture strictly adheres to **.NET 10** standards, utilizing **C# 14** features such as `ref struct`, `Span<T>`, and zero-allocation memory patterns to prevent Garbage Collection (GC) pressure.

---

## 2. Technical Architecture & Constraints

### 2.1 Performance Targets

* **Throughput:** > 22,000 plies per second (approx. 300 games/sec).
* **Memory Footprint:** < 500 MB RAM under load.
* **GC Pressure:** Gen 2 collections must remain at 0 during batch processing.

### 2.2 Core Technologies

* **Runtime:** .NET 10 (Preview).
* **Language:** C# 14.
* **Parallelism:** `System.Threading.Channels` for producer/consumer streaming.
* **IO:** `System.IO.Pipelines` for high-performance PGN parsing.

---

## 3. The Elegance Metric (E)

The Elegance score is a weighted sum of five components, normalized to a 0-100 scale.

**The Formula:**

> **E = w1(S) + w2(C) + w3(T) + w4(Q) + w5(L)**

Where:

* **S (Soundness):** Positional stability and material retention.
* **C (Coherence):** Logical consistency of the plan (absence of erratic evaluation swings).
* **T (Tactical Density):** Frequency of forcing sequences (captures/checks).
* **Q (Quiet Brilliancy):** Non-forcing moves that significantly improve position.
* **L (Length Penalty):** Logistic decay for games > 80 moves.

### 3.1 Pre-Flight Filtering

Before scoring, games must pass validity checks to ensure CPU cycles are not wasted on noise.

```csharp
public static bool IsScorable(PgnGame game)
{
    // Reject trivially short games
    if (game.Moves.Count < 20) return false;

    // Reject draws by insufficient material (e.g., K+N vs K)
    if (game.Result == "1/2-1/2" && BoardUtils.IsInsufficientMaterial(game.FinalPosition))
        return false;

    // Reject repetition draws (often lack "Elegance")
    if (game.Termination?.Contains("repetition", StringComparison.OrdinalIgnoreCase) == true)
        return false;

    return true;
}

```

---

## 4. Shannon-Lite Evaluation Model

To meet performance targets, we cannot generate full legal move lists for every evaluation (which is O(N)). Instead, we use a **Pseudo-Mobility** heuristic which is O(64).

### 4.1 Evaluation Function (E_sl)

The evaluation at any given ply `t` is calculated as:

> **E_sl(t) = Material + Mobility_pseudo + Structure + KingSafety**

### 4.2 Optimized Mobility (C# 14 Implementation)

We replace the expensive `GetLegalMoves()` call with a bitwise attack map scan.

```csharp
/// <summary>
/// Estimates mobility control using pseudo-legal attack maps.
/// Allocation-free.
/// </summary>
private static int EstimateMobilityDifference(ZeroAllocChessBoard board)
{
    int whiteScore = 0;
    int blackScore = 0;

    // Fast iteration over 64 squares using Span
    ReadOnlySpan<Square> squares = BoardConstants.AllSquares; 

    foreach (var sq in squares)
    {
        var piece = board.GetPieceAt(sq);
        if (piece.Type == PieceType.None) continue;

        // Count pseudo-legal attacks (ignores pins/check validity for speed)
        // This is 10-20x faster than GetLegalMoves()
        int attacks = board.GetAttackCount(sq, piece); 
        
        if (piece.Color == Color.White) whiteScore += attacks;
        else blackScore += attacks;
    }
    
    // Weight: 10 centipawns per controlled square differential
    return (whiteScore - blackScore) * 10;
}

```

### 4.3 Checkpoint Strategy

Calculating `E_sl` is expensive. We employ a hybrid checkpoint strategy:

1. **Standard:** Evaluate every 8 plies.
2. **Quiet Trigger:** Force an immediate evaluation if a move is "Quiet" (not a capture/check) to detect hidden brilliancies.

---

## 5. Component Algorithms

### 5.1 Soundness (S) & Sacrifice Validation

Sacrifices are the heart of elegance. A sacrifice is valid **only** if it is sound. We remove reliance on game result or Elo tags (which may be missing) and use a **Future-State Validation**.

**Algorithm:**
A move is a Valid Sacrifice if:

1. **Material Loss:** The move results in a Static Exchange Evaluation (SEE) deficit > 100cp.
2. **Compensation:** Within the next 12 plies, one of the following occurs:
* **Mate:** The opponent is mated.
* **Material Recovery:** Material balance returns to equal or better.
* **Crushing Attack:** Opponent King Safety score drops below -300cp.



### 5.2 Coherence (C) - The Second Derivative

Coherence measures if a player follows a consistent plan. We detect "Trend Breaks" using the **Second Derivative (Acceleration)** of the evaluation history.

**Definitions:**

> **Delta1 = E(t) - E(t-1)** *(Velocity)*
> **Delta2 = Delta1(t) - Delta1(t-1)** *(Acceleration)*

**Logic:**
If the sign of **Delta1** flips (change in direction) AND **|Delta2| > 50cp**, a **Trend Break** is recorded.

* **Penalty:** -5 points per Trend Break.

### 5.3 Quiet Brilliancy (Q)

This detects "silent killers"—moves that are not captures or checks but drastically improve the position.

**Logic:**

1. Identify a non-forcing move (not capture, not check).
2. Calculate `E_sl` immediately after the move.
3. If `E_sl` improves by **>= 100cp** compared to the previous state AND Mobility increases by **>= 15%**, mark as **Quiet Brilliance**.

---

## 6. Static Exchange Evaluation (SEE)

Implementation of a robust Swap algorithm is critical for identifying sacrifices. This implementation handles X-Rays (e.g., Rook attacking through a Queen).

```csharp
public static int StaticExchangeEvaluation(ZeroAllocChessBoard board, Move move)
{
    // C# 14: Use stackalloc to avoid heap allocation for the gain array
    Span<int> gains = stackalloc int[32]; 
    int depth = 0;
    
    Square targetSq = move.To;
    
    // Initial capture value
    gains[0] = PieceValues[(int)board.GetPieceAt(targetSq).Type];
    
    PieceType attackerType = move.Piece.Type;
    Color sideToMove = move.Piece.Color;
    
    // Speculative execution on internal board state
    board.MakeMove(move); 
    depth++;

    // Swap loop (Max depth 6 per spec for performance)
    while (depth < 6) 
    {
        sideToMove = sideToMove.Opposite();

        // GetLeastValuableAttacker must handle X-Rays (batteries)
        var nextAttacker = board.GetLeastValuableAttacker(targetSq, sideToMove);
        
        if (nextAttacker == null) break;

        // Calculate gain: Value of victim - Value of previous attacker
        gains[depth] = PieceValues[(int)attackerType] - gains[depth - 1];

        // Pruning: If the current stand is worse than previous, stop
        if (Math.Max(-gains[depth - 1], gains[depth]) < 0) break;

        attackerType = nextAttacker.Type;
        board.MakeMove(new Move(nextAttacker.Square, targetSq));
        depth++;
    }

    // Minimax back-propagation to find the best outcome for the starter
    while (--depth > 0)
    {
        gains[depth - 1] = -Math.Max(-gains[depth - 1], gains[depth]);
    }

    board.UndoMoves(depth); // Important: Restore board state
    return gains[0];
}

```

---

## 7. Memory & Data Structures

### 7.1 The Zero-Allocation Board

To avoid allocating 30,000 board objects per second, we must use a stateful approach.

```csharp
public class ZeroAllocChessBoard
{
    // 12 bitboards for piece/color combinations
    private ulong[] _bitboards = new ulong[12]; 
    
    // Pre-allocated stack for history (avoiding List<T> resizing)
    private readonly Move[] _moveStack = new Move[512]; 
    private readonly ulong[] _hashStack = new ulong[512];
    private int _plyCount = 0;

    public void MakeMove(Move move)
    {
        // Mutate bitboards in place using XOR operations
        ApplyMoveToBitboards(move);
        
        // Push state to fixed-size arrays
        _moveStack[_plyCount] = move;
        _hashStack[_plyCount] = _currentZobristHash;
        _plyCount++;
    }

    public void UnmakeMove()
    {
        _plyCount--;
        var move = _moveStack[_plyCount];
        // Reverse bitboard mutations
        UnapplyMoveToBitboards(move);
    }
}

```

---

## 8. Final Scoring & Persistence

### 8.1 Normalization

To prevent resolution loss, the score is mapped to 0-100 (integer) rather than 0-9.

* **Raw Score (S_raw):** Calculated via weighted formula.
* **Normalization:** `Final = min(100, max(0, S_raw * 100))`

### 8.2 PGN Tag Output

The tool will append the following tags to the PGN output:

```text
[Elegance "82"]
[EleganceDetails "S=25;C=15;T=20;Q=22;L=0"]

```

### 8.3 Goldens (Regression Tests)

* **Anderssen vs. Kieseritzky (Immortal Game):** Must score > 90.
* **Karpov vs. Kasparov (WCh 1984, Game 9):** Must score < 40 (Grinding positional struggle).
* **Tal vs. Botvinnik (1960, Game 6):** Must score > 85 (High Tactical Density + Sacrifices).