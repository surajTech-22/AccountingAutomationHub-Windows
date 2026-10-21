namespace AccountingAutomationHub.Application.Governance;

public sealed record RecurringDirectLine(string Ledger, string Group, string DrCr, decimal Amount, string CommonLedger = "", bool IsBalanceLine = false, long MasterId = 0, long CommonMasterId = 0);
public sealed record RecurringDirectOccurrence(DateTime Date, decimal Amount, IReadOnlyList<RecurringDirectLine> Lines);
public sealed record RecurringDirectPlan(string PlanId, long ClientId, string FinancialYear, string Module, string AmountMode, decimal Amount, IReadOnlyList<DateTime> Dates, IReadOnlyList<RecurringDirectLine> Lines, string Narration = "", IReadOnlyList<decimal>? FrozenAmounts = null, bool LineAmountsAreTotals = false);
public sealed record RecurringDirectAllocation(DateTime Date, decimal Amount, bool IsManual = false);
public sealed record RecurringDirectAllocationResult(IReadOnlyList<RecurringDirectAllocation> Allocations, decimal TargetAmount, decimal AllocatedAmount, decimal RemainingAmount);

public static class RecurringDirectPlanner
{
    private static readonly string[] FrequencyOrder = ["Daily", "Weekly", "15 Days", "Monthly", "Quarterly", "Half-Yearly", "Yearly"];
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static IReadOnlyList<string> AvailableFrequencies(DateTime start, DateTime end, IEnumerable<DateTime> holidays)
    {
        ValidateRange(start, end);
        var result = new List<string> { "Daily" };
        foreach (var frequency in FrequencyOrder.Skip(1))
        {
            var periods = NaturalPeriods(start.Date, end.Date, frequency);
            if (periods.Count < 2) continue;
            if (TimesOptions(start, end, frequency, holidays).Count == 0) continue;
            result.Add(frequency);
        }
        return result;
    }

    public static IReadOnlyList<int> TimesOptions(DateTime start, DateTime end, string frequency, IEnumerable<DateTime> holidays)
    {
        ValidateRange(start, end);
        frequency = NormalizeFrequency(frequency);
        if (frequency == "Daily") return [1];
        var excluded = HolidaySet(holidays);
        var periods = NaturalPeriods(start.Date, end.Date, frequency);
        if (periods.Count == 0) return [];
        // Start/End may cut through the first or last true calendar period. Those
        // edge fragments must not reduce the Times choices for every full period.
        // Use the largest real in-range period capacity, then let generation create
        // only the dates that actually exist in each partial edge period.
        var maximumCapacity = periods
            .Select(period => EligibleDates(period.Start < start.Date ? start.Date : period.Start,
                period.End > end.Date ? end.Date : period.End, excluded).Count)
            .DefaultIfEmpty(0).Max();
        if (maximumCapacity <= 0) return [];
        if (frequency == "Weekly")
            return Enumerable.Range(1, Math.Min(4, maximumCapacity)).ToArray();
        return Enumerable.Range(1, maximumCapacity)
            .Where(value => IsIrreducibleTimes(frequency, value))
            .ToArray();
    }

    public static (string Frequency, int Times) NormalizeFrequencyTimes(string frequency, int times)
    {
        frequency = NormalizeFrequency(frequency);
        times = Math.Max(1, times);
        for (var guard = 0; guard < 10; guard++)
        {
            switch (frequency)
            {
                case "Weekly" when times > 4:
                    frequency = "Daily"; times = 1; continue;
                case "15 Days" when times % 2 == 0:
                    frequency = "Weekly"; times /= 2; continue;
                case "Monthly" when times % 2 == 0:
                    frequency = "15 Days"; times /= 2; continue;
                case "Quarterly" when times % 3 == 0:
                    frequency = "Monthly"; times /= 3; continue;
                case "Half-Yearly" when times % 2 == 0:
                    frequency = "Quarterly"; times /= 2; continue;
                case "Yearly" when times % 2 == 0:
                    frequency = "Half-Yearly"; times /= 2; continue;
                default:
                    return (frequency, times);
            }
        }
        return (frequency, times);
    }

