using System.Globalization;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class AdvancedCloneConfiguration
{
    public string AmountMode { get; set; } = "Specific Amount";
    public decimal Amount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string BasicFrequency { get; set; } = "Monthly";
    public bool UseEveryNDays { get; set; }
    public int EveryNDays { get; set; } = 1;
    public bool UseTotalTransactions { get; set; }
    public int TotalTransactions { get; set; } = 1;
    public bool UseDistribution { get; set; }
    public string DistributionPeriod { get; set; } = "Monthly";
    public int TransactionsPerPeriod { get; set; } = 1;
    public bool OverrideAdvancedRules { get; set; }
    public IReadOnlyList<DateTime> ManualHolidays { get; set; } = [];
}

public readonly record struct CloneScheduleEntry(DateTime Date, decimal Amount);

public static class AdvancedCloneScheduleService
{
    public static IReadOnlyList<CloneScheduleEntry> Generate(AdvancedCloneConfiguration configuration)
    {
        if (configuration.EndDate.Date < configuration.StartDate.Date)
            throw new InvalidOperationException("End Date cannot be before Start Date.");
        if (configuration.Amount <= 0m)
            throw new InvalidOperationException("Enter an amount greater than zero.");

        var specific = configuration.AmountMode.Equals("Specific Amount", StringComparison.OrdinalIgnoreCase);
        var dates = specific
            ? GenerateSpecificDates(configuration)
            : GenerateBasicDates(configuration.StartDate.Date, configuration.EndDate.Date, configuration.BasicFrequency, configuration.ManualHolidays);
        if (dates.Count == 0)
            throw new InvalidOperationException("No eligible working dates were found in the selected range.");

        if (specific)
            return dates.Select(date => new CloneScheduleEntry(date, Round(configuration.Amount))).ToArray();

        var total = Round(configuration.Amount);
        var each = Round(total / dates.Count);
        var entries = new List<CloneScheduleEntry>(dates.Count);
        decimal allocated = 0m;
        for (var i = 0; i < dates.Count; i++)
        {
            var amount = i == dates.Count - 1 ? Round(total - allocated) : each;
            allocated += amount;
            entries.Add(new CloneScheduleEntry(dates[i], amount));
        }
        return entries;
    }

