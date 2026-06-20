namespace PgnTools.Views.Tools;

public sealed partial class ChessUnannotatorView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ChessUnannotatorViewModel),
            typeof(ChessUnannotatorView),
            new PropertyMetadata(null));

    public ChessUnannotatorViewModel ViewModel
    {
        get => (ChessUnannotatorViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ChessUnannotatorView()
    {
        InitializeComponent();
    }
}
