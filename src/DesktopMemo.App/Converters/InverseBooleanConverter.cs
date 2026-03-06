using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopMemo.App.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // RadioButton 取消选中时写回 false，返回 DoNothing 避免写回 true 引发绑定循环
            return boolValue ? (object)false : System.Windows.Data.Binding.DoNothing;
        }

        return DependencyProperty.UnsetValue;
    }
}