    public static (DateTime Start, DateTime End) PatternPeriod(DateTime start, DateTime end, string frequency)
    {
        ValidateRange(start, end);
        frequency = NormalizeFrequency(frequency);
        if (frequency == "Daily") return (start.Date, start.Date);
        var periods = NaturalPeriods(start.Date, end.Date, frequency);
        if (periods.Count == 0) return (start.Date, end.Date);
        return (periods[0].Start, periods[0].End);
    }

    public static (DateTime Start, DateTime End) PatternWindow(DateTime start, DateTime end, string frequency)
    {
        ValidateRange(start, end);
        frequency = NormalizeFrequency(frequency);
        if (frequency == "Daily") return (start.Date, start.Date);
        var periods = NaturalPeriods(start.Date, end.Date, frequency);
        if (periods.Count == 0) return (start.Date, end.Date);
        var first = periods[0];
        return (first.Start < start.Date ? start.Date : first.Start,
            first.End > end.Date ? end.Date : first.End);
    }

    public static IReadOnlyList<DateTime> DefaultPatternDates(DateTime start, DateTime end, string frequency, int times, IEnumerable<DateTime> holidays)
    {
        frequency = NormalizeFrequency(frequency);
        if (frequency == "Daily") return [start.Date];
        var excluded = HolidaySet(holidays);
        var window = PatternWindow(start, end, frequency);
        var eligible = EligibleDates(window.Start, window.End, excluded);
        if (eligible.Count < times) throw new InvalidOperationException($"Only {eligible.Count:N0} eligible date(s) are available in the first {frequency} period.");
        if (times <= 1) return [eligible[eligible.Count / 2]];
        var selected = new List<DateTime>();
        for (var i = 0; i < times; i++)
        {
            var position = (int)Math.Round(i * (eligible.Count - 1d) / (times - 1d), MidpointRounding.AwayFromZero);
            var date = eligible[Math.Clamp(position, 0, eligible.Count - 1)];
            if (!selected.Contains(date)) selected.Add(date);
        }
        foreach (var date in eligible)
        {
            if (selected.Count >= times) break;
            if (!selected.Contains(date)) selected.Add(date);
        }
        return selected.OrderBy(value => value).Take(times).ToArray();
    }

    // Backward-compatible R23 entry point. Weekly retains its proved one-random-working-date-per-week behavior.
    // Other frequencies retain the historical anchored-date semantics for saved/legacy callers.
    public static IReadOnlyList<DateTime> Dates(DateTime start, DateTime end, string frequency, IEnumerable<DateTime> holidays, Random? randomSource = null)
    {
        ValidateRange(start, end);
        var excluded = HolidaySet(holidays);
        frequency = NormalizeFrequency(frequency);
        if (frequency == "Weekly")
        {
            var rng = randomSource ?? Random.Shared;
            var weekly = new List<DateTime>();
            foreach (var period in NaturalPeriods(start.Date, end.Date, "Weekly"))
            {
                var actualStart = period.Start < start.Date ? start.Date : period.Start;
                var actualEnd = period.End > end.Date ? end.Date : period.End;
                var eligible = EligibleDates(actualStart, actualEnd, excluded);
                if (eligible.Count > 0) weekly.Add(eligible[rng.Next(eligible.Count)]);
            }
            return weekly.OrderBy(value => value).ToArray();
        }
        if (frequency == "Daily")
            return EligibleDates(start.Date, end.Date, excluded);
        var result = new List<DateTime>();
        var anchor = start.Date;
        for (var i = 0; i < 10000; i++)
        {
            DateTime date = frequency switch
            {
                "15 Days" => anchor.AddDays(15 * i),
                "Monthly" => AnchoredMonth(anchor, i),
                "Quarterly" => AnchoredMonth(anchor, 3 * i),
                "Half-Yearly" => AnchoredMonth(anchor, 6 * i),
                "Yearly" => AnchoredMonth(anchor, 12 * i),
                _ => throw new InvalidOperationException("Choose a supported recurring frequency.")
            };
            if (date > end.Date) return result;
            if (IsEligible(date, start.Date, end.Date, excluded)) result.Add(date);
        }
        throw new InvalidOperationException("The recurring schedule exceeds the supported occurrence limit.");
    }

