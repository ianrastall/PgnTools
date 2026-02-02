using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Elegance tool.
/// </summary>
public partial class EleganceViewModel : BaseViewModel, IDisposable
{
    private readonly IEleganceService _eleganceService;
    private readonly IEleganceGoldenValidationService _goldenValidationService;
    private readonly IStockfishDownloaderService _stockfishDownloaderService;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(EleganceViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _inputFileName = string.Empty;

    [ObservableProperty]
    private string _enginePath = string.Empty;

    [ObservableProperty]
    private string _engineName = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private int _depth = 14;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Select input/output PGN files and a UCI engine";

    [ObservableProperty]
    private double _progressValue;

    public EleganceViewModel(
        IEleganceService eleganceService,
        IEleganceGoldenValidationService goldenValidationService,
        IStockfishDownloaderService stockfishDownloaderService,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _eleganceService = eleganceService;
        _goldenValidationService = goldenValidationService;
        _stockfishDownloaderService = stockfishDownloaderService;
        _windowService = windowService;
        _settings = settings;
        Title = "Elegance";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }

    [RelayCommand]
    private async Task SelectInputFileAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSingleFileAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Input",
                ".pgn");
            if (file == null)
            {
                return;
            }

            var validation = await FileValidationHelper.ValidateReadableFileAsync(file);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot access file: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
            }

            InputFilePath = file.Path;
            InputFileName = file.Name;
            StatusMessage = $"Selected input: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;

            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                var directory = Path.GetDirectoryName(InputFilePath) ?? string.Empty;
                var suggestedName = $"{Path.GetFileNameWithoutExtension(InputFilePath)}-elegance.pgn";
                OutputFilePath = Path.Combine(directory, suggestedName);
                OutputFileName = Path.GetFileName(OutputFilePath);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting input file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task SelectEngineAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSingleFileAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Engine",
                ".exe");
            if (file == null)
            {
                return;
            }

            var validation = await FileValidationHelper.ValidateReadableFileAsync(file);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot access engine: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
            }

            EnginePath = file.Path;
            EngineName = file.Name;
            StatusMessage = $"Selected engine: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting engine: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadLatestEngine))]
    private async Task DownloadLatestEngineAsync()
    {
        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            StatusMessage = "Preparing Stockfish download...";
            StatusSeverity = InfoBarSeverity.Informational;
            ProgressValue = 0;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var statusProgress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail(ProgressValue);
            });

            var result = await _stockfishDownloaderService.DownloadLatestAsync(
                statusProgress,
                _cancellationTokenSource.Token);

            EnginePath = result.ExecutablePath;
            EngineName = Path.GetFileName(result.ExecutablePath);
            StatusMessage = $"Stockfish downloaded ({result.Tag}, {result.Variant}).";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stockfish download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error downloading Stockfish: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
        }
    }

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = string.IsNullOrWhiteSpace(InputFilePath)
                ? "output-elegance.pgn"
                : $"{Path.GetFileNameWithoutExtension(InputFilePath)}-elegance.pgn";

            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                suggestedName,
                new Dictionary<string, IList<string>>
                {
                    { "PGN Files", [".pgn"] }
                },
                $"{SettingsPrefix}.Picker.Output");

            if (file == null)
            {
                return;
            }

            OutputFilePath = file.Path;
            OutputFileName = file.Name;
            StatusMessage = $"Selected output: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting output file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(InputFilePath) ||
            string.IsNullOrWhiteSpace(EnginePath) ||
            string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return;
        }

        var inputFullPath = Path.GetFullPath(InputFilePath);
        var outputFullPath = Path.GetFullPath(OutputFilePath);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Input and output files must be different.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (Depth <= 0)
        {
            StatusMessage = "Depth must be greater than zero.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!File.Exists(EnginePath))
        {
            StatusMessage = "Engine executable not found.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            ProgressValue = 0;
            StatusMessage = "Running engine analysis and scoring elegance...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                ProgressValue = p;
                StatusMessage = $"Analyzing and scoring... {p:0}%";
                StatusDetail = BuildProgressDetail(p);
            });

            var result = await _eleganceService.TagEleganceAsync(
                InputFilePath,
                OutputFilePath,
                EnginePath,
                Depth,
                progress,
                _cancellationTokenSource.Token);

            if (result.ProcessedGames == 0)
            {
                StatusMessage = "No games found.";
                StatusSeverity = InfoBarSeverity.Warning;
                StatusDetail = BuildProgressDetail(100, 0, null, "games");
                return;
            }

            StatusMessage =
                $"Completed! Tagged {result.ProcessedGames:N0} games • Avg {result.AverageScore:0.0}" +
                $" (S:{result.AverageSoundness:0.0} C:{result.AverageCoherence:0.0} T:{result.AverageTactical:0.0} Q:{result.AverageQuiet:0.0})";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100, result.ProcessedGames, null, "games");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Elegance tagging cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            ProgressValue = 0;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
        }
    }

    [RelayCommand(CanExecute = nameof(CanValidateGoldens))]
    private async Task ValidateGoldensAsync()
    {
        if (string.IsNullOrWhiteSpace(EnginePath) || !File.Exists(EnginePath))
        {
            StatusMessage = "Select a valid engine first.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Assets", "elegance-goldens.json");
        if (!File.Exists(manifestPath))
        {
            StatusMessage = "Golden manifest not found: Assets/elegance-goldens.json";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            ProgressValue = 0;
            StatusMessage = "Running golden validation...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var statusProgress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail(ProgressValue);
            });

            var summary = await _goldenValidationService.ValidateAsync(
                manifestPath,
                EnginePath,
                Depth,
                statusProgress,
                _cancellationTokenSource.Token);

            ProgressValue = 100;

            if (summary.Total == 0)
            {
                StatusMessage = "No golden cases found in manifest.";
                StatusSeverity = InfoBarSeverity.Warning;
                StatusDetail = BuildProgressDetail(100, 0, null, "cases");
                return;
            }

            var failed = summary.Cases.Where(c => !c.Passed).ToList();
            if (failed.Count == 0)
            {
                StatusMessage = $"Golden validation passed ({summary.Passed}/{summary.Total}).";
                StatusSeverity = InfoBarSeverity.Success;
                StatusDetail = BuildProgressDetail(100, summary.Total, summary.Total, "cases");
                return;
            }

            var firstFailure = failed[0];
            StatusMessage = $"Golden validation failed ({summary.Passed}/{summary.Total}). First: {firstFailure.Name} -> {firstFailure.Message}";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(100, summary.Total, summary.Total, "cases");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Golden validation cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error validating goldens: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
        }
    }

    private bool CanRun() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(InputFilePath) &&
        !string.IsNullOrWhiteSpace(EnginePath) &&
        !string.IsNullOrWhiteSpace(OutputFilePath) &&
        File.Exists(InputFilePath) &&
        File.Exists(EnginePath) &&
        Depth > 0;

    private bool CanValidateGoldens() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(EnginePath) &&
        File.Exists(EnginePath) &&
        Depth > 0;

    private bool CanDownloadLatestEngine() => !IsRunning;

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }

    partial void OnInputFilePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnEnginePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
        ValidateGoldensCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputFilePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
        ValidateGoldensCommand.NotifyCanExecuteChanged();
    }

    partial void OnDepthChanged(int value)
    {
        const int minDepth = 1;
        const int maxDepth = 50;

        if (value < minDepth)
        {
            Depth = minDepth;
            return;
        }

        if (value > maxDepth)
        {
            Depth = maxDepth;
            return;
        }

        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        ValidateGoldensCommand.NotifyCanExecuteChanged();
        DownloadLatestEngineCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SaveState();
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _executionLock.Dispose();
    }

    private void LoadState()
    {
        InputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", string.Empty);
        if (!string.IsNullOrWhiteSpace(InputFilePath) && File.Exists(InputFilePath))
        {
            InputFileName = Path.GetFileName(InputFilePath);
        }
        else
        {
            InputFilePath = string.Empty;
            InputFileName = string.Empty;
        }

        EnginePath = _settings.GetValue($"{SettingsPrefix}.{nameof(EnginePath)}", string.Empty);
        if (!string.IsNullOrWhiteSpace(EnginePath) && !File.Exists(EnginePath))
        {
            EnginePath = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(EnginePath) || IsTemporaryPath(EnginePath))
        {
            var preferredEnginePath = ResolvePreferredEnginePath();
            if (!string.IsNullOrWhiteSpace(preferredEnginePath))
            {
                EnginePath = preferredEnginePath;
            }
        }

        EngineName = !string.IsNullOrWhiteSpace(EnginePath) && File.Exists(EnginePath)
            ? Path.GetFileName(EnginePath)
            : string.Empty;

        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", string.Empty);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);

        Depth = _settings.GetValue($"{SettingsPrefix}.{nameof(Depth)}", Depth);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EnginePath)}", EnginePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(Depth)}", Depth);
    }

    private static string ResolvePreferredEnginePath()
    {
        var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        var assetsCandidate = FindStockfishExeUnder(assetsRoot);
        if (!string.IsNullOrWhiteSpace(assetsCandidate))
        {
            return assetsCandidate;
        }

        var localAppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools",
            "Stockfish");

        return FindStockfishExeUnder(localAppDataRoot) ?? string.Empty;
    }

    private static string? FindStockfishExeUnder(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(rootPath, "*.exe", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("stockfish", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Contains($"{Path.DirectorySeparatorChar}Assets{Path.DirectorySeparatorChar}stockfish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(path => path.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTemporaryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
    }
}
