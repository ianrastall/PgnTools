namespace PgnTools.Views.Tools;

public sealed partial class FilterView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(FilterViewModel),
            typeof(FilterView),
            new PropertyMetadata(null));

    public FilterViewModel ViewModel
    {
        get => (FilterViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public FilterView()
    {
        InitializeComponent();
    }
}
