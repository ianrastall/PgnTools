namespace PgnTools.Views.Tools;

public sealed partial class StockfishNormalizerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(StockfishNormalizerViewModel),
            typeof(StockfishNormalizerView),
            new PropertyMetadata(null));

    public StockfishNormalizerViewModel ViewModel
    {
        get => (StockfishNormalizerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public StockfishNormalizerView()
    {
        InitializeComponent();
    }
}
