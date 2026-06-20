namespace PgnTools.Views.Tools;

public sealed partial class PgnJoinerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(PgnJoinerViewModel), typeof(PgnJoinerView), new PropertyMetadata(null));

    public PgnJoinerViewModel ViewModel
    {
        get => (PgnJoinerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PgnJoinerView() { InitializeComponent(); }
}
