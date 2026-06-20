namespace PgnTools.Views.Tools;

public sealed partial class EleganceView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(EleganceViewModel), typeof(EleganceView), new PropertyMetadata(null));
    public EleganceViewModel ViewModel { get => (EleganceViewModel)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    public EleganceView() { InitializeComponent(); }
}
