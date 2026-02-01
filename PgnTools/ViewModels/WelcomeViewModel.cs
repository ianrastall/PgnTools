namespace PgnTools.ViewModels;

/// <summary>
/// ViewModel for the welcome page tool grid.
/// </summary>
public partial class WelcomeViewModel(INavigationService navigationService) : BaseViewModel
{
    public IReadOnlyList<ToolMenuItem> Tools { get; } = ToolRegistry.Tools;

    [RelayCommand]
    private void OpenTool(ToolMenuItem? tool)
    {
        if (tool == null)
        {
            return;
        }

        navigationService.NavigateTo(tool.Key);
    }
}
