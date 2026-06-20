namespace PgnTools.Views.Tools;

public sealed partial class RemoveDoublesView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(RemoveDoublesViewModel), typeof(RemoveDoublesView), new PropertyMetadata(null));

    public RemoveDoublesViewModel ViewModel
    {
        get => (RemoveDoublesViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public RemoveDoublesView() { InitializeComponent(); }
}
