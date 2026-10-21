using System.Globalization;
using System.Windows.Data;

namespace AccountingAutomationHub.Desktop.Converters;

public sealed class ZeroBlankDecimalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        try
        {
            var number = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return Math.Abs(number) < 0.0000005m ? string.Empty : number.ToString("N2", culture);
        }
        catch { return value.ToString() ?? string.Empty; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
