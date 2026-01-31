namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Lichess DB Downloader tool.
/// </summary>
public partial class LichessDbDownloaderViewModel : BaseViewModel, IDisposable
{
    private readonly ILichessDbDownloaderService _service;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private bool _outputPathSuggested;
    private LichessDbProgress? _lastProgress;
    private string _lastOutputFolder = string.Empty;
    private static readonly System.Text.RegularExpressions.Regex SuggestedNameRegex =
        new(@"^lichess-\d+-(\d{4}-\d{2}|0000-00)\.pgn$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ArchiveMonthRegex =
        new(@"\d{4}-\d{2}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private const string SettingsPrefix = nameof(LichessDbDownloaderViewModel);

    [ObservableProperty]
    private List<string> _availableArchives = new();

    [ObservableProperty]
    private string? _selectedArchive;

    [ObservableProperty]
    private int _minElo = 2500;

    [ObservableProperty]
    private bool _excludeBullet;

    [ObservableProperty]
    private bool _excludeNonStandard;

    [ObservableProperty]
    private bool _onlyCheckmates;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 100;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _statusMessage = "Select archive and output file";

    public LichessDbDownloaderViewModel(
        ILichessDbDownloaderService service,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _service = service;
        _windowService = windowService;
        _settings = settings;
        Title = "Lichess DB Filter";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
        _ = LoadArchiveListAsync();
    }

    private async Task LoadArchiveListAsync()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var listPath = Path.Combine(baseDir, "Assets", "list.txt");
            if (!File.Exists(listPath))
            {
                var fallback = Path.Combine("Assets", "list.txt");
                if (File.Exists(fallback))
                {
                    listPath = fallback;
                }
            }

            if (!File.Exists(listPath))
            {
                AvailableArchives = new List<string>();
                StatusMessage = "Archive list not found (Assets/list.txt).";
                StatusSeverity = InfoBarSeverity.Warning;
                return;
            }

            var lines = await File.ReadAllLinesAsync(listPath);
            var urls = lines
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();

            AvailableArchives = urls;

            if (urls.Count == 0)
            {
                StatusMessage = "Archive list is empty.";
                StatusSeverity = InfoBarSeverity.Warning;
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedArchive) || !urls.Contains(SelectedArchive))
            {
                SelectedArchive = urls.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading archive list: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        try
        {
            var file = await FilePickerHelper.PickSaveFileAsync(
                _windowService.WindowHandle,
                GetSuggestedFileName(),
                new Dictionary<string, IList<string>>
                {
                    { "PGN Files", [".pgn"] }
                },
                $"{SettingsPrefix}.Picker.Output");

            if (file != null)
            {
                OutputFilePath = file.Path;
                _outputPathSuggested = false;
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
    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedArchive))
        {
            StatusMessage = "Select a Lichess archive.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            ApplySuggestedOutputPath();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                await BrowseOutputAsync();
            }
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                return;
            }
        }

        var outputDirectory = Path.GetDirectoryName(OutputFilePath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            var validation = await FileValidationHelper.ValidateWritableFolderAsync(outputDirectory);
            if (!validation.Success)
            {
                StatusMessage = $"Cannot write to folder: {validation.ErrorMessage}";
                StatusSeverity = InfoBarSeverity.Error;
                return;
            }
        }

        if (!await _executionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRunning = true;
            StatusMessage = "Processing stream...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();
            ProgressValue = 0;
            ProgressMaximum = 100;
            IsIndeterminate = true;
            _lastProgress = null;

            _cancellationTokenSource = new CancellationTokenSource();
            var progress = new Progress<LichessDbProgress>(UpdateProgress);
            await _service.DownloadAndFilterAsync(
                SelectedArchive,
                OutputFilePath,
                MinElo,
                ExcludeBullet,
                ExcludeNonStandard,
                OnlyCheckmates,
                progress,
                _cancellationTokenSource.Token);

            StatusSeverity = InfoBarSeverity.Success;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
            StatusDetail = _lastProgress != null
                ? BuildLichessProgressDetail(_lastProgress)
                : BuildProgressDetail();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
            StatusDetail = _lastProgress != null
                ? BuildLichessProgressDetail(_lastProgress)
                : BuildProgressDetail();
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
        !string.IsNullOrWhiteSpace(SelectedArchive);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail();
    }

