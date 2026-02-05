// PGNTOOLS-TABLEBASES-BEGIN
using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Tablebase Downloader tool.
/// </summary>
public partial class TablebaseDownloaderViewModel(
    ITablebaseDownloaderService downloaderService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly ITablebaseDownloaderService _downloaderService = downloaderService;
    private readonly IWindowService _windowService = windowService;
    private readonly IAppSettingsService _settings = settings;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private bool _hadSkips;
    private TablebaseProgress? _lastProgress;
    private const string SettingsPrefix = nameof(TablebaseDownloaderViewModel);

    [ObservableProperty]
    private string _targetFolder = string.Empty;

    [ObservableProperty]
    private bool _download345 = true;

    [ObservableProperty]
    private bool _download6;

    [ObservableProperty]
    private bool _download7;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 100;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _statusMessage = "Select a destination folder and tablebase sets";

    public void Initialize()
    {
        Title = "Tablebases";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }

    [RelayCommand]
    private async Task SelectTargetFolderAsync()
    {
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Output");
            if (folder == null)
            {
                return;
            }

            TargetFolder = folder.Path;
            StatusMessage = $"Selected folder: {folder.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting folder: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetFolder))
        {
            StatusMessage = "Please select a destination folder.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (!Download345 && !Download6 && !Download7)
        {
            StatusMessage = "Please select at least one tablebase set.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var validation = await FileValidationHelper.ValidateWritableFolderAsync(TargetFolder);
        if (!validation.Success)
        {
            StatusMessage = $"Cannot write to folder: {validation.ErrorMessage}";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!await _executionLock.WaitAsync(0))
        {
            StatusMessage = "A download is already in progress.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        try
        {
            IsRunning = true;
            StatusMessage = "Preparing tablebase download...";
            StatusSeverity = InfoBarSeverity.Informational;
            ProgressValue = 0;
            ProgressMaximum = 100;
            IsIndeterminate = true;
            _lastProgress = null;
            _hadSkips = false;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();
            var progress = new Progress<TablebaseProgress>(UpdateProgress);
            var hadFailures = false;

            if (Download345)
            {
                StatusMessage = "Starting 3-4-5 tablebase download...";
                if (!await TryDownloadCategoryAsync(TablebaseCategory.Syzygy345, progress))
                {
                    hadFailures = true;
                }
            }

            if (Download6)
            {
                StatusMessage = "Starting 6-piece tablebase download...";
                if (!await TryDownloadCategoryAsync(TablebaseCategory.Syzygy6, progress))
                {
                    hadFailures = true;
                }
            }

            if (Download7)
            {
                StatusMessage = "Starting 7-piece tablebase download...";
                if (!await TryDownloadCategoryAsync(TablebaseCategory.Syzygy7, progress))
                {
                    hadFailures = true;
                }
            }

            if (hadFailures)
            {
                StatusMessage = "Tablebase downloads completed with errors.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else if (_hadSkips)
            {
                StatusMessage = "Tablebase downloads complete (some files skipped because they were in use).";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else
            {
                StatusMessage = "Tablebase downloads complete.";
                StatusSeverity = InfoBarSeverity.Success;
            }

            ProgressValue = 100;
            StatusDetail = BuildProgressDetail(100);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled.";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = _lastProgress != null
                ? BuildStatusDetail(_lastProgress, ProgressValue, GetDisplayIndex(_lastProgress))
                : BuildProgressDetail(ProgressValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = _lastProgress != null
                ? BuildStatusDetail(_lastProgress, ProgressValue, GetDisplayIndex(_lastProgress))
                : BuildProgressDetail(ProgressValue);
        }
        finally
        {
            IsRunning = false;
            var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
            cts?.Dispose();
            _executionLock.Release();
            StopProgressTimer();
        }
    }

    private bool CanStart() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(TargetFolder) &&
        (Download345 || Download6 || Download7);

    [RelayCommand]
    private void Cancel()
    {
        var cts = Volatile.Read(ref _cancellationTokenSource);
        if (cts == null || cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            cts.Cancel();
            StatusMessage = "Cancelling...";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    partial void OnTargetFolderChanged(string value)
    {
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnDownload345Changed(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnDownload6Changed(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnDownload7Changed(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
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
        var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void LoadState()
    {
        TargetFolder = _settings.GetValue($"{SettingsPrefix}.{nameof(TargetFolder)}", TargetFolder);
        Download345 = _settings.GetValue($"{SettingsPrefix}.{nameof(Download345)}", Download345);
        Download6 = _settings.GetValue($"{SettingsPrefix}.{nameof(Download6)}", Download6);
        Download7 = _settings.GetValue($"{SettingsPrefix}.{nameof(Download7)}", Download7);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(TargetFolder)}", TargetFolder);
        _settings.SetValue($"{SettingsPrefix}.{nameof(Download345)}", Download345);
        _settings.SetValue($"{SettingsPrefix}.{nameof(Download6)}", Download6);
        _settings.SetValue($"{SettingsPrefix}.{nameof(Download7)}", Download7);
    }

    private void UpdateProgress(TablebaseProgress progress)
    {
        _lastProgress = progress;
        if (progress.Stage == TablebaseProgressStage.SkippedLocked)
        {
            _hadSkips = true;
        }

        var totalFiles = progress.TotalFiles;
        var processedFiles = progress.FilesCompleted + progress.FilesSkipped;
        var hasTotal = progress.TotalBytes is > 0;
        var fileFraction = 0d;
        if (hasTotal)
        {
            fileFraction = progress.BytesRead >= progress.TotalBytes!.Value
                ? 1
                : Math.Clamp(progress.BytesRead / (double)progress.TotalBytes.Value, 0, 1);
        }

        var percent = totalFiles > 0
            ? ((processedFiles + fileFraction) / totalFiles) * 100
            : 0;

        if (totalFiles > 0)
        {
            ProgressValue = percent;
            ProgressMaximum = 100;
            IsIndeterminate = false;
        }
        else
        {
            ProgressValue = 0;
            ProgressMaximum = 100;
            IsIndeterminate = true;
        }

        var displayIndex = GetDisplayIndex(progress);
        var messagePrefix = progress.Stage switch
        {
            TablebaseProgressStage.AlreadyPresent => "Already present",
            TablebaseProgressStage.SkippedLocked => "Skipped (in use)",
            TablebaseProgressStage.Completed => "Downloaded",
            _ => "Downloading"
        };

        StatusMessage = totalFiles > 0
            ? $"{messagePrefix} {progress.CurrentFileName} ({displayIndex}/{totalFiles})"
            : $"{messagePrefix} {progress.CurrentFileName}";

        StatusDetail = BuildStatusDetail(progress, percent, displayIndex);
    }

    private static int GetDisplayIndex(TablebaseProgress progress)
    {
        var totalFiles = progress.TotalFiles;
        if (totalFiles <= 0)
        {
            return progress.FilesCompleted + progress.FilesSkipped;
        }

        if (progress.Stage is TablebaseProgressStage.Downloading or TablebaseProgressStage.Starting)
        {
            return Math.Min(progress.FilesCompleted + progress.FilesSkipped + 1, totalFiles);
        }

        return Math.Min(progress.FilesCompleted + progress.FilesSkipped, totalFiles);
    }

    private string BuildStatusDetail(TablebaseProgress progress, double? percent, int displayIndex)
    {
        var parts = new List<string>();

        var detail = BuildProgressDetail(percent, displayIndex, progress.TotalFiles, "files");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail);
        }

        if (progress.FilesSkipped > 0)
        {
            parts.Add($"{progress.FilesSkipped:N0} skipped");
        }

        if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
        {
            parts.Add($"{FormatBytes(progress.BytesRead)} / {FormatBytes(progress.TotalBytes.Value)}");
        }
        else if (progress.BytesRead > 0)
        {
            parts.Add($"{FormatBytes(progress.BytesRead)} downloaded");
        }

        if (progress.SpeedMbPerSecond > 0)
        {
            parts.Add($"{progress.SpeedMbPerSecond:0.0} MB/s");
        }

        return string.Join(" • ", parts);
    }

    private async Task<bool> TryDownloadCategoryAsync(
        TablebaseCategory category,
        IProgress<TablebaseProgress> progress)
    {
        try
        {
            await _downloaderService.DownloadCategoryAsync(
                category,
                TargetFolder,
                progress,
                _cancellationTokenSource?.Token ?? CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error downloading {TablebaseConstants.GetCategoryFolderName(category)} tablebases: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            return false;
        }
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
// PGNTOOLS-TABLEBASES-END
