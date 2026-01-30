using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the ECO Tagger tool.
/// </summary>
public partial class EcoTaggerViewModel : BaseViewModel, IDisposable
{
    private readonly IEcoTaggerService _ecoTaggerService;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(EcoTaggerViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _ecoReferenceFilePath = "eco.pgn";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select input and output PGN files";

    public EcoTaggerViewModel(
        IEcoTaggerService ecoTaggerService,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _ecoTaggerService = ecoTaggerService;
        _windowService = windowService;
        _settings = settings;
        Title = "ECO Tagger";
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
            StatusMessage = $"Selected input: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;

            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                var directory = Path.GetDirectoryName(InputFilePath) ?? string.Empty;
                var suggestedName = $"{Path.GetFileNameWithoutExtension(InputFilePath)}_eco_tagged.pgn";
                OutputFilePath = Path.Combine(directory, suggestedName);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting input file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = string.IsNullOrWhiteSpace(InputFilePath)
                ? "eco_tagged.pgn"
                : $"{Path.GetFileNameWithoutExtension(InputFilePath)}_eco_tagged.pgn";

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
    private async Task SelectEcoFileAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSingleFileAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.EcoReference",
                ".pgn");
            if (file == null)
            {
                return;
            }

            var validation = await FileValidationHelper.ValidateReadableFileAsync(file);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot access ECO file: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
            }

            EcoReferenceFilePath = file.Path;
            StatusMessage = $"Selected ECO reference: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting ECO file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(InputFilePath) ||
            string.IsNullOrWhiteSpace(OutputFilePath) ||
            string.IsNullOrWhiteSpace(EcoReferenceFilePath))
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

        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            ProgressValue = 0;
            StatusMessage = "Tagging ECO data...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                ProgressValue = p;
                StatusMessage = $"Tagging ECO data... {p:0}%";
                StatusDetail = BuildProgressDetail(p);
            });

            await _ecoTaggerService.TagEcoAsync(
                InputFilePath,
                OutputFilePath,
                EcoReferenceFilePath,
                progress,
                _cancellationTokenSource.Token);

            ProgressValue = 100;
            StatusMessage = "ECO tagging complete.";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "ECO tagging cancelled";
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
        !string.IsNullOrWhiteSpace(OutputFilePath) &&
        !string.IsNullOrWhiteSpace(EcoReferenceFilePath) &&
        File.Exists(InputFilePath) &&
        File.Exists(EcoReferenceFilePath);

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

    partial void OnOutputFilePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnEcoReferenceFilePathChanged(string value)
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
        InputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        if (!string.IsNullOrWhiteSpace(InputFilePath) && !File.Exists(InputFilePath))
        {
            InputFilePath = string.Empty;
        }

        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);

        EcoReferenceFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(EcoReferenceFilePath)}", EcoReferenceFilePath);
        if (!string.IsNullOrWhiteSpace(EcoReferenceFilePath) && !File.Exists(EcoReferenceFilePath))
        {
            EcoReferenceFilePath = "eco.pgn";
        }
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EcoReferenceFilePath)}", EcoReferenceFilePath);
    }
}
