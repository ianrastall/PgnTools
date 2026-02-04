# PgnTools

PgnTools is a high-performance WinUI 3 desktop application for large PGN workflows on Windows. It emphasizes streaming I/O and a tool-based UI for downloading, filtering, tagging, and analyzing chess games.

**Highlights**
- Streaming PGN reader/writer that processes games without loading entire files into memory.
- Tool-based UI with dedicated pages for each workflow.
- Self-contained Windows x64 builds via GitHub Actions.
- Modern stack: .NET 10, C# 14, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection.

**Tool Suite**
- Downloaders: Chess.com, Lichess (user + monthly DB), Lc0 matches, PGN Mentor, TWIC, Tablebases.
- Processing: Filter (Elo/ply/checkmate/annotations), Deduplicator, Splitter, Merger, Sorter, Tour Breaker.
- Tagging: Category Tagger, ECO Tagger, Elo Adder, Ply Count Adder.
- Analysis: Chess Analyzer (UCI/Stockfish), PGN Info, Stockfish Normalizer.
- Experimental/Hidden: Chess Unannotator, Checkmate Filter, Elegance scoring.

**Assets**
The app expects the following files under `PgnTools/Assets` (they are copied on publish). These enable specific tools:
- `Assets/eco.pgn` for ECO tagging.
- `Assets/ratings.bin.zst` for Elo Adder.
- `Assets/elegance-distributions.json` and `Assets/elegance-goldens.json` for Elegance scoring/validation.
- `Assets/Tablebases/download.txt` for tablebase URL lists.

Optional:
- Any UCI engine (Stockfish recommended). The app can download the latest Stockfish build.
- Syzygy tablebases for analysis (choose a folder at runtime).

**Build And Run**
Requirements:
- Windows 10 1809+ (x64).
- .NET 10 SDK (preview).
- Windows App SDK 1.8 (restored via NuGet).
- Visual Studio 2022 17.10+ recommended for WinUI development.

Build and run:
```powershell
dotnet restore PgnTools/PgnTools.csproj
dotnet build PgnTools/PgnTools.csproj -c Release
dotnet run --project PgnTools/PgnTools.csproj
```

Publish (matches CI):
```powershell
dotnet publish PgnTools/PgnTools.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish_output
```

**Prebuilt Builds**
- GitHub Actions workflow `Build PgnTools` publishes a self-contained single-file executable as an artifact.

**Repo Layout**
- `PgnTools/` WinUI 3 app source.
- `Docs/` tool documentation and design notes.
- `generate_codebase_dump.ps1` codebase snapshot generator for LLM handoff.

**Project Note**
- This repository was built with significant AI assistance; the owner provided direction and requirements.

**License**
See `LICENSE`.
