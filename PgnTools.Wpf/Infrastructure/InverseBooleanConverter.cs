using System.Globalization;
using System.Windows.Data;

namespace PgnTools.Wpf;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag ? !flag : value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag ? !flag : value;
    }
}
