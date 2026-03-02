using System.IO;
using System.Text.RegularExpressions;
using PgnTools.Helpers;
using PgnTools.Services;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Chess.com monthly crawl tool.
/// </summary>
public partial class ChesscomMonthlyDownloaderViewModel(
    IChesscomMonthlyDownloaderService service,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IChesscomMonthlyDownloaderService _service = service;
    private readonly IWindowService _windowService = windowService;
    private readonly IAppSettingsService _settings = settings;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private bool _outputPathSuggested;
    private bool _processedPathSuggested;
    private bool _logPathSuggested;
    private string _lastOutputFolder = string.Empty;
    private const string SettingsPrefix = nameof(ChesscomMonthlyDownloaderViewModel);
    private static readonly Regex SuggestedOutputRegex =
        new(@"^chesscom-\d+-\d{4}-\d{2}\.pgn$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SuggestedProcessedRegex =
        new(@"^chesscom-processed-\d+-\d{4}-\d{2}\.txt$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SuggestedLogRegex =
        new(@"^chesscom-crawl-\d+-\d{4}-\d{2}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [ObservableProperty]
    private int _targetYear;

    [ObservableProperty]
    private int _targetMonth;

    [ObservableProperty]
    private int _minElo = 2500;

    [ObservableProperty]
    private bool _excludeBullet;

    [ObservableProperty]
    private string _seedFilePath = string.Empty;

    [ObservableProperty]
    private string _seedFileName = string.Empty;

    [ObservableProperty]
    private string _processedFilePath = string.Empty;

    [ObservableProperty]
    private string _processedFileName = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private bool _enableLogging = true;

    [ObservableProperty]
    private string _logFilePath = string.Empty;

    [ObservableProperty]
    private string _logFileName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 100;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _statusMessage = "Select a seed list and output file";

    public void Initialize()
    {
        Title = "Chess.com Monthly Crawl";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }

    [RelayCommand]
    private async Task SelectSeedFileAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSingleFileAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Seed",
                ".txt");
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

            SeedFilePath = file.Path;
            SeedFileName = file.Name;
            StatusMessage = $"Selected seed list: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;

            if (_processedPathSuggested || string.IsNullOrWhiteSpace(ProcessedFilePath))
            {
                ApplySuggestedProcessedPath();
            }

            if (_outputPathSuggested || string.IsNullOrWhiteSpace(OutputFilePath))
            {
                ApplySuggestedOutputPath();
            }

            if (EnableLogging && (_logPathSuggested || string.IsNullOrWhiteSpace(LogFilePath)))
            {
                ApplySuggestedLogPath();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting seed list: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task SelectProcessedFileAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                GetSuggestedProcessedFileName(),
                new Dictionary<string, IList<string>>
                {
                    { "Text Files", [".txt"] }
                },
                $"{SettingsPrefix}.Picker.Processed");

            if (file == null)
            {
                return;
            }

            ProcessedFilePath = file.Path;
            ProcessedFileName = file.Name;
            _processedPathSuggested = false;
            StatusMessage = $"Selected processed list: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting processed list: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                GetSuggestedOutputFileName(),
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
            _outputPathSuggested = false;
            StatusMessage = $"Selected output: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting output file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task SelectLogFileAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                GetSuggestedLogFileName(),
                new Dictionary<string, IList<string>>
                {
                    { "Log Files", [".log"] }
                },
                $"{SettingsPrefix}.Picker.Log");

            if (file == null)
            {
                return;
            }

            LogFilePath = file.Path;
            LogFileName = file.Name;
            _logPathSuggested = false;
            StatusMessage = $"Selected log file: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting log file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private void ClearLogFile()
    {
        LogFilePath = string.Empty;
        LogFileName = string.Empty;
        _logPathSuggested = false;
        StatusMessage = "Log file cleared.";
        StatusSeverity = InfoBarSeverity.Informational;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            StatusMessage = "Target month is invalid.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (string.IsNullOrWhiteSpace(SeedFilePath) || !File.Exists(SeedFilePath))
        {
            StatusMessage = "Seed list file not found.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (string.IsNullOrWhiteSpace(ProcessedFilePath))
        {
            ApplySuggestedProcessedPath();
            if (string.IsNullOrWhiteSpace(ProcessedFilePath))
            {
                await SelectProcessedFileAsync();
            }
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            ApplySuggestedOutputPath();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                await SelectOutputFileAsync();
            }
        }

        if (string.IsNullOrWhiteSpace(ProcessedFilePath))
        {
            StatusMessage = "Processed list file is required.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            StatusMessage = "Output file is required.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (EnableLogging)
        {
            if (string.IsNullOrWhiteSpace(LogFilePath))
            {
                ApplySuggestedLogPath();
            }

            if (string.IsNullOrWhiteSpace(LogFilePath))
            {
                StatusMessage = "Log file path is required when logging is enabled.";
                StatusSeverity = InfoBarSeverity.Warning;
                return;
            }
        }

        var seedFullPath = Path.GetFullPath(SeedFilePath);
        var processedFullPath = Path.GetFullPath(ProcessedFilePath);
        var outputFullPath = Path.GetFullPath(OutputFilePath);
        var logFullPath = EnableLogging && !string.IsNullOrWhiteSpace(LogFilePath)
            ? Path.GetFullPath(LogFilePath)
            : null;

        if (string.Equals(seedFullPath, processedFullPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Seed and processed lists must be different files.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (string.Equals(seedFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processedFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Output file must be different from list files.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (logFullPath != null &&
            (string.Equals(logFullPath, seedFullPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(logFullPath, processedFullPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(logFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Log file must be different from seed, processed, and output files.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        try
        {
            await using var seedRead = File.OpenRead(seedFullPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cannot read seed list: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!await ValidateWritableFolderAsync(seedFullPath, "seed list"))
        {
            return;
        }

        if (!await ValidateWritableFolderAsync(processedFullPath, "processed list"))
        {
            return;
        }

        if (!await ValidateWritableFolderAsync(outputFullPath, "output file"))
        {
            return;
        }

        if (logFullPath != null && !await ValidateWritableFolderAsync(logFullPath, "log file"))
        {
            return;
        }

        if (!await _executionLock.WaitAsync(0))
        {
            StatusMessage = "A crawl is already in progress.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        try
        {
            IsRunning = true;
            ProgressValue = 0;
            ProgressMaximum = 100;
            IsIndeterminate = true;
            StatusMessage = "Starting crawl...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<ChesscomMonthlyCrawlProgress>(UpdateProgress);
            var result = await _service.CrawlMonthAsync(
                new ChesscomMonthlyCrawlOptions(
                    targetMonth,
                    MinElo,
                    seedFullPath,
                    processedFullPath,
                    outputFullPath,
                    LogFilePath: logFullPath,
                    ExcludeBullet: ExcludeBullet),
                progress,
                _cancellationTokenSource.Token);

            var message = result.GamesSaved == 0
                ? "Crawl complete. No games matched filters."
                : $"Crawl complete. Saved {result.GamesSaved:N0} game(s).";

            if (result.PlayersProcessed == 0)
            {
                message = "Crawl complete. No players were processed.";
            }

            StatusMessage = message;
            StatusSeverity = result.FailedPlayers > 0 || result.GamesSaved == 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;

            ProgressValue = 100;
            IsIndeterminate = false;
            StatusDetail = BuildCompletionDetail(result);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Crawl cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            ProgressValue = 0;
            IsIndeterminate = true;
            StatusDetail = BuildProgressDetail();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail();
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
        !string.IsNullOrWhiteSpace(SeedFilePath) &&
        File.Exists(SeedFilePath) &&
        TryGetTargetMonth(out _);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }

    partial void OnSeedFilePathChanged(string value)
    {
        SeedFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);

        if (EnableLogging && _logPathSuggested)
        {
            ApplySuggestedLogPath();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnProcessedFilePathChanged(string value)
    {
        ProcessedFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputFilePathChanged(string value)
    {
        OutputFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);

        var directory = Path.GetDirectoryName(value);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _lastOutputFolder = directory;
        }

        if (EnableLogging && _logPathSuggested)
        {
            ApplySuggestedLogPath();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnLogFilePathChanged(string value)
    {
        LogFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnEnableLoggingChanged(bool value)
    {
        if (value && (_logPathSuggested || string.IsNullOrWhiteSpace(LogFilePath)))
        {
            ApplySuggestedLogPath();
        }
    }

    partial void OnTargetYearChanged(int value)
    {
        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_processedPathSuggested)
        {
            ApplySuggestedProcessedPath();
        }

        if (EnableLogging && _logPathSuggested)
        {
            ApplySuggestedLogPath();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetMonthChanged(int value)
    {
        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_processedPathSuggested)
        {
            ApplySuggestedProcessedPath();
        }

        if (EnableLogging && _logPathSuggested)
        {
            ApplySuggestedLogPath();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnMinEloChanged(int value)
    {
        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_processedPathSuggested)
        {
            ApplySuggestedProcessedPath();
        }

        if (EnableLogging && _logPathSuggested)
        {
            ApplySuggestedLogPath();
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
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
        var defaultMonth = GetDefaultMonth();
        TargetYear = _settings.GetValue($"{SettingsPrefix}.{nameof(TargetYear)}", defaultMonth.Year);
        TargetMonth = _settings.GetValue($"{SettingsPrefix}.{nameof(TargetMonth)}", defaultMonth.Month);
        MinElo = _settings.GetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        ExcludeBullet = _settings.GetValue($"{SettingsPrefix}.{nameof(ExcludeBullet)}", ExcludeBullet);
        SeedFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(SeedFilePath)}", SeedFilePath);
        ProcessedFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(ProcessedFilePath)}", ProcessedFilePath);
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        EnableLogging = _settings.GetValue($"{SettingsPrefix}.{nameof(EnableLogging)}", EnableLogging);
        LogFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(LogFilePath)}", LogFilePath);
        _lastOutputFolder = _settings.GetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);

        if (!TryGetTargetMonth(out _))
        {
            TargetYear = defaultMonth.Year;
            TargetMonth = defaultMonth.Month;
        }

        SeedFileName = string.IsNullOrWhiteSpace(SeedFilePath) ? string.Empty : Path.GetFileName(SeedFilePath);
        ProcessedFileName = string.IsNullOrWhiteSpace(ProcessedFilePath) ? string.Empty : Path.GetFileName(ProcessedFilePath);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
        LogFileName = string.IsNullOrWhiteSpace(LogFilePath) ? string.Empty : Path.GetFileName(LogFilePath);

        _outputPathSuggested = string.IsNullOrWhiteSpace(OutputFilePath) || IsSuggestedOutputFileName(OutputFileName);
        _processedPathSuggested = string.IsNullOrWhiteSpace(ProcessedFilePath) || IsSuggestedProcessedFileName(ProcessedFileName);
        _logPathSuggested = string.IsNullOrWhiteSpace(LogFilePath) || IsSuggestedLogFileName(LogFileName);

        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_processedPathSuggested)
        {
            ApplySuggestedProcessedPath();
        }

        if (EnableLogging && _logPathSuggested)
        {
            ApplySuggestedLogPath();
        }
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(TargetYear)}", TargetYear);
        _settings.SetValue($"{SettingsPrefix}.{nameof(TargetMonth)}", TargetMonth);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ExcludeBullet)}", ExcludeBullet);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SeedFilePath)}", SeedFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ProcessedFilePath)}", ProcessedFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EnableLogging)}", EnableLogging);
        _settings.SetValue($"{SettingsPrefix}.{nameof(LogFilePath)}", LogFilePath);
        _settings.SetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);
    }

    private static DateOnly GetDefaultMonth()
    {
        var today = DateTime.Now;
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return firstOfMonth.AddMonths(-1);
    }

    private bool TryGetTargetMonth(out DateOnly month)
    {
        if (TargetYear < 1900 || TargetYear > 2100)
        {
            month = default;
            return false;
        }

        if (TargetMonth is < 1 or > 12)
        {
            month = default;
            return false;
        }

        month = new DateOnly(TargetYear, TargetMonth, 1);
        return true;
    }

    private void ApplySuggestedOutputPath()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            return;
        }

        var fileName = $"chesscom-{MinElo}-{targetMonth:yyyy-MM}.pgn";
        var directory = GetPreferredOutputFolder();
        OutputFilePath = Path.Combine(directory, fileName);
        _outputPathSuggested = true;
    }

    private void ApplySuggestedProcessedPath()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            return;
        }

        var fileName = $"chesscom-processed-{MinElo}-{targetMonth:yyyy-MM}.txt";
        var directory = GetPreferredOutputFolder();
        ProcessedFilePath = Path.Combine(directory, fileName);
        _processedPathSuggested = true;
    }

    private void ApplySuggestedLogPath()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            return;
        }

        var fileName = $"chesscom-crawl-{MinElo}-{targetMonth:yyyy-MM}.log";
        var directory = GetPreferredOutputFolder();
        LogFilePath = Path.Combine(directory, fileName);
        _logPathSuggested = true;
    }

    private string GetPreferredOutputFolder()
    {
        var seedDirectory = Path.GetDirectoryName(SeedFilePath);
        if (!string.IsNullOrWhiteSpace(seedDirectory))
        {
            return seedDirectory;
        }

        var outputDirectory = Path.GetDirectoryName(OutputFilePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            return outputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(_lastOutputFolder))
        {
            return _lastOutputFolder;
        }

        var defaultFolder = _settings.GetValue(AppSettingsKeys.DefaultDownloadFolder, string.Empty);
        if (!string.IsNullOrWhiteSpace(defaultFolder))
        {
            return defaultFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string GetSuggestedOutputFileName()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            return "chesscom.pgn";
        }

        return $"chesscom-{MinElo}-{targetMonth:yyyy-MM}.pgn";
    }

    private string GetSuggestedProcessedFileName()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            return "chesscom-processed.txt";
        }

        return $"chesscom-processed-{MinElo}-{targetMonth:yyyy-MM}.txt";
    }

    private string GetSuggestedLogFileName()
    {
        if (!TryGetTargetMonth(out var targetMonth))
        {
            return "chesscom-crawl.log";
        }

        return $"chesscom-crawl-{MinElo}-{targetMonth:yyyy-MM}.log";
    }

    private static bool IsSuggestedOutputFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return SuggestedOutputRegex.IsMatch(fileName);
    }

    private static bool IsSuggestedProcessedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return SuggestedProcessedRegex.IsMatch(fileName);
    }

    private static bool IsSuggestedLogFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return SuggestedLogRegex.IsMatch(fileName);
    }

    private void UpdateProgress(ChesscomMonthlyCrawlProgress progress)
    {
        var percent = progress.PlayersTotal > 0
            ? progress.PlayersProcessed / (double)progress.PlayersTotal * 100
            : 0;

        ProgressValue = percent;
        ProgressMaximum = 100;
        IsIndeterminate = progress.PlayersTotal <= 0;
        StatusMessage = progress.Message;
        StatusSeverity = progress.FailedPlayers > 0
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Informational;

        var detailParts = new List<string>();
        var progressDetail = BuildProgressDetail(percent, progress.PlayersProcessed, progress.PlayersTotal, "players");
        if (!string.IsNullOrWhiteSpace(progressDetail))
        {
            detailParts.Add(progressDetail);
        }

        detailParts.Add($"Games {progress.GamesSaved:N0}");
        if (progress.NewPlayers > 0)
        {
            detailParts.Add($"+{progress.NewPlayers:N0} new players");
        }

        if (progress.SeedSize > 0)
        {
            detailParts.Add($"Seed {progress.SeedSize:N0}");
        }

        if (progress.PlayersTotal > 0)
        {
            var pending = Math.Max(0, progress.PlayersTotal - progress.PlayersProcessed);
            detailParts.Add($"Pending {pending:N0}");
        }

        if (progress.FailedPlayers > 0)
        {
            detailParts.Add($"{progress.FailedPlayers:N0} failed");
        }

        StatusDetail = string.Join(" • ", detailParts);
    }

    private string BuildCompletionDetail(ChesscomMonthlyCrawlResult result)
    {
        var detailParts = new List<string>();
        var progressDetail = BuildProgressDetail(100, result.PlayersProcessed, result.PlayersTotal, "players");
        if (!string.IsNullOrWhiteSpace(progressDetail))
        {
            detailParts.Add(progressDetail);
        }

        detailParts.Add($"Games {result.GamesSaved:N0}");

        if (result.NewPlayers > 0)
        {
            detailParts.Add($"+{result.NewPlayers:N0} new players");
        }

        if (result.SeedSize > 0)
        {
            detailParts.Add($"Seed {result.SeedSize:N0}");
        }

        if (result.FailedPlayers > 0)
        {
            detailParts.Add($"{result.FailedPlayers:N0} failed");
        }

        if (EnableLogging && !string.IsNullOrWhiteSpace(LogFileName))
        {
            detailParts.Add($"Log {LogFileName}");
        }

        return string.Join(" • ", detailParts);
    }

    private async Task<bool> ValidateWritableFolderAsync(string filePath, string label)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            StatusMessage = $"Invalid {label} path.";
            StatusSeverity = InfoBarSeverity.Error;
            return false;
        }

        var validation = await FileValidationHelper.ValidateWritableFolderAsync(directory);
        if (!validation.Success)
        {
            StatusMessage = $"Cannot write to {label} folder: {validation.ErrorMessage}";
            StatusSeverity = InfoBarSeverity.Error;
            return false;
        }

        return true;
    }
}
