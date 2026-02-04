## High-level architecture

Think of the app as four **layers**, each with a clear responsibility: [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

1. **Domain layer (Chess + PGN core)**  
   - Pure C# library: PGN parser/writer, game model, ECO/opening database, rating DB, tagging logic, etc. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - No UI dependencies; fully testable and reusable (could be used by CLI or background services). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

2. **Tool layer (Use-case services)**  
   - Each “tool” (Filter, Joiner, Splitter, Chess.com downloader, TWIC downloader, Stockfish normalizer, etc.) is an application service that orchestrates domain objects plus IO. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - Interfaces like `IPgnFilterService`, `IChessAnalyzerService`, `ILichessDownloaderService` stay thin and focused: “Given these inputs, produce these outputs.” [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

3. **Application layer (Shell + navigation)**  
   - Host / DI setup in `App.xaml.cs` registering all core services and all tool services, plus shared services like window management, navigation, and settings. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - A `ToolRegistry` (which you already have) that knows about all tools: id, name, icon glyph, view model type, page type, category, and capabilities. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

4. **Presentation layer (Views + ViewModels)**  
   - One ViewModel per tool page, bound to the UI via WinUI XAML pages. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - A `MainViewModel` / `ShellViewModel` coordinating navigation, recent tasks, global status and error reporting. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

This is essentially what you have today, but the “ideal” version leans harder into: (a) clean separation of domain vs IO, and (b) cross-tool infrastructure instead of duplicating concerns per tool. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

***

## Core building blocks

### 1. Domain model

- `PgnGame`, `PgnTag`, `PgnMove`, `PgnFile` abstractions.  
- ECO / opening DB wrapped behind an `IEcoDatabase` using `eco.pgn` and any preprocessed indices. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- Rating database as `IRatingDatabase` (already present as `EmbeddedRatingsDatabase`) with a simple API: `GetRating(player, date)`, etc. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- Chess engine integration (Stockfish, Lc0) abstracted as `IChessEngine` and `IAnalysisSession` interfaces. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

Goal: You can write unit tests like “filter out all 10-move games” without touching disk. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

### 2. Tool services (“application services”)

For each tool, have:

- A **request DTO** (pure data):  
  - Example: `PgnFilterRequest { string InputPath; string OutputPath; FilterCriteria Criteria; }`  
- A **response DTO** summarizing results:  
  - Example: `PgnFilterResult { int InputGames; int OutputGames; IList<FilterWarning> Warnings; }`  
- A single method on the interface:  
  - `Task<PgnFilterResult> RunAsync(PgnFilterRequest request, CancellationToken ct);`

All your existing services registered in `RegisterToolServices` (`IPgnFilterService`, `IPgnJoinerService`, `IPgnSplitterService`, etc.) fit this pattern. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

### 3. Shared infrastructure

Instead of each tool reinventing the wheel:

- **IFilePickerService / IFolderPickerService**  
  Abstracted wrapper over WinUI pickers; ViewModels never see `FileOpenPicker` directly. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

- **IProgressService**  
  - Provides a `IProgressReporter` used by tool services to report percentage, phase name, and optional current file/game. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
  - Shell listens and shows unified progress in a status bar / overlay.

- **ILoggingService**  
  - Unified logging (file, debug output, maybe an in-app log viewer).  
  - Each service gets an `ILogger<T>` via DI. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

- **IAppSettingsService**  
  - Already present, but in the “ideal” version it centralizes all configuration: default directories, engine paths, thread counts, etc. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

- **Cancellation / task management**  
  - Each long-running operation gets a `CancellationToken` provided by the ViewModel via `ICommand` wrappers that create and manage it.  
  - Shell can show a global “Cancel current job” button if something is running.

***

## Tool workflow from user perspective

Here’s a canonical workflow for any tool; the ideal program makes this identical across all tools so things feel coherent. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

1. **Select tool from home/shell**  
   - User sees cards for “Filter”, “Sort”, “Join”, “Split”, “Download (Lichess)”, “Analyze (Stockfish)”, etc., each with icon and description. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - Clicking a card navigates to `ToolPage` with associated `ToolViewModel` via `NavigationService` and `ToolRegistry`. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

2. **Configure inputs**  
   - ViewModel exposes:
     - `InputPath` / `InputFiles`
     - `OutputPath` (optional, some tools only preview)
     - Tool-specific options (e.g., min Elo, result filters, deduplication strategy). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - File/folder pickers are invoked via `IFilePickerService` so the XAML just binds to commands.

3. **Validation & preview**  
   - `CanRun` logic in ViewModel validates required fields and simple invariants.  
   - Optionally, a “Preview” action loads a subset of games and shows summary stats: game count, players, ECO distribution. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

4. **Execute**  
   - User clicks “Run”:
     - ViewModel creates the request DTO.
     - Calls `await _service.RunAsync(request, ct)` on a background task.
     - Binds progress events to UI progress bar (via `IProgressService`). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

5. **Report results**  
   - When done, ViewModel receives a `Result` with counts and warnings.  
   - UI shows:
     - “Processed 123,456 games; output 45,678 games; 23 errors (view log).”  
     - Buttons: “Open output folder”, “Run another”, “Back to tools.”

6. **Error handling**  
   - Any exceptions bubble as `ToolError` objects with friendly messages plus technical detail in the log.  
   - ViewModel exposes `ErrorMessage` and `ErrorDetails` properties for a consistent error dialog.

This same pattern applies to downloaders, analyzers, cleaners, etc.; only the request/response DTOs differ. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

***

## Interoperation between tools

To make tools compose nicely:

1. **Shared PGN representation and streaming**  
   - All services read/write PGN using the same `IPgnReader` / `IPgnWriter` abstractions registered once in DI. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
   - Internally, use streaming (enumerable games) so “Filter → Sort → Deduplicate” can run on huge files without blowing memory.

2. **Pipelines / chained workflows**

   Add an optional “pipeline runner” concept:

   - A `PipelineDefinition` is just a list of steps: each step references a tool, its config, and input/output file templates.  
   - For example:
     - Step 1: Download Lichess DB chunk
     - Step 2: Filter by rating + date
     - Step 3: Deduplicate
     - Step 4: Normalize with Stockfish  
   - A `PipelineRunnerService` executes each tool service in order, passing outputs as next inputs.

   In the UI, you could later expose “Saved workflows” built on top of this.

3. **Metadata-only tools**

   - Tools like `PgnInfoService` should operate purely on the domain model without altering PGN, and their results should be trivially usable by other tools (e.g., filtering by computed stats). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

4. **Shared caching**

   - Opening classification (`EcoTaggerService`), ratings, engine evaluations can be cached:
     - `IEcoTaggerService` caches by FEN or move sequence.
     - `IRatingDatabase` caches per player/date queries. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

   Tools then share the same services via DI, making repeated operations cheaper.

***

## Ideal project structure (within the solution)

Something like: [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

- `PgnTools.Models` (already present)  
  - Domain entities, request/response DTOs, enums, configuration types. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- `PgnTools.Services`  
  - Core PGN IO, chess engine, ECO DB, rating DB, shared utilities (logging adapters, progress, file system). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- `PgnTools.ToolServices`  
  - One service per tool implementing `IPgnFilterService`, `IPgnJoinerService`, etc. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- `PgnTools.ViewModels`  
  - Shell and per-tool ViewModels. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- `PgnTools.Views`  
  - XAML pages, styles, theme resources (which you already have under WinUI resources). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- `PgnTools` (app)  
  - `App.xaml`, `App.xaml.cs`, `MainWindow`, DI configuration (your `ConfigureServices` and `RegisterToolServices/RegisterViewModels`). [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
- `PgnTools.Tests`  
  - Unit tests for domain and services using small PGN fixtures.

You’re already close to this; the “perfect” version just enforces that each layer only depends downward: Views → ViewModels → Tool Services → Domain / Core Services. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)

***

## How everything interoperates in practice

Putting it all together, an example end-to-end scenario:

1. User picks “Lichess DB Downloader” tool.  
2. `LichessDbDownloaderViewModel` uses `ILichessDbDownloaderService` to download monthly PGN files into a chosen folder. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
3. Once complete, the tool offers “Run filter on downloaded files”:
   - It invokes navigation to `FilterViewModel`, pre-populating `InputFolder` with the downloader’s output path.  
4. `FilterViewModel` lets user set rating + date criteria, then calls `IPgnFilterService.RunAsync`, which:
   - Uses shared `IPgnReader` to stream games.
   - Uses `IRatingDatabase` and `IEcoTaggerService` to enrich games.
   - Writes filtered PGN to `OutputPath` via shared `IPgnWriter`. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
5. The result summary is displayed, and from there the user can navigate directly to “Sort” or “Deduplicate” with the output file pre-filled.

Because all tools share core services and a consistent request/response pattern, composing them is trivial and doesn’t create dependency tangles. [ppl-ai-file-upload.s3.amazonaws](https://ppl-ai-file-upload.s3.amazonaws.com/web/direct-files/attachments/32456247/b1b4002c-93f4-4724-a632-de7ade766d99/codebase_pgn_tools.txt)
