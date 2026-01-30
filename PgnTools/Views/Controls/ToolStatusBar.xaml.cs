namespace PgnTools.Views.Controls;

/// <summary>
/// Shared status bar with progress for tool pages.
/// </summary>
public sealed partial class ToolStatusBar : UserControl
{
    public static readonly DependencyProperty StatusMessageProperty =
        DependencyProperty.Register(
            nameof(StatusMessage),
            typeof(string),
            typeof(ToolStatusBar),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusSeverityProperty =
        DependencyProperty.Register(
            nameof(StatusSeverity),
            typeof(InfoBarSeverity),
            typeof(ToolStatusBar),
            new PropertyMetadata(InfoBarSeverity.Informational));

    public static readonly DependencyProperty StatusDetailProperty =
        DependencyProperty.Register(
            nameof(StatusDetail),
            typeof(string),
            typeof(ToolStatusBar),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsRunningProperty =
        DependencyProperty.Register(
            nameof(IsRunning),
            typeof(bool),
            typeof(ToolStatusBar),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(
            nameof(IsIndeterminate),
            typeof(bool),
            typeof(ToolStatusBar),
            new PropertyMetadata(true));

    public static readonly DependencyProperty ProgressValueProperty =
        DependencyProperty.Register(
            nameof(ProgressValue),
            typeof(double),
            typeof(ToolStatusBar),
            new PropertyMetadata(0d));

    public static readonly DependencyProperty ProgressMaximumProperty =
        DependencyProperty.Register(
            nameof(ProgressMaximum),
            typeof(double),
            typeof(ToolStatusBar),
            new PropertyMetadata(100d));

    public ToolStatusBar()
    {
        InitializeComponent();
    }

    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public InfoBarSeverity StatusSeverity
    {
        get => (InfoBarSeverity)GetValue(StatusSeverityProperty);
        set => SetValue(StatusSeverityProperty, value);
    }

    public string StatusDetail
    {
        get => (string)GetValue(StatusDetailProperty);
        set => SetValue(StatusDetailProperty, value);
    }

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
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
