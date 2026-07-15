using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NativeCodexAssistant.App.Services;

public sealed class BooleanToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            return new GridLength(0);
        }

        return double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            ? new GridLength(width)
            : GridLength.Auto;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
