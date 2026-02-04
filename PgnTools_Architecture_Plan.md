# PgnTools: Official Software Architecture Plan
**Version 2.0 - Complete Redesign**  
*February 2026*

---

## Executive Summary

This document presents a comprehensive architectural plan for PgnTools, synthesizing insights from the current codebase and recommendations from multiple architectural reviews. The goal is to transform PgnTools from a collection of utilities into a cohesive, professional-grade PGN processing suite.

### Core Principles

1. **Streaming-First Architecture** - Never load entire files into memory
2. **Pipeline Composition** - Tools compose into workflows without manual file management  
3. **Unified Execution Model** - Consistent progress reporting, cancellation, and error handling
4. **Clean Separation of Concerns** - Domain logic independent of UI
5. **Zero-Allocation Where Possible** - Leverage `Span<T>`, `Memory<T>`, and efficient parsing

---

## 1. Layered Architecture

The application will be restructured into four distinct layers with clear dependencies:

```
┌─────────────────────────────────────────────┐
│  Presentation Layer (PgnTools.UI)           │
│  - WinUI 3 Views & ViewModels               │
│  - Navigation & Window Management           │
│  - User Settings & Themes                   │
└─────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│  Application Layer (PgnTools.Application)   │
│  - Operation Runner & Orchestration         │
│  - Tool Registry & Discovery                │
│  - Pipeline Builder & Composition           │
│  - Progress Aggregation & Reporting         │
└─────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│  Domain Layer (PgnTools.Core)               │
│  - PGN Parsing (Streaming, Zero-Alloc)      │
│  - Chess Models (Game, Move, Position)      │
│  - Domain Services (Tool Operations)        │
│  - Interfaces & Abstractions                │
└─────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│  Infrastructure Layer                       │
│  - File I/O & Streaming                     │
│  - HTTP Clients (Downloaders)               │
│  - Process Management (Engines)             │
│  - Databases (ECO, Ratings, Caches)         │
└─────────────────────────────────────────────┘
```

### 1.1 Project Structure

