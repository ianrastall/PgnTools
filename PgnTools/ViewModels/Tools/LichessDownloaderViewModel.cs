using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Lichess Downloader tool.
/// </summary>
public partial class LichessDownloaderViewModel(
    ILichessDownloaderService service,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly ILichessDownloaderService _service = service;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private LichessDownloadProgress? _lastProgress;
    private const string SettingsPrefix = nameof(LichessDownloaderViewModel);

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _maxGames = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 100;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _statusMessage = "Enter a Lichess username";
    public void Initialize()
    {
        Title = "Lichess Downloader";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }
    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = string.IsNullOrWhiteSpace(Username)
                ? "lichess_games.pgn"
                : $"{Username}_lichess.pgn";

            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                suggestedName,
                new Dictionary<string, IList<string>>
                {
                    { "PGN Files", [".pgn"] }
                },
                $"{SettingsPrefix}.Picker.Output");

            if (file != null)
            {
                OutputFilePath = file.Path;
                OutputFileName = file.Name;
                StatusMessage = $"Selected output: {file.Name}";
                StatusSeverity = InfoBarSeverity.Informational;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting output file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            await SelectOutputFileAsync();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                return;
            }
        }

        var outputDirectory = Path.GetDirectoryName(OutputFilePath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            var validation = await FileValidationHelper.ValidateWritableFolderAsync(outputDirectory);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot write to folder: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
            }
        }

        int? max = null;
        if (int.TryParse(MaxGames, out var parsed) && parsed > 0)
        {
            max = parsed;
        }
        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }
        try
        {
            IsRunning = true;
            StatusMessage = "Downloading games...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();
            ProgressValue = 0;
            ProgressMaximum = 100;
            IsIndeterminate = true;
            _lastProgress = null;

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<LichessDownloadProgress>(UpdateProgress);
            await _service.DownloadUserGamesAsync(
                Username,
                OutputFilePath,
                max,
                progress,
                _cancellationTokenSource.Token);

            StatusMessage = "Download complete.";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = _lastProgress != null
                ? BuildLichessProgressDetail(_lastProgress)
                : BuildProgressDetail();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = _lastProgress != null
                ? BuildLichessProgressDetail(_lastProgress)
                : BuildProgressDetail();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = _lastProgress != null
                ? BuildLichessProgressDetail(_lastProgress)
                : BuildProgressDetail();
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
        !IsRunning && !string.IsNullOrWhiteSpace(Username);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail();
    }
    partial void OnUsernameChanged(string value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsRunningChanged(bool value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
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
        Username = _settings.GetValue($"{SettingsPrefix}.{nameof(Username)}", Username);
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
        MaxGames = _settings.GetValue($"{SettingsPrefix}.{nameof(MaxGames)}", MaxGames);
    }
    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(Username)}", Username);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MaxGames)}", MaxGames);
    }

    private void UpdateProgress(LichessDownloadProgress progress)
    {
        _lastProgress = progress;

        if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
        {
            IsIndeterminate = false;
            ProgressMaximum = progress.TotalBytes.Value;
            ProgressValue = progress.BytesRead;
        }
        else
        {
            IsIndeterminate = true;
            ProgressMaximum = 100;
            ProgressValue = 0;
        }

        StatusDetail = BuildLichessProgressDetail(progress);
    }

    private string BuildLichessProgressDetail(LichessDownloadProgress progress)
    {
        var parts = new List<string>();
        double? percent = null;

        if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
        {
            percent = Math.Clamp(progress.BytesRead / (double)progress.TotalBytes.Value * 100.0, 0, 100);
            parts.Add($"{FormatBytes(progress.BytesRead)} / {FormatBytes(progress.TotalBytes.Value)}");
        }
        else if (progress.BytesRead > 0)
        {
            parts.Add($"{FormatBytes(progress.BytesRead)} downloaded");
        }

        var detail = BuildProgressDetail(percent);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail);
        }

        return string.Join(" • ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= scale && unitIndex < units.Length - 1)
        {
            value /= scale;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}