    public static IReadOnlyList<DateTime> Dates(
        DateTime start, DateTime end, string frequency, int times, string dateMode,
        IReadOnlyList<int>? fixedPeriodOffsets, IEnumerable<DateTime> holidays, Random? randomSource = null)
    {
        ValidateRange(start, end);
        frequency = NormalizeFrequency(frequency);
        if (frequency == "Daily")
            return EligibleDates(start.Date, end.Date, HolidaySet(holidays));
        times = Math.Max(1, times);
        var allowedTimes = TimesOptions(start, end, frequency, holidays);
        if (!allowedTimes.Contains(times))
            throw new InvalidOperationException($"{times:N0} time(s) is not valid for {frequency} inside the selected true calendar periods.");
        var periods = NaturalPeriods(start.Date, end.Date, frequency);
        var excluded = HolidaySet(holidays);
        var rng = randomSource ?? Random.Shared;
        var random = dateMode.Trim().Equals("Random", StringComparison.OrdinalIgnoreCase);
        var offsets = fixedPeriodOffsets?.ToArray() ?? [];
        if (!random && (offsets.Length != times || offsets.Distinct().Count() != offsets.Length || offsets.Any(value => value < 0)))
            throw new InvalidOperationException("Choose one distinct fixed date pattern for every selected Times occurrence.");
        var result = new List<DateTime>();
        foreach (var period in periods)
        {
            var actualStart = period.Start < start.Date ? start.Date : period.Start;
            var actualEnd = period.End > end.Date ? end.Date : period.End;
            var eligible = EligibleDates(actualStart, actualEnd, excluded);
            if (eligible.Count == 0) continue;
            if (random)
            {
                // A partial first/last true period can legitimately contain fewer
                // in-range dates than the selected Times value. Never fabricate an
                // out-of-range date and never reject the whole schedule: use every
                // valid date that exists in that edge period.
                result.AddRange(eligible.OrderBy(_ => rng.Next()).Take(Math.Min(times, eligible.Count)));
                continue;
            }
            var used = new HashSet<DateTime>();
            foreach (var offset in offsets)
            {
                var candidate = period.Start.AddDays(offset);
                if (candidate > period.End) candidate = period.End;
                if (candidate < start.Date || candidate > end.Date) continue;
                var resolved = ResolveFixedDate(candidate, actualStart, actualEnd, excluded, used, rng);
                if (resolved.HasValue)
                {
                    used.Add(resolved.Value);
                    result.Add(resolved.Value);
                }
            }
        }
        return result.Distinct().OrderBy(value => value).ToArray();
    }