```
PgnTools.sln
│
├── src/
│   ├── PgnTools.Core/                  # Domain layer (no dependencies on UI or infra)
│   │   ├── Models/
│   │   │   ├── PgnGame.cs
│   │   │   ├── PgnHeader.cs
│   │   │   ├── Move.cs
│   │   │   └── Position.cs
│   │   ├── Parsing/
│   │   │   ├── PgnReader.cs            # Streaming IAsyncEnumerable<PgnGame>
│   │   │   ├── PgnWriter.cs
│   │   │   ├── PgnTokenizer.cs         # Span<char> based
│   │   │   └── SanParser.cs
│   │   ├── Operations/                 # Tool logic
│   │   │   ├── Interfaces/
│   │   │   │   ├── IPgnOperation.cs
│   │   │   │   ├── IPgnFilter.cs
│   │   │   │   └── IPgnTransform.cs
│   │   │   ├── Filtering/
│   │   │   │   ├── EloFilter.cs
│   │   │   │   ├── CheckmateFilter.cs
│   │   │   │   └── NonStandardFilter.cs
│   │   │   ├── Transformation/
│   │   │   │   ├── EcoTagger.cs
│   │   │   │   ├── EloAdder.cs
│   │   │   │   └── PlycountAdder.cs
│   │   │   ├── Analysis/
│   │   │   │   ├── ChessAnalyzer.cs
│   │   │   │   └── EleganceScorer.cs
│   │   │   └── Utilities/
│   │   │       ├── Deduplicator.cs
│   │   │       ├── Splitter.cs
│   │   │       ├── Joiner.cs
│   │   │       └── Sorter.cs
│   │   └── Engine/
│   │       ├── IEngineController.cs
│   │       ├── UciEngine.cs
│   │       └── EnginePool.cs
│   │
│   ├── PgnTools.Infrastructure/        # External dependencies
│   │   ├── IO/
│   │   │   ├── StreamingFileReader.cs
│   │   │   ├── StreamingFileWriter.cs
│   │   │   └── TempFileManager.cs
│   │   ├── Http/
│   │   │   ├── LichessClient.cs
│   │   │   ├── ChesscomClient.cs
│   │   │   └── TwicClient.cs
│   │   ├── Databases/
│   │   │   ├── EcoDatabase.cs
│   │   │   ├── RatingsDatabase.cs
│   │   │   └── IndexCache.cs
│   │   └── Processes/
│   │       ├── EngineProcessManager.cs
│   │       └── TablebaseManager.cs
│   │
│   ├── PgnTools.Application/           # Orchestration layer
│   │   ├── Pipelines/
│   │   │   ├── PipelineBuilder.cs
│   │   │   ├── PipelineExecutor.cs
│   │   │   └── PgnDataset.cs
│   │   ├── Operations/
│   │   │   ├── OperationRunner.cs
│   │   │   ├── OperationContext.cs
│   │   │   └── OperationResult.cs
│   │   ├── Registry/
│   │   │   ├── ToolRegistry.cs
│   │   │   ├── ToolDescriptor.cs
│   │   │   └── ToolCapabilities.cs
│   │   ├── Progress/
│   │   │   ├── IProgressTracker.cs
│   │   │   ├── ProgressAggregator.cs
│   │   │   └── ProgressEvent.cs
│   │   └── Services/
│   │       ├── SessionService.cs       # Current working file context
│   │       └── WorkspaceService.cs
│   │
│   └── PgnTools.UI/                    # WinUI 3 presentation
│       ├── Shell/
│       │   ├── MainWindow.xaml
│       │   ├── ShellPage.xaml
│       │   └── ShellViewModel.cs
│       ├── ViewModels/
│       │   ├── Base/
│       │   │   ├── ViewModelBase.cs
│       │   │   └── ToolViewModelBase.cs
│       │   └── Tools/
│       │       ├── FilterViewModel.cs
│       │       ├── EcoTaggerViewModel.cs
│       │       └── [... other tool VMs]
│       ├── Views/
│       │   └── Tools/
│       │       ├── FilterPage.xaml
│       │       ├── EcoTaggerPage.xaml
│       │       └── [... other tool pages]
│       ├── Controls/
│       │   ├── ProgressPanel.xaml
│       │   ├── DatasetSelector.xaml
│       │   └── StatusBar.xaml
│       ├── Services/
│       │   ├── INavigationService.cs
│       │   ├── NavigationService.cs
│       │   ├── IDialogService.cs
│       │   └── ThemeService.cs
│       └── App.xaml.cs
│
└── tests/
    ├── PgnTools.Core.Tests/
    ├── PgnTools.Infrastructure.Tests/
    └── PgnTools.Integration.Tests/
```

---

## 2. Core Abstractions & Interfaces

### 2.1 The Dataset Concept

All tools operate on **PGN Datasets** rather than raw file paths. This enables composition and metadata tracking.

```csharp
public sealed record PgnDataset(
    string Path,                        // Primary file path
    DatasetFormat Format,               // PGN, Zipped, etc.
    DatasetMetadata Metadata,           // Source, created date, tool chain
    long? KnownGameCount = null,        // Cached count (optional)
    IReadOnlyDictionary<string, string>? Sidecars = null  // Indexes, caches
);

public enum DatasetFormat
{
    Pgn,
    CompressedPgn,
    MultiFile
}

public sealed record DatasetMetadata(
    string Source,                      // "TWIC 1500", "Lichess", "User Upload"
    DateTime CreatedUtc,
    string? CreatedByTool = null,       // "Eco Tagger v2.0"
    IReadOnlyList<string>? ToolChain = null  // ["Download", "Filter", "Tag"]
);
```

### 2.2 The Operation Interface

Every tool implements a unified interface:

```csharp
public interface IPgnOperation
{
    /// <summary>
    /// Metadata about this operation
    /// </summary>
    OperationMetadata Metadata { get; }
    
    /// <summary>
    /// Validate options before execution
    /// </summary>
    OperationValidation Validate(object options);
    
    /// <summary>
    /// Execute the operation
    /// </summary>
    Task<OperationResult> ExecuteAsync(
        OperationContext context,
        CancellationToken cancellationToken);
}

public sealed record OperationMetadata(
    string Name,
    string Key,                         // Unique identifier
    string Description,
    ToolCategory Category,
    ToolCapabilities Capabilities
);

public sealed record ToolCapabilities(
    bool RequiresEngine,
    bool RequiresNetwork,
    bool ProducesMultipleFiles,
    bool SupportsMultipleInputs,
    bool ProducesSidecar,
    TimeComplexity Complexity           // O(n), O(n log n), etc.
);

public sealed record OperationContext(
    PgnDataset? InputDataset,           // Null for downloaders
    string OutputPath,
    object Options,                     // Tool-specific strongly-typed options
    IProgressTracker Progress,
    IServiceProvider Services           // For DI of readers, writers, engines
);

public sealed record OperationResult(
    bool Success,
    PgnDataset? OutputDataset,
    ResultStatistics Statistics,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage,
    TimeSpan Duration
);

public sealed record ResultStatistics(
    long GamesProcessed,
    long GamesOutput,
    long GamesModified,
    long BytesRead,
    long BytesWritten
);
```

