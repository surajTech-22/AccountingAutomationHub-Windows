using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Models;

public static class ClientDisplayNameModes
{
    public const string ClientName = "Client Name";
    public const string TradeName = "Trade Name";
    public const string LegalName = "Legal Name";

    public static IReadOnlyList<string> All { get; } =
    [
        ClientName,
        TradeName,
        LegalName
    ];

    public static string Normalize(string? value) =>
        value?.Trim() switch
        {
            TradeName => TradeName,
            LegalName => LegalName,
            _ => ClientName
        };

    public static string Resolve(
        ClientProfile? client,
        string? mode)
    {
        if (client is null)
        {
            return "No client selected";
        }

        var preferred = Normalize(mode) switch
        {
            TradeName => client.TradeName,
            LegalName => client.LegalName,
            _ => client.ClientName
        };

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        var fallback = new[]
        {
            client.ClientName,
            client.TradeName,
            client.LegalName,
            client.ClientCode
        }
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return string.IsNullOrWhiteSpace(fallback)
            ? "Unnamed client"
            : fallback.Trim();
    }
}
