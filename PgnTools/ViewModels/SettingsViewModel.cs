using System.Collections.ObjectModel;
using System.Linq;
using PgnTools.Helpers;
using PgnTools.Services;

namespace PgnTools.ViewModels;

/// <summary>
/// ViewModel for application settings.
/// </summary>
public partial class SettingsViewModel(
    IAppSettingsService settings,
    IWindowService windowService) : BaseViewModel, IInitializable, IDisposable
{
    private readonly IAppSettingsService _settings = settings;
    private readonly IWindowService _windowService = windowService;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<AccentColorOption> _accentOptions = new();

    [ObservableProperty]
    private AccentColorOption? _selectedAccent;

    [ObservableProperty]
    private string _tablebasesFolder = string.Empty;

    [ObservableProperty]
    private string _defaultDownloadFolder = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public void Initialize()
    {
        Title = "Settings";
        StatusSeverity = InfoBarSeverity.Informational;

        AccentOptions = new ObservableCollection<AccentColorOption>(AccentColorManager.GetAccentOptions());
        LoadState();
    }

    [RelayCommand]
    private async Task SelectTablebasesFolderAsync()
    {
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(
                _windowService.WindowHandle,
                $"{AppSettingsKeys.TablebasesFolder}.Picker");
            if (folder == null)
            {
                return;
            }

            TablebasesFolder = folder.Path;
            StatusMessage = $"Tablebases folder set to {folder.Path}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting tablebases folder: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private void ClearTablebasesFolder()
    {
        TablebasesFolder = string.Empty;
        StatusMessage = "Tablebases folder cleared.";
        StatusSeverity = InfoBarSeverity.Informational;
    }

    [RelayCommand]
    private async Task SelectDownloadFolderAsync()
    {
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(
                _windowService.WindowHandle,
                $"{AppSettingsKeys.DefaultDownloadFolder}.Picker");
            if (folder == null)
            {
                return;
            }

            DefaultDownloadFolder = folder.Path;
            StatusMessage = $"Download folder set to {folder.Path}";
            StatusSeverity = InfoBarSeverity.Informational;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error selecting download folder: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private void ClearDownloadFolder()
    {
        DefaultDownloadFolder = string.Empty;
        StatusMessage = "Download folder cleared.";
        StatusSeverity = InfoBarSeverity.Informational;
    }

    partial void OnSelectedAccentChanged(AccentColorOption? value)
    {
        if (_disposed)
        {
            return;
        }

        var key = value?.Key ?? "System";
        _settings.SetValue(AppSettingsKeys.AccentColor, key);
        AccentColorManager.ApplyAccent(value?.Color);
    }

    partial void OnTablebasesFolderChanged(string value)
    {
        if (_disposed)
        {
            return;
        }

        _settings.SetValue(AppSettingsKeys.TablebasesFolder, value ?? string.Empty);
    }

    partial void OnDefaultDownloadFolderChanged(string value)
    {
        if (_disposed)
        {
            return;
        }

        _settings.SetValue(AppSettingsKeys.DefaultDownloadFolder, value ?? string.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void LoadState()
    {
        TablebasesFolder = _settings.GetValue(AppSettingsKeys.TablebasesFolder, string.Empty);
        DefaultDownloadFolder = _settings.GetValue(AppSettingsKeys.DefaultDownloadFolder, string.Empty);

        var savedAccent = _settings.GetValue(AppSettingsKeys.AccentColor, "System");
        SelectedAccent = AccentOptions.FirstOrDefault(option =>
            string.Equals(option.Key, savedAccent, StringComparison.OrdinalIgnoreCase))
            ?? AccentOptions.FirstOrDefault();

        AccentColorManager.ApplyAccent(SelectedAccent?.Color);
    }
}