    partial void OnSelectedArchiveChanged(string? value)
    {
        if (_outputPathSuggested || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            ApplySuggestedOutputPath();
        }
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnMinEloChanged(int value)
    {
        if (_outputPathSuggested || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            ApplySuggestedOutputPath();
        }
    }

    partial void OnOutputFilePathChanged(string value)
    {
        OutputFileName = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value);

        var directory = Path.GetDirectoryName(value);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _lastOutputFolder = directory;
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
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
        MinElo = _settings.GetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        ExcludeBullet = _settings.GetValue($"{SettingsPrefix}.{nameof(ExcludeBullet)}", ExcludeBullet);
        ExcludeNonStandard = _settings.GetValue($"{SettingsPrefix}.{nameof(ExcludeNonStandard)}", ExcludeNonStandard);
        OnlyCheckmates = _settings.GetValue($"{SettingsPrefix}.{nameof(OnlyCheckmates)}", OnlyCheckmates);
        _lastOutputFolder = _settings.GetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);

        var selected = _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedArchive)}", SelectedArchive);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            SelectedArchive = selected;
        }

        _outputPathSuggested = string.IsNullOrWhiteSpace(OutputFilePath) || IsSuggestedFileName(OutputFileName);
        if (_outputPathSuggested && !string.IsNullOrWhiteSpace(SelectedArchive))
        {
            ApplySuggestedOutputPath();
        }
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(MinElo)}", MinElo);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ExcludeBullet)}", ExcludeBullet);
        _settings.SetValue($"{SettingsPrefix}.{nameof(ExcludeNonStandard)}", ExcludeNonStandard);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OnlyCheckmates)}", OnlyCheckmates);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedArchive)}", SelectedArchive ?? string.Empty);
    }

    private void ApplySuggestedOutputPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedArchive))
        {
            return;
        }

        var suggestedName = GetSuggestedFileName();
        if (string.IsNullOrWhiteSpace(suggestedName))
        {
            return;
        }

        var directory = GetPreferredOutputFolder();
        OutputFilePath = Path.Combine(directory, suggestedName);
        _outputPathSuggested = true;
    }

    private string GetPreferredOutputFolder()
    {
        var directory = Path.GetDirectoryName(OutputFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        if (!string.IsNullOrWhiteSpace(_lastOutputFolder))
        {
            return _lastOutputFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string GetSuggestedFileName()
    {
        var monthToken = ExtractArchiveMonth(SelectedArchive);
        if (string.IsNullOrWhiteSpace(monthToken))
        {
            monthToken = "0000-00";
        }

        return $"lichess-{MinElo}-{monthToken}.pgn";
    }

    private static string ExtractArchiveMonth(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var match = ArchiveMonthRegex.Match(url);
        return match.Success ? match.Value : string.Empty;
    }

    private bool IsSuggestedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return SuggestedNameRegex.IsMatch(fileName);
    }

    private void UpdateProgress(LichessDbProgress progress)
    {
        _lastProgress = progress;

        if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
        {
            IsIndeterminate = false;
            ProgressMaximum = progress.TotalBytes.Value;
            ProgressValue = progress.BytesRead;
        }
        else
        {
            IsIndeterminate = true;
            ProgressMaximum = 100;
            ProgressValue = 0;
        }

        StatusMessage = progress.Stage switch
        {
            LichessDbProgressStage.Completed => "Finished successfully.",
            LichessDbProgressStage.Filtering => "Filtering games...",
            _ => "Downloading archive..."
        };

        StatusDetail = BuildLichessProgressDetail(progress);
    }

    private string BuildLichessProgressDetail(LichessDbProgress progress)
    {
        var parts = new List<string>
        {
            $"Kept {progress.GamesKept:N0} / Seen {progress.GamesSeen:N0}"
        };

        if (progress.BytesRead > 0)
        {
            var bytes = FormatBytes(progress.BytesRead);
            if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
            {
                var total = FormatBytes(progress.TotalBytes.Value);
                parts.Add($"{bytes} / {total}");
            }
            else
            {
                parts.Add($"{bytes} downloaded");
            }
        }

        var elapsed = BuildProgressDetail();
        if (!string.IsNullOrWhiteSpace(elapsed))
        {
            parts.Add(elapsed);
        }

        return string.Join(" • ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= scale && unitIndex < units.Length - 1)
        {
            value /= scale;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
