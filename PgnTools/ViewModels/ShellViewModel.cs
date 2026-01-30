namespace PgnTools.ViewModels;

/// <summary>
/// ViewModel for the shell page.
/// </summary>
public partial class ShellViewModel : BaseViewModel
{
    private readonly INavigationService _navigationService;

    public ShellViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;
        Title = "PGN Tools";
    }

    [RelayCommand]
    private void NavigateToTool(string toolKey)
    {
        _navigationService.NavigateTo(toolKey);
    }
}