### 2.3 Progress Tracking

Standardized progress reporting across all tools:

```csharp
public interface IProgressTracker
{
    /// <summary>
    /// Report progress for a specific phase
    /// </summary>
    void Report(ProgressEvent progressEvent);
    
    /// <summary>
    /// Current overall progress (0-100)
    /// </summary>
    double OverallPercent { get; }
}

public sealed record ProgressEvent(
    string Phase,                       // "Reading", "Analyzing", "Writing"
    double PhasePercent,                // 0-100 for this phase
    long? CurrentItem = null,
    long? TotalItems = null,
    string? Unit = null,                // "games", "bytes", "MB"
    string? Message = null              // Optional status text
);
```

---

## 3. The Operation Runner

All tool execution flows through a single `OperationRunner` that provides:
- Consistent cancellation handling
- Standard exception normalization
- Progress aggregation
- Timing and telemetry
- History tracking

```csharp
public sealed class OperationRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OperationRunner> _logger;
    
    public async Task<OperationResult> RunAsync(
        IPgnOperation operation,
        OperationContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            // Validate
            var validation = operation.Validate(context.Options);
            if (!validation.IsValid)
            {
                return OperationResult.Failure(
                    validation.ErrorMessage,
                    duration: sw.Elapsed);
            }
            
            // Execute
            context.Progress.Report(new ProgressEvent("Starting", 0));
            
            var result = await operation.ExecuteAsync(context, cancellationToken);
            
            sw.Stop();
            return result with { Duration = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled(sw.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation {OpName} failed", operation.Metadata.Name);
            return OperationResult.Failure(ex.Message, sw.Elapsed);
        }
    }
}
```

---

## 4. Pipeline Composition

Tools can be chained together into pipelines:

```csharp
public sealed class PipelineBuilder
{
    private readonly List<(IPgnOperation Operation, object Options)> _stages = new();
    
    public PipelineBuilder Add(IPgnOperation operation, object options)
    {
        _stages.Add((operation, options));
        return this;
    }
    
    public Pipeline Build() => new(_stages);
}

public sealed class Pipeline
{
    private readonly IReadOnlyList<(IPgnOperation Op, object Options)> _stages;
    
    public async Task<OperationResult> ExecuteAsync(
        PgnDataset? initialDataset,
        string finalOutputPath,
        IProgressTracker progress,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var currentDataset = initialDataset;
        var tempFiles = new List<string>();
        
        try
        {
            for (var i = 0; i < _stages.Count; i++)
            {
                var (operation, options) = _stages[i];
                var isLastStage = i == _stages.Count - 1;
                
                // Output to final path on last stage, temp file otherwise
                var outputPath = isLastStage 
                    ? finalOutputPath 
                    : Path.GetTempFileName() + ".pgn";
                
                if (!isLastStage)
                {
                    tempFiles.Add(outputPath);
                }
                
                var context = new OperationContext(
                    InputDataset: currentDataset,
                    OutputPath: outputPath,
                    Options: options,
                    Progress: progress.CreateSubTracker($"Stage {i + 1}"),
                    Services: services
                );
                
                var runner = services.GetRequiredService<OperationRunner>();
                var result = await runner.RunAsync(operation, context, cancellationToken);
                
                if (!result.Success)
                {
                    return result; // Fail fast
                }
                
                currentDataset = result.OutputDataset;
            }
            
            return OperationResult.Success(currentDataset);
        }
        finally
        {
            // Clean up temp files
            foreach (var temp in tempFiles)
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }
}
```

---

## 5. Tool Registry & Discovery

Tools are registered declaratively:

