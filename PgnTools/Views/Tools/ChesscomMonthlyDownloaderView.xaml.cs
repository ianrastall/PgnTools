using System.ComponentModel;

namespace PgnTools.Views.Tools;

public sealed partial class ChesscomMonthlyDownloaderView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ChesscomMonthlyDownloaderViewModel),
            typeof(ChesscomMonthlyDownloaderView),
            new PropertyMetadata(null, OnViewModelChanged));

    public ChesscomMonthlyDownloaderViewModel ViewModel
    {
        get => (ChesscomMonthlyDownloaderViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ChesscomMonthlyDownloaderView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ChesscomMonthlyDownloaderView)d;

        if (e.OldValue is INotifyPropertyChanged oldNotify)
        {
            oldNotify.PropertyChanged -= view.OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newNotify)
        {
            newNotify.PropertyChanged += view.OnViewModelPropertyChanged;
        }

        view.UpdateLogGridState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChesscomMonthlyDownloaderViewModel.EnableLogging) or null or "")
        {
            UpdateLogGridState();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is INotifyPropertyChanged notify)
        {
            notify.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is INotifyPropertyChanged notify)
        {
            notify.PropertyChanged -= OnViewModelPropertyChanged;
            notify.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateLogGridState();
    }

    private void UpdateLogGridState()
    {
        LogFileContainer.IsEnabled = ViewModel?.EnableLogging ?? false;
    }
}
