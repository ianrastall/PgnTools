namespace PgnTools.Views.Tools;

public sealed partial class PgnSorterView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(PgnSorterViewModel), typeof(PgnSorterView), new PropertyMetadata(null));

    public PgnSorterViewModel ViewModel
    {
        get => (PgnSorterViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PgnSorterView() { InitializeComponent(); }
}
