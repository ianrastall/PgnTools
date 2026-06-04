using System.IO;
using PgnTools.Helpers;
using PgnTools.Services;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Chess.com events downloader tool.
/// </summary>
public partial class ChesscomEventsDownloaderViewModel(
    IChesscomEventsDownloaderService service,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private const int DefaultLatestEventId = 24000;
    private const int DefaultRequestDelayMs = 0;
    private const string SettingsPrefix = nameof(ChesscomEventsDownloaderViewModel);

    private readonly IChesscomEventsDownloaderService _service = service;
    private readonly IWindowService _windowService = windowService;
    private readonly IAppSettingsService _settings = settings;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;
    private bool _operationInFlight;
    private bool _executionLockDisposed;
    private bool _outputPathSuggested = true;
    private bool _titledTuesdayOutputPathSuggested = true;
    private bool _statusPathSuggested = true;
    private string _lastOutputFolder = string.Empty;

    [ObservableProperty]
    private double _startEventId = 1;

    [ObservableProperty]
    private double _endEventId = DefaultLatestEventId;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private string _titledTuesdayOutputFilePath = string.Empty;

    [ObservableProperty]
    private string _titledTuesdayOutputFileName = string.Empty;

    [ObservableProperty]
    private string _statusFilePath = string.Empty;

    [ObservableProperty]
    private string _statusFileName = string.Empty;

    [ObservableProperty]
    private bool _resumeFromStatus = true;

    [ObservableProperty]
    private double _requestDelayMs = DefaultRequestDelayMs;

    [ObservableProperty]
    private bool _inferEveryFifthDeadIds = true;

    [ObservableProperty]
    private string _cookieHeader = string.Empty;

    [ObservableProperty]
    private bool _hasCapturedSession;

    [ObservableProperty]
    private string _sessionStatusMessage = "Sign in to Chess.com below before starting the event download";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select an event ID range and output files";

    public void Initialize()
    {
        Title = "Chess.com Events Downloader";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        if (_disposed)
        {
            return;
        }

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

            if (_statusPathSuggested || string.IsNullOrWhiteSpace(StatusFilePath))
            {
                ApplySuggestedStatusPath();
            }

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
    private async Task SelectStatusFileAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                GetSuggestedStatusFileName(),
                new Dictionary<string, IList<string>>
                {
                    { "CSV Files", [".csv"] }
                },
                $"{SettingsPrefix}.Picker.Status");

            if (file == null)
            {
                return;
            }

            StatusFilePath = file.Path;
            StatusFileName = file.Name;
            _statusPathSuggested = false;
            StatusMessage = $"Selected status CSV: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting status CSV: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task SelectTitledTuesdayOutputFileAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                GetSuggestedTitledTuesdayOutputFileName(),
                new Dictionary<string, IList<string>>
                {
                    { "PGN Files", [".pgn"] }
                },
                $"{SettingsPrefix}.Picker.TitledTuesdayOutput");

            if (file == null)
            {
                return;
            }

            TitledTuesdayOutputFilePath = file.Path;
            TitledTuesdayOutputFileName = file.Name;
            _titledTuesdayOutputPathSuggested = false;
            StatusMessage = $"Selected Titled Tuesday output: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting Titled Tuesday output file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!TryBuildEventRange(out var start, out var end))
        {
            StatusMessage = "Enter a valid event ID range.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            ApplySuggestedOutputPath();
        }

        if (string.IsNullOrWhiteSpace(TitledTuesdayOutputFilePath))
        {
            ApplySuggestedTitledTuesdayOutputPath();
        }

        if (string.IsNullOrWhiteSpace(StatusFilePath))
        {
            ApplySuggestedStatusPath();
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            await SelectOutputFileAsync();
        }

        if (string.IsNullOrWhiteSpace(TitledTuesdayOutputFilePath))
        {
            await SelectTitledTuesdayOutputFileAsync();
        }

        if (string.IsNullOrWhiteSpace(StatusFilePath))
        {
            await SelectStatusFileAsync();
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath) ||
            string.IsNullOrWhiteSpace(TitledTuesdayOutputFilePath) ||
            string.IsNullOrWhiteSpace(StatusFilePath))
        {
            StatusMessage = "Main output PGN, Titled Tuesday PGN, and status CSV are required.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (!TryGetRequestDelay(out var requestDelayMs))
        {
            StatusMessage = "Request delay must be between 0 and 60,000 ms.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (string.IsNullOrWhiteSpace(CookieHeader))
        {
            StatusMessage = "Chess.com session not captured. Sign in below, then start the download again.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var outputFullPath = Path.GetFullPath(OutputFilePath);
        var titledTuesdayOutputFullPath = Path.GetFullPath(TitledTuesdayOutputFilePath);
        var statusFullPath = Path.GetFullPath(StatusFilePath);

        if (string.Equals(outputFullPath, statusFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(titledTuesdayOutputFullPath, statusFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(titledTuesdayOutputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Main output PGN, Titled Tuesday PGN, and status CSV must be different files.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!await ValidateWritableFolderAsync(outputFullPath, "output file") ||
            !await ValidateWritableFolderAsync(titledTuesdayOutputFullPath, "Titled Tuesday output file") ||
            !await ValidateWritableFolderAsync(statusFullPath, "status CSV"))
        {
            return;
        }

        if (!await _executionLock.WaitAsync(0))
        {
            StatusMessage = "An event download is already in progress.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        _operationInFlight = true;
        CancellationTokenSource? cts = null;

        try
        {
            IsRunning = true;
            ProgressValue = 0;
            StatusMessage = "Starting Chess.com events download...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0, 0, end - start + 1, "events");

            cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;
            var progress = new Progress<ChesscomEventsDownloadProgress>(UpdateProgress);

            var result = await _service.DownloadEventsAsync(
                new ChesscomEventsDownloadOptions(
                    start,
                    end,
                    outputFullPath,
                    titledTuesdayOutputFullPath,
                    statusFullPath,
                    string.IsNullOrWhiteSpace(CookieHeader) ? null : CookieHeader.Trim(),
                    ResumeFromStatus,
                    requestDelayMs,
                    requestDelayMs,
                    InferEveryFifthDeadIds: InferEveryFifthDeadIds),
                progress,
                cts.Token);

            ProgressValue = 100;
            StatusMessage = result.Saved == 0
                ? "Events download complete. No PGNs were saved."
                : $"Events download complete. Saved {result.Saved:N0} event PGN(s).";
            StatusSeverity = result.Failed > 0 || result.Saved == 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            StatusDetail = BuildCompletionDetail(result);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Events download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
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
            CompleteOperation(cts);
        }
    }

    private bool CanRun() =>
        !_disposed &&
        !IsRunning &&
        TryBuildEventRange(out _, out _) &&
        TryGetRequestDelay(out _) &&
        !string.IsNullOrWhiteSpace(OutputFilePath) &&
        !string.IsNullOrWhiteSpace(TitledTuesdayOutputFilePath) &&
        !string.IsNullOrWhiteSpace(StatusFilePath);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }

    public void ApplyCapturedCookieHeader(string cookieHeader, int cookieCount)
    {
        CookieHeader = cookieHeader;
        HasCapturedSession = !string.IsNullOrWhiteSpace(cookieHeader);
        SessionStatusMessage = HasCapturedSession
            ? $"Chess.com session captured ({cookieCount:N0} cookie(s))."
            : "No Chess.com cookies were found. Sign in below, then try again.";
    }

    public void ClearCapturedSession(string message)
    {
        CookieHeader = string.Empty;
        HasCapturedSession = false;
        SessionStatusMessage = message;
    }

    partial void OnStartEventIdChanged(double value)
    {
        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_statusPathSuggested)
        {
            ApplySuggestedStatusPath();
        }

        if (_titledTuesdayOutputPathSuggested)
        {
            ApplySuggestedTitledTuesdayOutputPath();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnEndEventIdChanged(double value)
    {
        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_statusPathSuggested)
        {
            ApplySuggestedStatusPath();
        }

        if (_titledTuesdayOutputPathSuggested)
        {
            ApplySuggestedTitledTuesdayOutputPath();
        }

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

        if (_statusPathSuggested)
        {
            ApplySuggestedStatusPath();
        }

        if (_titledTuesdayOutputPathSuggested)
        {
            ApplySuggestedTitledTuesdayOutputPath();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnTitledTuesdayOutputFilePathChanged(string value)
    {
        TitledTuesdayOutputFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);

        var directory = Path.GetDirectoryName(value);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _lastOutputFolder = directory;
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusFilePathChanged(string value)
    {
        StatusFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnRequestDelayMsChanged(double value)
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

        if (!_operationInFlight)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            DisposeExecutionLock();
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    private bool TryBuildEventRange(out int start, out int end)
    {
        start = 0;
        end = 0;

        if (!TryGetEventId(StartEventId, out start) || !TryGetEventId(EndEventId, out end))
        {
            return false;
        }

        return end >= start;
    }

    private static bool TryGetEventId(double value, out int eventId)
    {
        eventId = 0;

        if (!double.IsFinite(value))
        {
            return false;
        }

        var rounded = Math.Round(value);
        if (rounded < 1 || rounded > int.MaxValue)
        {
            return false;
        }

        eventId = (int)rounded;
        return true;
    }

    private bool TryGetRequestDelay(out int requestDelayMs)
    {
        requestDelayMs = 0;

        if (!double.IsFinite(RequestDelayMs))
        {
            return false;
        }

        var rounded = Math.Round(RequestDelayMs);
        if (rounded is < 0 or > 60000)
        {
            return false;
        }

        requestDelayMs = (int)rounded;
        return true;
    }

    private void UpdateProgress(ChesscomEventsDownloadProgress progress)
    {
        var percent = progress.Total > 0
            ? progress.Processed / (double)progress.Total * 100
            : 0;

        ProgressValue = percent;
        StatusMessage = progress.Message;
        StatusSeverity = progress.Failed > 0
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Informational;
        StatusDetail = BuildProgressDetailWithCounts(
            percent,
            progress.Processed,
            progress.Total,
            progress.Saved,
            progress.Missing,
            progress.NonPgn,
            progress.Failed,
            progress.Skipped,
            progress.BytesWritten,
            progress.TitledTuesdaySaved,
            progress.TitledTuesdayBytesWritten);
    }

    private string BuildCompletionDetail(ChesscomEventsDownloadResult result) =>
        BuildProgressDetailWithCounts(
            100,
            result.Processed,
            result.Total,
            result.Saved,
            result.Missing,
            result.NonPgn,
            result.Failed,
            result.Skipped,
            result.BytesWritten,
            result.TitledTuesdaySaved,
            result.TitledTuesdayBytesWritten);

    private string BuildProgressDetailWithCounts(
        double percent,
        int processed,
        int total,
        int saved,
        int missing,
        int nonPgn,
        int failed,
        int skipped,
        long bytesWritten,
        int titledTuesdaySaved,
        long titledTuesdayBytesWritten)
    {
        var parts = new List<string>();
        var progressDetail = BuildProgressDetail(percent, processed, total, "events");
        if (!string.IsNullOrWhiteSpace(progressDetail))
        {
            parts.Add(progressDetail);
        }

        parts.Add($"Saved {saved:N0}");
        parts.Add($"Titled Tuesday {titledTuesdaySaved:N0}");
        parts.Add($"Missing {missing:N0}");

        if (nonPgn > 0)
        {
            parts.Add($"Non-PGN {nonPgn:N0}");
        }

        if (skipped > 0)
        {
            parts.Add($"Skipped {skipped:N0}");
        }

        if (failed > 0)
        {
            parts.Add($"Failed {failed:N0}");
        }

        if (bytesWritten > 0)
        {
            parts.Add($"Written {FormatBytes(bytesWritten)}");
        }

        if (titledTuesdayBytesWritten > 0)
        {
            parts.Add($"Titled Tuesday {FormatBytes(titledTuesdayBytesWritten)}");
        }

        return string.Join(" • ", parts);
    }

    private void CompleteOperation(CancellationTokenSource? cts)
    {
        IsRunning = false;

        if (ReferenceEquals(_cancellationTokenSource, cts))
        {
            _cancellationTokenSource = null;
        }

        cts?.Dispose();
        _executionLock.Release();
        _operationInFlight = false;

        if (_disposed)
        {
            DisposeExecutionLock();
        }

        StopProgressTimer();
    }

    private void DisposeExecutionLock()
    {
        if (_executionLockDisposed)
        {
            return;
        }

        _executionLockDisposed = true;
        _executionLock.Dispose();
    }

    private void LoadState()
    {
        StartEventId = _settings.GetValue($"{SettingsPrefix}.{nameof(StartEventId)}", StartEventId);
        EndEventId = _settings.GetValue($"{SettingsPrefix}.{nameof(EndEventId)}", EndEventId);
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        TitledTuesdayOutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(TitledTuesdayOutputFilePath)}", TitledTuesdayOutputFilePath);
        StatusFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(StatusFilePath)}", StatusFilePath);
        ResumeFromStatus = _settings.GetValue($"{SettingsPrefix}.{nameof(ResumeFromStatus)}", ResumeFromStatus);
        RequestDelayMs = _settings.GetValue($"{SettingsPrefix}.{nameof(RequestDelayMs)}", RequestDelayMs);
        InferEveryFifthDeadIds = _settings.GetValue($"{SettingsPrefix}.{nameof(InferEveryFifthDeadIds)}", InferEveryFifthDeadIds);
        _lastOutputFolder = _settings.GetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);

        if (!TryGetRequestDelay(out _))
        {
            RequestDelayMs = DefaultRequestDelayMs;
        }

        _outputPathSuggested = string.IsNullOrWhiteSpace(OutputFilePath) ||
                               IsSuggestedEventsFileName(Path.GetFileName(OutputFilePath), ".pgn");
        _titledTuesdayOutputPathSuggested = string.IsNullOrWhiteSpace(TitledTuesdayOutputFilePath) ||
                                            IsSuggestedTitledTuesdayFileName(Path.GetFileName(TitledTuesdayOutputFilePath));
        _statusPathSuggested = string.IsNullOrWhiteSpace(StatusFilePath) ||
                               IsSuggestedEventsFileName(Path.GetFileName(StatusFilePath), ".csv");

        if (_outputPathSuggested)
        {
            ApplySuggestedOutputPath();
        }

        if (_titledTuesdayOutputPathSuggested)
        {
            ApplySuggestedTitledTuesdayOutputPath();
        }

        if (_statusPathSuggested)
        {
            ApplySuggestedStatusPath();
        }

        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
        TitledTuesdayOutputFileName = string.IsNullOrWhiteSpace(TitledTuesdayOutputFilePath) ? string.Empty : Path.GetFileName(TitledTuesdayOutputFilePath);
        StatusFileName = string.IsNullOrWhiteSpace(StatusFilePath) ? string.Empty : Path.GetFileName(StatusFilePath);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(StartEventId)}", StartEventId);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EndEventId)}", EndEventId);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(TitledTuesdayOutputFilePath)}", TitledTuesdayOutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(StatusFilePath)}", StatusFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ResumeFromStatus)}", ResumeFromStatus);
        _settings.SetValue($"{SettingsPrefix}.{nameof(RequestDelayMs)}", RequestDelayMs);
        _settings.SetValue($"{SettingsPrefix}.{nameof(InferEveryFifthDeadIds)}", InferEveryFifthDeadIds);
        _settings.SetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);
    }

    private void ApplySuggestedOutputPath()
    {
        OutputFilePath = Path.Combine(GetPreferredOutputFolder(), GetSuggestedOutputFileName());
        _outputPathSuggested = true;
    }

    private void ApplySuggestedTitledTuesdayOutputPath()
    {
        TitledTuesdayOutputFilePath = Path.Combine(GetPreferredOutputFolder(), GetSuggestedTitledTuesdayOutputFileName());
        _titledTuesdayOutputPathSuggested = true;
    }

    private void ApplySuggestedStatusPath()
    {
        StatusFilePath = Path.Combine(GetPreferredOutputFolder(), GetSuggestedStatusFileName());
        _statusPathSuggested = true;
    }

    private string GetSuggestedOutputFileName()
    {
        if (!TryBuildEventRange(out var start, out var end))
        {
            return "chesscom-events.pgn";
        }

        return $"chesscom-events-{start:D5}-{end:D5}.pgn";
    }

    private string GetSuggestedTitledTuesdayOutputFileName()
    {
        if (!TryBuildEventRange(out var start, out var end))
        {
            return "chesscom-events-titled-tuesday.pgn";
        }

        return $"chesscom-events-titled-tuesday-{start:D5}-{end:D5}.pgn";
    }

    private string GetSuggestedStatusFileName()
    {
        if (!TryBuildEventRange(out var start, out var end))
        {
            return "chesscom-events-status.csv";
        }

        return $"chesscom-events-{start:D5}-{end:D5}.csv";
    }

    private string GetPreferredOutputFolder()
    {
        var outputDirectory = Path.GetDirectoryName(OutputFilePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            return outputDirectory;
        }

        var titledTuesdayOutputDirectory = Path.GetDirectoryName(TitledTuesdayOutputFilePath);
        if (!string.IsNullOrWhiteSpace(titledTuesdayOutputDirectory))
        {
            return titledTuesdayOutputDirectory;
        }

        var statusDirectory = Path.GetDirectoryName(StatusFilePath);
        if (!string.IsNullOrWhiteSpace(statusDirectory))
        {
            return statusDirectory;
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

    private static bool IsSuggestedEventsFileName(string? fileName, string extension)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.StartsWith("chesscom-events-", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuggestedTitledTuesdayFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.StartsWith("chesscom-events-titled-tuesday-", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