    public static RecurringDirectAllocationResult AllocateLumpSum(
        decimal total,
        IReadOnlyList<DateTime> scheduleDates,
        decimal? maximumAmount,
        string maximumMode,
        IReadOnlyList<RecurringDirectAllocation>? manualAllocations = null,
        Random? randomSource = null)
    {
        total = Round(total);
        if (total <= 0m) throw new InvalidOperationException("Enter a positive lump sum.");
        var manual = (manualAllocations ?? []).Select(value => value with { Date = value.Date.Date, Amount = Round(value.Amount), IsManual = true }).ToArray();
        if (manual.Any(value => value.Amount <= 0m)) throw new InvalidOperationException("Manual allocation amounts must be positive.");
        var manualTotal = Round(manual.Sum(value => value.Amount));
        if (manualTotal > total) throw new InvalidOperationException("Manual allocations exceed the lump sum target.");
        var autoTarget = Round(total - manualTotal);
        var dates = scheduleDates.Select(value => value.Date).Distinct().OrderBy(value => value).ToArray();
        var automatic = new List<RecurringDirectAllocation>();
        if (autoTarget > 0m)
        {
            if (dates.Length == 0) return Result(automatic, manual, total);
            if (!maximumAmount.HasValue || maximumAmount.Value <= 0m)
            {
                var values = Distribute(autoTarget, dates.Length, true, randomSource);
                automatic.AddRange(dates.Select((date, index) => new RecurringDirectAllocation(date, values[index])));
            }
            else
            {
                var maximum = Round(maximumAmount.Value);
                if (maximum <= 0m) throw new InvalidOperationException("Maximum Amount must be greater than zero.");
                switch (NormalizeMaximumMode(maximumMode))
                {
                    case "Per Voucher":
                    {
                        var minimumVouchers = checked((int)Math.Ceiling(autoTarget / maximum));
                        var voucherCount = Math.Max(dates.Length, minimumVouchers);
                        var values = EqualDivide(autoTarget, voucherCount);
                        for (var index = 0; index < values.Count; index++)
                            automatic.Add(new RecurringDirectAllocation(dates[index % dates.Length], values[index]));
                        break;
                    }
                    case "Per Day":
                    {
                        var capacity = Round(maximum * dates.Length);
                        var allocated = Math.Min(autoTarget, capacity);
                        var usedDates = PositiveSlotCount(allocated, dates.Length);
                        if (usedDates > 0)
                        {
                            var values = EqualDivide(allocated, usedDates);
                            for (var index = 0; index < values.Count; index++)
                                automatic.Add(new RecurringDirectAllocation(dates[index], values[index]));
                        }
                        break;
                    }
                    case "Per Month":
                    {
                        var months = dates.GroupBy(value => (value.Year, value.Month)).OrderBy(group => group.Key.Year).ThenBy(group => group.Key.Month).ToArray();
                        var monthCapacity = Round(maximum * months.Length);
                        var allocated = Math.Min(autoTarget, monthCapacity);
                        var usedMonths = PositiveSlotCount(allocated, months.Length);
                        if (usedMonths > 0)
                        {
                            var monthAmounts = EqualDivide(allocated, usedMonths);
                            for (var monthIndex = 0; monthIndex < usedMonths; monthIndex++)
                            {
                                var monthDates = months[monthIndex].OrderBy(value => value).ToArray();
                                var usedDates = PositiveSlotCount(monthAmounts[monthIndex], monthDates.Length);
                                var dayAmounts = EqualDivide(monthAmounts[monthIndex], usedDates);
                                for (var dayIndex = 0; dayIndex < dayAmounts.Count; dayIndex++)
                                    automatic.Add(new RecurringDirectAllocation(monthDates[dayIndex], dayAmounts[dayIndex]));
                            }
                        }
                        break;
                    }
                }
            }
        }
        return Result(automatic, manual, total);
    }

    private static RecurringDirectAllocationResult Result(IEnumerable<RecurringDirectAllocation> automatic, IEnumerable<RecurringDirectAllocation> manual, decimal total)
    {
        var allocations = automatic.Concat(manual)
            .Where(value => value.Amount > 0m)
            .OrderBy(value => value.Date).ThenBy(value => value.IsManual ? 1 : 0)
            .ToArray();
        var allocated = Round(allocations.Sum(value => value.Amount));
        var remaining = Round(total - allocated);
        if (remaining < 0m) throw new InvalidOperationException("Recurring allocations exceed the lump sum target.");
        return new RecurringDirectAllocationResult(allocations, total, allocated, remaining);
    }

    private static int PositiveSlotCount(decimal amount, int availableSlots)
    {
        if (amount <= 0m || availableSlots <= 0) return 0;
        var paise = checked((long)(Round(amount) * 100m));
        return (int)Math.Min(availableSlots, paise);
    }

    private static IReadOnlyList<decimal> EqualDivide(decimal total, int count)
    {
        total = Round(total);
        if (count <= 0) return [];
        var paise = checked((long)(total * 100m));
        if (paise < count) throw new InvalidOperationException($"{total:N2} cannot be divided into {count:N0} positive vouchers at paise precision.");
        var baseValue = paise / count;
        var remainder = paise % count;
        var result = new decimal[count];
        for (var index = 0; index < count; index++)
            result[index] = (baseValue + (index < remainder ? 1 : 0)) / 100m;
        return result;
    }

    private static string NormalizeMaximumMode(string value) => value.Trim().ToUpperInvariant() switch
    {
        "PER VOUCHER" => "Per Voucher",
        "PER DAY" => "Per Day",
        "PER MONTH" => "Per Month",
        _ => throw new InvalidOperationException("Choose Maximum Amount as Per Voucher, Per Day or Per Month.")
    };

    private static bool IsIrreducibleTimes(string frequency, int value) => frequency switch
    {
        "15 Days" => value % 2 != 0,
        "Monthly" => value % 2 != 0,
        "Quarterly" => value % 3 != 0,
        "Half-Yearly" => value % 2 != 0,
        "Yearly" => value % 2 != 0,
        _ => true
    };

