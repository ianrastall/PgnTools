using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the general PGN Filter tool.
/// </summary>
public partial class FilterViewModel : BaseViewModel, IDisposable
{
    private readonly IPgnFilterService _filterService;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(FilterViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _inputFileName = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private int _minElo;

    [ObservableProperty]
    private int _maxElo = 5000;

    [ObservableProperty]
    private bool _requireBothElos;

    [ObservableProperty]
    private int _minPlyCount;

    [ObservableProperty]
    private int _maxPlyCount;

    [ObservableProperty]
    private bool _onlyCheckmates;

    [ObservableProperty]
    private bool _removeComments;

    [ObservableProperty]
    private bool _removeNags;

    [ObservableProperty]
    private bool _removeVariations;

    [ObservableProperty]
    private bool _removeNonStandard;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select input and output PGN files";

    public FilterViewModel(
        IPgnFilterService filterService,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _filterService = filterService;
        _windowService = windowService;
        _settings = settings;
        Title = "Filter";
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

            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                var directory = Path.GetDirectoryName(InputFilePath) ?? string.Empty;
                var suggestedName = $"{Path.GetFileNameWithoutExtension(InputFilePath)}_filtered.pgn";
                OutputFilePath = Path.Combine(directory, suggestedName);
                OutputFileName = Path.GetFileName(OutputFilePath);
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
                ? "filtered.pgn"
                : $"{Path.GetFileNameWithoutExtension(InputFilePath)}_filtered.pgn";

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
            OutputFileName = file.Name;
            StatusMessage = $"Selected output: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
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
        if (string.IsNullOrWhiteSpace(InputFilePath) || string.IsNullOrWhiteSpace(OutputFilePath))
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

        if (MinElo > 0 && MaxElo > 0 && MaxElo < MinElo)
        {
            StatusMessage = "Max Elo must be greater than or equal to Min Elo.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
        }

        if (MinPlyCount > 0 && MaxPlyCount > 0 && MaxPlyCount < MinPlyCount)
        {
            StatusMessage = "Max ply count must be greater than or equal to Min ply count.";
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
            StatusMessage = "Filtering games...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                ProgressValue = p;
                StatusMessage = $"Filtering games... {p:0}%";
                StatusDetail = BuildProgressDetail(p);
            });

            var options = new PgnFilterOptions(
                MinElo > 0 ? MinElo : null,
                MaxElo > 0 ? MaxElo : null,
                RequireBothElos,
                OnlyCheckmates,
                RemoveComments,
                RemoveNags,
                RemoveVariations,
                RemoveNonStandard,
                MinPlyCount > 0 ? MinPlyCount : null,
                MaxPlyCount > 0 ? MaxPlyCount : null);

            var result = await _filterService.FilterAsync(
                InputFilePath,
                OutputFilePath,
                options,
                progress,
                _cancellationTokenSource.Token);

            StatusMessage = result.Processed == 0
                ? "No games found."
                : $"Done. Processed: {result.Processed:N0}, Kept: {result.Kept:N0}, Updated: {result.Modified:N0}.";
            StatusSeverity = result.Processed == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100, result.Processed, null, "games");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Filtering cancelled";
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
        if (_executionLock.CurrentCount > 0)
        {
            _executionLock.Dispose();
        }
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

        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", string.Empty);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);

        MinElo = _settings.GetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        MaxElo = _settings.GetValue($"{SettingsPrefix}.{nameof(MaxElo)}", MaxElo);
        RequireBothElos = _settings.GetValue($"{SettingsPrefix}.{nameof(RequireBothElos)}", RequireBothElos);
        MinPlyCount = _settings.GetValue($"{SettingsPrefix}.{nameof(MinPlyCount)}", MinPlyCount);
        MaxPlyCount = _settings.GetValue($"{SettingsPrefix}.{nameof(MaxPlyCount)}", MaxPlyCount);
        OnlyCheckmates = _settings.GetValue($"{SettingsPrefix}.{nameof(OnlyCheckmates)}", OnlyCheckmates);
        RemoveComments = _settings.GetValue($"{SettingsPrefix}.{nameof(RemoveComments)}", RemoveComments);
        RemoveNags = _settings.GetValue($"{SettingsPrefix}.{nameof(RemoveNags)}", RemoveNags);
        RemoveVariations = _settings.GetValue($"{SettingsPrefix}.{nameof(RemoveVariations)}", RemoveVariations);
        RemoveNonStandard = _settings.GetValue($"{SettingsPrefix}.{nameof(RemoveNonStandard)}", RemoveNonStandard);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MaxElo)}", MaxElo);
        _settings.SetValue($"{SettingsPrefix}.{nameof(RequireBothElos)}", RequireBothElos);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MinPlyCount)}", MinPlyCount);
        _settings.SetValue($"{SettingsPrefix}.{nameof(MaxPlyCount)}", MaxPlyCount);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OnlyCheckmates)}", OnlyCheckmates);
        _settings.SetValue($"{SettingsPrefix}.{nameof(RemoveComments)}", RemoveComments);
        _settings.SetValue($"{SettingsPrefix}.{nameof(RemoveNags)}", RemoveNags);
        _settings.SetValue($"{SettingsPrefix}.{nameof(RemoveVariations)}", RemoveVariations);
        _settings.SetValue($"{SettingsPrefix}.{nameof(RemoveNonStandard)}", RemoveNonStandard);
    }
}
