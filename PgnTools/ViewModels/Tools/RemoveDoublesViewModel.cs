using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Deduplicator tool.
/// </summary>
public partial class RemoveDoublesViewModel(
    IRemoveDoublesService removeDoublesService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IRemoveDoublesService _removeDoublesService = removeDoublesService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(RemoveDoublesViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _inputFileName = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Select input and output PGN files";

    [ObservableProperty]
    private long _progressGames;
    public void Initialize()
    {
        Title = "Deduplicator";
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
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = string.IsNullOrWhiteSpace(InputFilePath)
                ? "deduplicated.pgn"
                : $"{Path.GetFileNameWithoutExtension(InputFilePath)}_deduplicated.pgn";

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
        if (!await _executionLock.WaitAsync(0))
        {
            return;
    }
        try
        {
            IsRunning = true;
            ProgressGames = 0;
            StatusMessage = "Removing duplicates...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(null, 0, null, "games");

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<(long games, string message)>(p =>
            {
                ProgressGames = p.games;
                StatusMessage = p.message;
                StatusDetail = BuildProgressDetail(null, p.games, null, "games");
            });

            var result = await _removeDoublesService.DeduplicateAsync(
                InputFilePath,
                OutputFilePath,
                progress,
                _cancellationTokenSource.Token);

            StatusMessage = result.Processed == 0
                ? "No games found."
                : $"Deduplication complete: {result.Kept:N0} unique games saved ({result.Removed:N0} removed)";
            StatusSeverity = result.Processed == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100, result.Processed, null, "games");
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "Deduplication cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(null, ProgressGames, null, "games");
    }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(null, ProgressGames, null, "games");
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
        File.Exists(InputFilePath) &&
        !string.IsNullOrWhiteSpace(OutputFilePath);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(null, ProgressGames, null, "games");
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
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", string.Empty);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
    }
    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
    }
}






