# PgnTools: Architecture Improvements & Migration Guide
**Current vs. Proposed Architecture**

---

## Executive Summary

This document highlights the key improvements in the proposed architecture and provides a practical migration path from the current implementation to the new design.

---

## 1. Key Architectural Improvements

### 1.1 From Service Interfaces to Operation Pattern

**Current Approach:**
```csharp
// Each tool has a unique interface
public interface IPgnFilterService
{
    Task<PgnFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        PgnFilterOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IEcoTaggerService
{
    Task<EcoTaggerResult> TagAsync(
        string inputFilePath,
        string outputFilePath,
        EcoTaggerOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

// Result types are all different
public record PgnFilterResult(long Processed, long Kept, long Modified);
public record EcoTaggerResult(long Total, long Tagged);
```

**Problems:**
- Each tool defines its own interface and result type
- No common abstraction for tools
- Can't build generic pipeline infrastructure
- Progress reporting is inconsistent (some use `double`, some use custom types)
- No standardized metadata or validation

**Proposed Approach:**
```csharp
// Unified interface for all operations
public interface IPgnOperation
{
    OperationMetadata Metadata { get; }
    OperationValidation Validate(object options);
    Task<OperationResult> ExecuteAsync(OperationContext context, CancellationToken ct);
}

// Standardized result across all operations
public record OperationResult(
    bool Success,
    PgnDataset? OutputDataset,
    ResultStatistics Statistics,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage,
    TimeSpan Duration
);
```

**Benefits:**
- All tools share a common contract
- Generic infrastructure (OperationRunner, Pipeline) works with any tool
- Consistent progress reporting via `IProgressTracker`
- Built-in validation and metadata
- Tools can be composed into workflows

---

### 1.2 From File Paths to Dataset Objects

**Current Approach:**
```csharp
// Tools work with raw file paths
await filterService.FilterAsync(
    inputFilePath: "input.pgn",
    outputFilePath: "output.pgn",
    options: filterOptions
);

// No context about where the file came from or how it was created
```

**Problems:**
- Lose provenance information (how was this file created?)
- Can't track tool chains
- No cached metadata (game count, format, etc.)
- Manual file management between tools

**Proposed Approach:**
```csharp
// Tools work with PgnDataset objects
public record PgnDataset(
    string Path,
    DatasetFormat Format,
    DatasetMetadata Metadata,
    long? KnownGameCount = null,
    IReadOnlyDictionary<string, string>? Sidecars = null
);

public record DatasetMetadata(
    string Source,                  // "TWIC 1500", "Lichess Elite 2024-01"
    DateTime CreatedUtc,
    string? CreatedByTool = null,   // "ECO Tagger v2.0"
    IReadOnlyList<string>? ToolChain = null  // ["Download", "Filter", "Tag"]
);
```

**Benefits:**
- Full provenance tracking (you can see exactly how a file was created)
- Cached metadata speeds up operations (no need to count games repeatedly)
- SessionService can track "current working dataset"
- Users can chain tools without managing intermediate files

---

### 1.3 From Ad-Hoc Progress to Structured Events

**Current Approach:**
```csharp
// Progress is just a double (0-100)
IProgress<double>? progress = null;

// In service:
progress?.Report(processed / (double)total * 100);

// ViewModel must reconstruct context:
Progress = value;
StatusMessage = $"Processed {_processedCount} games"; // ViewModel tracks count separately
```

**Problems:**
- No context about what's happening
- ViewModel has to maintain parallel state
- Can't show phase information or item counts
- Multi-phase operations are difficult

**Proposed Approach:**
```csharp
public record ProgressEvent(
    string Phase,                   // "Reading", "Analyzing", "Writing"
    double PhasePercent,            // 0-100 for current phase
    long? CurrentItem = null,       // Item number
    long? TotalItems = null,        // Total items (if known)
    string? Unit = null,            // "games", "bytes", "MB"
    string? Message = null          // Optional status message
);

// In operation:
context.Progress.Report(new ProgressEvent(
    Phase: "Filtering",
    PhasePercent: percent,
    CurrentItem: processed,
    TotalItems: total,
    Unit: "games",
    Message: $"{kept:N0} games kept"
));

// ViewModel automatically gets all context:
Progress = evt.PhasePercent;
StatusMessage = $"{evt.Phase}: {evt.CurrentItem:N0} / {evt.TotalItems:N0} {evt.Unit}";
```

**Benefits:**
- Rich progress information out of the box
- ViewModel doesn't need to track parallel state
- Multi-phase operations are natural
- Consistent UX across all tools

---

### 1.4 From Manual DI Registration to Registry Pattern

