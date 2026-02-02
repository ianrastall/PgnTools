namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the PGN Splitter tool.
/// </summary>
public partial class PgnSplitterViewModel(
    IPgnSplitterService pgnSplitterService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IPgnSplitterService _pgnSplitterService = pgnSplitterService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private bool _inputFileExists;
    private const string SettingsPrefix = nameof(PgnSplitterViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _inputFileName = string.Empty;

    [ObservableProperty]
    private string _outputFolderPath = string.Empty;

    [ObservableProperty]
    private string _outputFolderName = string.Empty;

    [ObservableProperty]
    private double _chunkSize = 1000;

    [ObservableProperty]
    private int _selectedStrategyIndex;

    [ObservableProperty]
    private int _selectedDatePrecisionIndex = 2;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Select an input PGN and output folder";

    [ObservableProperty]
    private long _progressGames;
    public void Initialize()
    {
        Title = "PGN Splitter";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }
    public bool IsChunkStrategy => SelectedStrategyIndex == 0;
    public bool IsDateStrategy => SelectedStrategyIndex == 4;

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
        var chunkSize = (int)Math.Round(ChunkSize);

        if (IsChunkStrategy && chunkSize < 1)
        {
            StatusMessage = "Chunk size must be at least 1.";
            StatusSeverity = InfoBarSeverity.Error;
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
            ProgressGames = 0;
            StatusMessage = "Splitting PGN...";
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

            var strategy = SelectedStrategyIndex switch
            {
                1 => PgnSplitStrategy.Event,
                2 => PgnSplitStrategy.Site,
                3 => PgnSplitStrategy.Eco,
                4 => PgnSplitStrategy.Date,
                _ => PgnSplitStrategy.Chunk
            };

            var datePrecision = SelectedDatePrecisionIndex switch
            {
                0 => PgnDatePrecision.Century,
                1 => PgnDatePrecision.Decade,
                3 => PgnDatePrecision.Month,
                4 => PgnDatePrecision.Day,
                _ => PgnDatePrecision.Year
            };

            var result = await _pgnSplitterService.SplitAsync(
                InputFilePath,
                OutputFolderPath,
                strategy,
                chunkSize,
                datePrecision,
                progress,
                _cancellationTokenSource.Token);

            StatusMessage = $"Split complete: {result.Games:N0} games -> {result.FilesCreated:N0} file(s)";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100, result.Games, null, "games");
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "Split cancelled";
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
        _inputFileExists &&
        !string.IsNullOrWhiteSpace(OutputFolderPath);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(null, ProgressGames, null, "games");
    }
    partial void OnInputFilePathChanged(string value)
    {
        UpdateInputFileMetadata(value);
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
    partial void OnSelectedStrategyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsChunkStrategy));
        OnPropertyChanged(nameof(IsDateStrategy));
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
        if (!_inputFileExists)
        {
            InputFilePath = string.Empty;
    }
        OutputFolderPath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFolderPath)}", OutputFolderPath);
        OutputFolderName = string.IsNullOrWhiteSpace(OutputFolderPath)
            ? string.Empty
            : Path.GetFileName(OutputFolderPath.TrimEnd(Path.DirectorySeparatorChar));

        ChunkSize = _settings.GetValue($"{SettingsPrefix}.{nameof(ChunkSize)}", ChunkSize);
        SelectedStrategyIndex = _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedStrategyIndex)}", SelectedStrategyIndex);
        SelectedDatePrecisionIndex = _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedDatePrecisionIndex)}", SelectedDatePrecisionIndex);
    }
    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFolderPath)}", OutputFolderPath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ChunkSize)}", ChunkSize);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedStrategyIndex)}", SelectedStrategyIndex);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedDatePrecisionIndex)}", SelectedDatePrecisionIndex);
    }
    private void UpdateInputFileMetadata(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _inputFileExists = false;
            InputFileName = string.Empty;
            return;
    }
        _inputFileExists = File.Exists(value);
        InputFileName = _inputFileExists ? Path.GetFileName(value) : string.Empty;
    }
}






