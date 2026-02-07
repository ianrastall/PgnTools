using PgnTools.Models;
using Windows.Storage;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the PGN Info tool.
/// </summary>
public partial class PgnInfoViewModel(
    IPgnInfoService pgnInfoService,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IPgnInfoService _pgnInfoService = pgnInfoService;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposeLockOnRelease;
    private bool _disposed;
    private const string SettingsPrefix = nameof(PgnInfoViewModel);

    [ObservableProperty]
    private string _selectedFilePath = string.Empty;

    [ObservableProperty]
    private string _selectedFileName = string.Empty;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _statusMessage = "Select a PGN file to analyze";

    [ObservableProperty]
    private PgnStatistics _statistics = new();

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private long _progressGames;
    public void Initialize()
    {
        Title = "PGN Info";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }
    [RelayCommand]
    private async Task SelectFileAsync()
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
            SelectedFilePath = file.Path;
            SelectedFileName = file.Name;
            StatusMessage = $"Ready to analyze: {file.Name}";
            StatusSeverity = InfoBarSeverity.Informational;
            HasResults = false;
            Statistics = new PgnStatistics();
    }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting file: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
    }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (_disposed || string.IsNullOrEmpty(SelectedFilePath))
        {
            return;
    }
        if (!await _executionLock.WaitAsync(0))
        {
            return;
    }
        try
        {
            IsAnalyzing = true;
            HasResults = false;
            Statistics = new PgnStatistics();
            ProgressGames = 0;
            StatusMessage = "Analyzing...";
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

            Statistics = await _pgnInfoService.AnalyzeFileAsync(
                SelectedFilePath,
                progress,
                _cancellationTokenSource.Token);

            HasResults = true;
            StatusMessage = $"Analysis complete: {Statistics.Games:N0} games processed";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100, Statistics.Games, null, "games");
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled";
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
            IsAnalyzing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            try
            {
                _executionLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() may have already torn down the semaphore.
            }
            if (_disposeLockOnRelease)
            {
                _executionLock.Dispose();
            }
            StopProgressTimer();
    }
    }

    private bool CanAnalyze() =>
        !string.IsNullOrWhiteSpace(SelectedFilePath) &&
        File.Exists(SelectedFilePath) &&
        !IsAnalyzing;

    [RelayCommand]
    private void CancelAnalysis()
    {
        _cancellationTokenSource?.Cancel();
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(null, ProgressGames, null, "games");
    }
    partial void OnSelectedFilePathChanged(string value)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsAnalyzingChanged(bool value)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
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
        if (IsAnalyzing || _executionLock.CurrentCount == 0)
        {
            _disposeLockOnRelease = true;
            return;
        }
        _executionLock.Dispose();
    }
    private void LoadState()
    {
        SelectedFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(SelectedFilePath)}", SelectedFilePath);
        if (!string.IsNullOrWhiteSpace(SelectedFilePath) && File.Exists(SelectedFilePath))
        {
            SelectedFileName = Path.GetFileName(SelectedFilePath);
    }
        else
        {
            SelectedFilePath = string.Empty;
            SelectedFileName = string.Empty;
    }
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(SelectedFilePath)}", SelectedFilePath);
    }
}