```csharp
public sealed record ToolDescriptor(
    string Key,
    string DisplayName,
    string Description,
    string IconGlyph,
    ToolCategory Category,
    Type OperationType,                 // Must implement IPgnOperation
    Type ViewModelType,                 // For UI
    Type PageType                       // For navigation
);

public enum ToolCategory
{
    Download,
    Filter,
    Transform,
    Analysis,
    Utility
}

public static class ToolRegistry
{
    public static IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(
            Key: "EcoTagger",
            DisplayName: "ECO Tagger",
            Description: "Add ECO codes, opening names, and variations",
            IconGlyph: "\uE8EC",
            Category: ToolCategory.Transform,
            OperationType: typeof(EcoTaggerOperation),
            ViewModelType: typeof(EcoTaggerViewModel),
            PageType: typeof(EcoTaggerPage)
        ),
        // ... register all tools
    ];
    
    public static ToolDescriptor? GetByKey(string key) =>
        Tools.FirstOrDefault(t => t.Key == key);
}
```

---

## 6. Streaming Architecture

### 6.1 Core Streaming Principle

**Never load entire files into memory.** All operations stream `IAsyncEnumerable<PgnGame>`:

```csharp
// Example: Filter operation
public sealed class FilterOperation : IPgnOperation
{
    private readonly PgnReader _reader;
    private readonly PgnWriter _writer;
    
    public async Task<OperationResult> ExecuteAsync(
        OperationContext context,
        CancellationToken ct)
    {
        var options = (FilterOptions)context.Options;
        var stats = new ResultStatistics.Builder();
        
        await using var output = File.Create(context.OutputPath);
        
        // Stream games through filter
        await foreach (var game in _reader.ReadGamesAsync(
            context.InputDataset!.Path, ct))
        {
            stats.Processed++;
            
            if (!PassesFilter(game, options))
            {
                continue; // Skip this game
            }
            
            await _writer.WriteGameAsync(output, game, ct);
            stats.Kept++;
            
            context.Progress.Report(new ProgressEvent(
                "Filtering",
                (double)stats.Processed / stats.Total * 100,
                stats.Processed,
                stats.Total,
                "games"
            ));
        }
        
        return OperationResult.Success(
            new PgnDataset(context.OutputPath, ...),
            stats.Build()
        );
    }
}
```

### 6.2 Zero-Allocation Parsing

The `PgnReader` uses `Span<char>` for tokenization where possible:

```csharp
public partial class PgnReader
{
    // Use Span<char> for in-place parsing
    private static bool TryParseHeader(
        ReadOnlySpan<char> line,
        out string key,
        out string value)
    {
        // Parse without allocating strings until necessary
        // Use stackalloc for small buffers
        // Only allocate when we have a complete, valid header
    }
}
```

---

## 7. UI Architecture

### 7.1 MVVM Pattern

All ViewModels inherit from `ToolViewModelBase`:

```csharp
public abstract class ToolViewModelBase : ObservableObject
{
    protected readonly INavigationService Navigation;
    protected readonly IDialogService Dialogs;
    protected readonly OperationRunner Runner;
    protected readonly SessionService Session;
    
    // Common properties all tools need
    public string? InputFilePath { get; set; }
    public string? OutputFilePath { get; set; }
    
    public bool IsRunning { get; protected set; }
    public double Progress { get; protected set; }
    public string? StatusMessage { get; protected set; }
    public TimeSpan? Elapsed { get; protected set; }
    
    // Common commands
    public IAsyncRelayCommand ExecuteCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand BrowseInputCommand { get; }
    public IRelayCommand BrowseOutputCommand { get; }
    
    protected abstract Task<OperationResult> ExecuteOperationAsync(
        CancellationToken ct);
}
```

### 7.2 Session Context

The `SessionService` tracks the current working file, enabling workflow chaining:

```csharp
public sealed class SessionService
{
    public PgnDataset? CurrentDataset { get; private set; }
    
    public event EventHandler<PgnDataset>? DatasetChanged;
    
    public void SetDataset(PgnDataset dataset)
    {
        CurrentDataset = dataset;
        DatasetChanged?.Invoke(this, dataset);
    }
    
    // When a tool completes, it can update the session
    public void UpdateFromResult(OperationResult result)
    {
        if (result.Success && result.OutputDataset != null)
        {
            SetDataset(result.OutputDataset);
        }
    }
}
```

### 7.3 User Workflow

1. **Select Input** - User picks a file OR runs a downloader
   - If file: SessionService tracks it as current dataset
   - If downloader: Output becomes current dataset

