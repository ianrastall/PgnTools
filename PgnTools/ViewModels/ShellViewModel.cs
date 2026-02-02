namespace PgnTools.ViewModels;

/// <summary>
/// ViewModel for the shell page.
/// </summary>
public partial class ShellViewModel(INavigationService navigationService) : BaseViewModel, IInitializable
{
    private readonly INavigationService _navigationService = navigationService;
    public void Initialize()
    {
        Title = "PGN Tools";
    }
    [RelayCommand]
    private void NavigateToTool(string toolKey)
    {
        _navigationService.NavigateTo(toolKey);
    }
}





