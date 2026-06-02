using System.IO;
using PgnTools.Services;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the TWIC Downloader tool.
/// </summary>
public partial class TwicDownloaderViewModel(
    ITwicDownloaderService twicDownloaderService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private const int BaseIssue = 920;
    private const string SettingsPrefix = nameof(TwicDownloaderViewModel);

    private readonly ITwicDownloaderService _twicDownloaderService = twicDownloaderService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;
    private bool _operationInFlight;
    private bool _executionLockDisposed;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private double _startIssue = BaseIssue;

    [ObservableProperty]
    private double _endIssue;

    [ObservableProperty]
    private int _estimatedLatestIssue;

    [ObservableProperty]
    private int _latestIssue;

    [ObservableProperty]
    private int _selectedModeIndex;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Select output file and download range";

    public bool IsCustomRange => SelectedModeIndex == 1;

    public void Initialize()
    {
        Title = "TWIC Downloader";
        StatusSeverity = InfoBarSeverity.Informational;
        EstimatedLatestIssue = TwicDownloaderService.CalculateEstimatedLatestIssue();
        LatestIssue = EstimatedLatestIssue;
        EndIssue = LatestIssue;
        SelectedModeIndex = 0;
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
            var suggestedName = "twic_collection.pgn";
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

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ProbeLatestAsync()
    {
        if (_disposed || !_executionLock.Wait(0))
        {
            return;
        }

        _operationInFlight = true;
        CancellationTokenSource? cts = null;

        try
        {
            IsRunning = true;
            EstimatedLatestIssue = TwicDownloaderService.CalculateEstimatedLatestIssue();
            StatusMessage = "Probing TWIC for the latest issue...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;
            var progress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail();
            });

            var result = await TwicDownloaderService.ProbeLatestIssueAsync(
                EstimatedLatestIssue,
                progress,
                cts.Token);

            LatestIssue = result.Issue;
            StatusMessage = result.IsConfirmed
                ? $"Latest confirmed issue: {result.Issue}"
                : $"Estimated latest issue: {result.Issue} (not confirmed)";
            StatusSeverity = result.IsConfirmed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Probe cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
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
            CompleteOperation(cts);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return;
        }

        if (!TryBuildIssueRange(out var start, out var end))
        {
            StatusMessage = "Enter a valid issue range.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (!_executionLock.Wait(0))
        {
            return;
        }

        _operationInFlight = true;
        CancellationTokenSource? cts = null;

        try
        {
            IsRunning = true;
            StatusMessage = "Starting TWIC download...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;
            var progress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail();
            });

            var issuesWritten = await _twicDownloaderService.DownloadIssuesAsync(
                start,
                end,
                OutputFilePath,
                progress,
                cts.Token);

            StatusSeverity = issuesWritten > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "TWIC download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
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
            CompleteOperation(cts);
        }
    }

    private bool CanRun()
    {
        if (_disposed || IsRunning || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return false;
        }

        return TryBuildIssueRange(out _, out _);
    }

    private bool CanProbe() => !_disposed && !IsRunning;

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail();
    }

    partial void OnOutputFilePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnStartIssueChanged(double value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnEndIssueChanged(double value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        ProbeLatestCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        if (!IsCustomRange)
        {
            EndIssue = LatestIssue;
        }

        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnLatestIssueChanged(int value)
    {
        if (!IsCustomRange)
        {
            EndIssue = value;
        }

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

        if (!_operationInFlight)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            DisposeExecutionLock();
        }

        RunCommand.NotifyCanExecuteChanged();
        ProbeLatestCommand.NotifyCanExecuteChanged();
    }

    private bool TryBuildIssueRange(out int start, out int end)
    {
        start = 0;
        end = 0;

        if (!IsCustomRange)
        {
            start = BaseIssue;
            end = LatestIssue;
            return end >= BaseIssue;
        }

        return TryGetIssueNumber(StartIssue, out start) &&
               TryGetIssueNumber(EndIssue, out end) &&
               end >= start;
    }

    private static bool TryGetIssueNumber(double value, out int issue)
    {
        issue = 0;

        if (!double.IsFinite(value))
        {
            return false;
        }

        var rounded = Math.Round(value);
        if (rounded < 1 || rounded > int.MaxValue)
        {
            return false;
        }

        issue = (int)rounded;
        return true;
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
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            var defaultFolder = _settings.GetValue(AppSettingsKeys.DefaultDownloadFolder, string.Empty);
            if (!string.IsNullOrWhiteSpace(defaultFolder))
            {
                OutputFilePath = Path.Combine(defaultFolder, "twic_collection.pgn");
            }
        }

        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);

        SelectedModeIndex = _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedModeIndex)}", SelectedModeIndex);
        StartIssue = _settings.GetValue($"{SettingsPrefix}.{nameof(StartIssue)}", StartIssue);
        EndIssue = _settings.GetValue($"{SettingsPrefix}.{nameof(EndIssue)}", EndIssue);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedModeIndex)}", SelectedModeIndex);
        _settings.SetValue($"{SettingsPrefix}.{nameof(StartIssue)}", StartIssue);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EndIssue)}", EndIssue);
    }
}