2. **Navigate to Tool** - Click tool in sidebar
   - ViewModel auto-populates InputPath from SessionService.CurrentDataset
   - User configures tool-specific options

3. **Execute** - Click "Run"
   - OperationRunner executes the tool
   - Progress updates stream to UI
   - Result updates SessionService

4. **Chain or Export**
   - User can immediately navigate to another tool (input auto-filled)
   - Or export/open folder/copy stats

---

## 8. Progress Reporting Implementation

### 8.1 Unified Progress Event

```csharp
public sealed class ProgressTracker : IProgressTracker
{
    private readonly IProgress<ProgressEvent>? _uiProgress;
    private double _overallPercent;
    
    public void Report(ProgressEvent evt)
    {
        _overallPercent = evt.PhasePercent;
        _uiProgress?.Report(evt);
    }
    
    public double OverallPercent => _overallPercent;
    
    // For multi-phase operations
    public IProgressTracker CreateSubTracker(string phaseName, double weight = 1.0)
    {
        return new SubProgressTracker(this, phaseName, weight);
    }
}
```

### 8.2 ViewModel Integration

```csharp
public sealed class FilterViewModel : ToolViewModelBase
{
    private async Task<OperationResult> ExecuteOperationAsync(CancellationToken ct)
    {
        var progress = new Progress<ProgressEvent>(evt =>
        {
            // Update UI on UI thread
            Progress = evt.PhasePercent;
            StatusMessage = $"{evt.Phase}: {evt.CurrentItem:N0} / {evt.TotalItems:N0} {evt.Unit}";
        });
        
        var tracker = new ProgressTracker(progress);
        
        var operation = _services.GetRequiredService<FilterOperation>();
        var context = new OperationContext(
            InputDataset: new PgnDataset(InputFilePath!, ...),
            OutputPath: OutputFilePath!,
            Options: BuildOptions(),
            Progress: tracker,
            Services: _services
        );
        
        return await Runner.RunAsync(operation, context, ct);
    }
}
```

---

## 9. Dependency Injection Configuration

```csharp
public sealed class App : Application
{
    private IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        // Infrastructure
        services.AddSingleton<PgnReader>();
        services.AddSingleton<PgnWriter>();
        services.AddSingleton<HttpClient>(CreateHttpClient());
        
        // Core Services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<OperationRunner>();
        
        // Register all operations from ToolRegistry
        foreach (var tool in ToolRegistry.Tools)
        {
            services.AddTransient(tool.OperationType);
        }
        
        // Register all ViewModels
        foreach (var tool in ToolRegistry.Tools)
        {
            services.AddTransient(tool.ViewModelType);
        }
        
        // Databases
        services.AddSingleton<IEcoDatabase, EcoDatabase>();
        services.AddSingleton<IRatingsDatabase, RatingsDatabase>();
        
        return services.BuildServiceProvider();
    }
}
```

---

## 10. File I/O Patterns

### 10.1 Atomic File Replacement

All tools write to a temp file, then atomically replace:

```csharp
public static class FileReplacementHelper
{
    public static async Task WriteAndReplaceAsync(
        string targetPath,
        Func<Stream, Task> writeAction)
    {
        var tempPath = targetPath + ".tmp";
        
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await writeAction(stream);
            }
            
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }
    }
}
```

### 10.2 Streaming with Progress

Progress reports based on stream position:

```csharp
private static void ReportProgress(
    Stream stream,
    long totalBytes,
    IProgressTracker progress)
{
    var percent = stream.Position / (double)totalBytes * 100;
    progress.Report(new ProgressEvent(
        "Processing",
        percent,
        stream.Position,
        totalBytes,
        "bytes"
    ));
}
```

---

## 11. Error Handling Strategy

### 11.1 Operation-Level Errors

```csharp
public sealed record OperationResult
{
    public static OperationResult Success(PgnDataset output, ResultStatistics stats) =>
        new(true, output, stats, [], null, TimeSpan.Zero);
    
    public static OperationResult Failure(string error, TimeSpan duration) =>
        new(false, null, default, [], error, duration);
    
    public static OperationResult Cancelled(TimeSpan duration) =>
        new(false, null, default, [], "Operation cancelled", duration);
}
```

### 11.2 UI Error Display

