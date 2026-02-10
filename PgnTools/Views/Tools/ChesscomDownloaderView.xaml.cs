namespace PgnTools.Views.Tools;

public sealed partial class ChesscomDownloaderView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ChesscomDownloaderViewModel),
            typeof(ChesscomDownloaderView),
            new PropertyMetadata(null));

    public ChesscomDownloaderViewModel ViewModel
    {
        get => (ChesscomDownloaderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ChesscomDownloaderView()
    {
        InitializeComponent();
    }
}
