namespace PgnTools.Views.Tools;

public sealed partial class LichessDownloaderView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(LichessDownloaderViewModel),
            typeof(LichessDownloaderView),
            new PropertyMetadata(null));

    public LichessDownloaderViewModel ViewModel
    {
        get => (LichessDownloaderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public LichessDownloaderView()
    {
        InitializeComponent();
    }
}
