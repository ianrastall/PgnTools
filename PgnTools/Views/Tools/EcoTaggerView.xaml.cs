namespace PgnTools.Views.Tools;

public sealed partial class EcoTaggerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(EcoTaggerViewModel),
            typeof(EcoTaggerView),
            new PropertyMetadata(null));

    public EcoTaggerViewModel ViewModel
    {
        get => (EcoTaggerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public EcoTaggerView()
    {
        InitializeComponent();
    }
}
