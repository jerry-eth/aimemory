using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiMemoryManager.Converters;

/// <summary>true → Collapsed,false → Visible(用于"无档案引导卡""空态文案"这类反向显隐)。</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}