**Current Approach:**
```csharp
// In App.xaml.cs - manually register every service
private static void RegisterToolServices(IServiceCollection services)
{
    services.AddTransient<ICategoryTaggerService, CategoryTaggerService>();
    services.AddTransient<ICheckmateFilterService, CheckmateFilterService>();
    services.AddTransient<IChessAnalyzerService, ChessAnalyzerService>();
    // ... 20+ more manual registrations
}

private static void RegisterViewModels(IServiceCollection services)
{
    services.AddTransient<CategoryTaggerViewModel>();
    services.AddTransient<CheckmateFilterViewModel>();
    services.AddTransient<ChessAnalyzerViewModel>();
    // ... 20+ more manual registrations
}

// And then manually register pages
private static void RegisterPages(INavigationService navigationService)
{
    foreach (var tool in ToolRegistry.Tools)
    {
        navService.RegisterPage(tool.Key, tool.PageType);
    }
}
```

**Problems:**
- Error-prone (easy to forget to register a new tool)
- Three separate places to add a new tool
- No compile-time checking that all pieces exist

**Proposed Approach:**
```csharp
// Single source of truth: ToolRegistry
public static class ToolRegistry
{
    public static IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(
            Key: "EcoTagger",
            DisplayName: "ECO Tagger",
            Description: "Add ECO codes and opening names",
            IconGlyph: "\uE8EC",
            Category: ToolCategory.Transform,
            OperationType: typeof(EcoTaggerOperation),  // Auto-registered
            ViewModelType: typeof(EcoTaggerViewModel),  // Auto-registered
            PageType: typeof(EcoTaggerPage)             // Auto-registered
        ),
        // Add new tool - that's it!
    ];
}

// In App.xaml.cs - automatic registration from registry
private static void ConfigureServices(IServiceCollection services)
{
    // Core services
    services.AddSingleton<OperationRunner>();
    services.AddSingleton<SessionService>();
    
    // Auto-register all operations and ViewModels from ToolRegistry
    foreach (var tool in ToolRegistry.Tools)
    {
        services.AddTransient(tool.OperationType);
        services.AddTransient(tool.ViewModelType);
    }
}
```

**Benefits:**
- Single place to add a new tool
- Reduced boilerplate
- Registry is the single source of truth for tool metadata
- Can generate tool lists, navigation, help text automatically

---

### 1.5 From Mixed Responsibilities to Clean Layers

**Current Structure (Flat):**
```
PgnTools/
├── Services/              # Mix of domain logic and infrastructure
│   ├── PgnReader.cs       # Core: Domain
│   ├── PgnFilterService.cs # Core: Domain
│   ├── LichessDownloaderService.cs  # Infrastructure: HTTP
│   ├── StockfishDownloaderService.cs # Infrastructure: HTTP
│   ├── EmbeddedRatingsDatabase.cs    # Infrastructure: Data
│   └── NavigationService.cs          # UI: Application
├── ViewModels/            # Presentation layer
└── Views/                 # Presentation layer
```

**Problems:**
- Domain logic (filtering, tagging) mixed with infrastructure (HTTP, database)
- Can't test domain logic without mocking file I/O
- Hard to reuse core logic in CLI or API

**Proposed Structure (Layered):**
```
PgnTools.Core/             # Pure domain - no UI, no HTTP, no file I/O dependencies
├── Models/                # PgnGame, Move, Position
├── Parsing/               # PgnReader, PgnWriter (interfaces)
└── Operations/            # All tool logic

PgnTools.Infrastructure/   # External dependencies
├── IO/                    # Concrete file readers/writers
├── Http/                  # Downloaders
├── Databases/             # ECO, Ratings
└── Processes/             # Engine management

PgnTools.Application/      # Orchestration - no UI
├── Pipelines/             # Pipeline builder & executor
├── Operations/            # OperationRunner
└── Registry/              # ToolRegistry

PgnTools.UI/               # WinUI 3 presentation
├── ViewModels/
└── Views/
```

**Benefits:**
- Core domain logic is testable without any mocking
- Could build a CLI using just Core + Infrastructure
- Could expose an API using Core + Infrastructure
- Clear dependency flow (UI → App → Core → Infra)

---

## 2. Specific Improvements in Current Code

### 2.1 PgnReader: Already Good, Minor Enhancements

**Current Implementation:**
```csharp
public async IAsyncEnumerable<PgnGame> ReadGamesAsync(
    string filePath,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    await using var stream = new FileStream(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.SequentialScan | FileOptions.Asynchronous);

    await foreach (var game in ReadGamesAsync(stream, cancellationToken))
    {
        yield return game;
    }
}
```

**Analysis:** ✅ Already excellent - streaming, async, proper buffering