    private sealed record NaturalPeriod(DateTime Start, DateTime End, string Label);

    private static IReadOnlyList<NaturalPeriod> NaturalPeriods(DateTime start, DateTime end, string frequency)
    {
        frequency = NormalizeFrequency(frequency);
        var result = new List<NaturalPeriod>();
        if (frequency == "Daily")
        {
            for (var date = start.Date; date <= end.Date; date = date.AddDays(1)) result.Add(new(date, date, date.ToString("dd-MMM-yyyy")));
            return result;
        }
        DateTime PeriodStart(DateTime date) => frequency switch
        {
            "Weekly" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            "15 Days" => new DateTime(date.Year, date.Month, date.Day <= 15 ? 1 : 16),
            "Monthly" => new DateTime(date.Year, date.Month, 1),
            "Quarterly" => FiscalQuarterStart(date),
            "Half-Yearly" => FiscalHalfYearStart(date),
            "Yearly" => FiscalYearStart(date),
            _ => throw new InvalidOperationException("Choose a supported recurring frequency.")
        };
        DateTime PeriodEnd(DateTime periodStart) => frequency switch
        {
            "Weekly" => periodStart.AddDays(6),
            "15 Days" => periodStart.Day == 1 ? new DateTime(periodStart.Year, periodStart.Month, 15) : new DateTime(periodStart.Year, periodStart.Month, DateTime.DaysInMonth(periodStart.Year, periodStart.Month)),
            "Monthly" => periodStart.AddMonths(1).AddDays(-1),
            "Quarterly" => periodStart.AddMonths(3).AddDays(-1),
            "Half-Yearly" => periodStart.AddMonths(6).AddDays(-1),
            "Yearly" => periodStart.AddYears(1).AddDays(-1),
            _ => periodStart
        };
        DateTime Next(DateTime periodStart) => frequency switch
        {
            "Weekly" => periodStart.AddDays(7),
            "15 Days" => periodStart.Day == 1 ? new DateTime(periodStart.Year, periodStart.Month, 16) : new DateTime(periodStart.Year, periodStart.Month, 1).AddMonths(1),
            "Monthly" => periodStart.AddMonths(1),
            "Quarterly" => periodStart.AddMonths(3),
            "Half-Yearly" => periodStart.AddMonths(6),
            "Yearly" => periodStart.AddYears(1),
            _ => periodStart.AddDays(1)
        };
        var current = PeriodStart(start.Date);
        for (var guard = 0; guard < 10000 && current <= end.Date; guard++)
        {
            var periodEnd = PeriodEnd(current);
            var label = frequency switch
            {
                "Weekly" => $"Week {current:dd-MMM}–{periodEnd:dd-MMM}",
                "15 Days" => $"{current:dd-MMM}–{periodEnd:dd-MMM-yyyy}",
                "Monthly" => current.ToString("MMM yyyy"),
                "Quarterly" => $"Quarter {current:MMM}–{periodEnd:MMM yyyy}",
                "Half-Yearly" => $"Half-year {current:MMM}–{periodEnd:MMM yyyy}",
                "Yearly" => $"FY {current:yyyy}-{periodEnd:yy}",
                _ => current.ToString("dd-MMM-yyyy")
            };
            result.Add(new(current, periodEnd, label));
            current = Next(current);
        }
        return result;
    }

    private static DateTime FiscalQuarterStart(DateTime date)
    {
        var fyYear = date.Month >= 4 ? date.Year : date.Year - 1;
        var offset = (date.Month - 4 + 12) % 12;
        var quarter = offset / 3;
        return new DateTime(fyYear, 4, 1).AddMonths(quarter * 3);
    }

    private static DateTime FiscalHalfYearStart(DateTime date)
    {
        var fyYear = date.Month >= 4 ? date.Year : date.Year - 1;
        var fyStart = new DateTime(fyYear, 4, 1);
        return date < fyStart.AddMonths(6) ? fyStart : fyStart.AddMonths(6);
    }

    private static DateTime FiscalYearStart(DateTime date) => new(date.Month >= 4 ? date.Year : date.Year - 1, 4, 1);

