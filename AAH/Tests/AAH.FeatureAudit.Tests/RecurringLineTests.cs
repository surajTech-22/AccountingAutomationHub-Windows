using AccountingAutomationHub.Application.Governance;
internal static class RecurringLineTests
{
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var dates = Enumerable.Range(0, 53).Select(i => new DateTime(2025, 4, 1).AddDays(i * 6)).ToArray();
        var cashLines = new[] { new RecurringDirectLine("Freight", "Indirect Expenses", "Dr", 447910m, "Cash"), new RecurringDirectLine("Rounding", "Indirect Expenses", "Cr", 0.07m, "Cash") };
        foreach (var seed in new[] { 1, 33, 91 })
        {
            var amounts = RecurringDirectPlanner.Distribute(447910.07m, dates.Length, true, new Random(seed));
            var plan = new RecurringDirectPlan("lines", 1, "2025-26", "Cash", "Lump Sum", 447910.07m, dates, cashLines, FrozenAmounts: amounts, LineAmountsAreTotals: true);
            var rows = RecurringDirectPlanner.Build(plan);
            for (int i = 0; i < cashLines.Length; i++)
                Check(rows.Sum(x => x.Lines[i].Amount) == cashLines[i].Amount, "Lump sum changed a ledger total.");
            Check(rows.All(x => x.Lines.All(l => l.Amount >= 0) && x.Lines.Sum(l => l.Amount) == x.Amount), "Invalid per-date allocation.");
            Check(RecurringDirectPlanner.Build(plan).SelectMany(x => x.Lines.Select(l => l.Amount)).SequenceEqual(rows.SelectMany(x => x.Lines.Select(l => l.Amount))), "Frozen preview changed.");
        }
        var journalLines = new[] { new RecurringDirectLine("A", "Expenses", "Dr", 3.01m), new RecurringDirectLine("B", "Expenses", "Dr", 6.99m), new RecurringDirectLine("C", "Liabilities", "Cr", 10m) };
        var journal = new RecurringDirectPlan("journal-lines", 1, "2025-26", "Journal", "Lump Sum", 10m, dates, journalLines, FrozenAmounts: RecurringDirectPlanner.Distribute(10m, 53, true, new Random(7)), LineAmountsAreTotals: true);
        var built = RecurringDirectPlanner.Build(journal);
        for (int i = 0; i < journalLines.Length; i++) Check(built.Sum(x => x.Lines[i].Amount) == journalLines[i].Amount, "Journal ledger total drifted.");
        Check(built.All(x => x.Lines.Where(l => l.DrCr == "Dr").Sum(l => l.Amount) == x.Lines.Where(l => l.DrCr == "Cr").Sum(l => l.Amount)), "Journal date is not balanced.");
        var fixedRows = RecurringDirectPlanner.Build(journal with { AmountMode = "Fixed Amount", FrozenAmounts = null, LineAmountsAreTotals = false });
        Check(fixedRows.All(x => x.Lines.Select(l => l.Amount).SequenceEqual(journalLines.Select(l => l.Amount))) && fixedRows.Sum(x => x.Amount) == 530m, "Fixed amounts were divided instead of repeated.");
        Check(RecurringDirectPlanner.LegacyLineTotals([cashLines[0] with { Amount = 0m }],447910m,false)[0].Amount == 447910m, "Earlier single-line total was lost.");