**Minor Enhancement:**
```csharp
// Move to Core as interface, keep implementation in Infrastructure
namespace PgnTools.Core.Parsing;
public interface IPgnReader
{
    IAsyncEnumerable<PgnGame> ReadGamesAsync(
        string filePath,
        CancellationToken ct = default);
    
    IAsyncEnumerable<PgnGame> ReadGamesAsync(
        Stream stream,
        CancellationToken ct = default);
}

// Allows for mocking in tests, alternative implementations (compressed, network, etc.)
```

### 2.2 Services: Convert to Operations

**Current: PgnFilterService**
```csharp
public sealed class PgnFilterService : IPgnFilterService
{
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;
    
    public async Task<PgnFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        PgnFilterOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // Implementation...
    }
}
```

**Proposed: FilterOperation**
```csharp
public sealed class FilterOperation : IPgnOperation
{
    private readonly IPgnReader _reader;
    private readonly IPgnWriter _writer;
    
    public OperationMetadata Metadata { get; } = new(...);
    
    public OperationValidation Validate(object options) => ...;
    
    public async Task<OperationResult> ExecuteAsync(
        OperationContext context,
        CancellationToken ct)
    {
        var options = (FilterOptions)context.Options;
        var dataset = context.InputDataset!;
        
        // Same implementation, but:
        // 1. Input/output from context
        // 2. Rich progress events
        // 3. Returns standardized OperationResult
        // 4. Produces PgnDataset with metadata
    }
}
```

**Migration Strategy:**
1. Keep existing service for backward compatibility
2. Create new operation wrapper that calls existing service
3. Gradually move ViewModels to use operations
4. Eventually inline service logic into operation and remove service

### 2.3 ViewModels: Reduce Boilerplate

**Current: Every ViewModel Repeats Pattern**
```csharp
public partial class EcoTaggerViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _inputFilePath;
    
    [ObservableProperty]
    private string? _outputFilePath;
    
    [ObservableProperty]
    private bool _isRunning;
    
    [ObservableProperty]
    private double _progress;
    
    // ... 50+ lines of boilerplate repeated in every tool
    
    [RelayCommand]
    private async Task ExecuteAsync()
    {
        try
        {
            IsRunning = true;
            // ... manual progress tracking
            await _service.TagAsync(...);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
```

**Proposed: Base Class Handles Common Concerns**
```csharp
public partial class EcoTaggerViewModel : ToolViewModelBase
{
    private readonly EcoTaggerOperation _operation;
    
    // Only tool-specific properties
    [ObservableProperty]
    private bool _addEcoCode = true;
    
    [ObservableProperty]
    private bool _addOpening = true;
    
    [ObservableProperty]
    private bool _overwriteExisting;
    
    // Base class provides: InputFilePath, OutputFilePath, IsRunning, 
    // Progress, ExecuteCommand, CancelCommand, etc.
    
    protected override async Task<OperationResult> ExecuteOperationAsync(CancellationToken ct)
    {
        var context = new OperationContext(
            Session.CurrentDataset,
            OutputFilePath!,
            new EcoTaggerOptions(AddEcoCode, AddOpening, AddVariation, OverwriteExisting),
            CreateProgressTracker(),
            Services
        );
        
        return await Runner.RunAsync(_operation, context, ct);
    }
}
```

**Benefits:**
- 80% less boilerplate per ViewModel
- Consistent error handling and progress reporting
- Easy to add new tools

---

## 3. Migration Strategy

### Phase 1: Foundation (Week 1-2)

**Goal:** Set up new projects without breaking existing code

1. **Create new projects:**
   ```
   PgnTools.Core/
   PgnTools.Infrastructure/
   PgnTools.Application/
   ```

2. **Move core types to PgnTools.Core:**
   - Copy `PgnGame`, `PgnStatistics` → `PgnTools.Core/Models/`
   - Copy `PgnReader` interface → `PgnTools.Core/Parsing/`
   - Keep implementation in Infrastructure for now

3. **Create new abstractions:**
   - `IPgnOperation` interface
   - `OperationContext`, `OperationResult` records
   - `PgnDataset` record

4. **Build OperationRunner:**
   - Implement `OperationRunner` in Application layer
   - Unit test with simple mock operation

5. **Update main project references:**
   ```xml
   <ItemGroup>
     <ProjectReference Include="../PgnTools.Core/PgnTools.Core.csproj" />
     <ProjectReference Include="../PgnTools.Infrastructure/PgnTools.Infrastructure.csproj" />
     <ProjectReference Include="../PgnTools.Application/PgnTools.Application.csproj" />
   </ItemGroup>
   ```

**Validation:** All existing code still compiles and runs. New projects build but aren't used yet.

---

### Phase 2: First Operation (Week 3)

