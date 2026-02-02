# PgnTools

A high-performance WinUI 3 desktop application for managing Portable Game Notation (PGN) files. This comprehensive toolkit provides various utilities for downloading, filtering, tagging, sorting, merging, and analyzing chess games.

## AI-Generated Project

**This application is completely written by artificial intelligence.** The project owner contributed the ideas and direction, while AI handled all of the implementation, including code, architecture, and documentation.

---

## Getting the Application

### Downloading the Latest Build

The easiest way to get PgnTools is through the GitHub Actions workflow artifacts:

1. Go to the **[Actions](../../actions)** tab of this repository
2. Click on the most recent successful **"Build PgnTools"** workflow run
3. Scroll down to the **Artifacts** section
4. Download the **`PgnTools-Build`** artifact
5. Extract the ZIP file to your desired location
6. Run `PgnTools.exe` to start the application

> **Note:** The artifact is a self-contained single-file executable for Windows x64. No additional runtime installation is required.

---

## Required Assets

PgnTools requires certain asset files to function properly. These must be placed in the `Assets` folder next to the executable.

### Asset Files

| File | Description | Required For |
|------|-------------|--------------|
| `eco.pgn` | ECO (Encyclopedia of Chess Openings) database | ECO Tagger tool |
| `ratings.bin.zst` | Historical player ratings database (Zstandard compressed) | Elo Adder tool |
| `lc0/` | Lc0 engine folder | Chess Analyzer tool (optional) |

### Placing Assets

After extracting the application:

1. Locate the `Assets` folder in your extracted directory
2. If it doesn't exist, create a folder named `Assets` next to `PgnTools.exe`
3. Place the required asset files in the `Assets` folder

```
PgnTools/
├── PgnTools.exe
└── Assets/
    ├── eco.pgn
    ├── ratings.bin.zst
    └── lc0/
        └── (Lc0 engine files)
```

---

## Tools

PgnTools includes the following utilities:

### Downloaders

| Tool | Description |
|------|-------------|
| **Chess.com** | Download games via the Chess.com Public API |
| **Lichess** | Download user games and access monthly database tools |
| **Lc0** | Download and collate Lc0 match PGNs |
| **PGN Mentor** | Download games from PGN Mentor |
| **TWIC Downloader** | Download games from The Week in Chess |

### Processing & Filtering

| Tool | Description |
|------|-------------|
| **Filter** | Filter games by Elo, ply count, checkmate endings, and annotations |
| **Deduplicator** | Remove duplicate games from PGN files |
| **Splitter** | Split PGN files into smaller chunks or filter games |
| **Merger** | Merge multiple PGN files into one |
| **Sorter** | Sort games by Elo, Date, or other criteria |
| **Tour Breaker** | Extract valid tournaments from PGN files |

### Tagging & Metadata

| Tool | Description |
|------|-------------|
| **ECO Tagger** | Tag games with ECO codes, Opening names, and Variation names |
| **Category Tagger** | Tag games by tournament category |
| **Elo Adder** | Add historical Elo ratings to games |
| **Ply Count Adder** | Add PlyCount tags to games |

### Analysis & Utilities

| Tool | Description |
|------|-------------|
| **Chess Analyzer** | Perform engine analysis of games |
| **PGN Info** | Display summary statistics for PGN files |
| **Stockfish Normalizer** | Fix and normalize engine names in PGN headers |

---

## Tech Stack

- **.NET 10** (Preview/Latest)
- **WinUI 3** (Windows App SDK)
- **C# 14**
- **CommunityToolkit.Mvvm** for MVVM architecture

---

## License

See the [LICENSE](LICENSE) file for details.