        var start = new DateTime(2025, 4, 2); var end = new DateTime(2025, 5, 6);
        var holidays = Enumerable.Range(0,5).Select(i => new DateTime(2025,4,7).AddDays(i)).ToHashSet();
        DateTime Week(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        var expectedWeeks = Enumerable.Range(0,(end-start).Days+1).Select(i=>start.AddDays(i)).Where(d=>d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d)).Select(Week).Distinct().ToArray();
        var allChoices = new HashSet<DateTime>();
        for (int seed=0; seed<20; seed++)
        {
            var weekly = RecurringDirectPlanner.Dates(start,end,"Weekly",holidays,new Random(seed));
            Check(weekly.Select(Week).SequenceEqual(expectedWeeks), "Weekly schedule omitted or duplicated an eligible calendar week.");
            Check(weekly.All(d=>d>=start && d<=end && !holidays.Contains(d) && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)), "Weekly date violated limits/exclusions.");
            allChoices.UnionWith(weekly);
        }
        Check(allChoices.Count > expectedWeeks.Length, "Weekly dates remained fixed to the anchor weekday.");


        // R21 true-calendar recurrence availability: do not derive frequency by dividing day counts.
        var aprToJun = RecurringDirectPlanner.AvailableFrequencies(new DateTime(2025,4,1), new DateTime(2025,6,30), []);
        Check(aprToJun.Contains("Monthly"), "Apr-Jun must expose its three true calendar months.");
        Check(!aprToJun.Contains("Quarterly") && !aprToJun.Contains("Half-Yearly") && !aprToJun.Contains("Yearly"), "A single Apr-Jun quarter incorrectly exposed a broader recurring frequency.");
        var aprToSep = RecurringDirectPlanner.AvailableFrequencies(new DateTime(2025,4,1), new DateTime(2025,9,30), []);
        Check(aprToSep.Contains("Quarterly") && !aprToSep.Contains("Half-Yearly") && !aprToSep.Contains("Yearly"), "True FY-quarter/half-year availability is wrong.");
        var fullFy = RecurringDirectPlanner.AvailableFrequencies(new DateTime(2025,4,1), new DateTime(2026,3,31), []);
        Check(fullFy.Contains("Quarterly") && fullFy.Contains("Half-Yearly") && !fullFy.Contains("Yearly"), "Full-FY natural-period availability is wrong.");

        // Times options expose only density that is not an exact denser-frequency duplicate.
        var weeklyTimes = RecurringDirectPlanner.TimesOptions(new DateTime(2025,4,1), new DateTime(2025,6,30), "Weekly", []);
        Check(weeklyTimes.SequenceEqual(new[]{1,2,3,4}), "Weekly must stop at four Times.");
        var weeklyEdgeRandom = RecurringDirectPlanner.Dates(new DateTime(2025,4,1),new DateTime(2025,6,30),"Weekly",4,"Random",[],[],new Random(17));
        Check(weeklyEdgeRandom.All(x=>x>=new DateTime(2025,4,1) && x<=new DateTime(2025,6,30)), "Weekly partial edge period generated an out-of-range date.");
        Check(weeklyEdgeRandom.GroupBy(Week).All(g=>g.Count()<=4), "Weekly partial edge period exceeded four Times.");
        var fifteenTimes = RecurringDirectPlanner.TimesOptions(new DateTime(2025,4,1), new DateTime(2025,6,30), "15 Days", []);
        Check(fifteenTimes.All(x=>x%2==1) && fifteenTimes.Contains(5) && fifteenTimes.Contains(7), "15 Days must retain odd non-reducible Times such as 5 and 7.");
        var monthlyTimes = RecurringDirectPlanner.TimesOptions(new DateTime(2025,4,1), new DateTime(2025,6,30), "Monthly", []);
        Check(monthlyTimes.All(x=>x%2==1) && monthlyTimes.Contains(5), "Monthly reducible even Times were not removed.");
        var quarterTimes = RecurringDirectPlanner.TimesOptions(new DateTime(2025,4,1), new DateTime(2025,9,30), "Quarterly", []);
        Check(quarterTimes.All(x=>x%3!=0) && quarterTimes.Contains(2) && quarterTimes.Contains(5), "Quarterly must remove exact Monthly multiples only.");
        Check(RecurringDirectPlanner.NormalizeFrequencyTimes("Weekly",5)==("Daily",1), "Weekly x5 did not normalize to Daily.");
        Check(RecurringDirectPlanner.NormalizeFrequencyTimes("15 Days",12)==("Daily",1), "15 Days x12 did not collapse through Weekly to Daily.");
        Check(RecurringDirectPlanner.NormalizeFrequencyTimes("Monthly",6)==("15 Days",3), "Monthly x6 did not normalize to 15 Days x3.");
        Check(RecurringDirectPlanner.NormalizeFrequencyTimes("Quarterly",6)==("15 Days",1), "Quarterly x6 did not normalize through Monthly/15 Days.");

        // Random is generalized beyond Weekly and remains inside each true natural period.
        var monthlyRandom = RecurringDirectPlanner.Dates(new DateTime(2025,4,1),new DateTime(2025,6,30),"Monthly",3,"Random",[],[],new Random(91));
        Check(monthlyRandom.Count==9 && monthlyRandom.GroupBy(x=>(x.Year,x.Month)).All(g=>g.Count()==3), "Monthly Random did not select Times independently in each true month.");
        Check(monthlyRandom.All(x=>x.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)), "Generalized Random selected a weekend.");
        var fifteenRandom = RecurringDirectPlanner.Dates(new DateTime(2025,4,1),new DateTime(2025,4,30),"15 Days",5,"Random",[],[],new Random(33));
        Check(fifteenRandom.Count==10 && fifteenRandom.Count(x=>x.Day<=15)==5 && fifteenRandom.Count(x=>x.Day>=16)==5, "15 Days did not use true 1-15 / 16-month-end periods.");

        // Fixed pattern offsets repeat by the true period, with exclusion repair staying inside the period.
        var fixedMonthly = RecurringDirectPlanner.Dates(new DateTime(2025,4,1),new DateTime(2025,6,30),"Monthly",3,"Fixed",[4,14,24],[],new Random(7));
        Check(fixedMonthly.Count==9 && fixedMonthly.GroupBy(x=>(x.Year,x.Month)).All(g=>g.Count()==3), "Monthly fixed multiple-date pattern was not repeated in every true month.");

        // Lump Sum maximum modes and manual override.
        var allocationDates = new[]{new DateTime(2025,4,4),new DateTime(2025,4,14),new DateTime(2025,4,24)};
        var perVoucher = RecurringDirectPlanner.AllocateLumpSum(1000m,allocationDates,100m,"Per Voucher",[],new Random(1));
        Check(perVoucher.RemainingAmount==0m && perVoucher.Allocations.Count==10 && perVoucher.Allocations.All(x=>x.Amount==100m), "Per Voucher did not produce an equal same-day-capable split.");
        Check(perVoucher.Allocations.GroupBy(x=>x.Date).Any(g=>g.Count()>1), "Per Voucher did not allow multiple vouchers on the same date.");
        Check(perVoucher.Allocations.GroupBy(x=>x.Date).Count()==3, "Per Voucher concentrated allocation on one date instead of spreading it.");
        var perDay = RecurringDirectPlanner.AllocateLumpSum(1000m,allocationDates,200m,"Per Day");
        Check(perDay.AllocatedAmount==600m && perDay.RemainingAmount==400m && perDay.Allocations.All(x=>x.Amount<=200m), "Per Day capacity/Remaining is wrong.");
        var monthDates = new[]{new DateTime(2025,4,4),new DateTime(2025,4,14),new DateTime(2025,5,5),new DateTime(2025,5,15)};
        var perMonth = RecurringDirectPlanner.AllocateLumpSum(1000m,monthDates,300m,"Per Month");
        Check(perMonth.AllocatedAmount==600m && perMonth.RemainingAmount==400m && perMonth.Allocations.GroupBy(x=>(x.Date.Year,x.Date.Month)).All(g=>g.Sum(x=>x.Amount)<=300m), "Per Month capacity/Remaining is wrong.");
        var manuallyBalanced = RecurringDirectPlanner.AllocateLumpSum(1000m,monthDates,300m,"Per Month",[new RecurringDirectAllocation(new DateTime(2025,5,20),400m,true)]);
        Check(manuallyBalanced.RemainingAmount==0m && manuallyBalanced.Allocations.Any(x=>x.IsManual&&x.Amount==400m), "Manual override did not consume the remaining amount.");

        // Same-day duplicate occurrences are required by Per Voucher splitting and must be build-safe.
        var sameDayPlan = new RecurringDirectPlan("same-day",1,"2025-26","Cash","Lump Sum",100m,
            [new DateTime(2025,4,4),new DateTime(2025,4,4)],
            [new RecurringDirectLine("Freight","Indirect Expenses","Dr",100m,"Cash")],
            FrozenAmounts:[50m,50m],LineAmountsAreTotals:true);
        Check(RecurringDirectPlanner.Build(sameDayPlan).Count==2, "Same-day Per Voucher occurrences were rejected.");
        Console.WriteLine("PASS recurring R21 true periods, advanced Times, generalized Fixed/Random dates, maximum allocation, manual balance and same-day vouchers.");
    }
}
