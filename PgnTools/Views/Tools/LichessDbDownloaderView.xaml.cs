namespace PgnTools.Views.Tools;

public sealed partial class LichessDbDownloaderView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(LichessDbDownloaderViewModel),
            typeof(LichessDbDownloaderView),
            new PropertyMetadata(null));

    public LichessDbDownloaderViewModel ViewModel
    {
        get => (LichessDbDownloaderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public LichessDbDownloaderView()
    {
        InitializeComponent();
    }
}
