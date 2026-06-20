using System.Windows;
using System.Windows.Controls;

namespace PgnTools.Wpf.Controls;

public partial class ToolStatusView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty StatusMessageProperty =
        DependencyProperty.Register(nameof(StatusMessage), typeof(string), typeof(ToolStatusView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusDetailProperty =
        DependencyProperty.Register(nameof(StatusDetail), typeof(string), typeof(ToolStatusView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusSeverityProperty =
        DependencyProperty.Register(nameof(StatusSeverity), typeof(InfoBarSeverity), typeof(ToolStatusView), new PropertyMetadata(InfoBarSeverity.Informational));

    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(ToolStatusView), new PropertyMetadata(false));

    public static readonly DependencyProperty ProgressValueProperty =
        DependencyProperty.Register(nameof(ProgressValue), typeof(double), typeof(ToolStatusView), new PropertyMetadata(0d));

    public static readonly DependencyProperty ProgressMaximumProperty =
        DependencyProperty.Register(nameof(ProgressMaximum), typeof(double), typeof(ToolStatusView), new PropertyMetadata(100d));

    public ToolStatusView()
    {
        InitializeComponent();
    }

    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public string StatusDetail
    {
        get => (string)GetValue(StatusDetailProperty);
        set => SetValue(StatusDetailProperty, value);
    }

    public InfoBarSeverity StatusSeverity
    {
        get => (InfoBarSeverity)GetValue(StatusSeverityProperty);
        set => SetValue(StatusSeverityProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public double ProgressValue
    {
        get => (double)GetValue(ProgressValueProperty);
        set => SetValue(ProgressValueProperty, value);
    }

    public double ProgressMaximum
    {
        get => (double)GetValue(ProgressMaximumProperty);
        set => SetValue(ProgressMaximumProperty, value);
    }
}
