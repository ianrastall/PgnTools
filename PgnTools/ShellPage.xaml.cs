using PgnTools.Models;

namespace PgnTools;

/// <summary>
/// Shell page containing the navigation view and content frame.
/// </summary>
public sealed partial class ShellPage : Page
{
    private readonly INavigationService _navigationService;
    public ShellViewModel ViewModel { get; }
    
    /// <summary>
    /// Gets the title bar element for window customization.
    /// </summary>
    public Grid CustomTitleBar => (Grid)FindName("AppTitleBar");

    public ShellPage() : this(App.GetService<ShellViewModel>(), App.GetService<INavigationService>())
    {
    }

    public ShellPage(ShellViewModel viewModel, INavigationService navigationService)
    {
        ViewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        _navigationService = navigationService ?? throw new System.ArgumentNullException(nameof(navigationService));
        this.InitializeComponent();
        ApplyToolIconsFromAssets();
        
        // Set up navigation service with the content frame
        _navigationService.Frame = ContentFrame;
        
        // Navigate to default page
        ContentFrame.Navigate(typeof(WelcomePage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            // Navigate to settings page when implemented
            return;
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            // Navigate to settings page when implemented
            return;
        }

        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            var tool = ToolRegistry.GetTool(tag);
            if (tool != null)
            {
                ContentFrame.Navigate(tool.PageType);
            }
        }
    }

    private void ApplyToolIconsFromAssets()
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is not string tag)
            {
                continue;
            }

            var tool = ToolRegistry.GetTool(tag);
            if (tool?.IconAssetUri == null)
            {
                continue;
            }

            item.Icon = new BitmapIcon
            {
                UriSource = tool.IconAssetUri,
                ShowAsMonochrome = false
            };
        }
    }
}
