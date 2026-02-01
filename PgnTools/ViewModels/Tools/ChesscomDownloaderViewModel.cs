using System.IO;
using PgnTools.Helpers;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the Chess.com Downloader tool.
/// </summary>
public partial class ChesscomDownloaderViewModel : BaseViewModel, IDisposable
{
    private readonly IChesscomDownloaderService _service;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
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

    public ChesscomDownloaderViewModel(
        IChesscomDownloaderService service,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _service = service;
        _windowService = windowService;
        _settings = settings;
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
                : $"{Username}_games.pgn";

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
        if (string.IsNullOrWhiteSpace(Username))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            await SelectOutputFileAsync();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
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
            var tempOutputPath = outputFullPath + ".tmp";
            var completedWrite = false;

            if (Path.GetDirectoryName(outputFullPath) is { } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var completed = 0;
            var failed = 0;
            var firstOutput = true;

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
                                await writer.WriteLineAsync();
                            }

                            if (!string.IsNullOrWhiteSpace(pgn))
                            {
                                await writer.WriteLineAsync(pgn.TrimEnd());
                            }

                            firstOutput = false;
                            completed++;
                        }
                        catch
                        {
                            failed++;
                        }

                        ProgressValue = (i + 1) / (double)archives.Count * 100.0;
                        StatusDetail = BuildProgressDetail(ProgressValue, i + 1, archives.Count, "archives");
                    }

                    await writer.FlushAsync();
                }

                FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
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
            StatusDetail = BuildProgressDetail(100, completed + failed, archives.Count, "archives");
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
            _executionLock.Release();
            StopProgressTimer();
        }
    }

    private bool CanRun() =>
        !IsRunning && !string.IsNullOrWhiteSpace(Username);

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

        var parts = archiveUrl.TrimEnd('/').Split('/');
        if (parts.Length < 2)
        {
            return false;
        }

        return int.TryParse(parts[^2], out year) && int.TryParse(parts[^1], out month);
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
        Username = _settings.GetValue($"{SettingsPrefix}.{nameof(Username)}", Username);
        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(Username)}", Username);
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
    }
}

