namespace PgnTools.Views.Tools;

public sealed partial class ChessAnalyzerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ChessAnalyzerViewModel), typeof(ChessAnalyzerView), new PropertyMetadata(null));

    public ChessAnalyzerViewModel ViewModel
    {
        get => (ChessAnalyzerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ChessAnalyzerView() { InitializeComponent(); }
}
