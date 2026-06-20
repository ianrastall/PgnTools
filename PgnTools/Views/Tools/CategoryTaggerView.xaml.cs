namespace PgnTools.Views.Tools;

public sealed partial class CategoryTaggerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(CategoryTaggerViewModel),
            typeof(CategoryTaggerView),
            new PropertyMetadata(null));

    public CategoryTaggerViewModel ViewModel
    {
        get => (CategoryTaggerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public CategoryTaggerView()
    {
        InitializeComponent();
    }
}
