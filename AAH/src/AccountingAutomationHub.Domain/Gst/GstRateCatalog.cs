namespace AccountingAutomationHub.Domain.Gst;

public static class GstRateCatalog
{
    private static readonly decimal[] Rates =
    [
        0m,
        0.1m,
        0.25m,
        1m,
        1.5m,
        3m,
        5m,
        6m,
        7.5m,
        12m,
        18m,
        28m,
        40m
    ];

    public static IReadOnlyList<decimal> AllowedRates => Rates;

    public static bool TryNormalize(decimal value, out decimal normalized)
    {
        normalized = Rates
            .OrderBy(candidate => Math.Abs(candidate - value))
            .First();
        return Math.Abs(normalized - value) <= 0.005m;
    }


    public static bool TryDeriveFromTaxAmounts(
        decimal taxableValue,
        decimal igst,
        decimal cgst,
        decimal sgst,
        out decimal rate)
    {
        rate = 0m;
        if (Math.Abs(taxableValue) <= 0.005m) return false;

        var totalTax = Math.Abs(igst) + Math.Abs(cgst) + Math.Abs(sgst);
        if (totalTax <= 0.005m) return false;

        var derived = Math.Abs(totalTax / taxableValue * 100m);
        var nearest = Rates.OrderBy(candidate => Math.Abs(candidate - derived)).First();
        if (Math.Abs(nearest - derived) > 0.15m) return false;

        var expectedTotalTax = Math.Abs(taxableValue) * nearest / 100m;
        var allowedTaxVariance = Math.Max(0.50m, Math.Abs(taxableValue) * 0.000005m);
        if (Math.Abs(expectedTotalTax - totalTax) > allowedTaxVariance) return false;

        var hasIgst = Math.Abs(igst) > 0.005m;
        var hasCgst = Math.Abs(cgst) > 0.005m;
        var hasSgst = Math.Abs(sgst) > 0.005m;
        if (hasIgst && (hasCgst || hasSgst)) return false;
        if (!hasIgst && hasCgst != hasSgst) return false;
        if (!hasIgst && hasCgst && Math.Abs(Math.Abs(cgst) - Math.Abs(sgst)) > 0.05m) return false;

        rate = nearest;
        return true;
    }

    public static bool IsAllowed(decimal value) =>
        TryNormalize(value, out _);
}
