using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for local chess engine source compilation.
/// </summary>
public partial class CompilerViewModel(
    IStockfishCompilerService compilerService,
    IBerserkCompilerService berserkCompilerService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private const string SettingsPrefix = nameof(CompilerViewModel);
    private const int MaxLogLines = 5000;
    private const int GitHubTagPageSize = 100;
    private const int GitHubTagPageLimit = 10;
    private static readonly Uri StockfishTagsApiBaseUri = new("https://api.github.com/repos/official-stockfish/Stockfish/tags");
    private static readonly Uri BerserkTagsApiBaseUri = new("https://api.github.com/repos/jhonnold/berserk/tags");
    private static readonly HttpClient GitHubClient = CreateGitHubClient();

    private static readonly List<string> EngineOptions =
    [
        "Stockfish",
        "Berserk"
    ];

    private static readonly List<string> StockfishFallbackSourceRefOptions =
    [
        "master",
        "sf_18",
        "sf_17.1",
        "sf_17",
        "sf_16.1",
        "sf_16",
        "sf_15.1",
        "sf_15",
        "sf_14.1",
        "sf_14",
        "sf_13",
        "sf_12",
        "sf_11",
        "sf_10"
    ];

    private static readonly List<string> BerserkFallbackSourceRefOptions =
    [
        "main",
        "13",
        "12.1",
        "12",
        "11.1",
        "11",
        "10",
        "9",
        "8.5.1",
        "8",
        "7",
        "6",
        "5"
    ];

    private static readonly List<string> BuildTargetOptions =
    [
        "profile-build",
        "build"
    ];

    private static readonly List<string> CompilerOptions =
    [
        "auto",
        "mingw",
        "clang"
    ];

    private static readonly List<string> ArchitectureOptions =
    [
        "native",
        "x86-64-avx512icl",
        "x86-64-vnni512",
        "x86-64-avx512",
        "x86-64-avxvnni",
        "x86-64-bmi2",
        "x86-64-avx2",
        "x86-64-sse41-popcnt",
        "x86-64-modern",
        "x86-64-ssse3",
        "x86-64-sse3-popcnt",
        "x86-64",
        "x86-32-sse41-popcnt",
        "x86-32-sse2",
        "x86-32",
        "ppc-64",
        "ppc-64-altivec",
        "ppc-64-vsx",
        "ppc-32",
        "armv7",
        "armv7-neon",
        "armv8",
        "armv8-dotprod",
        "e2k",
        "apple-silicon",
        "general-64",
        "general-32",
        "riscv64",
        "loongarch64",
        "loongarch64-lsx",
        "loongarch64-lasx"
    ];

    private readonly IStockfishCompilerService _compilerService = compilerService;
    private readonly IBerserkCompilerService _berserkCompilerService = berserkCompilerService;
    private readonly IWindowService _windowService = windowService;
    private readonly IAppSettingsService _settings = settings;
    private readonly Queue<string> _logLines = new();
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;
    private bool _disposeRequested;

    [ObservableProperty]
    private List<string> _engines = [.. EngineOptions];

    [ObservableProperty]
    private string _selectedEngine = EngineOptions[0];

    [ObservableProperty]
    private List<SourceRefOption> _availableSourceRefs = BuildSourceRefOptions(StockfishFallbackSourceRefOptions, EngineOptions[0]);

    [ObservableProperty]
    private string _selectedSourceRef = StockfishFallbackSourceRefOptions[0];

    [ObservableProperty]
    private string _sourceRef = "master";

    [ObservableProperty]
    private List<string> _availableBuildTargets = [.. BuildTargetOptions];

    [ObservableProperty]
    private string _selectedBuildTarget = BuildTargetOptions[0];

    [ObservableProperty]
    private List<string> _availableCompilers = [.. CompilerOptions];

    [ObservableProperty]
    private string _selectedCompiler = CompilerOptions[0];

    [ObservableProperty]
    private bool _autoInstallToolchain = true;

    [ObservableProperty]
    private string _toolchainSummary = "Detecting compiler toolchain...";

    [ObservableProperty]
    private string _recommendedCompiler = "mingw";

    [ObservableProperty]
    private string _activeMsysRoot = string.Empty;

    [ObservableProperty]
    private List<string> _availableArchitectures = [.. ArchitectureOptions];

    [ObservableProperty]
    private string _selectedArchitecture = ArchitectureOptions[0];

    [ObservableProperty]
    private string _parallelJobs = Math.Max(1, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _downloadNetwork = true;

    [ObservableProperty]
    private bool _stripExecutable = true;

    [ObservableProperty]
    private string _workspaceFolder = string.Empty;

    [ObservableProperty]
    private string _gitHubPat = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _buildOutput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Select compile settings and choose a workspace folder";

    public void Initialize()
    {
        Title = "Compiler";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();

        if (string.IsNullOrWhiteSpace(WorkspaceFolder))
        {
            WorkspaceFolder = GetDefaultWorkspaceFolder();
        }

        StatusDetail = BuildProgressDetail(0);
        _ = RefreshSourceRefsInternalAsync(userInitiated: false);
        _ = DetectToolchainAsync();
    }

    public void SetGitHubPat(string value)
    {
        GitHubPat = value ?? string.Empty;
    }

    [RelayCommand]
    private async Task BrowseWorkspaceAsync()
    {
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Workspace");
            if (folder == null)
            {
                return;
            }

            WorkspaceFolder = folder.Path;
            StatusMessage = $"Selected workspace: {folder.Path}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting workspace: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageToolchain))]
    private async Task DetectToolchainAsync()
    {
        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusMessage = "Detecting compiler toolchain...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            var cts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _cancellationTokenSource, cts);
            previousCts?.Dispose();

            var output = new Progress<string>(AppendOutputLine);
            var probe = await _compilerService.ProbeToolchainAsync(
                SelectedCompiler,
                DownloadNetwork,
                output,
                cts.Token);

            ApplyToolchainProbe(probe);
            StatusMessage = probe.IsReadyForSelection
                ? "Compiler toolchain ready."
                : "Compiler toolchain setup required.";
            StatusSeverity = probe.IsReadyForSelection
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            StatusDetail = probe.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Toolchain detection cancelled.";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Toolchain detection failed: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue);
            AppendOutputLine($"Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
            cts?.Dispose();
            _executionLock.Release();
            StopProgressTimer();

            if (_disposeRequested)
            {
                Dispose();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageToolchain))]
    private async Task InstallOrRepairToolchainAsync()
    {
        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusMessage = "Installing or repairing compiler toolchain...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            var cts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _cancellationTokenSource, cts);
            previousCts?.Dispose();

            var output = new Progress<string>(AppendOutputLine);
            var probe = await _compilerService.InstallOrRepairToolchainAsync(
                SelectedCompiler,
                DownloadNetwork,
                output,
                cts.Token);

            ApplyToolchainProbe(probe);
            StatusMessage = "Toolchain installation/repair complete.";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = probe.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Toolchain installation cancelled.";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Toolchain installation failed: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue);
            AppendOutputLine($"Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
            cts?.Dispose();
            _executionLock.Release();
            StopProgressTimer();

            if (_disposeRequested)
            {
                Dispose();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageToolchain))]
    private Task RefreshSourceRefsAsync() => RefreshSourceRefsInternalAsync(userInitiated: true);

    private async Task RefreshSourceRefsInternalAsync(bool userInitiated)
    {
        try
        {
            var refs = await FetchSourceRefsAsync(SelectedEngine, CancellationToken.None);
            if (refs.Count == 0)
            {
                refs = [.. GetFallbackSourceRefs(SelectedEngine)];
            }

            var options = BuildSourceRefOptions(refs, SelectedEngine);
            AvailableSourceRefs = options;

            var sourceRefMatch = options.FirstOrDefault(
                option => string.Equals(option.Ref, SourceRef, StringComparison.OrdinalIgnoreCase));
            SelectedSourceRef = sourceRefMatch?.Ref ?? string.Empty;

            if (string.IsNullOrWhiteSpace(SourceRef))
            {
                SourceRef = refs[0];
                SelectedSourceRef = refs[0];
            }

            if (userInitiated)
            {
                StatusMessage = $"Loaded {refs.Count:N0} {SelectedEngine} source refs.";
                StatusSeverity = InfoBarSeverity.Success;
            }
        }
        catch (OperationCanceledException)
        {
            if (userInitiated)
            {
                StatusMessage = "Source ref refresh cancelled.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
        }
        catch (Exception ex)
        {
            var fallbackRefs = GetFallbackSourceRefs(SelectedEngine);
            AvailableSourceRefs = BuildSourceRefOptions(fallbackRefs, SelectedEngine);
            var sourceRefMatch = fallbackRefs.FirstOrDefault(
                option => string.Equals(option, SourceRef, StringComparison.OrdinalIgnoreCase));
            SelectedSourceRef = sourceRefMatch ?? string.Empty;

            AppendOutputLine(
                $"Failed to refresh {SelectedEngine} tags from GitHub. Using fallback list. Error: {ex.Message}");
            if (userInitiated)
            {
                StatusMessage = $"Could not refresh {SelectedEngine} tags. Using fallback version list.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCompile))]
    private async Task StartCompileAsync()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceFolder))
        {
            StatusMessage = "Please select a workspace folder.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (!int.TryParse(ParallelJobs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jobs) || jobs < 1)
        {
            StatusMessage = "Parallel jobs must be a positive whole number.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var validation = await FileValidationHelper.ValidateWritableFolderAsync(WorkspaceFolder);
        if (!validation.Success)
        {
            StatusMessage = $"Cannot write to workspace folder: {validation.ErrorMessage}";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            ClearOutput();
            IsRunning = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusMessage = $"Starting {SelectedEngine} compilation...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            var cts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _cancellationTokenSource, cts);
            previousCts?.Dispose();

            var progress = new Progress<StockfishCompileProgress>(OnCompileProgress);
            var output = new Progress<string>(AppendOutputLine);
            var buildTarget = string.Equals(SelectedBuildTarget, "build", StringComparison.OrdinalIgnoreCase)
                ? StockfishBuildTarget.Build
                : StockfishBuildTarget.ProfileBuild;
            var options = new StockfishCompileOptions(
                WorkspaceFolder,
                SourceRef,
                SelectedArchitecture,
                SelectedCompiler,
                buildTarget,
                jobs,
                DownloadNetwork,
                StripExecutable,
                AutoInstallToolchain,
                GitHubPat);
            AppendOutputLine(
                $"Starting build: engine={SelectedEngine}, target={buildTarget}, arch={SelectedArchitecture}, compiler={SelectedCompiler}, jobs={jobs}, net={DownloadNetwork}, strip={StripExecutable}");

            var result = string.Equals(SelectedEngine, "Berserk", StringComparison.OrdinalIgnoreCase)
                ? await _berserkCompilerService.CompileAsync(options, progress, output, cts.Token)
                : await _compilerService.CompileAsync(options, progress, output, cts.Token);

            ProgressValue = 100;
            IsIndeterminate = false;
            StatusMessage = $"Compilation complete: {Path.GetFileName(result.OutputBinaryPath)}";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = $"{BuildProgressDetail(100)} • {result.OutputBinaryPath}";
            AppendOutputLine($"Build completed. Output: {result.OutputBinaryPath}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Compilation cancelled.";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compilation failed: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue);
            AppendOutputLine($"Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
            cts?.Dispose();
            _executionLock.Release();
            StopProgressTimer();

            if (_disposeRequested)
            {
                Dispose();
            }
        }
    }

    private bool CanCompile() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(WorkspaceFolder) &&
        !string.IsNullOrWhiteSpace(SourceRef);

    private bool CanManageToolchain() => !IsRunning;

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }

    [RelayCommand]
    private void ClearOutput()
    {
        _logLines.Clear();
        BuildOutput = string.Empty;
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (string.IsNullOrWhiteSpace(BuildOutput))
        {
            StatusMessage = "No build log to copy.";
            StatusSeverity = InfoBarSeverity.Informational;
            return;
        }

        var package = new DataPackage();
        package.SetText(BuildOutput);
        Clipboard.SetContent(package);
        StatusMessage = "Build log copied to clipboard.";
        StatusSeverity = InfoBarSeverity.Success;
    }

    partial void OnSourceRefChanged(string value)
    {
        StartCompileCommand.NotifyCanExecuteChanged();

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var sourceRefMatch = AvailableSourceRefs.FirstOrDefault(
            option => string.Equals(option.Ref, value, StringComparison.OrdinalIgnoreCase));
        if (sourceRefMatch != null &&
            !string.Equals(sourceRefMatch.Ref, SelectedSourceRef, StringComparison.Ordinal))
        {
            SelectedSourceRef = sourceRefMatch.Ref;
        }
    }

    partial void OnSelectedSourceRefChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!string.Equals(SourceRef, value, StringComparison.Ordinal))
        {
            SourceRef = value;
        }
    }

    partial void OnWorkspaceFolderChanged(string value)
    {
        StartCompileCommand.NotifyCanExecuteChanged();
    }

    partial void OnParallelJobsChanged(string value)
    {
        StartCompileCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCompilerChanged(string value)
    {
        if (string.Equals(value, "gcc", StringComparison.OrdinalIgnoreCase))
        {
            SelectedCompiler = "mingw";
            return;
        }
    }

    partial void OnSelectedEngineChanged(string value)
    {
        var fallback = ValidateSelection(value, EngineOptions, EngineOptions[0]);
        if (!string.Equals(value, fallback, StringComparison.Ordinal))
        {
            SelectedEngine = fallback;
            return;
        }

        var fallbackRefs = GetFallbackSourceRefs(fallback);
        AvailableSourceRefs = BuildSourceRefOptions(fallbackRefs, fallback);

        if (string.IsNullOrWhiteSpace(SourceRef))
        {
            SourceRef = fallbackRefs[0];
            SelectedSourceRef = fallbackRefs[0];
        }
        else
        {
            var sourceRefMatch = fallbackRefs.FirstOrDefault(
                option => string.Equals(option, SourceRef, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(sourceRefMatch))
            {
                SourceRef = fallbackRefs[0];
                SelectedSourceRef = fallbackRefs[0];
            }
            else
            {
                SelectedSourceRef = sourceRefMatch;
            }
        }

        _ = RefreshSourceRefsInternalAsync(userInitiated: false);
        StartCompileCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        StartCompileCommand.NotifyCanExecuteChanged();
        DetectToolchainCommand.NotifyCanExecuteChanged();
        InstallOrRepairToolchainCommand.NotifyCanExecuteChanged();
        RefreshSourceRefsCommand.NotifyCanExecuteChanged();
    }

    public void RequestDispose()
    {
        if (_disposed)
        {
            return;
        }

        if (IsRunning)
        {
            _disposeRequested = true;
            Cancel();
            return;
        }

        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SaveState();

        var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (IsRunning || _executionLock.CurrentCount == 0)
        {
            _disposeRequested = true;
            return;
        }

        _disposed = true;
        _executionLock.Dispose();
    }

    private void OnCompileProgress(StockfishCompileProgress progress)
    {
        StatusMessage = progress.Message;

        if (progress.Percent.HasValue)
        {
            ProgressValue = progress.Percent.Value;
            IsIndeterminate = false;
        }
        else
        {
            IsIndeterminate = true;
        }

        var stageText = progress.Stage switch
        {
            StockfishCompileStage.Preparing => "Preparing",
            StockfishCompileStage.Toolchain => "Toolchain",
            StockfishCompileStage.Cloning => "Cloning",
            StockfishCompileStage.DownloadingNetwork => "Downloading network",
            StockfishCompileStage.Building => "Compiling",
            StockfishCompileStage.Stripping => "Stripping",
            StockfishCompileStage.Finalizing => "Finalizing",
            StockfishCompileStage.Completed => "Complete",
            _ => string.Empty
        };

        var detail = BuildProgressDetail(progress.Percent);
        StatusDetail = string.IsNullOrWhiteSpace(stageText)
            ? detail
            : string.IsNullOrWhiteSpace(detail)
                ? stageText
                : $"{detail} • {stageText}";
    }

    private void ApplyToolchainProbe(StockfishToolchainProbe probe)
    {
        ToolchainSummary = probe.Summary;
        RecommendedCompiler = probe.RecommendedCompiler;
        ActiveMsysRoot = probe.ActiveMsysRoot ?? string.Empty;
    }

    private void AppendOutputLine(string line)
    {
        if (line == null)
        {
            return;
        }

        _logLines.Enqueue(line);
        while (_logLines.Count > MaxLogLines)
        {
            _logLines.Dequeue();
        }

        BuildOutput = string.Join(Environment.NewLine, _logLines);
    }

    private void LoadState()
    {
        SelectedEngine = ValidateSelection(
            _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedEngine)}", SelectedEngine),
            EngineOptions,
            EngineOptions[0]);

        var fallbackRefs = GetFallbackSourceRefs(SelectedEngine);
        AvailableSourceRefs = BuildSourceRefOptions(fallbackRefs, SelectedEngine);
        SourceRef = _settings.GetValue($"{SettingsPrefix}.{nameof(SourceRef)}", SourceRef);
        var sourceRefMatch = fallbackRefs.FirstOrDefault(
            option => string.Equals(option, SourceRef, StringComparison.OrdinalIgnoreCase));
        SelectedSourceRef = sourceRefMatch ?? string.Empty;
        if (string.IsNullOrWhiteSpace(SourceRef))
        {
            SourceRef = fallbackRefs[0];
            SelectedSourceRef = fallbackRefs[0];
        }

        SelectedBuildTarget = ValidateSelection(
            _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedBuildTarget)}", SelectedBuildTarget),
            BuildTargetOptions,
            BuildTargetOptions[0]);
        var savedCompiler = _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedCompiler)}", SelectedCompiler);
        if (string.Equals(savedCompiler, "gcc", StringComparison.OrdinalIgnoreCase))
        {
            savedCompiler = "mingw";
        }

        SelectedCompiler = ValidateSelection(
            savedCompiler,
            CompilerOptions,
            CompilerOptions[0]);
        SelectedArchitecture = ValidateSelection(
            _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedArchitecture)}", SelectedArchitecture),
            ArchitectureOptions,
            ArchitectureOptions[0]);
        ParallelJobs = _settings.GetValue($"{SettingsPrefix}.{nameof(ParallelJobs)}", ParallelJobs);
        DownloadNetwork = _settings.GetValue($"{SettingsPrefix}.{nameof(DownloadNetwork)}", DownloadNetwork);
        StripExecutable = _settings.GetValue($"{SettingsPrefix}.{nameof(StripExecutable)}", StripExecutable);
        AutoInstallToolchain = _settings.GetValue($"{SettingsPrefix}.{nameof(AutoInstallToolchain)}", AutoInstallToolchain);
        WorkspaceFolder = _settings.GetValue($"{SettingsPrefix}.{nameof(WorkspaceFolder)}", WorkspaceFolder);
        GitHubPat = string.Empty;
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedEngine)}", SelectedEngine);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SourceRef)}", SourceRef);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedBuildTarget)}", SelectedBuildTarget);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedCompiler)}", SelectedCompiler);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedArchitecture)}", SelectedArchitecture);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ParallelJobs)}", ParallelJobs);
        _settings.SetValue($"{SettingsPrefix}.{nameof(DownloadNetwork)}", DownloadNetwork);
        _settings.SetValue($"{SettingsPrefix}.{nameof(StripExecutable)}", StripExecutable);
        _settings.SetValue($"{SettingsPrefix}.{nameof(AutoInstallToolchain)}", AutoInstallToolchain);
        _settings.SetValue($"{SettingsPrefix}.{nameof(WorkspaceFolder)}", WorkspaceFolder);
    }

    private string GetDefaultWorkspaceFolder()
    {
        var configured = _settings.GetValue(AppSettingsKeys.DefaultDownloadFolder, string.Empty);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static List<string> GetFallbackSourceRefs(string engine) =>
        string.Equals(engine, "Berserk", StringComparison.OrdinalIgnoreCase)
            ? BerserkFallbackSourceRefOptions
            : StockfishFallbackSourceRefOptions;

    private static List<SourceRefOption> BuildSourceRefOptions(IEnumerable<string> refs, string engine)
    {
        var options = new List<SourceRefOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceRef in refs)
        {
            if (string.IsNullOrWhiteSpace(sourceRef))
            {
                continue;
            }

            var normalized = sourceRef.Trim();
            if (!seen.Add(normalized))
            {
                continue;
            }

            options.Add(new SourceRefOption(normalized, FormatSourceRefDisplayName(normalized, engine)));
        }

        return options;
    }

    private static string FormatSourceRefDisplayName(string sourceRef, string engine)
    {
        if (string.Equals(engine, "Berserk", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(sourceRef, "main", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sourceRef, "master", StringComparison.OrdinalIgnoreCase))
            {
                return "Berserk Dev";
            }

            if (TryParseBerserkVersionText(sourceRef, out var berserkVersion))
            {
                return $"Berserk {berserkVersion}";
            }

            return sourceRef;
        }

        if (string.Equals(sourceRef, "master", StringComparison.OrdinalIgnoreCase))
        {
            return "Stockfish Dev";
        }

        if (TryParseStableVersionText(sourceRef, out var versionText))
        {
            return $"Stockfish {versionText}";
        }

        return sourceRef;
    }

    private static bool TryParseStableVersionText(string sourceRef, out string versionText)
    {
        versionText = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceRef) ||
            !sourceRef.StartsWith("sf_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = sourceRef[3..];
        var segments = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        var normalizedSegments = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            normalizedSegments[i] = parsed.ToString(CultureInfo.InvariantCulture);
        }

        versionText = string.Join(".", normalizedSegments);
        return true;
    }

    private static bool TryParseBerserkVersionText(string sourceRef, out string versionText)
    {
        versionText = string.Empty;
        var normalized = sourceRef.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        var normalizedSegments = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            normalizedSegments[i] = parsed.ToString(CultureInfo.InvariantCulture);
        }

        versionText = string.Join(".", normalizedSegments);
        return true;
    }

    private async Task<List<string>> FetchSourceRefsAsync(string engine, CancellationToken ct)
    {
        if (string.Equals(engine, "Berserk", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchBerserkSourceRefsAsync(ct).ConfigureAwait(false);
        }

        return await FetchStockfishSourceRefsAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<string>> FetchStockfishSourceRefsAsync(CancellationToken ct)
    {
        var stableTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= GitHubTagPageLimit; page++)
        {
            using var request = CreateGitHubTagRequest(StockfishTagsApiBaseUri, page);
            using var response = await GitHubClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (IsRateLimited(response))
            {
                throw new InvalidOperationException(
                    "GitHub API rate limit exceeded. Set PGNTOOLS_GITHUB_TOKEN or GITHUB_TOKEN to increase the limit.");
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var tagCount = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                tagCount++;
                if (!item.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (TryParseStableTag(name, out _))
                {
                    stableTags.Add(name!);
                }
            }

            if (tagCount < GitHubTagPageSize)
            {
                break;
            }
        }

        var orderedStableTags = stableTags
            .Select(tag => (Tag: tag, Version: ParseStableTag(tag)))
            .Where(x => x.Version.HasValue)
            .OrderByDescending(x => x.Version!.Value.Major)
            .ThenByDescending(x => x.Version!.Value.Minor)
            .ThenByDescending(x => x.Version!.Value.Patch)
            .ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Tag)
            .ToList();

        var sourceRefs = new List<string> { "master" };
        sourceRefs.AddRange(orderedStableTags);
        return sourceRefs;
    }

    private async Task<List<string>> FetchBerserkSourceRefsAsync(CancellationToken ct)
    {
        var versionTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= GitHubTagPageLimit; page++)
        {
            using var request = CreateGitHubTagRequest(BerserkTagsApiBaseUri, page);
            using var response = await GitHubClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (IsRateLimited(response))
            {
                throw new InvalidOperationException(
                    "GitHub API rate limit exceeded. Set PGNTOOLS_GITHUB_TOKEN or GITHUB_TOKEN to increase the limit.");
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var tagCount = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                tagCount++;
                if (!item.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (TryParseBerserkTag(name, out _))
                {
                    versionTags.Add(name!);
                }
            }

            if (tagCount < GitHubTagPageSize)
            {
                break;
            }
        }

        var ordered = versionTags
            .Select(tag => (Tag: tag, Version: ParseBerserkTag(tag)))
            .Where(x => x.Version.HasValue)
            .OrderByDescending(x => x.Version!.Value.Major)
            .ThenByDescending(x => x.Version!.Value.Minor)
            .ThenByDescending(x => x.Version!.Value.Patch)
            .ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Tag)
            .ToList();

        var refs = new List<string> { "main" };
        refs.AddRange(ordered);
        return refs;
    }

    private HttpRequestMessage CreateGitHubTagRequest(Uri apiBaseUri, int page)
    {
        var uri = new Uri($"{apiBaseUri}?per_page={GitHubTagPageSize}&page={page}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var token = GetGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private string? GetGitHubToken()
    {
        if (!string.IsNullOrWhiteSpace(GitHubPat))
        {
            return GitHubPat.Trim();
        }

        var token = Environment.GetEnvironmentVariable("PGNTOOLS_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        token = Environment.GetEnvironmentVariable("GH_TOKEN");
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
        {
            return false;
        }

        foreach (var value in remaining)
        {
            if (string.Equals(value, "0", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseStableTag(string? tag, out StableTagVersion version)
    {
        var parsed = ParseStableTag(tag);
        if (!parsed.HasValue)
        {
            version = default;
            return false;
        }

        version = parsed.Value;
        return true;
    }

    private static bool TryParseBerserkTag(string? tag, out BerserkTagVersion version)
    {
        var parsed = ParseBerserkTag(tag);
        if (!parsed.HasValue)
        {
            version = default;
            return false;
        }

        version = parsed.Value;
        return true;
    }

    private static StableTagVersion? ParseStableTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            !tag.StartsWith("sf_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var versionText = tag[3..];
        var segments = versionText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Length > 3)
        {
            return null;
        }

        var parts = new int[3];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return null;
            }
        }

        return new StableTagVersion(parts[0], parts[1], parts[2]);
    }

    private static BerserkTagVersion? ParseBerserkTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var normalized = tag.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 1 or > 3)
        {
            return null;
        }

        var parts = new int[3];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return null;
            }
        }

        return new BerserkTagVersion(parts[0], parts[1], parts[2]);
    }

    private static string ValidateSelection(string? value, IReadOnlyCollection<string> options, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            options.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return value;
        }

        return fallback;
    }

    public sealed record SourceRefOption(string Ref, string DisplayName);

    private readonly record struct StableTagVersion(int Major, int Minor, int Patch);
    private readonly record struct BerserkTagVersion(int Major, int Minor, int Patch);
}