**Goal:** Prove the pattern with one complete tool

1. **Pick simplest tool:** `PlycountAdder` (no complex logic, no external dependencies)

2. **Create `PlycountAdderOperation`:**
   - Implement `IPgnOperation`
   - Unit tests
   
3. **Create base ViewModel:**
   - Implement `ToolViewModelBase` with all common logic
   - Test with existing page

4. **Convert ViewModel:**
   - Create new `PlycountAdderViewModel : ToolViewModelBase`
   - Keep existing service for now
   - Update page binding (should be minimal changes)

5. **End-to-end test:**
   - Run tool in UI
   - Verify progress reporting
   - Verify result handling

**Validation:** One tool works end-to-end with new architecture. Existing tools still work with old pattern.

---

### Phase 3: Convert More Tools (Week 4-5)

**Goal:** Convert tools in order of complexity

**Batch 1: Simple transformations (no external dependencies)**
- `EloAdder`
- `CategoryTagger`
- `ChessUnannotator`

**Batch 2: Tools with database dependencies**
- `EcoTagger` (requires ECO database)
- `RemoveDoubles` (deduplication logic)

**Batch 3: Complex tools**
- `PgnFilter` (multiple options, complex logic)
- `ChessAnalyzer` (engine integration)
- `Elegance` (analysis + scoring)

**Batch 4: Downloaders**
- `TwicDownloader`
- `LichessDownloader`
- `ChesscomDownloader`

For each batch:
1. Create Operation class
2. Write unit tests
3. Convert ViewModel to inherit from `ToolViewModelBase`
4. Integration test
5. Remove old service interface once confirmed working

**Validation:** All tools work with new architecture. Old service interfaces can be deleted.

---

### Phase 4: Session & Pipeline (Week 6)

**Goal:** Add advanced features

1. **Implement SessionService:**
   - Track current dataset
   - Update from operation results
   - Wire up to UI

2. **Update all ViewModels:**
   - Auto-populate input from session
   - Update session on completion

3. **Build Pipeline infrastructure:**
   - `PipelineBuilder`
   - `Pipeline` executor
   - Multi-phase progress aggregation

4. **Add Pipeline UI (optional):**
   - Page for building pipelines
   - Visual tool chain display

**Validation:** Users can seamlessly chain tools without managing files manually.

---

### Phase 5: Testing & Polish (Week 7)

1. **Performance testing:**
   - Test with multi-GB files (TWIC, Lichess databases)
   - Profile memory usage
   - Optimize hot paths

2. **Integration tests:**
   - End-to-end pipeline tests
   - Error recovery tests
   - Cancellation tests

3. **Documentation:**
   - Update README
   - Document operation interface for contributors
   - Add examples

4. **UI polish:**
   - Consistent styling
   - Better error messages
   - Tooltips and help text

**Validation:** All tests pass, performance targets met, ready for release.

---

## 4. Risk Mitigation

### Risk 1: Breaking Changes During Migration

**Mitigation:**
- Keep old and new code side-by-side during migration
- Feature flag for new operations (optional toggle in Settings)
- Migrate one tool at a time
- Extensive testing before deleting old code

### Risk 2: Performance Regression

**Mitigation:**
- Benchmark before/after for each converted tool
- Profile memory usage with large files
- Optimize if regression detected (unlikely since core logic doesn't change)

### Risk 3: Increased Complexity

**Mitigation:**
- The abstraction reduces overall complexity (less boilerplate)
- Clear documentation of patterns
- Benefits outweigh learning curve (easier to add new tools)

---

## 5. Benefits Summary

### For Users
- Smoother workflow (tools chain together automatically)
- Better progress feedback (phase info, item counts)
- Provenance tracking (see how files were created)
- More reliable (atomic file operations, error recovery)

### For Developers
- Less boilerplate (80% reduction in ViewModel code)
- Easier to add new tools (inherit base, implement operation)
- Better testability (domain logic separate from UI/IO)
- Composable operations (build complex workflows)

### For the Project
- More maintainable architecture
- Could support CLI/API in future
- Easier to onboard contributors
- Professional-grade code structure

---

## Conclusion

The proposed architecture is:
- **Evolutionary, not revolutionary:** Builds on existing code's strengths
- **Incrementally adoptable:** Can migrate one tool at a time
- **Low risk:** Old and new code coexist during migration
- **High value:** Significant improvements in maintainability and UX

The current codebase is already well-structured (streaming I/O, DI, MVVM). This architecture formalizes the patterns that are already emerging and eliminates duplication.

**Recommended Next Step:** Start with Phase 1 (foundation) and Phase 2 (first operation) as a proof-of-concept. If successful, proceed with full migration.
