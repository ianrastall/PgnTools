using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the PGN Mentor Downloader tool.
/// </summary>
public partial class PgnMentorDownloaderViewModel(
    IPgnMentorDownloaderService pgnMentorDownloaderService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IPgnMentorDownloaderService _pgnMentorDownloaderService = pgnMentorDownloaderService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(PgnMentorDownloaderViewModel);

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Select output file to download and combine PGNs";
    public void Initialize()
    {
        Title = "PGN Mentor Downloader";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }
    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = "pgnmentor_collection.pgn";
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
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return;
    }
        if (!await _executionLock.WaitAsync(0))
        {
            return;
    }
        try
        {
            IsRunning = true;
            StatusMessage = "Downloading PGN Mentor files...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            _cancellationTokenSource = new CancellationTokenSource();
            var progress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail();
            });

            await _pgnMentorDownloaderService.DownloadAndCombineAsync(
                OutputFilePath,
                progress,
                _cancellationTokenSource.Token);

            StatusMessage = "PGN Mentor download complete.";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail();
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "PGN Mentor download cancelled";
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

    private bool CanRun() =>
        !IsRunning && !string.IsNullOrWhiteSpace(OutputFilePath);

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
        _executionLock.Dispose();
    }
    private void LoadState()
    {
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
    }
    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
    }
}






