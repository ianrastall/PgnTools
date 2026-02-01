using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Tournament Breaker tool.
/// </summary>
public partial class TourBreakerViewModel : BaseViewModel, IDisposable
{
    private readonly ITourBreakerService _tourBreakerService;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(TourBreakerViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _inputFileName = string.Empty;

    [ObservableProperty]
    private string _outputFolderPath = string.Empty;

    [ObservableProperty]
    private string _outputFolderName = string.Empty;

    [ObservableProperty]
    private int _minElo = 2700;

    [ObservableProperty]
    private int _minGames = 4;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select an input PGN and output folder";

    public TourBreakerViewModel(
        ITourBreakerService tourBreakerService,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _tourBreakerService = tourBreakerService;
        _windowService = windowService;
        _settings = settings;
        Title = "Tournament Breaker";
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting input file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
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
        if (string.IsNullOrWhiteSpace(InputFilePath) || string.IsNullOrWhiteSpace(OutputFolderPath))
        {
            return;
        }

        var folderValidation = await FileValidationHelper.ValidateWritableFolderAsync(OutputFolderPath);
        if (!folderValidation.Success)
        {
            StatusMessage = $"Cannot write to folder: {folderValidation.ErrorMessage}";
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
            StatusMessage = "Scanning tournaments...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                ProgressValue = p;
                StatusMessage = $"Breaking tournaments... {p:0}%";
                StatusDetail = BuildProgressDetail(p);
            });

            var tournaments = await _tourBreakerService.BreakTournamentsAsync(
                InputFilePath,
                OutputFolderPath,
                MinElo,
                MinGames,
                progress,
                _cancellationTokenSource.Token);

            ProgressValue = 100;
            StatusMessage = tournaments == 0
                ? "No tournaments matched the filters."
                : $"Done. Extracted {tournaments:N0} tournament(s).";
            StatusSeverity = tournaments == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Tournament breaking cancelled";
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

    private bool CanRun() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(InputFilePath) &&
        !string.IsNullOrWhiteSpace(OutputFolderPath) &&
        File.Exists(InputFilePath);

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

        OutputFolderPath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFolderPath)}", OutputFolderPath);
        OutputFolderName = string.IsNullOrWhiteSpace(OutputFolderPath)
            ? string.Empty
            : Path.GetFileName(OutputFolderPath.TrimEnd(Path.DirectorySeparatorChar));

        MinElo = _settings.GetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        MinGames = _settings.GetValue($"{SettingsPrefix}.{nameof(MinGames)}", MinGames);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFolderPath)}", OutputFolderPath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MinGames)}", MinGames);
    }
}