    private static IReadOnlyList<DateTime> GenerateSpecificDates(AdvancedCloneConfiguration c)
    {
        var start = c.StartDate.Date;
        var end = c.EndDate.Date;
        var holidays = c.ManualHolidays;

        if (c.UseEveryNDays)
        {
            if (c.EveryNDays <= 0) throw new InvalidOperationException("Every N Days must be at least 1.");
            var result = new List<DateTime>();
            var raw = start;
            while (raw <= end)
            {
                var moved = NextWorkingDay(raw, end, holidays);
                if (moved <= end && (!result.Contains(moved) || c.OverrideAdvancedRules)) result.Add(moved);
                if (c.UseTotalTransactions && result.Count >= c.TotalTransactions) break;
                raw = raw.AddDays(c.EveryNDays);
            }
            if (c.UseTotalTransactions && result.Count < c.TotalTransactions && !c.OverrideAdvancedRules)
                throw new InvalidOperationException($"Only {result.Count:N0} eligible interval date(s) fit the selected range. Reduce Total Transactions, increase the date range, or use Override Advanced Rules for this client.");
            if (c.UseTotalTransactions && result.Count < c.TotalTransactions && c.OverrideAdvancedRules)
            {
                var fallback = WorkingDates(start, end, holidays).ToArray();
                if (fallback.Length == 0) return result;
                var index = 0;
                while (result.Count < c.TotalTransactions) result.Add(fallback[index++ % fallback.Length]);
            }
            return c.UseTotalTransactions ? result.Take(Math.Max(0, c.TotalTransactions)).OrderBy(date => date).ToArray() : result.OrderBy(date => date).ToArray();
        }

        if (c.UseDistribution)
        {
            if (c.TransactionsPerPeriod <= 0) throw new InvalidOperationException("Transactions per period must be at least 1.");
            var result = new List<DateTime>();
            foreach (var period in Periods(start, end, c.DistributionPeriod))
            {
                var candidates = WorkingDates(period.Start, period.End, holidays).ToList();
                if (candidates.Count < c.TransactionsPerPeriod && !c.OverrideAdvancedRules)
                    throw new InvalidOperationException($"{period.Label} has only {candidates.Count:N0} eligible working day(s), so {c.TransactionsPerPeriod:N0} transactions cannot be scheduled for that {c.DistributionPeriod.ToLowerInvariant()} period.");
                Shuffle(candidates);
                if (candidates.Count > 0)
                {
                    for (var i = 0; i < c.TransactionsPerPeriod; i++)
                    {
                        if (i < candidates.Count) result.Add(candidates[i]);
                        else if (c.OverrideAdvancedRules) result.Add(candidates[i % candidates.Count]);
                    }
                }
            }
            result.Sort();
            if (c.UseTotalTransactions)
            {
                if (c.TotalTransactions <= 0) throw new InvalidOperationException("Total Transactions must be at least 1.");
                if (result.Count < c.TotalTransactions && !c.OverrideAdvancedRules)
                    throw new InvalidOperationException($"The selected distribution creates only {result.Count:N0} transaction(s). Reduce Total Transactions or use Override Advanced Rules.");
                if (result.Count > c.TotalTransactions) result = SpreadTake(result, c.TotalTransactions).ToList();
            }
            return result;
        }

        if (c.UseTotalTransactions)
        {
            if (c.TotalTransactions <= 0) throw new InvalidOperationException("Total Transactions must be at least 1.");
            return RandomAcrossMonths(start, end, c.TotalTransactions, holidays, c.OverrideAdvancedRules);
        }

        return GenerateBasicDates(start, end, c.BasicFrequency, holidays);
    }

    private static IReadOnlyList<DateTime> GenerateBasicDates(DateTime start, DateTime end, string frequency, IReadOnlyList<DateTime> holidays)
    {
        var dates = new List<DateTime>();
        var current = start;
        while (current <= end)
        {
            var moved = NextWorkingDay(current, end, holidays);
            if (moved <= end && !dates.Contains(moved)) dates.Add(moved);
            current = frequency.Trim().ToUpperInvariant() switch
            {
                "DAILY" => current.AddDays(1),
                "WEEKLY" => current.AddDays(7),
                "15 DAYS" or "15DAYS" or "FORTNIGHTLY" => current.AddDays(15),
                "QUARTERLY" => current.AddMonths(3),
                "HALF-YEARLY" or "HALF YEARLY" => current.AddMonths(6),
                _ => current.AddMonths(1)
            };
        }
        return dates.OrderBy(date => date).ToArray();
    }

    private static IReadOnlyList<DateTime> RandomAcrossMonths(DateTime start, DateTime end, int count, IReadOnlyList<DateTime> holidays, bool allowDuplicates)
    {
        var groups = WorkingDates(start, end, holidays)
            .GroupBy(date => new DateTime(date.Year, date.Month, 1))
            .OrderBy(group => group.Key)
            .Select(group => group.ToList())
            .ToList();
        if (groups.Count == 0) return [];
        foreach (var group in groups) Shuffle(group);
        var totalUnique = groups.Sum(group => group.Count);
        if (count > totalUnique && !allowDuplicates)
            throw new InvalidOperationException($"Only {totalUnique:N0} eligible working date(s) exist in the selected range. Reduce Total Transactions or use Override Advanced Rules for this client.");

        var result = new List<DateTime>(count);
        var monthIndex = 0;
        var positions = new int[groups.Count];
        while (result.Count < count)
        {
            var attempts = 0;
            var added = false;
            while (attempts < groups.Count)
            {
                var index = monthIndex++ % groups.Count;
                attempts++;
                if (groups[index].Count == 0) continue;
                if (positions[index] < groups[index].Count)
                {
                    result.Add(groups[index][positions[index]++]);
                    added = true;
                    break;
                }
                if (allowDuplicates)
                {
                    result.Add(groups[index][positions[index]++ % groups[index].Count]);
                    added = true;
                    break;
                }
            }
            if (!added) break;
        }
        return result.OrderBy(date => date).ToArray();
    }

