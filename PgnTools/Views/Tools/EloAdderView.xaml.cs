namespace PgnTools.Views.Tools;

public sealed partial class EloAdderView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(EloAdderViewModel),
            typeof(EloAdderView),
            new PropertyMetadata(null));

    public EloAdderViewModel ViewModel
    {
        get => (EloAdderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public EloAdderView()
    {
        InitializeComponent();
    }
}
