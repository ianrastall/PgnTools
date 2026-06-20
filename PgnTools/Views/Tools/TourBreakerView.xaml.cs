namespace PgnTools.Views.Tools;

public sealed partial class TourBreakerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(TourBreakerViewModel), typeof(TourBreakerView), new PropertyMetadata(null));

    public TourBreakerViewModel ViewModel
    {
        get => (TourBreakerViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public TourBreakerView() { InitializeComponent(); }
}