    private static HashSet<DateTime> HolidaySet(IEnumerable<DateTime> holidays) => holidays.Select(value => value.Date).ToHashSet();

    private static bool IsEligible(DateTime date, DateTime start, DateTime end, HashSet<DateTime> excluded) =>
        date >= start && date <= end &&
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
        !(date.Month == 1 && date.Day == 26) && !(date.Month == 8 && date.Day == 15) &&
        !(date.Month == 10 && date.Day == 2) && !excluded.Contains(date);

    private static List<DateTime> EligibleDates(DateTime start, DateTime end, HashSet<DateTime> excluded)
    {
        var result = new List<DateTime>();
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            if (IsEligible(date, start.Date, end.Date, excluded)) result.Add(date);
        return result;
    }

    private static DateTime? ResolveFixedDate(DateTime candidate, DateTime start, DateTime end, HashSet<DateTime> excluded, HashSet<DateTime> used, Random rng)
    {
        if (IsEligible(candidate, start, end, excluded) && !used.Contains(candidate)) return candidate;
        var eligible = EligibleDates(start, end, excluded).Where(value => !used.Contains(value)).ToArray();
        if (eligible.Length == 0) return null;
        var before = eligible.Where(value => value < candidate).OrderByDescending(value => value).FirstOrDefault();
        var after = eligible.Where(value => value > candidate).OrderBy(value => value).FirstOrDefault();
        var choices = new List<DateTime>();
        if (before != default) choices.Add(before);
        if (after != default) choices.Add(after);
        if (choices.Count == 0) return eligible[rng.Next(eligible.Length)];
        return choices[rng.Next(choices.Count)];
    }

    private static void ValidateRange(DateTime start, DateTime end)
    {
        if (end.Date < start.Date || end.Date > start.Date.AddYears(20)) throw new InvalidOperationException("Invalid recurring date range.");
    }

