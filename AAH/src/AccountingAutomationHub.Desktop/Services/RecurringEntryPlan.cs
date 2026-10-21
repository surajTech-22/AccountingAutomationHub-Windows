namespace AccountingAutomationHub.Desktop.Services;

public sealed class RecurringEntryPlan
{
    public string Module { get; init; } = string.Empty;
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<decimal> DateAmounts { get; init; } = [];
    public string AmountMode { get; init; } = "Fixed Amount";
    public string Frequency { get; init; } = "Monthly";
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public string Narration { get; init; } = string.Empty;
    public IReadOnlyList<DateTime> Dates { get; init; } = [];
    public IReadOnlyList<RecurringEntryLinePlan> Lines { get; init; } = [];
}

public sealed class RecurringEntryLinePlan
{
    public string Ledger { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string DrCr { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string CommonLedger { get; init; } = string.Empty;
    public bool IsBalanceLine { get; init; }
}

public static class RecurringAmountDistribution
{
    public static IReadOnlyList<decimal> Fixed(decimal amount, int count)
    {
        if (amount <= 0m) throw new InvalidOperationException("Enter an amount greater than zero.");
        if (count <= 0) return [];
        var rounded = Round(amount);
        return Enumerable.Repeat(rounded, count).ToArray();
    }

    public static IReadOnlyList<decimal> RandomDivide(decimal total, int count)
    {
        total = Round(total);
        if (total <= 0m) throw new InvalidOperationException("Enter an amount greater than zero.");
        if (count <= 0) return [];
        var cents = checked((long)Math.Round(total * 100m, 0, MidpointRounding.AwayFromZero));
        if (cents < count)
            throw new InvalidOperationException($"{total:N2} cannot be divided into {count:N0} positive entries at paise precision.");
        if (count == 1) return [total];

        // One paise is reserved for every occurrence. The remaining cents are
        // allocated with random weights, then any exact remainder is scattered.
        var remaining = cents - count;
        var weights = Enumerable.Range(0, count).Select(_ => 0.20d + Random.Shared.NextDouble()).ToArray();
        var weightTotal = weights.Sum();
        var allocated = new long[count];
        long used = 0;
        for (var i = 0; i < count; i++)
        {
            allocated[i] = 1 + (long)Math.Floor(remaining * (weights[i] / weightTotal));
            used += allocated[i];
        }
        var remainder = cents - used;
        var order = Enumerable.Range(0, count).OrderBy(_ => Random.Shared.Next()).ToArray();
        for (long i = 0; i < remainder; i++) allocated[order[(int)(i % count)]]++;

        // Avoid an accidentally equal-looking lump-sum series where practical.
        if (count > 1 && allocated.Distinct().Count() == 1 && cents >= count + 2)
        {
            allocated[0]++;
            allocated[1]--;
        }
        return allocated.Select(value => value / 100m).ToArray();
    }

    public static IReadOnlyList<DateTime> GenerateDates(
        DateTime start,
        DateTime end,
        string frequency,
        IReadOnlyList<DateTime> manualHolidays)
    {
        if (end.Date < start.Date) throw new InvalidOperationException("End Date cannot be before Start Date.");
        var manual = manualHolidays.Select(value => value.Date).ToHashSet();
        var result = new List<DateTime>();
        var current = start.Date;
        while (current <= end.Date)
        {
            if (AdvancedCloneScheduleService.IsWorkingDay(current, manual) && !result.Contains(current))
                result.Add(current);
            current = NormalizeFrequency(frequency) switch
            {
                "Daily" => current.AddDays(1),
                "Weekly" => current.AddDays(7),
                "15 Days" => current.AddDays(15),
                "Quarterly" => current.AddMonths(3),
                "Yearly" => current.AddYears(1),
                _ => current.AddMonths(1)
            };
        }
        return result;
    }

    public static string NormalizeFrequency(string value) => value.Trim().ToUpperInvariant() switch
    {
        "DAILY" => "Daily",
        "WEEKLY" => "Weekly",
        "15 DAYS" or "15DAYS" or "FORTNIGHTLY" => "15 Days",
        "QUARTERLY" => "Quarterly",
        "YEARLY" => "Yearly",
        _ => "Monthly"
    };

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
