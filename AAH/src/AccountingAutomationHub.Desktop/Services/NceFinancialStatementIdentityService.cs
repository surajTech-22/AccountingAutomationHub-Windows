using System;
using System.Collections.Generic;
using System.Linq;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R51 NCE Financial Statements identity authority.
///
/// Source authorities are resolved before presentation:
/// Trial Balance mapping is the calling/source layer for ordinary mapped ledgers; special Notes
/// replace only the portions owned by a stronger source authority.
/// TB owns ordinary mapped ledgers and movement data; Stock Summary owns inventory and the
/// opening/closing inventory legs of COGS; Debtor/Creditor schedules own party disclosure and
/// opposite-balance routing; each Note owns its visibility/totals; BS/P&L consume those Note
/// results. Physical workbook sheets are layout containers only.
/// </summary>
internal static class NceFinancialStatementIdentityService
{
    private const decimal VisibilityTolerance = 0.004m;

    internal sealed record DetailIdentity(string Ledger, decimal Fy, decimal Py)
    {
        public bool HasData => HasAmount(Fy) || HasAmount(Py);
    }

    internal sealed record HeadingIdentity(
        string Identity,
        string FsHead,
        string Title,
        string MappingKey,
        decimal Fy,
        decimal Py,
        IReadOnlyList<DetailIdentity> Details,
        bool ForceVisible = false)
    {
        public bool HasData => ForceVisible || HasAmount(Fy) || HasAmount(Py) || Details.Any(x => x.HasData);
    }

    internal sealed record NoteIdentity(
        string Note,
        string Title,
        string Statement,
        IReadOnlyList<HeadingIdentity> Headings,
        bool NarrativeAvailable = false,
        string PresentationVariant = "",
        decimal? FyAuthoritativeTotal = null,
        decimal? PyAuthoritativeTotal = null)
    {
        public bool HasData => NarrativeAvailable || HasAmount(FyTotal) || HasAmount(PyTotal) || Headings.Any(x => x.HasData);
        public decimal FyTotal => FyAuthoritativeTotal ?? Headings.Where(x => x.HasData).Sum(x => x.Fy);
        public decimal PyTotal => PyAuthoritativeTotal ?? Headings.Where(x => x.HasData).Sum(x => x.Py);
        public NceFinancialStatementPresentationAuthority.NotePresentationSpec Presentation
            => NceFinancialStatementPresentationAuthority.ForNote(Note);
    }

    internal sealed record FaceLineIdentity(
        string Identity,
        string Statement,
        string? Note,
        decimal Fy,
        decimal Py)
    {
        public bool HasData => HasAmount(Fy) || HasAmount(Py);
    }

    internal sealed record OwnerCapitalIdentity(
        string Name,
        decimal Share,
        decimal FyOpening,
        decimal FyCredits,
        decimal FyDebits,
        decimal FyClosing,
        decimal PyClosing);

    internal sealed record PpeMovementIdentity(
        string Title,
        string FsHead,
        string Basis,
        decimal FyOpening,
        decimal FyAdditions,
        decimal FyDisposals,
        decimal FyDepreciation,
        decimal FyAccumulatedDepreciation,
        decimal FyClosingGross,
        decimal FyClosing,
        decimal PyOpening,
        decimal PyAdditions,
        decimal PyDisposals,
        decimal PyDepreciation,
        decimal PyAccumulatedDepreciation,
        decimal PyClosingGross,
        decimal PyClosing);

    internal sealed record SourceCheck(
        string Name,
        decimal FySource,
        decimal FyIdentity,
        decimal PySource,
        decimal PyIdentity,
        string Guidance = "")
    {
        public bool Passed => Math.Abs(FySource - FyIdentity) <= 0.01m && Math.Abs(PySource - PyIdentity) <= 0.01m;
    }

    internal sealed class Snapshot
    {
        public Snapshot(
            IReadOnlyDictionary<string, decimal> fyByMappingKey,
            IReadOnlyDictionary<string, decimal> pyByMappingKey,
            IReadOnlyDictionary<string, NoteIdentity> notes,
            IReadOnlyDictionary<string, FaceLineIdentity> balanceSheet,
            IReadOnlyDictionary<string, FaceLineIdentity> profitAndLoss,
            IReadOnlyList<OwnerCapitalIdentity> ownerCapital,
            IReadOnlyList<PpeMovementIdentity> ppeMovements,
            IReadOnlyList<SourceCheck> sourceChecks)
        {
            FyByMappingKey = fyByMappingKey;
            PyByMappingKey = pyByMappingKey;
            Notes = notes;
            BalanceSheet = balanceSheet;
            ProfitAndLoss = profitAndLoss;
            OwnerCapital = ownerCapital;
            PpeMovements = ppeMovements;
            SourceChecks = sourceChecks;
        }

        public IReadOnlyDictionary<string, decimal> FyByMappingKey { get; }
        public IReadOnlyDictionary<string, decimal> PyByMappingKey { get; }
        public IReadOnlyDictionary<string, NoteIdentity> Notes { get; }
        public IReadOnlyDictionary<string, FaceLineIdentity> BalanceSheet { get; }
        public IReadOnlyDictionary<string, FaceLineIdentity> ProfitAndLoss { get; }
        public IReadOnlyList<OwnerCapitalIdentity> OwnerCapital { get; }
        public IReadOnlyList<PpeMovementIdentity> PpeMovements { get; }
        public IReadOnlyList<SourceCheck> SourceChecks { get; }

        public NoteIdentity? Note(string note)
            => Notes.TryGetValue(note, out var value) ? value : null;

        public bool NoteHasData(string note)
            => Notes.TryGetValue(note, out var value) && value.HasData;

