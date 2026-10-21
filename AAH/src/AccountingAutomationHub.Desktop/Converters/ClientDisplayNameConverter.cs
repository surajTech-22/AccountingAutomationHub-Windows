using System.Globalization;
using System.Windows.Data;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Converters;

public sealed class ClientDisplayNameConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var clientName = values.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
        var tradeName = values.ElementAtOrDefault(1)?.ToString() ?? string.Empty;
        var legalName = values.ElementAtOrDefault(2)?.ToString() ?? string.Empty;
        var mode = values.ElementAtOrDefault(3)?.ToString();

        return ClientDisplayNameModes.Resolve(
            new ClientProfile
            {
                ClientName = clientName,
                TradeName = tradeName,
                LegalName = legalName
            },
            mode);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
