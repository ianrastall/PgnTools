using System.IO;
using System.Text;
using PgnTools.Helpers;
using PgnTools.Services;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Chess.com Downloader tool.
/// </summary>
public partial class ChesscomDownloaderViewModel(
    IChesscomDownloaderService service,
    IWindowService windowService,
    IAppSettingsService settings) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IChesscomDownloaderService _service = service;
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposeLockOnRelease;
    private bool _disposed;
    private const string SettingsPrefix = nameof(ChesscomDownloaderViewModel);
    private const int BufferSize = 65536;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Enter a Chess.com username";

    [ObservableProperty]
    private double _progressValue;
    public void Initialize()
    {
        Title = "Chess.com Downloader";
        StatusSeverity = InfoBarSeverity.Informational;
        LoadState();
    }
    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = string.IsNullOrWhiteSpace(Username)
                ? "chesscom_games.pgn"
                : $"{SanitizeFileNamePart(Username)}_games.pgn";

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
    private async Task DownloadAllAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(Username))
        {
            return;
    }
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            await SelectOutputFileAsync();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                StatusMessage = "Output file is required.";
                StatusSeverity = InfoBarSeverity.Warning;
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
            ProgressValue = 0;
            StatusMessage = "Fetching archives...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var archives = await _service.GetArchivesAsync(Username, _cancellationTokenSource.Token);
            if (archives.Count == 0)
            {
                StatusMessage = "No archives found for this user.";
                StatusSeverity = InfoBarSeverity.Warning;
                StatusDetail = BuildProgressDetail(0, 0, 0, "archives");
                return;
    }
            var outputFullPath = Path.GetFullPath(OutputFilePath);
            var outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var validation = await FileValidationHelper.ValidateWritableFolderAsync(outputDirectory);
                if (!validation.Success)
                {
                    StatusMessage = $"Cannot write to folder: {validation.ErrorMessage}";
                    StatusSeverity = InfoBarSeverity.Error;
                    StatusDetail = BuildProgressDetail(0, 0, 0, "archives");
                    return;
                }
            }

            var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);
            var completedWrite = false;
            var completed = 0;
            var failed = 0;
            var firstOutput = true;
            string? lastErrorMessage = null;

            try
            {
                await using (var outputStream = new FileStream(
                    tempOutputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan | FileOptions.Asynchronous))
                using (var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), BufferSize, leaveOpen: true))
                {
                    for (var i = 0; i < archives.Count; i++)
                    {
                        _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                        if (!TryParseArchive(archives[i], out var year, out var month))
                        {
                            failed++;
                            continue;
    }
                        StatusMessage = $"Downloading {year}/{month:D2} ({i + 1}/{archives.Count})...";

                        try
                        {
                            var pgn = await _service.DownloadPlayerGamesPgnAsync(Username, year, month, _cancellationTokenSource.Token);
                            if (!firstOutput)
                            {
                                await writer.WriteLineAsync();
    }
                            if (!string.IsNullOrWhiteSpace(pgn))
                            {
                                await writer.WriteLineAsync(pgn.TrimEnd());
                                firstOutput = false;
    }
                            completed++;
    }
                        catch (Exception ex)
                        {
                            failed++;
                            lastErrorMessage = $"{year}/{month:D2}: {ex.GetType().Name} - {ex.Message}";
    }
                        ProgressValue = (i + 1) / (double)archives.Count * 100.0;
                        var detail = BuildProgressDetail(ProgressValue, i + 1, archives.Count, "archives");
                        if (!string.IsNullOrWhiteSpace(lastErrorMessage))
                        {
                            detail = $"{detail} | Last error: {lastErrorMessage}";
                        }
                        StatusDetail = detail;
    }
                    await writer.FlushAsync();
    }
                await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, _cancellationTokenSource.Token);
                completedWrite = true;
    }
            finally
            {
                if (!completedWrite && File.Exists(tempOutputPath))
                {
                    try
                    {
                        File.Delete(tempOutputPath);
    }
                    catch
                    {
                    }
                }
            }

            StatusMessage = $"Download complete. {completed:N0} archive(s) saved{(failed > 0 ? $", {failed:N0} failed." : ".")}";
            StatusSeverity = failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            var completionDetail = BuildProgressDetail(100, completed + failed, archives.Count, "archives");
            if (!string.IsNullOrWhiteSpace(lastErrorMessage))
            {
                completionDetail = $"{completionDetail} | Last error: {lastErrorMessage}";
            }
            StatusDetail = completionDetail;
    }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled";
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

    private bool CanRun() =>
        !_disposed && !IsRunning && !string.IsNullOrWhiteSpace(Username);

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
    }
    partial void OnUsernameChanged(string value)
    {
        DownloadAllCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsRunningChanged(bool value)
    {
        DownloadAllCommand.NotifyCanExecuteChanged();
    }
    private static bool TryParseArchive(string archiveUrl, out int year, out int month)
    {
        year = 0;
        month = 0;

        if (string.IsNullOrWhiteSpace(archiveUrl))
        {
            return false;
    }
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.Segments;
        if (segments.Length < 2)
        {
            return false;
        }

        var yearSegment = segments[^2].Trim('/');
        var monthSegment = segments[^1].Trim('/');

        if (!int.TryParse(yearSegment, out year) || year < 2000)
        {
            year = 0;
            return false;
        }

        if (!int.TryParse(monthSegment, out month) || month is < 1 or > 12)
        {
            month = 0;
            return false;
        }

        return true;
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
        if (IsRunning || _executionLock.CurrentCount == 0)
        {
            _disposeLockOnRelease = true;
            return;
        }
        _executionLock.Dispose();
    }
    private void LoadState()
    {
        Username = _settings.GetValue($"{SettingsPrefix}.{nameof(Username)}", Username);
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            var defaultFolder = _settings.GetValue(AppSettingsKeys.DefaultDownloadFolder, string.Empty);
            if (!string.IsNullOrWhiteSpace(defaultFolder))
            {
                var suggestedName = string.IsNullOrWhiteSpace(Username)
                    ? "chesscom_games.pgn"
                    : $"{SanitizeFileNamePart(Username)}_games.pgn";
                OutputFilePath = Path.Combine(defaultFolder, suggestedName);
            }
        }
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
    }
    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(Username)}", Username);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
    }

    private static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "chesscom";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (Array.IndexOf(invalid, c) >= 0)
            {
                continue;
            }
            builder.Append(c);
        }

        var sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "chesscom";
        }

        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }
}






