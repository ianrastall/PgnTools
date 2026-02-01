using System.Globalization;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Lc0 Downloader tool.
/// </summary>
public partial class Lc0DownloaderViewModel : BaseViewModel, IDisposable
{
    private readonly ILc0DownloaderService _service;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private bool _outputPathSuggested;
    private string _lastOutputFolder = string.Empty;
    private static readonly DateOnly EarliestArchiveMonth = new(2018, 1, 1);
    private static readonly System.Text.RegularExpressions.Regex ArchiveTokenRegex =
        new(@"^\d{4}-\d{2}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex SuggestedNameRegex =
        new(@"^lc0-(\d{4}-\d{2}|0000-00)\.pgn$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private const string SettingsPrefix = nameof(Lc0DownloaderViewModel);

    [ObservableProperty]
    private List<string> _availableArchives = new();

    [ObservableProperty]
    private string? _selectedArchive;

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
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select archive and output file";

    public Lc0DownloaderViewModel(
        ILc0DownloaderService service,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _service = service;
        _windowService = windowService;
        _settings = settings;
        Title = "Lc0";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
        LoadArchiveList();
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
        if (!TryParseArchiveMonth(SelectedArchive, out var archiveMonth))
        {
            StatusMessage = "Select a valid monthly archive.";
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
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusMessage = $"Preparing Lc0 archive {archiveMonth:yyyy-MM}...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail();

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<Lc0DownloadProgress>(update =>
            {
                StatusMessage = update.Message;

                if (update.Percent.HasValue)
                {
                    ProgressValue = update.Percent.Value;
                    IsIndeterminate = false;
                }
                else
                {
                    IsIndeterminate = true;
                }

                var unit = update.Phase == Lc0DownloadPhase.Scraping ? "pages" : "matches";
                StatusDetail = BuildProgressDetail(update.Percent, update.Current, update.Total, unit);
            });

            var result = await _service.DownloadAndProcessAsync(
                new Lc0DownloadOptions(
                    OutputFilePath,
                    archiveMonth,
                    ExcludeNonStandard,
                    OnlyCheckmates),
                progress,
                _cancellationTokenSource.Token);

            ProgressValue = 100;
            IsIndeterminate = false;

            if (result.TotalMatches == 0)
            {
                StatusMessage = $"No matches found for {archiveMonth:yyyy-MM}.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else if (result.FailedMatches > 0)
            {
                StatusMessage =
                    $"Completed with errors. Wrote {result.GamesKept:N0} game(s) from {result.ProcessedMatches:N0}/{result.TotalMatches:N0} matches; {result.FailedMatches:N0} failed.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else if (result.GamesKept == 0)
            {
                StatusMessage =
                    $"No games matched the selected filters for {archiveMonth:yyyy-MM}.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else
            {
                StatusMessage =
                    $"Completed. Wrote {result.GamesKept:N0} game(s) from {result.ProcessedMatches:N0} matches.";
                StatusSeverity = InfoBarSeverity.Success;
            }

            StatusDetail = BuildProgressDetail(100, result.ProcessedMatches, result.TotalMatches, "matches");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Lc0 download cancelled";
            StatusSeverity = InfoBarSeverity.Warning;
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
        !string.IsNullOrWhiteSpace(SelectedArchive);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }

    partial void OnSelectedArchiveChanged(string? value)
    {
        if (_outputPathSuggested || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            ApplySuggestedOutputPath();
        }

        StartCommand.NotifyCanExecuteChanged();
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
        _executionLock.Dispose();
    }

    private void LoadState()
    {
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
        _settings.SetValue($"{SettingsPrefix}.{nameof(ExcludeNonStandard)}", ExcludeNonStandard);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OnlyCheckmates)}", OnlyCheckmates);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        _settings.SetValue($"{SettingsPrefix}.LastOutputFolder", _lastOutputFolder);
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedArchive)}", SelectedArchive ?? string.Empty);
    }

    private void LoadArchiveList()
    {
        var utcNow = DateTime.UtcNow;
        var latestArchiveMonth = new DateOnly(utcNow.Year, utcNow.Month, 1);
        if (latestArchiveMonth < EarliestArchiveMonth)
        {
            AvailableArchives = new List<string>();
            StatusMessage = "No monthly archives are currently available.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var archives = BuildAvailableArchives(latestArchiveMonth, EarliestArchiveMonth);
        AvailableArchives = archives;

        if (archives.Count == 0)
        {
            StatusMessage = "No monthly archives are currently available.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedArchive) ||
            !archives.Any(archive => string.Equals(archive, SelectedArchive, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedArchive = archives[0];
        }
    }

    private static List<string> BuildAvailableArchives(DateOnly newestMonth, DateOnly oldestMonth)
    {
        var archives = new List<string>();
        for (var month = newestMonth; month >= oldestMonth; month = month.AddMonths(-1))
        {
            archives.Add($"{month:yyyy-MM}");
        }

        return archives;
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
        var monthToken = string.IsNullOrWhiteSpace(SelectedArchive) ? "0000-00" : SelectedArchive;
        return $"lc0-{monthToken}.pgn";
    }

    private static bool TryParseArchiveMonth(string? archiveToken, out DateOnly month)
    {
        month = default;

        if (string.IsNullOrWhiteSpace(archiveToken) || !ArchiveTokenRegex.IsMatch(archiveToken))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            archiveToken,
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out month);
    }

    private static bool IsSuggestedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return SuggestedNameRegex.IsMatch(fileName);
    }
}
