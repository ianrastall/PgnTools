namespace PgnTools.Views.Tools;

public sealed partial class PgnSplitterView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(PgnSplitterViewModel), typeof(PgnSplitterView), new PropertyMetadata(null));

    public PgnSplitterViewModel ViewModel
    {
        get => (PgnSplitterViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PgnSplitterView() { InitializeComponent(); }
}