        public FaceLineIdentity? Face(string statement, string identity)
        {
            var source = statement.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase)
                ? BalanceSheet
                : ProfitAndLoss;
            return source.TryGetValue(identity, out var value) ? value : null;
        }
    }

    private sealed class BuildContext
    {
        public required FinancialStatementBasicInfo BasicInfo { get; init; }
        public required IReadOnlyList<FinancialStatementMappingOption> Catalog { get; init; }
        public required FinancialStatementLedgerRow[] FyRows { get; init; }
        public required FinancialStatementLedgerRow[] PyRows { get; init; }
        public required FinancialStatementStockRow[] FyStock { get; init; }
        public required FinancialStatementStockRow[] PyStock { get; init; }
        public required FinancialStatementLedgerRow[] FyDebtors { get; init; }
        public required FinancialStatementLedgerRow[] PyDebtors { get; init; }
        public required FinancialStatementLedgerRow[] FyCreditors { get; init; }
        public required FinancialStatementLedgerRow[] PyCreditors { get; init; }
        public required IReadOnlyDictionary<string, decimal> Fy { get; init; }
        public required IReadOnlyDictionary<string, decimal> Py { get; init; }
        public required IReadOnlyDictionary<string, decimal> FyNonParty { get; init; }
        public required IReadOnlyDictionary<string, decimal> PyNonParty { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<DetailIdentity>> Details { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<DetailIdentity>> NonPartyDetails { get; init; }
    }

    public static Snapshot Build(
        FinancialStatementBasicInfo basicInfo,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        IReadOnlyList<FinancialStatementMappingOption> catalog,
        IEnumerable<FinancialStatementStockRow>? fyStock = null,
        IEnumerable<FinancialStatementStockRow>? pyStock = null,
        IEnumerable<FinancialStatementLedgerRow>? fyDebtors = null,
        IEnumerable<FinancialStatementLedgerRow>? pyDebtors = null,
        IEnumerable<FinancialStatementLedgerRow>? fyCreditors = null,
        IEnumerable<FinancialStatementLedgerRow>? pyCreditors = null)
    {
        var fySnapshot = fyRows?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var pySnapshot = pyRows?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var fyStockSnapshot = fyStock?.ToArray() ?? Array.Empty<FinancialStatementStockRow>();
        var pyStockSnapshot = pyStock?.ToArray() ?? Array.Empty<FinancialStatementStockRow>();
        var fyDebtorSnapshot = fyDebtors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var pyDebtorSnapshot = pyDebtors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var fyCreditorSnapshot = fyCreditors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();
        var pyCreditorSnapshot = pyCreditors?.ToArray() ?? Array.Empty<FinancialStatementLedgerRow>();

        var fy = AggregateMappings(fySnapshot);
        var py = AggregateMappings(pySnapshot);
        var fyNonPartyRows = fySnapshot.Where(x => !IsPartyLedger(x)).ToArray();
        var pyNonPartyRows = pySnapshot.Where(x => !IsPartyLedger(x)).ToArray();
        var context = new BuildContext
        {
            BasicInfo = basicInfo,
            Catalog = catalog,
            FyRows = fySnapshot,
            PyRows = pySnapshot,
            FyStock = fyStockSnapshot,
            PyStock = pyStockSnapshot,
            FyDebtors = fyDebtorSnapshot,
            PyDebtors = pyDebtorSnapshot,
            FyCreditors = fyCreditorSnapshot,
            PyCreditors = pyCreditorSnapshot,
            Fy = fy,
            Py = py,
            FyNonParty = AggregateMappings(fyNonPartyRows),
            PyNonParty = AggregateMappings(pyNonPartyRows),
            Details = AggregateDetails(fySnapshot, pySnapshot),
            NonPartyDetails = AggregateDetails(fyNonPartyRows, pyNonPartyRows)
        };

        var ownerCapital = BuildOwnerCapital(context);
        var ppeMovements = BuildPpeMovements(context);

        var notes = new[]
        {
            BuildNote01(context), BuildNote02(context), BuildNote03(context), BuildNote04(context),
            BuildNote05(context), BuildNote06(context), BuildNote07(context), BuildNote08(context),
            BuildNote09(context), BuildNote10(context), BuildNote11(context), BuildNote12(context),
            BuildNote13(context, ppeMovements), BuildNote14(context), BuildNote15(context), BuildNote16(context),
            BuildNote17(context), BuildNote18(context), BuildNote19(context), BuildNote20(context),
            BuildNote21(context), BuildNote22(context), BuildNote23(context), BuildNote24(context),
            BuildNote25(context), BuildNote26(context), BuildNote27(context), BuildNote28(context)
        }.ToDictionary(x => x.Note, StringComparer.OrdinalIgnoreCase);

        var balanceSheet = BuildFaceLines("Balance Sheet", notes, fy, py);
        var profitAndLoss = BuildFaceLines("P&L Account", notes, fy, py);
        AddPnlFaceOnlyLines(profitAndLoss, fy, py);
        var sourceChecks = BuildSourceChecks(context, notes, ppeMovements);

        return new Snapshot(fy, py, notes, balanceSheet, profitAndLoss, ownerCapital, ppeMovements, sourceChecks);
    }

    private static NoteIdentity BuildNote01(BuildContext c)
        => new("1", "Brief about the Entity", "Disclosure", Array.Empty<HeadingIdentity>(), NarrativeAvailable: true);

    private static NoteIdentity BuildNote02(BuildContext c)
        => new("2", "Summary of Significant Accounting Policies", "Disclosure", Array.Empty<HeadingIdentity>(), NarrativeAvailable: true);

    private static NoteIdentity BuildNote03(BuildContext c)
    {
        var fyRows = CapitalRows(c.FyRows, c.Catalog);
        var pyRows = CapitalRows(c.PyRows, c.Catalog);
        // Capital is presented on the TB ownership sign convention: credit balances are
        // positive and debit balances are negative, so debit-balance capital reduces owners' funds.
        var fy = fyRows.Sum(x => x.Closing);
        var py = pyRows.Sum(x => x.Closing);
        var details = MergeDetailRows(fyRows, pyRows, x => x.Closing);
        return new NoteIdentity("3", "Owners' Capital Account", "Balance Sheet", new[]
        {
            new HeadingIdentity("NOTE:3:CAPITAL", "Owners' capital account", "Owners' capital account", "CAPITAL_SOURCE", fy, py, details)
        });
    }

    private static NoteIdentity BuildNote04(BuildContext c)
    {
        // FIXED6: the standard Tally Profit & Loss A/c is a period-result bridge, not an
        // independent reserve. Proprietor/NCE capital already contains the posted result through
        // the capital/current-account movements. Excluding the bridge prevents the year's P&L
        // from being counted again as Reserves and Surplus.
        var fyRows = c.FyRows.Where(x => !FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(x.Ledger)).ToArray();
        var pyRows = c.PyRows.Where(x => !FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(x.Ledger)).ToArray();
        return BuildMappedNote(c, "4", "Reserves and Surplus", AggregateMappings(fyRows), AggregateMappings(pyRows), AggregateDetails(fyRows, pyRows));
    }
    private static NoteIdentity BuildNote05(BuildContext c) => BuildMappedNote(c, "5", "Long-term Borrowings");
    private static NoteIdentity BuildNote06(BuildContext c) => BuildMappedNote(c, "6", "Deferred Tax Balances");
    private static NoteIdentity BuildNote07(BuildContext c) => BuildMappedNote(c, "7", "Other Long-term Liabilities");
    private static NoteIdentity BuildNote08(BuildContext c) => BuildMappedNote(c, "8", "Long-term Provisions");
    private static NoteIdentity BuildNote09(BuildContext c) => BuildMappedNote(c, "9", "Short-term Borrowings");

    private static NoteIdentity BuildNote10(BuildContext c)
    {
        if (!HasScheduleAuthority(c.FyCreditors, c.PyCreditors))
            return BuildMappedNote(c, "10", "Trade Payables");

        var headings = new[]
        {
            ScheduleHeading("10", "Trade payables", "MSME dues", "TP_MSME", c.FyCreditors, c.PyCreditors,
                x => x.Closing > VisibilityTolerance && x.ScheduleClass1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase) && x.ScheduleClass2.Equals("MSME", StringComparison.OrdinalIgnoreCase)),
            ScheduleHeading("10", "Trade payables", "Other trade payables", "TP_OTHER", c.FyCreditors, c.PyCreditors,
                x => x.Closing > VisibilityTolerance && x.ScheduleClass1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase) && x.ScheduleClass2.Equals("Others", StringComparison.OrdinalIgnoreCase)),
            ScheduleHeading("10", "Trade payables", "Interest due under MSMED Act", "TP_INTEREST", c.FyCreditors, c.PyCreditors, x => false),
            ScheduleHeading("10", "Trade payables", "Unbilled trade payables", "TP_UNBILLED", c.FyCreditors, c.PyCreditors, x => false),
            ScheduleHeading("10", "Trade payables", "Other trade payable balances", "TP_DISPUTED", c.FyCreditors, c.PyCreditors,
                x => x.Closing > VisibilityTolerance && x.ScheduleClass1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase) &&
                     x.ScheduleClass2.StartsWith("Disputed", StringComparison.OrdinalIgnoreCase))
        };

        var hasFyAuthority = TryScheduleAuthority(c.FyCreditors, out var fyTotal);
        var hasPyAuthority = TryScheduleAuthority(c.PyCreditors, out var pyTotal);
        return new NoteIdentity(
            "10", "Trade Payables", "Balance Sheet", headings,
            FyAuthoritativeTotal: hasFyAuthority ? fyTotal : null,
            PyAuthoritativeTotal: hasPyAuthority ? pyTotal : null);
    }

    private static NoteIdentity BuildNote11(BuildContext c)
    {
        var baseNote = BuildMappedNote(c, "11", "Other Current Liabilities", c.FyNonParty, c.PyNonParty, c.NonPartyDetails);
        if (!HasScheduleAuthority(c.FyDebtors, c.PyDebtors)) return baseNote;
        var advance = ScheduleHeading("11", "Other current liabilities", "Advances from customers", "OCL_ADV_SCHEDULE",
            c.FyDebtors, c.PyDebtors,
            x => x.Closing > VisibilityTolerance && x.ScheduleClass1.Equals("Other Current Liabilities", StringComparison.OrdinalIgnoreCase) &&
                 x.ScheduleClass2.Equals("Advance from Customers", StringComparison.OrdinalIgnoreCase));

        // R52 FIXED17: a proved zero/empty official debtor schedule also owns the opposite-balance
        // customer-advance disclosure. Stale prepared debtor rows must not repopulate Note 11
        // after Note 18 has correctly been reduced to authoritative zero.
        var hasFyAuthority = TryScheduleAuthority(c.FyDebtors, out var fyDebtorTotal);
        var hasPyAuthority = TryScheduleAuthority(c.PyDebtors, out var pyDebtorTotal);
        if (hasFyAuthority && !HasAmount(fyDebtorTotal)) advance = advance with { Fy = 0m, Details = advance.Details.Select(x => x with { Fy = 0m }).ToArray() };
        if (hasPyAuthority && !HasAmount(pyDebtorTotal)) advance = advance with { Py = 0m, Details = advance.Details.Select(x => x with { Py = 0m }).ToArray() };
        return ReplaceHeading(baseNote, advance, "Advances from customers");
    }

    // FIXED7: statutory classification follows the user's saved mapping. Group/ledger names may
    // validate a result, but they must never move an already-mapped row to another Note.
    private static NoteIdentity BuildNote12(BuildContext c) => BuildMappedNote(c, "12", "Short-term Provisions");

    private static NoteIdentity BuildNote13(BuildContext c, IReadOnlyList<PpeMovementIdentity> movements)
    {
        var headings = movements.Select((x, i) => new HeadingIdentity(
            $"NOTE:13:PPE:{i}",
            x.FsHead,
            x.Title,
            "PPE_SOURCE",
            x.FyClosing,
            x.PyClosing,
            Array.Empty<DetailIdentity>(),
            ForceVisible: HasPpeMovementData(x))).ToArray();

        // R52: Note 13 must never fall back to generic closing-balance aggregation when the
        // selected Gross/Net source authority is unavailable. That fallback was the reason a
        // blank PPE schedule could coexist with a populated Balance Sheet PPE line. The source
        // check below reports the requested-vs-available authority explicitly before export.
        var variant = movements.FirstOrDefault()?.Basis ?? (c.BasicInfo.PpeBlock ?? string.Empty).Trim();
        return new NoteIdentity(
            "13",
            "Property, Plant and Equipment and Intangible Assets",
            "Balance Sheet",
            headings,
            PresentationVariant: variant);
    }

    private static NoteIdentity BuildNote14(BuildContext c) => BuildMappedNote(c, "14", "Investments");
    private static NoteIdentity BuildNote15(BuildContext c) => BuildMappedNote(c, "15", "Long-term Loans and Advances");
    private static NoteIdentity BuildNote16(BuildContext c) => BuildMappedNote(c, "16", "Other Non-current Assets");

    private static NoteIdentity BuildNote17(BuildContext c)
    {
        if (!HasStockAuthority(c.FyStock, c.PyStock)) return BuildMappedNote(c, "17", "Inventories");
        var map = new (string Mapping, string Title, string Key)[]
        {
            ("Raw Materials", "Raw materials", "INV_RM"),
            ("Work-in-Progress", "Work-in-progress", "INV_WIP"),
            ("Finished Goods", "Finished goods", "INV_FG"),
            ("Stock-in-Trade", "Stock-in-trade", "INV_SIT"),
            ("Packing Materials", "Packing materials", "INV_PACKING"),
            ("Other Materials", "Other inventories", "INV_OTHER")
        };
        var headings = map.Select(x => StockHeading("17", x.Title, x.Key, x.Mapping, c.FyStock, c.PyStock)).ToList();
        // Keep the statutory line available even when the selected stock classification catalogue
        // has no explicit Stores line; it remains zero rather than being manufactured.
        headings.Insert(4, new HeadingIdentity("NOTE:17:STORES", "Inventories", "Stores and spares", "INV_STORES", 0m, 0m, Array.Empty<DetailIdentity>()));

        var hasFyAuthority = TryStockAuthority(c.FyStock, out _, out var fyClosing);
        var hasPyAuthority = TryStockAuthority(c.PyStock, out _, out var pyClosing);

        // FIXED9: the user's Inventory Classification remains exactly where it is. When all
        // non-zero stock detail rows have one saved classification, the official XMLStatement
        // closing total can be owned by that same classification without changing any mapping.
        // If several classifications exist and the detail amounts do not reconcile, the linked
        // workbook remains reviewable but strict final export fails the allocation source check.
        ReconcileSingleStockClassification(headings, c.FyStock, map, Math.Abs(fyClosing), hasFyAuthority, fy: true);
        ReconcileSingleStockClassification(headings, c.PyStock, map, Math.Abs(pyClosing), hasPyAuthority, fy: false);

        return new NoteIdentity(
            "17", "Inventories", "Balance Sheet", headings,
            FyAuthoritativeTotal: hasFyAuthority ? Math.Abs(fyClosing) : null,
            PyAuthoritativeTotal: hasPyAuthority ? Math.Abs(pyClosing) : null);
    }

    private static NoteIdentity BuildNote18(BuildContext c)
    {
        if (!HasScheduleAuthority(c.FyDebtors, c.PyDebtors)) return BuildMappedNote(c, "18", "Trade Receivables");
        var headings = new[]
        {
            ScheduleHeading("18", "Trade receivables", "Secured, considered good", "TR_SEC_GOOD", c.FyDebtors, c.PyDebtors,
                x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase) && x.ScheduleClass2.StartsWith("Secured considered good", StringComparison.OrdinalIgnoreCase)),
            ScheduleHeading("18", "Trade receivables", "Unsecured, considered good", "TR_UNSEC_GOOD", c.FyDebtors, c.PyDebtors,
                x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase) && x.ScheduleClass2.StartsWith("Unsecured considered good", StringComparison.OrdinalIgnoreCase)),
            ScheduleHeading("18", "Trade receivables", "Doubtful receivables", "TR_DOUBT", c.FyDebtors, c.PyDebtors,
                x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase) && x.ScheduleClass2.StartsWith("Doubtful", StringComparison.OrdinalIgnoreCase)),
            ScheduleHeading("18", "Trade receivables", "Less: Provision for doubtful receivables", "TR_ALLOW", c.FyDebtors, c.PyDebtors,
                x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase) && x.ScheduleClass2.Contains("Provisions for doubtful", StringComparison.OrdinalIgnoreCase), -1m),
            ScheduleHeading("18", "Trade receivables", "Unbilled receivables", "TR_UNBILLED", c.FyDebtors, c.PyDebtors, x => false)
        }.ToList();

        var hasFyAuthority = TryScheduleAuthority(c.FyDebtors, out var fyTotal);
        var hasPyAuthority = TryScheduleAuthority(c.PyDebtors, out var pyTotal);

        // An explicit empty/zero XMLStatement Trade Receivables section is authoritative zero.
        // Do not turn stale credit/debit party-detail rows into a receivable by taking ABS().
        if (hasFyAuthority && !HasAmount(fyTotal))
            ZeroHeadingPeriod(headings, fy: true);
        if (hasPyAuthority && !HasAmount(pyTotal))
            ZeroHeadingPeriod(headings, fy: false);

        return new NoteIdentity(
            "18", "Trade Receivables", "Balance Sheet", headings,
            FyAuthoritativeTotal: hasFyAuthority ? Math.Abs(fyTotal) : null,
            PyAuthoritativeTotal: hasPyAuthority ? Math.Abs(pyTotal) : null);
    }

    private static NoteIdentity BuildNote19(BuildContext c) => BuildMappedNote(c, "19", "Cash and Bank Balances");
    private static NoteIdentity BuildNote20(BuildContext c) => BuildMappedNote(c, "20", "Short-term Loans and Advances");

    private static NoteIdentity BuildNote21(BuildContext c)
    {
        // Party schedules own party opposite-balance disclosures; every ordinary TB row stays
        // exactly where the user mapped it. No ledger-name based Rent/Advance relocation occurs.
        var fyBaseRows = c.FyRows.Where(x => !IsPartyLedger(x)).ToArray();
        var pyBaseRows = c.PyRows.Where(x => !IsPartyLedger(x)).ToArray();
        var baseNote = BuildMappedNote(c, "21", "Other Current Assets", AggregateMappings(fyBaseRows), AggregateMappings(pyBaseRows), AggregateDetails(fyBaseRows, pyBaseRows));
        var hasCreditorSchedule = HasScheduleAuthority(c.FyCreditors, c.PyCreditors);
        var hasDebtorSchedule = HasScheduleAuthority(c.FyDebtors, c.PyDebtors);
        if (!hasCreditorSchedule && !hasDebtorSchedule) return baseNote;

        var supplier = ScheduleHeadingAcross(
            "21", "Other current assets", "Advance to suppliers", "OCA_SUPPLIER_SCHEDULE",
            c.FyCreditors, c.PyCreditors, c.FyDebtors, c.PyDebtors,
            x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                 x.ScheduleClass2.Equals("Advance to suppliers", StringComparison.OrdinalIgnoreCase));

        var other = ScheduleHeadingAcross(
            "21", "Other current assets", "Other current assets", "OCA_OTHER_SCHEDULE",
            c.FyCreditors, c.PyCreditors, c.FyDebtors, c.PyDebtors,
            x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                 (x.ScheduleClass2.Equals("Other Receivables", StringComparison.OrdinalIgnoreCase) ||
                  x.ScheduleClass2.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase)));

        return ReplaceHeading(ReplaceHeading(baseNote, supplier, "Advance to suppliers"), other, "Other current assets", additive: true);
    }

    private static NoteIdentity BuildNote22(BuildContext c) => BuildMappedNote(c, "22", "Revenue from Operations");

    private static NoteIdentity BuildNote23(BuildContext c)
    {
        // R52 FIXED17: Other Income must preserve the saved TB sign. Auto mapping is not
        // permission to ABS a debit/negative income adjustment (for example PY Round Off).
        // Explicit As Is / Reverse selections remain authoritative.
        decimal IncomeAmount(FinancialStatementLedgerRow row) => row.AmountSign switch
        {
            "Reverse" => -row.Closing,
            _ => row.Closing
        };
        return BuildMappedNote(
            c, "23", "Other Income",
            AggregateMappings(c.FyRows, IncomeAmount),
            AggregateMappings(c.PyRows, IncomeAmount),
            AggregateDetails(c.FyRows, c.PyRows, IncomeAmount));
    }

    private static NoteIdentity BuildNote24(BuildContext c)
    {
        var baseNote = BuildMappedNote(c, "24", "Cost of Goods Sold");
        if (!HasStockAuthority(c.FyStock, c.PyStock)) return baseNote;

        var hasFyAuthority = TryStockAuthority(c.FyStock, out var fyAuthorityOpen, out var fyAuthorityClose);
        var hasPyAuthority = TryStockAuthority(c.PyStock, out var pyAuthorityOpen, out var pyAuthorityClose);

        var fyOpen = hasFyAuthority
            ? Math.Abs(fyAuthorityOpen)
            : Math.Abs(c.FyStock.Where(x => x.OpeningBalance.HasValue).Sum(x => x.OpeningBalance ?? 0m));
        var pyOpen = hasPyAuthority
            ? Math.Abs(pyAuthorityOpen)
            : Math.Abs(c.PyStock.Where(x => x.OpeningBalance.HasValue).Sum(x => x.OpeningBalance ?? 0m));
        var fyClose = hasFyAuthority
            ? Math.Abs(fyAuthorityClose)
            : Math.Abs(c.FyStock.Where(x => x.ClosingBalance.HasValue).Sum(x => x.ClosingBalance ?? 0m));
        var pyClose = hasPyAuthority
            ? Math.Abs(pyAuthorityClose)
            : Math.Abs(c.PyStock.Where(x => x.ClosingBalance.HasValue).Sum(x => x.ClosingBalance ?? 0m));

        // The mapped Purchase rows stay exactly where the user put them. Only opening/closing
        // inventory legs are source-owned by the official NCE Stock Summary.
        var open = new HeadingIdentity("NOTE:24:OPEN", "Cost of goods sold", "Opening inventory", "COGS_OPEN_STOCK", fyOpen, pyOpen, Array.Empty<DetailIdentity>());
        var close = new HeadingIdentity("NOTE:24:CLOSE", "Cost of goods sold", "Less: Closing inventory", "COGS_CLOSE_STOCK", -fyClose, -pyClose, Array.Empty<DetailIdentity>());
        return ReplaceHeading(ReplaceHeading(baseNote, open, "Opening inventory"), close, "Less: Closing inventory");
    }

    private static NoteIdentity BuildNote25(BuildContext c) => BuildMappedNote(c, "25", "Employee Benefits Expense");
    private static NoteIdentity BuildNote26(BuildContext c) => BuildMappedNote(c, "26", "Finance Costs");
    private static NoteIdentity BuildNote27(BuildContext c) => BuildMappedNote(c, "27", "Depreciation and Amortisation Expense");
    private static NoteIdentity BuildNote28(BuildContext c) => BuildMappedNote(c, "28", "Other Expenses");

    private static NoteIdentity BuildMappedNote(BuildContext c, string note, string title)
        => BuildMappedNote(c, note, title, c.Fy, c.Py, c.Details);

    private static NoteIdentity BuildMappedNote(
        BuildContext c,
        string note,
        string title,
        IReadOnlyDictionary<string, decimal> fySource,
        IReadOnlyDictionary<string, decimal> pySource,
        IReadOnlyDictionary<string, IReadOnlyList<DetailIdentity>> detailSource)
    {
        var options = c.Catalog
            .Where(x => x.Note.Equals(note, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.MappingKey))
            .GroupBy(x => $"{x.MappingKey}\u001f{x.FsHead}\u001f{x.NoteSubHead}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();

        var headings = options.Select(option =>
        {
            var fy = Value(fySource, option.MappingKey);
            var py = Value(pySource, option.MappingKey);
            var details = detailSource.TryGetValue(option.MappingKey, out var rows)
                ? rows.Where(x => x.HasData).ToArray()
                : Array.Empty<DetailIdentity>();
            return new HeadingIdentity(
                $"NOTE:{note}:KEY:{option.MappingKey}",
                option.FsHead,
                string.IsNullOrWhiteSpace(option.NoteSubHead) ? option.NoteHead : option.NoteSubHead,
                option.MappingKey,
                fy,
                py,
                details);
        }).ToArray();

        var statement = options.Select(x => x.Class1).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        return new NoteIdentity(note, title, statement, headings);
    }

    private static NoteIdentity ReplaceHeading(NoteIdentity note, HeadingIdentity replacement, string title, bool additive = false)
    {
        var list = note.Headings.ToList();
        var index = list.FindIndex(x => x.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (additive)
            {
                var existing = list[index];
                list[index] = existing with
                {
                    Fy = existing.Fy + replacement.Fy,
                    Py = existing.Py + replacement.Py,
                    Details = existing.Details.Concat(replacement.Details).ToArray()
                };
            }
            else list[index] = replacement;
        }
        else list.Add(replacement);
        return note with { Headings = list };
    }

    private static HeadingIdentity ScheduleHeading(
        string note,
        string fsHead,
        string title,
        string key,
        IReadOnlyList<FinancialStatementLedgerRow> fyRows,
        IReadOnlyList<FinancialStatementLedgerRow> pyRows,
        Func<FinancialStatementLedgerRow, bool> predicate,
        decimal sign = 1m)
    {
        var fyMatch = fyRows.Where(predicate).ToArray();
        var pyMatch = pyRows.Where(predicate).ToArray();
        var fy = sign * fyMatch.Sum(x => Math.Abs(x.Closing));
        var py = sign * pyMatch.Sum(x => Math.Abs(x.Closing));
        var details = MergeDetailRows(fyMatch, pyMatch, x => sign * Math.Abs(x.Closing));
        return new HeadingIdentity($"NOTE:{note}:SCHEDULE:{key}", fsHead, title, key, fy, py, details);
    }

    private static HeadingIdentity ScheduleHeadingAcross(
        string note,
        string fsHead,
        string title,
        string key,
        IReadOnlyList<FinancialStatementLedgerRow> fyPrimary,
        IReadOnlyList<FinancialStatementLedgerRow> pyPrimary,
        IReadOnlyList<FinancialStatementLedgerRow> fySecondary,
        IReadOnlyList<FinancialStatementLedgerRow> pySecondary,
        Func<FinancialStatementLedgerRow, bool> predicate)
    {
        var fyMatch = fyPrimary.Concat(fySecondary).Where(predicate).ToArray();
        var pyMatch = pyPrimary.Concat(pySecondary).Where(predicate).ToArray();
        var fy = fyMatch.Sum(x => Math.Abs(x.Closing));
        var py = pyMatch.Sum(x => Math.Abs(x.Closing));
        var details = MergeDetailRows(fyMatch, pyMatch, x => Math.Abs(x.Closing));
        return new HeadingIdentity($"NOTE:{note}:SCHEDULE:{key}", fsHead, title, key, fy, py, details);
    }

    private static HeadingIdentity StockHeading(
        string note,
        string title,
        string key,
        string mapping,
        IReadOnlyList<FinancialStatementStockRow> fyRows,
        IReadOnlyList<FinancialStatementStockRow> pyRows)
    {
        var fyMatch = fyRows.Where(x => x.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase)).ToArray();
        var pyMatch = pyRows.Where(x => x.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase)).ToArray();
        var fy = Math.Abs(fyMatch.Sum(x => x.ClosingBalance ?? 0m));
        var py = Math.Abs(pyMatch.Sum(x => x.ClosingBalance ?? 0m));
        var names = fyMatch.Select(x => x.Item).Concat(pyMatch.Select(x => x.Item)).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var details = names.Select(name => new DetailIdentity(
            name,
            Math.Abs(fyMatch.Where(x => x.Item.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(x => x.ClosingBalance ?? 0m)),
            Math.Abs(pyMatch.Where(x => x.Item.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(x => x.ClosingBalance ?? 0m)))).ToArray();
        return new HeadingIdentity($"NOTE:{note}:STOCK:{key}", "Inventories", title, key, fy, py, details);
    }

    private static IReadOnlyList<OwnerCapitalIdentity> BuildOwnerCapital(BuildContext c)
    {
        var fyRows = CapitalRows(c.FyRows, c.Catalog);
        var pyRows = CapitalRows(c.PyRows, c.Catalog);
        var owners = c.BasicInfo.OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Take(11).ToArray();
        if (owners.Length == 0)
            owners = new[] { new OwnerPartnerRow { Name = "Owner / Partners", ShareOfProfitLoss = 100m } };

        // Exact owner-tag/name matches are assigned first. Only the genuinely unassigned residual
        // is distributed by the saved owner/partner ratio. The old implementation distributed
        // the whole capital pool to every unmatched owner even when other owners already had exact
        // matches, which could double count Note 3.
        var fyExact = owners.Select(x => MatchOwnerRows(fyRows, x.Name).ToArray()).ToArray();
        var pyExact = owners.Select(x => MatchOwnerRows(pyRows, x.Name).ToArray()).ToArray();
        var fyAssigned = fyExact.SelectMany(x => x).ToHashSet();
        var pyAssigned = pyExact.SelectMany(x => x).ToHashSet();
        var fyResidual = fyRows.Where(x => !fyAssigned.Contains(x)).ToArray();
        var pyResidual = pyRows.Where(x => !pyAssigned.Contains(x)).ToArray();
        var unmatchedIndexes = Enumerable.Range(0, owners.Length)
            .Where(i => fyExact[i].Length == 0 || pyExact[i].Length == 0).ToArray();
        var residualShareTotal = unmatchedIndexes.Sum(i => Math.Abs(owners[i].ShareOfProfitLoss));

        decimal Ratio(int ownerIndex)
        {
            if (!unmatchedIndexes.Contains(ownerIndex)) return 0m;
            if (unmatchedIndexes.Length == 1) return 1m;
            if (residualShareTotal > 0m) return Math.Abs(owners[ownerIndex].ShareOfProfitLoss) / residualShareTotal;
            return 1m / unmatchedIndexes.Length;
        }

        var result = new List<OwnerCapitalIdentity>();
        for (var ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++)
        {
            var owner = owners[ownerIndex];
            var ownerFy = fyExact[ownerIndex];
            var ownerPy = pyExact[ownerIndex];
            var ratio = Ratio(ownerIndex);

            decimal Fy(Func<FinancialStatementLedgerRow, decimal> selector)
                => ownerFy.Length > 0 ? ownerFy.Sum(selector) : fyResidual.Sum(selector) * ratio;
            decimal Py(Func<FinancialStatementLedgerRow, decimal> selector)
                => ownerPy.Length > 0 ? ownerPy.Sum(selector) : pyResidual.Sum(selector) * ratio;

            result.Add(new OwnerCapitalIdentity(
                owner.Name, owner.ShareOfProfitLoss,
                Fy(x => x.Opening), Fy(x => Math.Abs(x.Credit)), Fy(x => Math.Abs(x.Debit)), Fy(x => x.Closing),
                Py(x => x.Closing)));
        }
        return result;
    }

    private static IReadOnlyList<PpeMovementIdentity> BuildPpeMovements(BuildContext c)
    {
        var requestedBasis = c.BasicInfo.PpeBlock.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
            ? "Net Block"
            : "Gross Block";

        // The presentation catalogue supplies stable asset-class identities (mapping key +
        // Note sub-heading). Gross/Net/Accumulated-Depreciation authority comes from the actual
        // TB C3 selection on each row, not from the presentation catalogue's default path.
        var options = c.Catalog.Where(x => x.Note.Equals("13", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => $"{x.MappingKey}\u001f{x.FsHead}\u001f{x.NoteSubHead}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();

        var output = new List<PpeMovementIdentity>();
        var fyDepreciationExpense = Note27Expense(c, fy: true);
        var pyDepreciationExpense = Note27Expense(c, fy: false);

        var fyNetRowsAll = options.Where(x => !IsDevelopmentPpeOption(x))
            .SelectMany(x => MatchPpeRows(c.FyRows, x, "Net Block")).ToArray();
        var pyNetRowsAll = options.Where(x => !IsDevelopmentPpeOption(x))
            .SelectMany(x => MatchPpeRows(c.PyRows, x, "Net Block")).ToArray();
        var fyNetCredits = fyNetRowsAll.Sum(x => Math.Abs(x.Credit));
        var pyNetCredits = pyNetRowsAll.Sum(x => Math.Abs(x.Credit));
        var fyNetCreditsAreDepreciation = Math.Abs(fyNetCredits - fyDepreciationExpense) <= 0.01m;
        var pyNetCreditsAreDepreciation = Math.Abs(pyNetCredits - pyDepreciationExpense) <= 0.01m;

        foreach (var option in options)
        {
            var title = string.IsNullOrWhiteSpace(option.NoteSubHead) ? option.NoteHead : option.NoteSubHead;

            if (IsDevelopmentPpeOption(option))
            {
                var fyRows = c.FyRows.Where(x => x.Mapping.Equals(option.MappingKey, StringComparison.OrdinalIgnoreCase)).ToArray();
                var pyRows = c.PyRows.Where(x => x.Mapping.Equals(option.MappingKey, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (fyRows.Length == 0 && pyRows.Length == 0) continue;

                output.Add(new PpeMovementIdentity(
                    title, option.FsHead, requestedBasis,
                    fyRows.Sum(x => Math.Abs(x.Opening)),
                    fyRows.Sum(x => Math.Abs(x.Debit)),
                    fyRows.Sum(x => Math.Abs(x.Credit)),
                    0m, 0m,
                    fyRows.Sum(x => Math.Abs(x.Closing)),
                    fyRows.Sum(x => Math.Abs(x.Closing)),
                    pyRows.Sum(x => Math.Abs(x.Opening)),
                    pyRows.Sum(x => Math.Abs(x.Debit)),
                    pyRows.Sum(x => Math.Abs(x.Credit)),
                    0m, 0m,
                    pyRows.Sum(x => Math.Abs(x.Closing)),
                    pyRows.Sum(x => Math.Abs(x.Closing))));
                continue;
            }

            if (requestedBasis.Equals("Net Block", StringComparison.OrdinalIgnoreCase))
            {
                var fyRows = MatchPpeRows(c.FyRows, option, "Net Block").ToArray();
                var pyRows = MatchPpeRows(c.PyRows, option, "Net Block").ToArray();
                if (fyRows.Length == 0 && pyRows.Length == 0) continue;

                var fyCredits = fyRows.Sum(x => Math.Abs(x.Credit));
                var pyCredits = pyRows.Sum(x => Math.Abs(x.Credit));
                output.Add(new PpeMovementIdentity(
                    title, option.FsHead, "Net Block",
                    fyRows.Sum(x => Math.Abs(x.Opening)),
                    fyRows.Sum(x => Math.Abs(x.Debit)),
                    0m,
                    fyNetCreditsAreDepreciation ? fyCredits : 0m,
                    0m,
                    0m,
                    fyRows.Sum(x => Math.Abs(x.Closing)),
                    pyRows.Sum(x => Math.Abs(x.Opening)),
                    pyRows.Sum(x => Math.Abs(x.Debit)),
                    0m,
                    pyNetCreditsAreDepreciation ? pyCredits : 0m,
                    0m,
                    0m,
                    pyRows.Sum(x => Math.Abs(x.Closing))));
                continue;
            }

            // Gross Block is accepted only from genuine gross-cost rows. Accumulated
            // depreciation is a separate source authority and is never manufactured from the
            // Net Block or from the P&L depreciation expense.
            var fyGross = MatchPpeRows(c.FyRows, option, "Gross Block").ToArray();
            var pyGross = MatchPpeRows(c.PyRows, option, "Gross Block").ToArray();
            var fyAccum = MatchPpeAccumulatedRows(c.FyRows, option).ToArray();
            var pyAccum = MatchPpeAccumulatedRows(c.PyRows, option).ToArray();
            if (fyGross.Length == 0 && pyGross.Length == 0 && fyAccum.Length == 0 && pyAccum.Length == 0) continue;

            var fyOpeningGross = fyGross.Sum(x => Math.Abs(x.Opening));
            var fyAdditions = fyGross.Sum(x => Math.Abs(x.Debit));
            var fyDisposals = fyGross.Sum(x => Math.Abs(x.Credit));
            var fyClosingGross = fyGross.Sum(x => Math.Abs(x.Closing));
            var fyAccumulated = fyAccum.Sum(x => Math.Abs(x.Closing));

            var pyOpeningGross = pyGross.Sum(x => Math.Abs(x.Opening));
            var pyAdditions = pyGross.Sum(x => Math.Abs(x.Debit));
            var pyDisposals = pyGross.Sum(x => Math.Abs(x.Credit));
            var pyClosingGross = pyGross.Sum(x => Math.Abs(x.Closing));
            var pyAccumulated = pyAccum.Sum(x => Math.Abs(x.Closing));

            output.Add(new PpeMovementIdentity(
                title, option.FsHead, "Gross Block",
                fyOpeningGross, fyAdditions, fyDisposals, 0m, fyAccumulated,
                fyClosingGross, fyClosingGross - fyAccumulated,
                pyOpeningGross, pyAdditions, pyDisposals, 0m, pyAccumulated,
                pyClosingGross, pyClosingGross - pyAccumulated));
        }
        return output;
    }

    private static IEnumerable<FinancialStatementLedgerRow> MatchPpeRows(
        IEnumerable<FinancialStatementLedgerRow> rows,
        FinancialStatementMappingOption option,
        string basis)
    {
        return rows.Where(x =>
            x.Mapping.Equals(option.MappingKey, StringComparison.OrdinalIgnoreCase) &&
            (basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
                ? IsNetPpeClassification(x.Classification3)
                : IsGrossPpeClassification(x.Classification3)));
    }

    private static IEnumerable<FinancialStatementLedgerRow> MatchPpeAccumulatedRows(
        IEnumerable<FinancialStatementLedgerRow> rows,
        FinancialStatementMappingOption option)
        => rows.Where(x =>
            x.Mapping.Equals(option.MappingKey, StringComparison.OrdinalIgnoreCase) &&
            IsAccumulatedDepreciationClassification(x.Classification3));

    private static bool IsDevelopmentPpeOption(FinancialStatementMappingOption option)
        => option.MappingKey.Equals("CWIP", StringComparison.OrdinalIgnoreCase) ||
           option.MappingKey.Equals("IAUD", StringComparison.OrdinalIgnoreCase) ||
           option.FsHead.Equals("Capital work-in-progress", StringComparison.OrdinalIgnoreCase) ||
           option.FsHead.Equals("Intangible assets under development", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetPpeClassification(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("PPE (Net)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PPE & IA (Net)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Intangible Assets (Net)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGrossPpeClassification(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("PPE (Gross)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Intangible Assets (Gross)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccumulatedDepreciationClassification(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("PPE (Accumulated Depreciation)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Intangible Assets (Accumulated Depreciation)", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal Note27Expense(BuildContext c, bool fy)
    {
        var keys = c.Catalog.Where(x => x.Note.Equals("27", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.MappingKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = fy ? c.FyRows : c.PyRows;
        return rows.Where(x => keys.Contains(x.Mapping)).Sum(x => Math.Abs(ResolveAmount(x)));
    }

    private static bool HasPpeBasisRows(BuildContext c, bool fy, string basis)
    {
        var keys = c.Catalog.Where(x => x.Note.Equals("13", StringComparison.OrdinalIgnoreCase) && !IsDevelopmentPpeOption(x))
            .Select(x => x.MappingKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = fy ? c.FyRows : c.PyRows;
        return rows.Any(x => keys.Contains(x.Mapping) &&
            (basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
                ? IsNetPpeClassification(x.Classification3)
                : IsGrossPpeClassification(x.Classification3)));
    }

    private static FinancialStatementLedgerRow[] PpeAccumulatedRows(BuildContext c, bool fy)
    {
        var keys = c.Catalog.Where(x => x.Note.Equals("13", StringComparison.OrdinalIgnoreCase) && !IsDevelopmentPpeOption(x))
            .Select(x => x.MappingKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = fy ? c.FyRows : c.PyRows;
        return rows.Where(x => keys.Contains(x.Mapping) && IsAccumulatedDepreciationClassification(x.Classification3)).ToArray();
    }

    private static bool HasPpeAccumulatedRows(BuildContext c, bool fy)
        => PpeAccumulatedRows(c, fy).Length > 0;

    private static decimal PpeBasisCreditMovement(BuildContext c, bool fy, string basis)
    {
        var keys = c.Catalog.Where(x => x.Note.Equals("13", StringComparison.OrdinalIgnoreCase) && !IsDevelopmentPpeOption(x))
            .Select(x => x.MappingKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = fy ? c.FyRows : c.PyRows;
        return rows.Where(x => keys.Contains(x.Mapping) &&
            (basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
                ? IsNetPpeClassification(x.Classification3)
                : IsGrossPpeClassification(x.Classification3)))
            .Sum(x => Math.Abs(x.Credit));
    }

    private static bool HasPpeMovementData(PpeMovementIdentity x)
        => HasAmount(x.FyOpening) || HasAmount(x.FyAdditions) || HasAmount(x.FyDisposals) ||
           HasAmount(x.FyDepreciation) || HasAmount(x.FyAccumulatedDepreciation) ||
           HasAmount(x.FyClosingGross) || HasAmount(x.FyClosing) ||
           HasAmount(x.PyOpening) || HasAmount(x.PyAdditions) || HasAmount(x.PyDisposals) ||
           HasAmount(x.PyDepreciation) || HasAmount(x.PyAccumulatedDepreciation) ||
           HasAmount(x.PyClosingGross) || HasAmount(x.PyClosing);

    private static decimal PpeMappedEvidence(BuildContext c, bool fy)
    {
        var keys = c.Catalog.Where(x => x.Note.Equals("13", StringComparison.OrdinalIgnoreCase) && !IsDevelopmentPpeOption(x))
            .Select(x => x.MappingKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = fy ? c.FyRows : c.PyRows;
        return rows.Where(x => keys.Contains(x.Mapping))
            .Sum(x => Math.Abs(x.Opening) + Math.Abs(x.Debit) + Math.Abs(x.Credit) + Math.Abs(x.Closing));
    }

    private static FinancialStatementLedgerRow[] CapitalRows(IEnumerable<FinancialStatementLedgerRow> rows, IReadOnlyList<FinancialStatementMappingOption> catalog)
    {
        var keys = catalog.Where(x => x.Note.Equals("3", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.MappingKey).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Where(x => keys.Contains(x.Mapping) ||
                               x.Mapping.StartsWith("CAP_", StringComparison.OrdinalIgnoreCase) ||
                               x.Mapping.StartsWith("CURR_", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static IEnumerable<FinancialStatementLedgerRow> MatchOwnerRows(IEnumerable<FinancialStatementLedgerRow> rows, string owner)
    {
        var ownerKey = NormalizeIdentity(owner);
        return rows.Where(x =>
            (!string.IsNullOrWhiteSpace(x.Classification5) && NormalizeIdentity(x.Classification5) == ownerKey) ||
            (ownerKey.Length > 0 && NormalizeIdentity(x.Ledger).Contains(ownerKey, StringComparison.Ordinal)));
    }

    private static string NormalizeIdentity(string? value)
        => new string((value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static IReadOnlyList<SourceCheck> BuildSourceChecks(
        BuildContext c,
        IReadOnlyDictionary<string, NoteIdentity> notes,
        IReadOnlyList<PpeMovementIdentity> ppe)
    {
        var checks = new List<SourceCheck>();
        void Add(string name, decimal fySource, decimal fyIdentity, decimal pySource, decimal pyIdentity, string guidance = "")
            => checks.Add(new SourceCheck(name, fySource, fyIdentity, pySource, pyIdentity, guidance));

        var fyCapitalRows = CapitalRows(c.FyRows, c.Catalog);
        var pyCapitalRows = CapitalRows(c.PyRows, c.Catalog);
        var fyCapital = fyCapitalRows.Sum(x => x.Closing);
        var pyCapital = pyCapitalRows.Sum(x => x.Closing);
        Add("Note 3 capital closing", fyCapital, notes["3"].FyTotal, pyCapital, notes["3"].PyTotal);
        Add("Note 3 capital movement",
            fyCapitalRows.Sum(x => x.Opening) + fyCapitalRows.Sum(x => x.Credit) - fyCapitalRows.Sum(x => Math.Abs(x.Debit)), fyCapital,
            pyCapitalRows.Sum(x => x.Opening) + pyCapitalRows.Sum(x => x.Credit) - pyCapitalRows.Sum(x => Math.Abs(x.Debit)), pyCapital);

        if (HasStockAuthority(c.FyStock, c.PyStock))
        {
            var hasFyStockAuthority = TryStockAuthority(c.FyStock, out var fyOpenAuthority, out var fyCloseAuthority);
            var hasPyStockAuthority = TryStockAuthority(c.PyStock, out var pyOpenAuthority, out var pyCloseAuthority);
            var fyStock = hasFyStockAuthority
                ? Math.Abs(fyCloseAuthority)
                : Math.Abs(c.FyStock.Where(x => x.ClosingBalance.HasValue).Sum(x => x.ClosingBalance ?? 0m));
            var pyStock = hasPyStockAuthority
                ? Math.Abs(pyCloseAuthority)
                : Math.Abs(c.PyStock.Where(x => x.ClosingBalance.HasValue).Sum(x => x.ClosingBalance ?? 0m));
            var fyOpen = hasFyStockAuthority
                ? Math.Abs(fyOpenAuthority)
                : Math.Abs(c.FyStock.Where(x => x.OpeningBalance.HasValue).Sum(x => x.OpeningBalance ?? 0m));
            var pyOpen = hasPyStockAuthority
                ? Math.Abs(pyOpenAuthority)
                : Math.Abs(c.PyStock.Where(x => x.OpeningBalance.HasValue).Sum(x => x.OpeningBalance ?? 0m));

            Add("Note 17 Stock Summary closing", fyStock, notes["17"].FyTotal, pyStock, notes["17"].PyTotal);
            Add(
                "Note 17 stock classification allocation",
                fyStock, notes["17"].Headings.Sum(x => x.Fy),
                pyStock, notes["17"].Headings.Sum(x => x.Py),
                "Official Stock Summary totals do not reconcile the saved Inventory Classification detail. AAH did not move any mapping; review only the source allocation before final values/PDF export.");

            var openHeading = notes["24"].Headings.FirstOrDefault(x => x.Title.Equals("Opening inventory", StringComparison.OrdinalIgnoreCase));
            var closeHeading = notes["24"].Headings.FirstOrDefault(x => x.Title.Equals("Less: Closing inventory", StringComparison.OrdinalIgnoreCase));
            Add("Note 24 opening inventory", fyOpen, openHeading?.Fy ?? 0m, pyOpen, openHeading?.Py ?? 0m);
            Add("Note 24 closing inventory", fyStock, Math.Abs(closeHeading?.Fy ?? 0m), pyStock, Math.Abs(closeHeading?.Py ?? 0m));
        }

        if (HasScheduleAuthority(c.FyCreditors, c.PyCreditors))
        {
            var fyDetailTp = c.FyCreditors.Where(x => x.Closing > VisibilityTolerance && x.ScheduleClass1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var pyDetailTp = c.PyCreditors.Where(x => x.Closing > VisibilityTolerance && x.ScheduleClass1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var fyTp = TryScheduleAuthority(c.FyCreditors, out var fyOfficialTp) ? fyOfficialTp : fyDetailTp;
            var pyTp = TryScheduleAuthority(c.PyCreditors, out var pyOfficialTp) ? pyOfficialTp : pyDetailTp;
            Add("Note 10 creditor schedule", fyTp, notes["10"].FyTotal, pyTp, notes["10"].PyTotal);
            Add(
                "Note 10 creditor schedule allocation",
                fyTp, notes["10"].Headings.Sum(x => x.Fy),
                pyTp, notes["10"].Headings.Sum(x => x.Py),
                "Official Trade Payables total does not reconcile the saved schedule detail. AAH did not alter the saved schedule mapping.");
        }
        if (HasScheduleAuthority(c.FyDebtors, c.PyDebtors))
        {
            var fyDetailTr = c.FyDebtors.Where(x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var pyDetailTr = c.PyDebtors.Where(x => x.Closing < -VisibilityTolerance && x.ScheduleClass1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var fyTr = TryScheduleAuthority(c.FyDebtors, out var fyOfficialTr) ? fyOfficialTr : fyDetailTr;
            var pyTr = TryScheduleAuthority(c.PyDebtors, out var pyOfficialTr) ? pyOfficialTr : pyDetailTr;
            Add("Note 18 debtor schedule", fyTr, notes["18"].FyTotal, pyTr, notes["18"].PyTotal);
            Add(
                "Note 18 debtor schedule allocation",
                fyTr, notes["18"].Headings.Sum(x => x.Fy),
                pyTr, notes["18"].Headings.Sum(x => x.Py),
                "Official Trade Receivables total does not reconcile the saved schedule detail. AAH did not alter the saved schedule mapping.");

            var fyCustomerAdv = c.FyDebtors.Where(x => x.ScheduleClass1.Equals("Other Current Liabilities", StringComparison.OrdinalIgnoreCase) &&
                                                       x.ScheduleClass2.Equals("Advance from Customers", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var pyCustomerAdv = c.PyDebtors.Where(x => x.ScheduleClass1.Equals("Other Current Liabilities", StringComparison.OrdinalIgnoreCase) &&
                                                       x.ScheduleClass2.Equals("Advance from Customers", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var oclHeading = notes["11"].Headings.FirstOrDefault(x => x.Title.Equals("Advances from customers", StringComparison.OrdinalIgnoreCase));
            Add("Note 11 debtor opposite balances", fyCustomerAdv, oclHeading?.Fy ?? 0m, pyCustomerAdv, oclHeading?.Py ?? 0m);
        }

        if (HasScheduleAuthority(c.FyCreditors, c.PyCreditors) || HasScheduleAuthority(c.FyDebtors, c.PyDebtors))
        {
            var fySupplierAdv = c.FyCreditors.Concat(c.FyDebtors).Where(x =>
                x.ScheduleClass1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                x.ScheduleClass2.Equals("Advance to suppliers", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var pySupplierAdv = c.PyCreditors.Concat(c.PyDebtors).Where(x =>
                x.ScheduleClass1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                x.ScheduleClass2.Equals("Advance to suppliers", StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.Closing));
            var ocaHeading = notes["21"].Headings.FirstOrDefault(x => x.Title.Equals("Advance to suppliers", StringComparison.OrdinalIgnoreCase));
            Add("Note 21 schedule opposite balances", fySupplierAdv, ocaHeading?.Fy ?? 0m, pySupplierAdv, ocaHeading?.Py ?? 0m);

            var fyOther = c.FyCreditors.Concat(c.FyDebtors).Where(x =>
                x.ScheduleClass1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                (x.ScheduleClass2.Equals("Other Receivables", StringComparison.OrdinalIgnoreCase) ||
                 x.ScheduleClass2.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase))).Sum(x => Math.Abs(x.Closing));
            var pyOther = c.PyCreditors.Concat(c.PyDebtors).Where(x =>
                x.ScheduleClass1.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) &&
                (x.ScheduleClass2.Equals("Other Receivables", StringComparison.OrdinalIgnoreCase) ||
                 x.ScheduleClass2.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase))).Sum(x => Math.Abs(x.Closing));
            var otherHeading = notes["21"].Headings.FirstOrDefault(x => x.Title.Equals("Other current assets", StringComparison.OrdinalIgnoreCase));
            var baseOtherFy = Value(c.FyNonParty, "OCA_OTHER");
            var baseOtherPy = Value(c.PyNonParty, "OCA_OTHER");
            Add("Note 21 schedule other current assets", baseOtherFy + fyOther, otherHeading?.Fy ?? 0m, baseOtherPy + pyOther, otherHeading?.Py ?? 0m);
        }

        // R51 compatibility marker: Note 13 PPE/IA movement authority is superseded by the
        // stronger R52 selected-basis and depreciation/gross-movement checks below.
        // R52 Note 13: first prove that the Basic Info selection has a real source authority.
        // A Net Block ledger may not be relabelled Gross Block, and Gross Block may not be
        // manufactured from P&L depreciation. When the requested basis is absent, the guidance
        // tells the user exactly which Basic Info choice/source mapping must be corrected.
        var requestedPpeBasis = c.BasicInfo.PpeBlock.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
            ? "Net Block"
            : "Gross Block";
        var alternativePpeBasis = requestedPpeBasis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
            ? "Gross Block"
            : "Net Block";
        var fyPpeMapped = PpeMappedEvidence(c, fy: true);
        var pyPpeMapped = PpeMappedEvidence(c, fy: false);
        var fyRequestedAvailable = HasPpeBasisRows(c, fy: true, requestedPpeBasis);
        var pyRequestedAvailable = HasPpeBasisRows(c, fy: false, requestedPpeBasis);
        var alternativeAvailable = HasPpeBasisRows(c, fy: true, alternativePpeBasis) ||
                                   HasPpeBasisRows(c, fy: false, alternativePpeBasis);
        var basisGuidance = alternativeAvailable
            ? $"{requestedPpeBasis} is selected in Basic Info, but the mapped Fixed Asset source is {alternativePpeBasis}. Select {alternativePpeBasis} or map genuine {requestedPpeBasis} authority; AAH will not relabel one basis as the other."
            : $"{requestedPpeBasis} is selected in Basic Info, but no matching Fixed Asset source authority is mapped. Map genuine {requestedPpeBasis} authority before export.";
        Add(
            "Note 13 selected PPE/IA block authority",
            fyPpeMapped > 0.01m ? 1m : 0m,
            fyPpeMapped <= 0.01m || fyRequestedAvailable ? (fyPpeMapped > 0.01m ? 1m : 0m) : 0m,
            pyPpeMapped > 0.01m ? 1m : 0m,
            pyPpeMapped <= 0.01m || pyRequestedAvailable ? (pyPpeMapped > 0.01m ? 1m : 0m) : 0m,
            basisGuidance);

        // R52 FIXED2: once the selected-basis authority check has already failed, do not
        // cascade secondary Net/Gross movement errors from an authority that is not present.
        // The first source-authority message is the actionable failure. Movement/depreciation
        // checks resume immediately when every period containing PPE evidence has the selected
        // basis, so the accounting safety gate itself is not weakened.
        var selectedPpeBasisAuthorityReady =
            (fyPpeMapped <= 0.01m || fyRequestedAvailable) &&
            (pyPpeMapped <= 0.01m || pyRequestedAvailable);

        if (selectedPpeBasisAuthorityReady)
        {
            if (requestedPpeBasis.Equals("Net Block", StringComparison.OrdinalIgnoreCase))
            {
                var fyDepreciation = Note27Expense(c, fy: true);
                var pyDepreciation = Note27Expense(c, fy: false);
                var fyNetCredits = PpeBasisCreditMovement(c, fy: true, "Net Block");
                var pyNetCredits = PpeBasisCreditMovement(c, fy: false, "Net Block");
                Add(
                    "Note 13 Net Block depreciation movement",
                    fyDepreciation,
                    fyNetCredits,
                    pyDepreciation,
                    pyNetCredits,
                    "Net Block Fixed Asset credit movements must reconcile to Note 27 depreciation/amortisation. If they do not, disposal/write-off authority must be identified separately; AAH will not guess the split.");

                Add(
                    "Note 13 Net Block movement arithmetic",
                    ppe.Where(x => x.Basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase))
                        .Sum(x => x.FyOpening + x.FyAdditions - x.FyDisposals - x.FyDepreciation),
                    ppe.Where(x => x.Basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)).Sum(x => x.FyClosing),
                    ppe.Where(x => x.Basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase))
                        .Sum(x => x.PyOpening + x.PyAdditions - x.PyDisposals - x.PyDepreciation),
                    ppe.Where(x => x.Basis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)).Sum(x => x.PyClosing),
                    "Net Block Note 13 must satisfy Opening + Additions - Disposals - Depreciation = Closing for FY and PY.");
            }
            else
            {
                var fyGrossNeedsAccumulated = Note27Expense(c, fy: true) > 0.01m && HasPpeBasisRows(c, fy: true, "Gross Block");
                var pyGrossNeedsAccumulated = Note27Expense(c, fy: false) > 0.01m && HasPpeBasisRows(c, fy: false, "Gross Block");
                Add(
                    "Note 13 Gross Block accumulated depreciation authority",
                    fyGrossNeedsAccumulated ? 1m : 0m,
                    !fyGrossNeedsAccumulated || HasPpeAccumulatedRows(c, fy: true) ? (fyGrossNeedsAccumulated ? 1m : 0m) : 0m,
                    pyGrossNeedsAccumulated ? 1m : 0m,
                    !pyGrossNeedsAccumulated || HasPpeAccumulatedRows(c, fy: false) ? (pyGrossNeedsAccumulated ? 1m : 0m) : 0m,
                    "Gross Block with depreciation requires separately mapped Accumulated Depreciation authority. AAH will not derive accumulated depreciation from Net Block or from the P&L expense.");

                Add(
                    "Note 13 Gross Block movement arithmetic",
                    ppe.Where(x => x.Basis.Equals("Gross Block", StringComparison.OrdinalIgnoreCase))
                        .Sum(x => x.FyOpening + x.FyAdditions - x.FyDisposals),
                    ppe.Where(x => x.Basis.Equals("Gross Block", StringComparison.OrdinalIgnoreCase)).Sum(x => x.FyClosingGross),
                    ppe.Where(x => x.Basis.Equals("Gross Block", StringComparison.OrdinalIgnoreCase))
                        .Sum(x => x.PyOpening + x.PyAdditions - x.PyDisposals),
                    ppe.Where(x => x.Basis.Equals("Gross Block", StringComparison.OrdinalIgnoreCase)).Sum(x => x.PyClosingGross),
                    "Gross Block Note 13 must satisfy Opening Gross Block + Additions - Disposals = Closing Gross Block.");

                var fyAccumRows = PpeAccumulatedRows(c, fy: true);
                var pyAccumRows = PpeAccumulatedRows(c, fy: false);
                Add(
                    "Note 13 Gross Block depreciation movement",
                    Note27Expense(c, fy: true),
                    fyAccumRows.Sum(x => Math.Abs(x.Credit)),
                    Note27Expense(c, fy: false),
                    pyAccumRows.Sum(x => Math.Abs(x.Credit)),
                    "Gross Block accumulated-depreciation credit movements must reconcile to Note 27 depreciation/amortisation. AAH will not manufacture the current-year charge.");

                Add(
                    "Note 13 Gross Block accumulated depreciation arithmetic",
                    fyAccumRows.Sum(x => Math.Abs(x.Opening) + Math.Abs(x.Credit) - Math.Abs(x.Debit)),
                    fyAccumRows.Sum(x => Math.Abs(x.Closing)),
                    pyAccumRows.Sum(x => Math.Abs(x.Opening) + Math.Abs(x.Credit) - Math.Abs(x.Debit)),
                    pyAccumRows.Sum(x => Math.Abs(x.Closing)),
                    "Accumulated Depreciation must satisfy Opening + Credits - Debits = Closing for FY and PY.");
            }
        }

        if (ppe.Count > 0)
            Add("Note 13 PPE/IA closing", ppe.Sum(x => x.FyClosing), notes["13"].FyTotal, ppe.Sum(x => x.PyClosing), notes["13"].PyTotal);

        // R50 compatibility marker: Note 21 creditor opposite balances are now checked together
        // with debtor-side opposite balances by the stronger Note 21 schedule source authority.
        return checks;
    }

    private static Dictionary<string, FaceLineIdentity> BuildFaceLines(
        string statement,
        IReadOnlyDictionary<string, NoteIdentity> notes,
        IReadOnlyDictionary<string, decimal> fy,
        IReadOnlyDictionary<string, decimal> py)
    {
        var output = new Dictionary<string, FaceLineIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes.Values)
        {
            if (!note.Statement.Equals(statement, StringComparison.OrdinalIgnoreCase)) continue;

            var fsHeads = note.Headings
                .Select(x => x.FsHead?.Trim() ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // FIXED9: where one Note has one face identity and a stronger official source total
            // (Stock Summary / Trade Receivables / Trade Payables), the face statement consumes
            // that proved Note total. The user's saved row mappings remain untouched.
            if (fsHeads.Length == 1 && (note.FyAuthoritativeTotal.HasValue || note.PyAuthoritativeTotal.HasValue))
            {
                AddOrMerge(output, new FaceLineIdentity(fsHeads[0], statement, note.Note, note.FyTotal, note.PyTotal));
                continue;
            }

            foreach (var group in note.Headings.GroupBy(x => x.FsHead, StringComparer.OrdinalIgnoreCase))
            {
                var name = group.Key?.Trim() ?? string.Empty;
                if (name.Length == 0) continue;
                AddOrMerge(output, new FaceLineIdentity(name, statement, note.Note, group.Sum(x => x.Fy), group.Sum(x => x.Py)));
            }
        }

        if (statement.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase))
        {
            if (notes.TryGetValue("3", out var capital))
                output["Owners' capital account"] = new FaceLineIdentity("Owners' capital account", statement, "3", capital.FyTotal, capital.PyTotal);
            var fyDeferredTaxNet = SumByPrefix(fy, "DTL") - SumByPrefix(fy, "DTA");
            var pyDeferredTaxNet = SumByPrefix(py, "DTL") - SumByPrefix(py, "DTA");
            output["Deferred tax liabilities (Net)"] = new FaceLineIdentity(
                "Deferred tax liabilities (Net)", statement, "6",
                Math.Max(fyDeferredTaxNet, 0m), Math.Max(pyDeferredTaxNet, 0m));
            output["Deferred tax assets (Net)"] = new FaceLineIdentity(
                "Deferred tax assets (Net)", statement, "6",
                Math.Max(-fyDeferredTaxNet, 0m), Math.Max(-pyDeferredTaxNet, 0m));
        }
        return output;
    }

    private static void AddPnlFaceOnlyLines(
        Dictionary<string, FaceLineIdentity> output,
        IReadOnlyDictionary<string, decimal> fy,
        IReadOnlyDictionary<string, decimal> py)
    {
        AddFaceKey(output, "Exceptional items", null, fy, py, "EXC");
        AddFaceKey(output, "Extraordinary items", null, fy, py, "EXTRAORDINARY");
        AddFaceKey(output, "Partners' remuneration", null, fy, py, "PARTNER_REMUN");
        AddFaceKey(output, "Current tax", null, fy, py, "TAX_CURR");
        AddFaceKey(output, "Deferred tax", null, fy, py, "TAX_DEFER");
        AddFaceKey(output, "Prior year tax", null, fy, py, "TAX_PRIOR");
        output["Tax expense"] = new FaceLineIdentity(
            "Tax expense", "P&L Account", null,
            Value(fy, "TAX_CURR") + Value(fy, "TAX_DEFER") + Value(fy, "TAX_PRIOR"),
            Value(py, "TAX_CURR") + Value(py, "TAX_DEFER") + Value(py, "TAX_PRIOR"));
        AddFaceKey(output, "Profit/(Loss) from Discontinuing Operations", null, fy, py, "DISCONT_PROFIT");
        AddFaceKey(output, "Tax Expense of Discontinuing Operations", null, fy, py, "DISCONT_TAX");
    }

    private static void AddFaceKey(
        Dictionary<string, FaceLineIdentity> output,
        string identity,
        string? note,
        IReadOnlyDictionary<string, decimal> fy,
        IReadOnlyDictionary<string, decimal> py,
        params string[] keys)
    {
        output[identity] = new FaceLineIdentity(identity, "P&L Account", note,
            keys.Sum(x => Value(fy, x)), keys.Sum(x => Value(py, x)));
    }

    private static void AddOrMerge(Dictionary<string, FaceLineIdentity> output, FaceLineIdentity line)
    {
        if (!output.TryGetValue(line.Identity, out var existing))
        {
            output[line.Identity] = line;
            return;
        }
        output[line.Identity] = existing with { Fy = existing.Fy + line.Fy, Py = existing.Py + line.Py };
    }

    private static Dictionary<string, decimal> AggregateMappings(IEnumerable<FinancialStatementLedgerRow> rows)
        => AggregateMappings(rows, ResolveAmount);

    private static Dictionary<string, decimal> AggregateMappings(
        IEnumerable<FinancialStatementLedgerRow> rows,
        Func<FinancialStatementLedgerRow, decimal> amountSelector)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.Mapping?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            var amount = amountSelector(row);
            result[key] = Value(result, key) + amount;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<DetailIdentity>> AggregateDetails(
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows)
        => AggregateDetails(fyRows, pyRows, ResolveAmount);

    private static IReadOnlyDictionary<string, IReadOnlyList<DetailIdentity>> AggregateDetails(
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        Func<FinancialStatementLedgerRow, decimal> amountSelector)
    {
        var rows = new Dictionary<string, MutableDetail>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<FinancialStatementLedgerRow> source, bool isFy)
        {
            foreach (var row in source)
            {
                var key = row.Mapping?.Trim() ?? string.Empty;
                var ledger = row.Ledger?.Trim() ?? string.Empty;
                if (key.Length == 0 || ledger.Length == 0) continue;
                var identity = $"{key}\u001f{ledger}";
                if (!rows.TryGetValue(identity, out var detail))
                {
                    detail = new MutableDetail(key, ledger);
                    rows[identity] = detail;
                }
                if (isFy) detail.Fy += amountSelector(row);
                else detail.Py += amountSelector(row);
            }
        }
        Add(fyRows, true);
        Add(pyRows, false);

        return rows.Values.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<DetailIdentity>)x.OrderBy(v => v.Ledger, StringComparer.OrdinalIgnoreCase)
                .Select(v => new DetailIdentity(v.Ledger, v.Fy, v.Py)).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<DetailIdentity> MergeDetailRows(
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        Func<FinancialStatementLedgerRow, decimal> amount)
    {
        var fy = fyRows.GroupBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(amount), StringComparer.OrdinalIgnoreCase);
        var py = pyRows.GroupBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(amount), StringComparer.OrdinalIgnoreCase);
        return fy.Keys.Concat(py.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new DetailIdentity(x, Value(fy, x), Value(py, x))).ToArray();
    }

    private sealed class MutableDetail(string key, string ledger)
    {
        public string Key { get; } = key;
        public string Ledger { get; } = ledger;
        public decimal Fy { get; set; }
        public decimal Py { get; set; }
    }

    private static decimal ResolveAmount(FinancialStatementLedgerRow row)
        => row.AmountSign switch
        {
            "As Is" => row.Closing,
            "Reverse" => -row.Closing,
            _ => Math.Abs(row.Closing)
        };

    private static bool IsPartyLedger(FinancialStatementLedgerRow row)
    {
        var text = $"{row.ParentGroup} {row.PrimaryGroup}".ToUpperInvariant();
        return text.Contains("SUNDRY DEBTOR", StringComparison.Ordinal) ||
               text.Contains("SUNDRY CREDITOR", StringComparison.Ordinal) ||
               text.Contains("TRADE RECEIV", StringComparison.Ordinal) ||
               text.Contains("TRADE PAYABLE", StringComparison.Ordinal);
    }

    private static bool TryScheduleAuthority(IReadOnlyList<FinancialStatementLedgerRow> rows, out decimal total)
    {
        var carrier = rows.FirstOrDefault(x => x.HasScheduleSourceAuthority);
        if (carrier is null)
        {
            total = 0m;
            return false;
        }

        total = Math.Abs(carrier.ScheduleSourceAuthorityTotal);
        return true;
    }

    private static bool TryStockAuthority(IReadOnlyList<FinancialStatementStockRow> rows, out decimal opening, out decimal closing)
    {
        var carrier = rows.FirstOrDefault(x => x.HasPeriodSourceAuthority);
        if (carrier is null)
        {
            opening = 0m;
            closing = 0m;
            return false;
        }

        opening = carrier.PeriodSourceOpeningTotal;
        closing = carrier.PeriodSourceClosingTotal;
        return true;
    }

    private static void ZeroHeadingPeriod(List<HeadingIdentity> headings, bool fy)
    {
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            var details = h.Details.Select(d => fy
                ? d with { Fy = 0m }
                : d with { Py = 0m }).ToArray();
            headings[i] = fy
                ? h with { Fy = 0m, Details = details }
                : h with { Py = 0m, Details = details };
        }
    }

    private static void ReconcileSingleStockClassification(
        List<HeadingIdentity> headings,
        IReadOnlyList<FinancialStatementStockRow> rows,
        IReadOnlyList<(string Mapping, string Title, string Key)> map,
        decimal authoritativeClosing,
        bool hasAuthority,
        bool fy)
    {
        if (!hasAuthority) return;
        var currentTotal = headings.Sum(x => fy ? x.Fy : x.Py);
        if (Math.Abs(currentTotal - authoritativeClosing) <= 0.01m) return;

        var mappings = rows
            .Where(x => Math.Abs(x.ClosingBalance ?? 0m) > VisibilityTolerance && !string.IsNullOrWhiteSpace(x.Mapping))
            .Select(x => x.Mapping.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mappings.Length != 1) return;

        var key = map.FirstOrDefault(x => x.Mapping.Equals(mappings[0], StringComparison.OrdinalIgnoreCase)).Key;
        if (string.IsNullOrWhiteSpace(key)) return;

        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            var amount = h.MappingKey.Equals(key, StringComparison.OrdinalIgnoreCase) ? authoritativeClosing : 0m;
            var details = h.Details.Select(d => fy
                ? d with { Fy = 0m }
                : d with { Py = 0m }).ToArray();
            headings[i] = fy
                ? h with { Fy = amount, Details = details }
                : h with { Py = amount, Details = details };
        }
    }

    private static bool HasScheduleAuthority(params IReadOnlyList<FinancialStatementLedgerRow>[] sets)
        => sets.Any(x => x.Count > 0);

    private static bool HasStockAuthority(params IReadOnlyList<FinancialStatementStockRow>[] sets)
        => sets.Any(x => x.Any(r => r.OpeningBalance.HasValue || r.ClosingBalance.HasValue));

    private static decimal SumByPrefix(IReadOnlyDictionary<string, decimal> source, params string[] prefixes)
        => source.Where(x => prefixes.Any(prefix => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Sum(x => x.Value);

    private static decimal Value(IReadOnlyDictionary<string, decimal> source, string key)
        => source.TryGetValue(key, out var value) ? value : 0m;

    private static bool HasAmount(decimal value) => Math.Abs(value) > VisibilityTolerance;
}