    private static IReadOnlyList<DateTime> SpreadTake(IReadOnlyList<DateTime> source, int count)
    {
        if (count >= source.Count) return source.ToArray();
        if (count <= 0) return [];
        if (count == 1) return [source[source.Count / 2]];
        var result = new List<DateTime>(count);
        for (var i = 0; i < count; i++)
        {
            var index = (int)Math.Round(i * (source.Count - 1d) / (count - 1d), MidpointRounding.AwayFromZero);
            result.Add(source[Math.Clamp(index, 0, source.Count - 1)]);
        }
        return result;
    }

    private static IEnumerable<DateTime> WorkingDates(DateTime start, DateTime end, IReadOnlyList<DateTime> manualHolidays)
    {
        var manual = manualHolidays.Select(date => date.Date).ToHashSet();
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            if (IsWorkingDay(date, manual)) yield return date;
    }

    public static int MaximumTransactionsPerPeriod(DateTime start, DateTime end, string period, IReadOnlyList<DateTime> manualHolidays)
    {
        if (end.Date < start.Date) return 0;
        var counts = Periods(start.Date, end.Date, period)
            .Select(window => WorkingDates(window.Start, window.End, manualHolidays).Count())
            .ToArray();
        return counts.Length == 0 ? 0 : counts.Min();
    }

    public static bool IsWorkingDay(DateTime date, IEnumerable<DateTime> manualHolidays)
    {
        var manual = manualHolidays.Select(value => value.Date).ToHashSet();
        return IsWorkingDay(date.Date, manual);
    }

    private static bool IsWorkingDay(DateTime date, HashSet<DateTime> manual)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if ((date.Month == 1 && date.Day == 26) || (date.Month == 8 && date.Day == 15) || (date.Month == 10 && date.Day == 2)) return false;
        return !manual.Contains(date.Date);
    }

    public static DateTime NextWorkingDay(DateTime date, DateTime end, IReadOnlyList<DateTime> manualHolidays)
    {
        var manual = manualHolidays.Select(value => value.Date).ToHashSet();
        var result = date.Date;
        while (result <= end.Date && !IsWorkingDay(result, manual)) result = result.AddDays(1);
        return result;
    }

    private static IEnumerable<(DateTime Start, DateTime End, string Label)> Periods(DateTime start, DateTime end, string period)
    {
        var normalized = period.Trim().ToUpperInvariant();
        if (normalized == "WEEKLY")
        {
            var current = start.Date;
            while (current <= end)
            {
                var pEnd = current.AddDays(6) > end ? end : current.AddDays(6);
                yield return (current, pEnd, $"Week {current:dd-MMM} to {pEnd:dd-MMM-yyyy}");
                current = pEnd.AddDays(1);
            }
            yield break;
        }

        var months = normalized switch { "QUARTERLY" => 3, "HALF-YEARLY" or "HALF YEARLY" => 6, _ => 1 };
        var cursor = new DateTime(start.Year, start.Month, 1);
        while (cursor <= end)
        {
            var pStart = cursor < start ? start : cursor;
            var rawEnd = cursor.AddMonths(months).AddDays(-1);
            var pEnd = rawEnd > end ? end : rawEnd;
            yield return (pStart, pEnd, months == 1 ? cursor.ToString("MMM yyyy", CultureInfo.InvariantCulture) : $"{pStart:MMM yyyy} to {pEnd:MMM yyyy}");
            cursor = cursor.AddMonths(months);
        }
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
