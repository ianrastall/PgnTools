using System.IO;

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
    private readonly ITwicDownloaderService _twicDownloaderService = twicDownloaderService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(TwicDownloaderViewModel);

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
    public bool IsCustomRange => SelectedModeIndex == 1;

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
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
        if (!await _executionLock.WaitAsync(0))
        {
            return;
    }
        try
        {
            IsRunning = true;
            StatusMessage = "Probing TWIC for the latest issue...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            _cancellationTokenSource = new CancellationTokenSource();
            var progress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail();
            });

            var latest = await TwicDownloaderService.ProbeLatestIssueAsync(
                LatestIssue,
                progress,
                _cancellationTokenSource.Token);

            LatestIssue = latest;
            StatusMessage = $"Latest issue detected: {latest}";
            StatusSeverity = InfoBarSeverity.Success;
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
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
    }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return;
    }
        var start = IsCustomRange ? (int)Math.Round(StartIssue) : BaseIssue;
        var end = IsCustomRange ? (int)Math.Round(EndIssue) : LatestIssue;

        if (start < 1 || end < 1 || end < start)
        {
            StatusMessage = "Enter a valid issue range.";
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
            StatusMessage = "Starting TWIC download...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            _cancellationTokenSource = new CancellationTokenSource();
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
                _cancellationTokenSource.Token);

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
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
    }
    }

    private bool CanRun()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return false;
    }
        if (!IsCustomRange)
        {
            return LatestIssue >= BaseIssue;
    }
        var start = (int)Math.Round(StartIssue);
        var end = (int)Math.Round(EndIssue);
        return start >= 1 && end >= start;
    }
    private bool CanProbe() => !IsRunning;

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
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _executionLock.Dispose();
    }
    private void LoadState()
    {
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
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






