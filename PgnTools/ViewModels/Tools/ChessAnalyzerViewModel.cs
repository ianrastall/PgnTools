using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Chess Analyzer tool.
/// </summary>
public partial class ChessAnalyzerViewModel(
    IChessAnalyzerService chessAnalyzerService,
    IStockfishDownloaderService stockfishDownloaderService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IChessAnalyzerService _chessAnalyzerService = chessAnalyzerService;
    private readonly IStockfishDownloaderService _stockfishDownloaderService = stockfishDownloaderService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private bool _disposeWhenIdle;
    private long _progressGames;
    private long _progressTotal;
    private const string SettingsPrefix = nameof(ChessAnalyzerViewModel);

    [ObservableProperty]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    private string _inputFileName = string.Empty;

    [ObservableProperty]
    private string _enginePath = string.Empty;

    [ObservableProperty]
    private string _engineName = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private int _depth = 18;

    [ObservableProperty]
    private bool _addEleganceTags;

    [ObservableProperty]
    private bool _useTablebases;

    [ObservableProperty]
    private string _tablebasePath = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select an input PGN and a UCI engine (e.g., Stockfish)";
    public void Initialize()
    {
        Title = "Chess Analyzer";
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
                var suggestedName = $"{Path.GetFileNameWithoutExtension(InputFilePath)}_analyzed.pgn";
                OutputFilePath = Path.Combine(directory, suggestedName);
                OutputFileName = Path.GetFileName(OutputFilePath);
    }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting input file: {ex.Message}";
    }
    }

    [RelayCommand]
    private async Task SelectEngineAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSingleFileAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Engine",
                ".exe");
            if (file == null)
            {
                return;
    }
            var validation = await FileValidationHelper.ValidateReadableFileAsync(file);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot access engine: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
    }
            EnginePath = file.Path;
            EngineName = file.Name;
            StatusMessage = $"Selected engine: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
    }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting engine: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
    }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadLatestEngine))]
    private async Task DownloadLatestEngineAsync()
    {
        if (!await _executionLock.WaitAsync(0))
        {
            return;
    }
        try
        {
            IsRunning = true;
            StatusMessage = "Preparing Stockfish download...";
            StatusSeverity = InfoBarSeverity.Informational;
            ProgressValue = 0;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var statusProgress = new Progress<string>(message =>
            {
                StatusMessage = message;
                StatusDetail = BuildProgressDetail(ProgressValue);
            });

            var result = await _stockfishDownloaderService.DownloadLatestAsync(
                statusProgress,
                _cancellationTokenSource.Token);

            EnginePath = result.ExecutablePath;
            EngineName = Path.GetFileName(result.ExecutablePath);
            StatusMessage = $"Stockfish downloaded ({result.Tag}, {result.Variant}).";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100);
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stockfish download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = BuildProgressDetail(ProgressValue);
    }
        catch (Exception ex)
        {
            StatusMessage = $"Error downloading Stockfish: {ex.Message}";
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
            TryDisposeWhenIdle();
    }
    }

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = string.IsNullOrWhiteSpace(InputFilePath)
                ? "analyzed.pgn"
                : $"{Path.GetFileNameWithoutExtension(InputFilePath)}_analyzed.pgn";

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

    [RelayCommand]
    private async Task SelectTablebaseFolderAsync()
    {
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Tablebases");
            if (folder == null)
            {
                return;
    }
            TablebasePath = folder.Path;
            UseTablebases = true;
            StatusMessage = $"Selected tablebase folder: {folder.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
    }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting tablebase folder: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
    }
    }

    private bool CanDownloadLatestEngine() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(InputFilePath) ||
            string.IsNullOrWhiteSpace(EnginePath) ||
            string.IsNullOrWhiteSpace(OutputFilePath))
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
        if (Depth <= 0)
        {
            StatusMessage = "Depth must be greater than zero.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
    }
        if (!File.Exists(EnginePath))
        {
            StatusMessage = "Engine executable not found.";
            StatusSeverity = InfoBarSeverity.Error;
            return;
    }
        if (UseTablebases && (string.IsNullOrWhiteSpace(TablebasePath) || !Directory.Exists(TablebasePath)))
        {
            StatusMessage = "Tablebase folder not found.";
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
            StatusMessage = "Initializing engine...";
            StatusSeverity = InfoBarSeverity.Informational;
            _progressGames = 0;
            _progressTotal = 0;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<AnalyzerProgress>(p =>
            {
                Interlocked.Exchange(ref _progressGames, p.ProcessedGames);
                Interlocked.Exchange(ref _progressTotal, p.TotalGames);
                ProgressValue = p.Percent;
                StatusMessage = $"Analyzing games... {p.Percent:0}%";
                StatusDetail = BuildProgressDetail(p.Percent, p.ProcessedGames, p.TotalGames, "games");
            });

            await _chessAnalyzerService.AnalyzePgnAsync(
                InputFilePath,
                OutputFilePath,
                EnginePath,
                Depth,
                UseTablebases ? TablebasePath : null,
                progress,
                _cancellationTokenSource.Token,
                AddEleganceTags);

            if (_cancellationTokenSource?.IsCancellationRequested == true)
            {
                throw new OperationCanceledException(_cancellationTokenSource.Token);
    }
            ProgressValue = 100;
            StatusMessage = AddEleganceTags
                ? "Analysis and Elegance tagging complete. (Move-by-move evals require SAN/FEN support.)"
                : "Analysis complete. (Move-by-move evals require SAN/FEN support.)";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100, _progressTotal > 0 ? _progressTotal : _progressGames, _progressTotal, "games");
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            ProgressValue = 0;
            StatusDetail = BuildProgressDetail(ProgressValue, _progressGames, _progressTotal, "games");
    }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = BuildProgressDetail(ProgressValue, _progressGames, _progressTotal, "games");
    }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _executionLock.Release();
            StopProgressTimer();
            TryDisposeWhenIdle();
    }
    }

    private bool CanRun() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(InputFilePath) &&
        !string.IsNullOrWhiteSpace(EnginePath) &&
        !string.IsNullOrWhiteSpace(OutputFilePath) &&
        File.Exists(InputFilePath) &&
        File.Exists(EnginePath) &&
        (!UseTablebases || (!string.IsNullOrWhiteSpace(TablebasePath) && Directory.Exists(TablebasePath)));

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling... finishing current game.";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }
    partial void OnInputFilePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }
    partial void OnEnginePathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !File.Exists(value))
        {
            StatusMessage = "Engine executable not found.";
            StatusSeverity = InfoBarSeverity.Error;
    }
        RunCommand.NotifyCanExecuteChanged();
    }
    partial void OnOutputFilePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }
    partial void OnDepthChanged(int value)
    {
        const int minDepth = 1;
        const int maxDepth = 50;

        if (value < minDepth)
        {
            Depth = minDepth;
            return;
    }
        if (value > maxDepth)
        {
            Depth = maxDepth;
            return;
    }
        RunCommand.NotifyCanExecuteChanged();
    }
    partial void OnUseTablebasesChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }
    partial void OnTablebasePathChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        DownloadLatestEngineCommand.NotifyCanExecuteChanged();
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

    public void DisposeWhenIdle()
    {
        if (_disposed)
        {
            return;
        }

        if (IsRunning)
        {
            _disposeWhenIdle = true;
            return;
        }

        Dispose();
    }

    private void TryDisposeWhenIdle()
    {
        if (_disposeWhenIdle && !_disposed && !IsRunning)
        {
            _disposeWhenIdle = false;
            Dispose();
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
        EnginePath = _settings.GetValue($"{SettingsPrefix}.{nameof(EnginePath)}", string.Empty);
        if (!string.IsNullOrWhiteSpace(EnginePath) && !File.Exists(EnginePath))
        {
            EnginePath = string.Empty;
    }
        if (string.IsNullOrWhiteSpace(EnginePath) || IsTemporaryPath(EnginePath))
        {
            var preferredEnginePath = ResolvePreferredEnginePath();
            if (!string.IsNullOrWhiteSpace(preferredEnginePath))
            {
                EnginePath = preferredEnginePath;
    }
        }

        EngineName = !string.IsNullOrWhiteSpace(EnginePath) && File.Exists(EnginePath)
            ? Path.GetFileName(EnginePath)
            : string.Empty;

        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", string.Empty);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);

        Depth = _settings.GetValue($"{SettingsPrefix}.{nameof(Depth)}", Depth);
        AddEleganceTags = _settings.GetValue($"{SettingsPrefix}.{nameof(AddEleganceTags)}", AddEleganceTags);
        UseTablebases = _settings.GetValue($"{SettingsPrefix}.{nameof(UseTablebases)}", UseTablebases);
        TablebasePath = _settings.GetValue($"{SettingsPrefix}.{nameof(TablebasePath)}", TablebasePath);
        if (!string.IsNullOrWhiteSpace(TablebasePath) && !Directory.Exists(TablebasePath))
        {
            TablebasePath = string.Empty;
            UseTablebases = false;
    }
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFilePath)}", InputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(EnginePath)}", EnginePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.{nameof(Depth)}", Depth);
        _settings.SetValue($"{SettingsPrefix}.{nameof(AddEleganceTags)}", AddEleganceTags);
        _settings.SetValue($"{SettingsPrefix}.{nameof(UseTablebases)}", UseTablebases);
        _settings.SetValue($"{SettingsPrefix}.{nameof(TablebasePath)}", TablebasePath);
    }
    private static string ResolvePreferredEnginePath()
    {
        var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        var assetsCandidate = FindStockfishExeUnder(assetsRoot);
        if (!string.IsNullOrWhiteSpace(assetsCandidate))
        {
            return assetsCandidate;
    }
        var localAppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools",
            "Stockfish");

        return FindStockfishExeUnder(localAppDataRoot) ?? string.Empty;
    }
    private static string? FindStockfishExeUnder(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return null;
    }
        try
        {
            return Directory.EnumerateFiles(rootPath, "*.exe", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("stockfish", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Contains($"{Path.DirectorySeparatorChar}Assets{Path.DirectorySeparatorChar}stockfish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(path => path.Length)
                .FirstOrDefault();
    }
        catch
        {
            return null;
    }
    }

    private static bool IsTemporaryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
    }
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
    }
}






