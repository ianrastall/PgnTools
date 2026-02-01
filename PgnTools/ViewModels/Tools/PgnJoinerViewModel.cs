using System.IO;

namespace PgnTools.ViewModels.Tools;

/// <summary>
/// ViewModel for the PGN Joiner tool.
/// </summary>
public partial class PgnJoinerViewModel : BaseViewModel, IDisposable
{
    private readonly IPgnJoinerService _joinerService;
    private readonly IAppSettingsService _settings;
    private readonly IWindowService _windowService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private bool _disposed;
    private const string SettingsPrefix = nameof(PgnJoinerViewModel);

    [ObservableProperty]
    private ObservableCollection<string> _inputFiles = new();

    [ObservableProperty]
    private string _outputFilePath = string.Empty;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Select PGN files to join";

    [ObservableProperty]
    private bool _isRunning;

    public PgnJoinerViewModel(
        IPgnJoinerService joinerService,
        IWindowService windowService,
        IAppSettingsService settings)
    {
        _joinerService = joinerService;
        _windowService = windowService;
        _settings = settings;
        Title = "PGN Joiner";
        StatusSeverity = InfoBarSeverity.Informational;

        InputFiles.CollectionChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
        LoadState();
    }

    [RelayCommand]
    private async Task SelectInputFilesAsync()
    {
        try
        {
            var files = await FilePickerHelper.PickMultipleFilesAsync(
                _windowService.WindowHandle,
                $"{SettingsPrefix}.Picker.Input",
                ".pgn");
            if (files.Count == 0)
            {
                return;
            }

            var added = 0;
            var skipped = 0;
            foreach (var file in files)
            {
                if (InputFiles.Any(path => string.Equals(path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var validation = await FileValidationHelper.ValidateReadableFileAsync(file);
                if (!validation.Success)
                {
                    skipped++;
                    continue;
                }

                InputFiles.Add(file.Path);
                added++;
            }

            if (added == 0 && skipped == 0)
            {
                StatusMessage = "No new files added.";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else if (skipped > 0)
            {
                StatusMessage = $"{added} file(s) added. {skipped} skipped (unreadable). Total: {InputFiles.Count}";
                StatusSeverity = InfoBarSeverity.Warning;
            }
            else
            {
                StatusMessage = $"{added} file(s) added. Total: {InputFiles.Count}";
                StatusSeverity = InfoBarSeverity.Informational;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting files: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private void ClearFiles()
    {
        InputFiles.Clear();
        StatusMessage = "File list cleared.";
        StatusSeverity = InfoBarSeverity.Informational;
    }

    [RelayCommand]
    private async Task SelectOutputFileAsync()
    {
        try
        {
            var suggestedName = "joined_games.pgn";
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
        if (InputFiles.Count == 0 || string.IsNullOrWhiteSpace(OutputFilePath))
        {
            return;
        }

        var outputFullPath = Path.GetFullPath(OutputFilePath);
        if (InputFiles.Any(path => string.Equals(Path.GetFullPath(path), outputFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Output file must be different from input files.";
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
            StatusMessage = "Joining files...";
            StatusSeverity = InfoBarSeverity.Informational;
            StartProgressTimer();
            StatusDetail = BuildProgressDetail(0);

            _cancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                ProgressValue = p;
                StatusMessage = $"Joining files... {p:0}%";
                StatusDetail = BuildProgressDetail(p);
            });

            await _joinerService.JoinFilesAsync(
                InputFiles,
                OutputFilePath,
                progress,
                _cancellationTokenSource.Token);

            StatusMessage = "Join complete.";
            StatusSeverity = InfoBarSeverity.Success;
            StatusDetail = BuildProgressDetail(100);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Join cancelled";
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
        InputFiles.Count > 0 &&
        InputFiles.All(File.Exists) &&
        !string.IsNullOrWhiteSpace(OutputFilePath);

    [RelayCommand]
    private void Cancel()
    {
        var cts = _cancellationTokenSource;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        StatusMessage = "Cancelling...";
        StatusSeverity = InfoBarSeverity.Warning;
        StatusDetail = BuildProgressDetail(ProgressValue);
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
        var savedInputs = _settings.GetValue($"{SettingsPrefix}.{nameof(InputFiles)}", new List<string>());
        InputFiles.Clear();
        foreach (var path in savedInputs)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                InputFiles.Add(path);
            }
        }

        OutputFilePath = _settings.GetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
        OutputFileName = string.IsNullOrWhiteSpace(OutputFilePath) ? string.Empty : Path.GetFileName(OutputFilePath);
    }

    private void SaveState()
    {
        _settings.SetValue($"{SettingsPrefix}.{nameof(InputFiles)}", InputFiles.ToList());
        _settings.SetValue($"{SettingsPrefix}.{nameof(OutputFilePath)}", OutputFilePath);
    }
}

