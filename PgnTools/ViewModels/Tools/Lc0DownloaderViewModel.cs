using System.Globalization;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Lc0 Downloader tool.
/// </summary>
public partial class Lc0DownloaderViewModel : BaseViewModel, IDisposable
{
    private readonly ILc0DownloaderService _service;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(Lc0DownloaderViewModel);

    [ObservableProperty]
    private string _outputFolderPath = string.Empty;

    [ObservableProperty]
    private string _outputFolderName = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _startDate;

    [ObservableProperty]
    private DateTimeOffset? _endDate;

    [ObservableProperty]
    private string _maxPages = string.Empty;

    [ObservableProperty]
    private string _maxMatches = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select output folder and download options";

    public Lc0DownloaderViewModel(
        ILc0DownloaderService service,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _service = service;
        _windowService = windowService;
        _settings = settings;
        Title = "Lc0 Downloader";
        StatusSeverity = InfoBarSeverity.Informational;
        IsIndeterminate = true;
        LoadState();
    }

    [RelayCommand]
    private async Task SelectOutputFolderAsync()
    {
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.OutputFolder");
            if (folder == null)
            {
                return;
            }

            var validation = await FileValidationHelper.ValidateWritableFolderAsync(folder.Path);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot write to folder: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
            }

            OutputFolderPath = folder.Path;
            OutputFolderName = folder.Name;
            StatusMessage = $"Selected output folder: {folder.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting output folder: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputFolderPath))
        {
            return;
        }

        var validation = await FileValidationHelper.ValidateWritableFolderAsync(OutputFolderPath);
        if (!validation.Success)
        {
            StatusMessage = $"Cannot write to folder: {validation.ErrorMessage}";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        var startDate = StartDate?.Date;
        var endDate = EndDate?.Date;

        if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
        {
            StatusMessage = "Start date must be before end date.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        var maxPages = ParsePositiveInt(MaxPages);
        var maxMatches = ParsePositiveInt(MaxMatches);

        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusMessage = "Preparing Lc0 download...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<Lc0DownloadProgress>(update =>
            {
                StatusMessage = update.Message;

                if (update.Percent.HasValue)
                {
                    ProgressValue = update.Percent.Value;
                    IsIndeterminate = false;
                }
                else
                {
                    IsIndeterminate = true;
                }

                var unit = update.Phase == Lc0DownloadPhase.Scraping ? "pages" : "matches";
                StatusDetail = BuildProgressDetail(update.Percent, update.Current, update.Total, unit);
            });

            var result = await _service.DownloadAndProcessAsync(
                new Lc0DownloadOptions(
                    OutputFolderPath,
                    startDate,
                    endDate,
                    maxPages,
                    maxMatches),
                progress,
                _cancellationTokenSource.Token);

            ProgressValue = 100;
            IsIndeterminate = false;

            if (result.TotalMatches == 0)
            {
                StatusMessage = "No matches found for the selected range.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else if (result.FailedMatches > 0)
            {
                StatusMessage = $"Completed with errors. {result.ProcessedMatches:N0} processed, {result.FailedMatches:N0} failed.";
                if (result.SkippedMatches > 0)
                {
                    StatusMessage += $" {result.SkippedMatches:N0} already processed.";
                }

                StatusSeverity = InfoBarSeverity.Warning;
            }
            else
            {
                StatusMessage = $"Completed. {result.ProcessedMatches:N0} matches processed.";
                if (result.SkippedMatches > 0)
                {
                    StatusMessage += $" {result.SkippedMatches:N0} already processed.";
                }

                StatusSeverity = InfoBarSeverity.Success;
            }

            StatusDetail = BuildProgressDetail(100, result.ProcessedMatches, result.TotalMatches, "matches");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Lc0 download cancelled";
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
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
        }
    }

    private static int? ParsePositiveInt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }

    private bool CanRun() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(OutputFolderPath);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }

    partial void OnOutputFolderPathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
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
        if (_executionLock.CurrentCount > 0)
        {
            _executionLock.Dispose();
        }
    }

    private void LoadState()
    {
        OutputFolderPath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFolderPath)}", OutputFolderPath);
        OutputFolderName = string.IsNullOrWhiteSpace(OutputFolderPath) ? string.Empty : Path.GetFileName(OutputFolderPath.TrimEnd(Path.DirectorySeparatorChar));

        StartDate = _settings.GetValue($"{SettingsPrefix}.{nameof(StartDate)}", StartDate);
        EndDate = _settings.GetValue($"{SettingsPrefix}.{nameof(EndDate)}", EndDate);
        MaxPages = _settings.GetValue($"{SettingsPrefix}.{nameof(MaxPages)}", MaxPages);
        MaxMatches = _settings.GetValue($"{SettingsPrefix}.{nameof(MaxMatches)}", MaxMatches);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFolderPath)}", OutputFolderPath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(StartDate)}", StartDate);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EndDate)}", EndDate);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MaxPages)}", MaxPages);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MaxMatches)}", MaxMatches);
    }
}
