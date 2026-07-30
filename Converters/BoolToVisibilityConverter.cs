using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GitDailyReport.Converters;

/// <summary>
/// bool → Visibility 转换器（true=Visible, false=Collapsed）
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is true;
        // ConverterParameter="Invert" 时反转
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return (invert ? !boolValue : boolValue) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}

/// <summary>
/// bool → string 转换器，用于按钮文本（显示/隐藏）
/// </summary>
public class BoolToToggleTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isVisible = value is true;
        return isVisible ? "🙈 隐藏" : "👁 显示";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