```csharp
// In ViewModel
protected async Task ExecuteAsync()
{
    try
    {
        var result = await ExecuteOperationAsync(Cts.Token);
        
        if (!result.Success)
        {
            await Dialogs.ShowErrorAsync(
                "Operation Failed",
                result.ErrorMessage ?? "Unknown error");
            return;
        }
        
        await Dialogs.ShowSuccessAsync(
            "Complete",
            $"Processed {result.Statistics.GamesProcessed:N0} games");
        
        Session.UpdateFromResult(result);
    }
    catch (Exception ex)
    {
        await Dialogs.ShowErrorAsync("Error", ex.Message);
    }
}
```

---

## 12. Testing Strategy

### 12.1 Unit Tests (Core)

```csharp
[Fact]
public async Task PgnReader_StreamsGamesCorrectly()
{
    var pgn = """
        [Event "Test"]
        [White "Alice"]
        [Black "Bob"]
        
        1. e4 e5 2. Nf3 Nc6 *
        
        [Event "Test2"]
        [White "Carol"]
        [Black "Dave"]
        
        1. d4 d5 *
        """;
    
    var stream = new MemoryStream(Encoding.UTF8.GetBytes(pgn));
    var reader = new PgnReader();
    
    var games = await reader.ReadGamesAsync(stream).ToListAsync();
    
    Assert.Equal(2, games.Count);
    Assert.Equal("Alice", games[0].Headers["White"]);
    Assert.Equal("Carol", games[1].Headers["White"]);
}
```

### 12.2 Integration Tests (Operations)

```csharp
[Fact]
public async Task FilterOperation_FiltersCorrectly()
{
    var services = CreateTestServices();
    var operation = new FilterOperation(
        services.GetRequiredService<PgnReader>(),
        services.GetRequiredService<PgnWriter>()
    );
    
    var inputPath = CreateTestPgn(1000); // Helper to create test file
    var outputPath = Path.GetTempFileName();
    
    var context = new OperationContext(
        new PgnDataset(inputPath, ...),
        outputPath,
        new FilterOptions(MinElo: 2000, ...),
        new NullProgressTracker(),
        services
    );
    
    var result = await operation.ExecuteAsync(context, CancellationToken.None);
    
    Assert.True(result.Success);
    Assert.True(result.Statistics.GamesOutput < result.Statistics.GamesProcessed);
}
```

---

## 13. Migration Path

### Phase 1: Core Refactoring (2-3 weeks)
1. Extract `PgnTools.Core` project
   - Move PgnReader, PgnWriter, PgnGame to Core
   - Create operation interfaces
   - Implement 2-3 operations (Filter, EcoTagger, Splitter)

2. Create `PgnTools.Application` project
   - Implement OperationRunner
   - Create ToolRegistry with descriptors
   - Build ProgressTracker

### Phase 2: UI Adaptation (1-2 weeks)
3. Create ToolViewModelBase
   - Migrate 2-3 ViewModels to new base
   - Test with existing pages

4. Implement SessionService
   - Add to DI
   - Wire up dataset tracking

### Phase 3: Full Migration (3-4 weeks)
5. Migrate all remaining tools to operation pattern
6. Implement pipeline builder
7. Add pipeline UI (advanced feature)
8. Performance testing with multi-GB files

### Phase 4: Polish (1 week)
9. Add telemetry/logging
10. Complete test coverage
11. Documentation

---

## 14. Performance Targets

- **Streaming Performance**: Process 1M games/minute on modern hardware
- **Memory Usage**: Stay under 500MB even for multi-GB files
- **UI Responsiveness**: Progress updates every 100ms, UI never freezes
- **Startup Time**: < 2 seconds cold start

---

## 15. Future Enhancements

### Advanced Pipeline UI
- Visual pipeline builder (drag-drop tools)
- Save/load pipeline templates
- Batch processing (run same pipeline on multiple files)

### History & Undo
- Track all operations
- Allow reverting to previous datasets
- Export operation history

### Parallel Processing
- Multi-threaded processing for independent operations
- GPU acceleration for analysis (future)

---

## Conclusion

This architecture provides:
- **Clean separation** between domain logic and UI
- **Composable operations** that can be chained into workflows
- **Consistent UX** across all tools
- **High performance** through streaming and zero-allocation parsing
- **Testability** through dependency injection and abstractions
- **Extensibility** through the operation and registry patterns

The migration path is incremental, allowing continuous functionality while refactoring.

