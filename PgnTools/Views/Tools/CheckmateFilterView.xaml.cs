namespace PgnTools.Views.Tools;

public sealed partial class CheckmateFilterView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(CheckmateFilterViewModel), typeof(CheckmateFilterView), new PropertyMetadata(null));
    public CheckmateFilterViewModel ViewModel { get => (CheckmateFilterViewModel)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    public CheckmateFilterView() { InitializeComponent(); }
}