    private static DateTime AnchoredMonth(DateTime anchor, int months)
    {
        var first = new DateTime(anchor.Year, anchor.Month, 1).AddMonths(months);
        return new DateTime(first.Year, first.Month, Math.Min(anchor.Day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    public static string NormalizeFrequency(string value) => value.Trim().ToUpperInvariant() switch
    {
        "DAILY" => "Daily",
        "WEEKLY" => "Weekly",
        "15 DAYS" or "15DAYS" or "FORTNIGHTLY" => "15 Days",
        "QUARTERLY" => "Quarterly",
        "HALF-YEARLY" or "HALF YEARLY" or "HALFYEARLY" or "HALF YEAR" => "Half-Yearly",
        "YEARLY" => "Yearly",
        _ => "Monthly"
    };

    public static IReadOnlyList<decimal> Distribute(decimal amount, int count, bool random, Random? randomSource = null)
    {
        amount = Round(amount);
        if (count <= 0 || count > 10000 || amount <= 0m) throw new InvalidOperationException("Select dates and enter a positive amount.");
        if (!random) return Enumerable.Repeat(amount, count).ToArray();
        var paise = checked((long)(amount * 100m));
        if (paise < count) throw new InvalidOperationException("The lump sum cannot provide a positive paise amount on every date.");
        if (count == 1) return [amount];
        var rng = randomSource ?? Random.Shared;
        var remaining = paise - count;
        var weights = Enumerable.Range(0, count).Select(_ => 0.2d + rng.NextDouble()).ToArray();
        var weightTotal = weights.Sum();
        var values = new long[count];
        long used = 0;
        for (var i = 0; i < count; i++)
        {
            values[i] = 1 + (long)Math.Floor(remaining * weights[i] / weightTotal);
            used += values[i];
        }
        var order = Enumerable.Range(0, count).OrderBy(_ => rng.Next()).ToArray();
        for (long i = 0; i < paise - used; i++) values[order[(int)(i % count)]]++;
        if (values.Distinct().Count() == 1 && paise >= count + 2) { values[0]++; values[1]--; }
        return values.Select(x => x / 100m).ToArray();
    }

    public static IReadOnlyList<RecurringDirectLine> BalanceJournal(IReadOnlyList<RecurringDirectLine> lines)
    {
        if (lines.Count < 2) throw new InvalidOperationException("A Journal requires at least two lines.");
        if (lines.Count(x => x.IsBalanceLine) > 1) throw new InvalidOperationException("Only one balancing line is permitted.");
        var regular = lines.Where(x => !x.IsBalanceLine).ToArray();
        decimal debit = 0, credit = 0;
        foreach (var line in regular)
        {
            if (line.Amount < 0m || Round(line.Amount) != line.Amount) throw new InvalidOperationException("Enter nonnegative amounts to two decimal places.");
            if (line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)) debit += line.Amount;
            else if (line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)) credit += line.Amount;
            else throw new InvalidOperationException("Each Journal line requires Dr or Cr.");
        }
        var result = regular.ToList();
        var balancing = lines.FirstOrDefault(x => x.IsBalanceLine);
        if (balancing is not null)
        {
            var difference = Round(debit - credit);
            result.Add(balancing with { DrCr = difference >= 0m ? "Cr" : "Dr", Amount = Math.Abs(difference) });
        }
        return result;
    }

    public static IReadOnlyList<RecurringDirectOccurrence> Build(RecurringDirectPlan plan)
    {
        if (plan.ClientId <= 0 || string.IsNullOrWhiteSpace(plan.FinancialYear) || string.IsNullOrWhiteSpace(plan.PlanId)) throw new InvalidOperationException("A valid Client/FY and plan identity are required.");
        var dates = plan.Dates.Select(x => x.Date).ToArray();
        if (dates.Length == 0 || dates.Length > 10000 || !dates.SequenceEqual(dates.OrderBy(x => x))) throw new InvalidOperationException("Select chronological dates. Same-day split vouchers are allowed only in chronological order.");
        var fy = plan.FinancialYear.Split('-', StringSplitOptions.TrimEntries);
        if (fy.Length == 0 || !int.TryParse(fy[0], out var firstYear)) throw new InvalidOperationException("Invalid Financial Year.");
        var from = new DateTime(firstYear, 4, 1);
        var to = from.AddYears(1).AddDays(-1);
        if (dates.Any(x => x < from || x > to)) throw new InvalidOperationException("Recurring dates must belong to the selected Financial Year.");
        var cash = plan.Module.Equals("Cash", StringComparison.OrdinalIgnoreCase);
        var journal = plan.Module.Equals("Journal", StringComparison.OrdinalIgnoreCase);
        if (!cash && !journal) throw new InvalidOperationException("Unsupported recurring module.");
        if (plan.Lines.Count == 0 || (journal && plan.Lines.Count < 2)) throw new InvalidOperationException("Enter the required accounting lines.");
        var sourceLines = journal ? BalanceJournal(plan.Lines) : plan.Lines;
        foreach (var line in sourceLines)
        {
            if (string.IsNullOrWhiteSpace(line.Ledger) || string.IsNullOrWhiteSpace(line.Group)) throw new InvalidOperationException("Every line requires a Ledger and Group.");
            if (cash && string.IsNullOrWhiteSpace(line.CommonLedger)) throw new InvalidOperationException("Every Cash line requires a Cash/Common Ledger.");
            if (line.DrCr is not ("Dr" or "Cr") || line.Amount < 0m || Round(line.Amount) != line.Amount) throw new InvalidOperationException("Invalid Dr/Cr or amount.");
        }
        if (journal && sourceLines.Where(x => x.DrCr == "Dr").Sum(x => x.Amount) != sourceLines.Where(x => x.DrCr == "Cr").Sum(x => x.Amount)) throw new InvalidOperationException("Unmatched Journal cannot be created.");
        var lump = plan.AmountMode.Equals("Lump Sum", StringComparison.OrdinalIgnoreCase);
        if (!lump && !plan.AmountMode.Equals("Fixed Amount", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose Fixed Amount or Lump Sum.");
        var baseAmount = journal ? sourceLines.Where(x => x.DrCr == "Dr").Sum(x => x.Amount) : sourceLines.Sum(x => x.Amount);
        if (baseAmount <= 0m) throw new InvalidOperationException("Enter a positive accounting amount.");
        var total = plan.Amount > 0m ? plan.Amount : baseAmount;
        if (plan.LineAmountsAreTotals && (!lump || total != baseAmount))
            throw new InvalidOperationException("The lump sum must equal the entered line totals.");
        IReadOnlyList<decimal> amounts = plan.FrozenAmounts is { Count: > 0 } ? plan.FrozenAmounts.ToArray() : Distribute(total, dates.Length, lump);
        if (amounts.Count != dates.Length || amounts.Any(x => x <= 0m || Round(x) != x) || (lump && amounts.Sum() != Round(total)) || (!lump && amounts.Any(x => x != Round(total)))) throw new InvalidOperationException("The frozen amount distribution does not match the plan.");
        var result = new List<RecurringDirectOccurrence>();
        var remainingLines = sourceLines.ToArray();
        foreach (var (date, index) in dates.Select((x, i) => (x, i)))
        {
            var allocationLines = plan.LineAmountsAreTotals ? remainingLines : sourceLines;
            IReadOnlyList<RecurringDirectLine> scaled;
            if (journal)
            {
                var numbered = allocationLines.Select((line, i) => (line, i)).ToArray();
                var drValues = ScaleIndexed(numbered.Where(x => x.line.DrCr == "Dr").ToArray(), amounts[index]);
                var crValues = ScaleIndexed(numbered.Where(x => x.line.DrCr == "Cr").ToArray(), amounts[index]);
                scaled = numbered.Select(x => x.line with { Amount = drValues.GetValueOrDefault(x.i, crValues.GetValueOrDefault(x.i)) }).ToArray();
                if (scaled.Where(x => x.DrCr == "Dr").Sum(x => x.Amount) != scaled.Where(x => x.DrCr == "Cr").Sum(x => x.Amount)) throw new InvalidOperationException("Generated Journal is unbalanced.");
            }
            else
            {
                var values = ScaleIndexed(allocationLines.Select((line, i) => (line, i)).ToArray(), amounts[index]);
                scaled = allocationLines.Select((line, i) => line with { Amount = values[i] }).ToArray();
            }
            if (plan.LineAmountsAreTotals)
                remainingLines = remainingLines.Select((line, i) => line with { Amount = line.Amount - scaled[i].Amount }).ToArray();
            result.Add(new RecurringDirectOccurrence(date, amounts[index], scaled));
        }
        return result;
    }

    public static IReadOnlyList<RecurringDirectLine> LegacyLineTotals(IReadOnlyList<RecurringDirectLine> lines, decimal total, bool journal)
    {
        if (lines.Count == 0) return lines;
        if (!journal && lines.Count == 1) return [lines[0] with { Amount = Round(total) }];
        var source = journal ? BalanceJournal(lines) : lines;
        var indexed = source.Select((line, i) => (line, i)).ToArray();
        if (source.Sum(x => x.Amount) <= 0m)
            throw new InvalidOperationException($"The previous total was {total:N2}. Enter a total for each accounting line.");
        var values = journal
            ? ScaleIndexed(indexed.Where(x => x.line.DrCr == "Dr").ToArray(), total)
                .Concat(ScaleIndexed(indexed.Where(x => x.line.DrCr == "Cr").ToArray(), total)).ToDictionary(x => x.Key, x => x.Value)
            : ScaleIndexed(indexed, total);
        return source.Select((line, i) => line with { Amount = values[i] }).ToArray();
    }

    private static Dictionary<int, decimal> ScaleIndexed((RecurringDirectLine line, int i)[] lines, decimal target)
    {
        var total = lines.Sum(x => x.line.Amount);
        if (target == 0m) return lines.ToDictionary(x => x.i, _ => 0m);
        if (total <= 0m) throw new InvalidOperationException("A nonzero accounting side has no source amount.");
        var cents = checked((long)(target * 100m));
        var values = lines.Select(x => (x.i, Exact: cents * x.line.Amount / total)).ToArray();
        var amounts = values.ToDictionary(x => x.i, x => (long)decimal.Floor(x.Exact));
        var remaining = cents - amounts.Values.Sum();
        foreach (var x in values.OrderByDescending(x => x.Exact - decimal.Floor(x.Exact)).ThenBy(x => x.i).Take(checked((int)remaining))) amounts[x.i]++;
        return amounts.ToDictionary(x => x.Key, x => x.Value / 100m);
    }
}
