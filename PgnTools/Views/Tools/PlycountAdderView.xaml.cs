namespace PgnTools.Views.Tools;

public sealed partial class PlycountAdderView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(PlycountAdderViewModel),
            typeof(PlycountAdderView),
            new PropertyMetadata(null));

    public PlycountAdderViewModel ViewModel
    {
        get => (PlycountAdderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PlycountAdderView()
    {
        InitializeComponent();
    }
}
