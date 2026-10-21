using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R15 R8 clean statutory renderer. The supplied preparation workbook is the behavioural
/// authority: deterministic mapping -> detailed notes/schedules -> face statements -> print pack.
/// Internal mapping/data mechanics are hidden from final presentation.
/// </summary>
public static class CleanFinancialStatementExportService
{
    [ThreadStatic]
    private static string? _activeNumberFormat;
    private static string NumberFormat => _activeNumberFormat ?? "#,##0.00;(#,##0.00);\"-\"";
    private const string DataSheetName = "_AAH_Data";

    private sealed class PreparedDetail
    {
        public string Key { get; init; } = string.Empty;
        public string Ledger { get; init; } = string.Empty;
        public string Parent { get; init; } = string.Empty;
        public decimal Fy { get; set; }
        public decimal Py { get; set; }
        public decimal FyOpening { get; set; }
        public decimal PyOpening { get; set; }
        public decimal FyDebit { get; set; }
        public decimal FyCredit { get; set; }
        public decimal PyDebit { get; set; }
        public decimal PyCredit { get; set; }
        public decimal FyAgeOver6Months { get; set; }
        public decimal FyAgeUnder6Months { get; set; }
        public decimal PyAgeOver6Months { get; set; }
        public decimal PyAgeUnder6Months { get; set; }
        public bool AutoMapped { get; set; }
    }

    private sealed class PreparedMappings
    {
        public Dictionary<string, decimal> Fy { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, decimal> Py { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PreparedDetail> Details { get; } = [];
        public List<FinancialStatementLedgerRow> FyUnmapped { get; } = [];
        public List<FinancialStatementLedgerRow> PyUnmapped { get; } = [];
    }

    public sealed record PreparationAssessment(int FyPending, int PyPending);

    /// <summary>
    /// R39 lightweight preparation path used by the live Financial Statements screen. It uses
    /// exactly the same mapping/schedule resolver as final export, but performs no Excel COM or
    /// file I/O. This keeps the working Financial Statements preparation current while the user
    /// maps TB/Stock/party schedules; final face-formula equality remains a fail-closed export gate.
    /// </summary>
    public static PreparationAssessment AssessPreparation(
        FinancialStatementBasicInfo basicInfo,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        IReadOnlyList<FinancialStatementMappingOption> activeCatalog)
    {
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(basicInfo.EntityType);
        if (profile != "NCE" && profile != "C_AS")
            return new PreparationAssessment(0, 0);

        var catalog = (activeCatalog ?? Array.Empty<FinancialStatementMappingOption>())
            .Where(x => !string.IsNullOrWhiteSpace(x.MappingKey) && !x.MappingKey.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (catalog.Length == 0) return new PreparationAssessment(0, 0);
        var validKeys = new HashSet<string>(catalog.Select(x => x.MappingKey), StringComparer.OrdinalIgnoreCase);
        var prepared = PrepareMappings(fyRows.ToArray(), pyRows.ToArray(), profile, validKeys, catalog);
        var fyPending = prepared.FyUnmapped.Count(RequiresFinalClassification);
        var pyPending = basicInfo.PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase)
            ? 0
            : prepared.PyUnmapped.Count(RequiresFinalClassification);
        return new PreparationAssessment(fyPending, pyPending);
    }

    public static void Export(
        string schemaTemplatePath,
        string destinationPath,
        FinancialStatementBasicInfo basicInfo,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        IEnumerable<EntitySignatoryRow> signatories,
        IEnumerable<OwnerPartnerRow> ownerPartners,
        bool keepLinks,
        string? pdfPath = null,
        bool allowIncompleteLinkedWorkbook = false)
    {
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(basicInfo.EntityType);
        if (profile != "NCE" && profile != "C_AS")
            throw new InvalidOperationException($"Clean statutory renderer is not enabled for profile {profile}.");

        var catalog = FinancialStatementWorkbookService.BuildMappingCatalog(schemaTemplatePath, basicInfo.EntityType)
            .Where(x => !string.IsNullOrWhiteSpace(x.MappingKey) && !x.MappingKey.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var validKeys = new HashSet<string>(catalog.Select(x => x.MappingKey), StringComparer.OrdinalIgnoreCase);
        var fySnapshot = fyRows.ToArray();
        var pySnapshot = pyRows.ToArray();
        var prepared = PrepareMappings(fySnapshot, pySnapshot, profile, validKeys, catalog);
        // R40: only the linked Excel working workbook may be generated before final mapping /
        // Balance Sheet matching is complete. Values-only Excel and PDF remain strict final outputs.
        var relaxedLinkedDraft = allowIncompleteLinkedWorkbook && keepLinks && string.IsNullOrWhiteSpace(pdfPath);
        if (!relaxedLinkedDraft) EnsureReadyForFinalOutput(prepared, basicInfo);

        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Microsoft Excel is not installed.");
        var previousNumberFormat = _activeNumberFormat;
        _activeNumberFormat = PresentationNumberFormat(basicInfo);
        dynamic? excel = null;
        dynamic? book = null;
        try
        {
            excel = Activator.CreateInstance(excelType)!;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.ScreenUpdating = false;
            try { excel.EnableEvents = false; } catch { }
            try { excel.SheetsInNewWorkbook = 1; } catch { }

            book = excel.Workbooks.Add();
            while (book.Worksheets.Count > 1) book.Worksheets[book.Worksheets.Count].Delete();
            dynamic first = book.Worksheets[1];
            first.Name = "Balance Sheet";
            dynamic pnl = AddSheetAfter(book, "Statement of Profit and Loss");

            if (profile == "NCE")
            {
                dynamic notes12 = AddSheetAfter(book, "Notes 1-2 - Entity and Policies");
                dynamic notes36 = AddSheetAfter(book, "Notes 3-6 - Capital and Funding");
                dynamic notes712 = AddSheetAfter(book, "Notes 7-12 - Liabilities");
                dynamic notes1316 = AddSheetAfter(book, "Notes 13-16 - Noncurrent Assets");
                dynamic notes1721 = AddSheetAfter(book, "Notes 17-21 - Current Assets");
                dynamic notes2228 = AddSheetAfter(book, "Notes 22-28 - Income & Expenses");
                dynamic data = AddSheetAfter(book, DataSheetName);
                WriteHiddenData(data, prepared, basicInfo);
                try { data.Visible = 2; } catch { } // xlSheetVeryHidden

                var fsRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                WriteNceNotes12(notes12, basicInfo);
                WriteNceNotes36(notes36, basicInfo, catalog, prepared, fsRefs, ownerPartners.ToArray());
                WriteNceNotes712(notes712, basicInfo, catalog, prepared, fsRefs);
                WriteNceNotes1316(notes1316, basicInfo, catalog, prepared, fsRefs);
                WriteNceNotes1721(notes1721, basicInfo, catalog, prepared, fsRefs);
                WriteNceNotes2228(notes2228, basicInfo, catalog, prepared, fsRefs);
                WriteNceProfitAndLoss(pnl, basicInfo, fsRefs);
                // Current-year P/L is part of proprietor/partner funds. Link the exact final
                // NCE P/L face result into Note 3 only after the P&L has been written.
                // Never bind to a historical hard-coded row because the face layout can move.
                BindNceProfitToCapital(notes36, pnl);
                WriteNceBalanceSheet(first, basicInfo, fsRefs);

                AddSignatureBlock(first, basicInfo, signatories, ownerPartners);
                AddSignatureBlock(pnl, basicInfo, signatories, ownerPartners);

                ApplyCommonPresentation(first, false, excel, basicInfo);
                ApplyCommonPresentation(pnl, false, excel, basicInfo);
                ApplyCommonPresentation(notes12, true, excel, basicInfo);
                ApplyCommonPresentation(notes36, true, excel, basicInfo);
                ApplyCommonPresentation(notes712, true, excel, basicInfo);
                ApplyCommonPresentation(notes1316, true, excel, basicInfo);
                ApplyCommonPresentation(notes1721, true, excel, basicInfo);
                ApplyCommonPresentation(notes2228, true, excel, basicInfo);
                try
                {
                    notes36.PageSetup.Orientation = 2; // Capital/current-account movement table is wide.
                    notes36.PageSetup.FitToPagesWide = 1;
                    notes1316.PageSetup.Orientation = 2; // PPE / detailed schedules need landscape room.
                    notes1316.PageSetup.FitToPagesWide = 1;
                }
                catch { }
            }
            else
            {
                dynamic notes = AddSheetAfter(book, "Notes to Accounts");
                dynamic data = AddSheetAfter(book, DataSheetName);
                WriteHiddenData(data, prepared, basicInfo);
                try { data.Visible = 2; } catch { }
                var refs = WriteCompanyNotes(notes, basicInfo, catalog, prepared);
                WriteDivisionIProfitAndLoss(pnl, basicInfo, refs);
                WriteDivisionIBalanceSheet(first, basicInfo, refs);
                AddSignatureBlock(first, basicInfo, signatories, ownerPartners);
                AddSignatureBlock(pnl, basicInfo, signatories, ownerPartners);
                ApplyCommonPresentation(first, false, excel, basicInfo);
                ApplyCommonPresentation(pnl, false, excel, basicInfo);
                ApplyCommonPresentation(notes, true, excel, basicInfo);
            }

            try { excel.CalculateFullRebuild(); } catch { try { excel.CalculateFull(); } catch { } }
            ApplyExampleFaceZeroRowVisibility(first, pnl, profile);
            if (!relaxedLinkedDraft) VerifyBalanceSheet(first, fySnapshot, pySnapshot);

            if (!keepLinks)
            {
                for (var i = 1; i <= book.Worksheets.Count; i++)
                {
                    dynamic ws = book.Worksheets[i];
                    if (string.Equals((string)ws.Name, DataSheetName, StringComparison.OrdinalIgnoreCase)) continue;
                    dynamic used = ws.UsedRange;
                    used.Value2 = used.Value2;
                }
                try { book.Worksheets[DataSheetName].Delete(); } catch { }
            }

            first.Activate();
            if (!string.IsNullOrWhiteSpace(pdfPath))
            {
                if (File.Exists(pdfPath)) File.Delete(pdfPath);
                book.ExportAsFixedFormat(0, pdfPath, 0, true, false, Type.Missing, Type.Missing, false, Type.Missing);
            }
            else
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                book.SaveAs(destinationPath, 51); // xlOpenXMLWorkbook
            }
        }
        finally
        {
            try { book?.Close(false); } catch { }
            try { excel?.Quit(); } catch { }
            _activeNumberFormat = previousNumberFormat;
        }
    }

    private static dynamic AddSheetAfter(dynamic book, string name)
    {
        dynamic sheet = book.Worksheets.Add(Type.Missing, book.Worksheets[book.Worksheets.Count], 1, Type.Missing);
        sheet.Name = name;
        return sheet;
    }

    private static PreparedMappings PrepareMappings(
        IReadOnlyList<FinancialStatementLedgerRow> fyRows,
        IReadOnlyList<FinancialStatementLedgerRow> pyRows,
        string profile,
        HashSet<string> validKeys,
        IReadOnlyList<FinancialStatementMappingOption> catalog)
    {
        var result = new PreparedMappings();
        var detailIndex = new Dictionary<string, PreparedDetail>(StringComparer.OrdinalIgnoreCase);
        AddPeriod(fyRows, true, profile, validKeys, catalog, result, detailIndex);
        AddPeriod(pyRows, false, profile, validKeys, catalog, result, detailIndex);
        return result;
    }

    private static void AddPeriod(
        IEnumerable<FinancialStatementLedgerRow> rows,
        bool current,
        string profile,
        HashSet<string> validKeys,
        IReadOnlyList<FinancialStatementMappingOption> catalog,
        PreparedMappings result,
        Dictionary<string, PreparedDetail> detailIndex)
    {
        foreach (var row in rows)
        {
            if (row.Opening == 0m && row.Debit == 0m && row.Credit == 0m && row.Closing == 0m) continue;

            // Party schedule classification is a separate mandatory workbook authority. A
            // creditor/debtor with an incomplete Q/R schedule must never silently fall back to
            // Others / Unsecured merely because its TB path is complete.
            if (row.HasScheduleContext && !row.HasCompleteScheduleMapping)
            {
                if (current) result.FyUnmapped.Add(row); else result.PyUnmapped.Add(row);
                continue;
            }

            var key = row.Mapping?.Trim() ?? string.Empty;
            var auto = false;
            // R9 FIXED13: the final-output gate must agree with what the TB grid calls
            // User Mapped or copied from the exact opposite-year ledger. A valid explicit workbook path is authoritative even when a broad
            // Tally group/name heuristic (for example bare CGST/SGST under Duties & Taxes)
            // cannot prove the direction. Re-resolve that visible path first so a stale internal
            // MappingKey cannot point to a different note than the grid selection.
            var explicitUserVisibleMapping = IsExplicitVisibleMappingOrigin(row.MappingOrigin) &&
                FinancialStatementWorkbookRules.IsValidVisiblePath(
                    profile, row.Classification3, row.Classification4, row.WorkbookClassification3);
            if (explicitUserVisibleMapping)
            {
                var visibleKey = ResolveReadOnlyHierarchyKey(row, profile, validKeys, catalog);
                if (visibleKey.Length > 0) key = visibleKey;
            }
            if (key.Length == 0 || !validKeys.Contains(key) ||
                (!explicitUserVisibleMapping && FinancialStatementPresentationExportService.IsObviouslyIncompatibleMapping(row, key, profile)))
            {
                // R15 R13/R14: export is a read-only renderer, never a mapping engine.
                // R15 R14: export remains read-only. However, the visible C1-C5 hierarchy is also
                // mapping authority. If an older saved row has a blank/stale internal MappingKey but
                // its visible hierarchy resolves to exactly one valid catalogue entry, consume that
                // key for this export only. The grid/persistence is never changed here.
                key = ResolveReadOnlyHierarchyKey(row, profile, validKeys, catalog);
            }
            if (key.Length == 0)
            {
                if (current) result.FyUnmapped.Add(row); else result.PyUnmapped.Add(row);
                continue;
            }

            // Original Debtors/Creditors Q/R schedule mapping is independent from TB C1/C2/C3.
            // Where a schedule classification exists, use it to select the detailed statutory
            // bucket for final notes/face statements without mutating the TB mapping itself.
            key = ResolveScheduleMappingKey(row, key, profile, validKeys);
            if (key.Length == 0)
            {
                if (current) result.FyUnmapped.Add(row); else result.PyUnmapped.Add(row);
                continue;
            }

            var bifurcation = FinancialStatementClassificationService.GetBifurcationKind(profile, row.Classification3);
            var split = FinancialStatementClassificationService.ResolveSplitTarget(profile, row.Classification3, row.Classification4, key);
            if (bifurcation == FinancialStatementClassificationService.BifurcationKind.CurrentNonCurrent && split is not null)
            {
                if (!FinancialStatementClassificationService.TryValidateBifurcation(row.Closing, row.NonCurrentAmount, out _))
                {
                    if (current) result.FyUnmapped.Add(row); else result.PyUnmapped.Add(row);
                    continue;
                }
                var nonCurrentRaw = split.CurrentOnly ? 0m : row.NonCurrentAmount.GetValueOrDefault();
                var currentRaw = row.Closing - nonCurrentRaw;
                if (Math.Abs(nonCurrentRaw) > 0.004m)
                    AddPreparedAmount(row, split.NonCurrentKey, nonCurrentRaw, current, profile, result, detailIndex, auto, includeMovement: false);
                AddPreparedAmount(row, split.CurrentKey, currentRaw, current, profile, result, detailIndex, auto, includeMovement: true);
                continue;
            }

            // Trade receivable J/K is an ageing split (>6 months / <6 months) in the supplied
            // workbook, not a separate Balance Sheet current/non-current face classification.
            AddPreparedAmount(row, key, row.Closing, current, profile, result, detailIndex, auto, includeMovement: true);
            if (bifurcation == FinancialStatementClassificationService.BifurcationKind.ReceivableAge)
            {
                if (!FinancialStatementClassificationService.TryValidateBifurcation(row.Closing, row.NonCurrentAmount, out _))
                {
                    if (current) result.FyUnmapped.Add(row); else result.PyUnmapped.Add(row);
                    continue;
                }
                var over6 = NormalizeRawAmount(row, row.NonCurrentAmount.GetValueOrDefault(), key, profile);
                var under6 = NormalizeRawAmount(row, row.Closing - row.NonCurrentAmount.GetValueOrDefault(), key, profile);
                var detailKey = $"{key}\u001f{row.Ledger.Trim()}";
                if (detailIndex.TryGetValue(detailKey, out var aged))
                {
                    if (current) { aged.FyAgeOver6Months += over6; aged.FyAgeUnder6Months += under6; }
                    else { aged.PyAgeOver6Months += over6; aged.PyAgeUnder6Months += under6; }
                }
            }
        }
    }


    private static bool IsExplicitVisibleMappingOrigin(string? origin) =>
        string.Equals(origin, "User Mapped", StringComparison.OrdinalIgnoreCase) ||
        (origin ?? string.Empty).StartsWith("Copied from ", StringComparison.OrdinalIgnoreCase) ||
        (origin ?? string.Empty).StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase);

    private static string ResolveScheduleMappingKey(
        FinancialStatementLedgerRow row, string baseKey, string profile, HashSet<string> validKeys)
    {
        // The original Debtors/Creditors sheets have their own Q (Schedule C1) and R
        // (Schedule C2) fields. Q can move the party balance out of Trade Payables/
        // Trade Receivables into Other Current/Non-current Assets/Liabilities; R then
        // selects the child disclosure. This schedule authority is independent from TB C1-C3.
        var schedule = (row.ScheduleClass1 ?? string.Empty).Trim();
        var detail = (row.ScheduleClass2 ?? string.Empty).Trim();
        if (schedule.Length == 0) return baseKey;

        string[] candidates = schedule switch
        {
            "Trade Payables" => detail switch
            {
                "MSME" => new[] { "TP_MSME", "TP_OTHER" },
                "Others" => new[] { "TP_OTHER" },
                "Disputed MSME" => new[] { "TP_DISP_MSME", "TP_MSME", "TP_OTHER" },
                "Disputed Others" => new[] { "TP_DISP_OTHER", "TP_OTHER" },
                _ => Array.Empty<string>()
            },
            "Other Non-Current Liabilities - Trade Payables" => new[] { "OLTL_OTHER", "TP_OTHER" },
            "Other Current Liabilities" => detail switch
            {
                "Advance from Customers" => new[] { "OCL_ADV", "OCL_OTHER" },
                "Other Payables" => new[] { "OCL_OTHER" },
                _ => Array.Empty<string>()
            },
            "Other Non-Current Liabilities" => new[] { "OLTL_OTHER", "OCL_OTHER" },
            "Trade Receivables" => detail switch
            {
                "Secured considered good - Undisputed" or "Secured considered good - Disputed"
                    => new[] { "TR_SEC_GOOD", "TR_GOOD", "TR_UNSEC_GOOD" },
                "Unsecured considered good - Undisputed" or "Unsecured considered good - Disputed"
                    => new[] { "TR_UNSEC_GOOD", "TR_GOOD" },
                "Doubtful - Undisputed" or "Doubtful - Disputed"
                    => new[] { "TR_DOUBT" },
                "Less: Provisions for doubtful debts"
                    => new[] { "TR_ALLOW", "TR_PROV" },
                _ => Array.Empty<string>()
            },
            "Other Non-Current Assets - Trade Receivables" => new[] { "ONCA_OTHER", "OCA_OTHER" },
            "Other Current Assets" => detail switch
            {
                "Advance to suppliers" => new[] { "OCA_SUPPLIER_ADV", "OCA_OTHER" },
                "Other Receivables" => new[] { "OCA_OTHER" },
                _ => Array.Empty<string>()
            },
            "Other Non-current Assets" => new[] { "ONCA_OTHER", "OCA_OTHER" },
            _ => Array.Empty<string>()
        };
        foreach (var candidate in candidates)
            if (validKeys.Contains(candidate)) return candidate;

        // An explicit schedule selection that cannot be represented by the active statutory
        // renderer is not allowed to fall back to the TB bucket. Returning blank makes the
        // final-output readiness gate stop instead of misclassifying the party.
        return string.Empty;
    }

    private static string ResolveReadOnlyHierarchyKey(
        FinancialStatementLedgerRow row,
        string profile,
        HashSet<string> validKeys,
        IReadOnlyList<FinancialStatementMappingOption> catalog)
    {
        if (!(profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) || profile.Equals("C_AS", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(row.Classification1) || string.IsNullOrWhiteSpace(row.Classification2) ||
            string.IsNullOrWhiteSpace(row.Classification3))
            return string.Empty;

        var candidates = catalog.Where(x =>
            x.Class1.Equals(row.Classification1 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.Class2.Equals(row.Classification2 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.Class3.Equals(row.Classification3 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(row.Classification4) ||
             x.Class4.Equals(row.Classification4 ?? string.Empty, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length == 0) return string.Empty;

        FinancialStatementMappingOption? option = null;
        if (!string.IsNullOrWhiteSpace(row.Classification5))
        {
            option = candidates.FirstOrDefault(x => x.Class5.Equals(row.Classification5, StringComparison.OrdinalIgnoreCase));
            option ??= candidates.FirstOrDefault(x => x.Class5.Equals("Specify nature", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var keys = candidates.Select(x => x.MappingKey).Where(validKeys.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (keys.Length == 1) option = candidates.FirstOrDefault(x => x.MappingKey.Equals(keys[0], StringComparison.OrdinalIgnoreCase));
            option ??= candidates.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Class5));
        }

        var resolved = option?.MappingKey?.Trim() ?? string.Empty;
        var explicitUserVisibleMapping = IsExplicitVisibleMappingOrigin(row.MappingOrigin) &&
            FinancialStatementWorkbookRules.IsValidVisiblePath(
                profile, row.Classification3, row.Classification4, row.WorkbookClassification3);
        return resolved.Length > 0 && validKeys.Contains(resolved) &&
               (explicitUserVisibleMapping ||
                !FinancialStatementPresentationExportService.IsObviouslyIncompatibleMapping(row, resolved, profile))
            ? resolved
            : string.Empty;
    }

    private static void AddPreparedAmount(
        FinancialStatementLedgerRow row,
        string key,
        decimal rawClosing,
        bool currentPeriod,
        string profile,
        PreparedMappings result,
        Dictionary<string, PreparedDetail> detailIndex,
        bool auto,
        bool includeMovement)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var amount = NormalizeRawAmount(row, rawClosing, key, profile);
        var bucket = currentPeriod ? result.Fy : result.Py;
        bucket[key] = bucket.GetValueOrDefault(key) + amount;

        var detailKey = $"{key}\u001f{row.Ledger.Trim()}";
        if (!detailIndex.TryGetValue(detailKey, out var detail))
        {
            detail = new PreparedDetail { Key = key, Ledger = row.Ledger.Trim(), Parent = row.ParentGroup.Trim(), AutoMapped = auto };
            detailIndex[detailKey] = detail;
            result.Details.Add(detail);
        }
        detail.AutoMapped |= auto;
        if (currentPeriod)
        {
            detail.Fy += amount;
            if (includeMovement)
            {
                detail.FyOpening += NormalizeOpeningAmount(row, key, profile);
                detail.FyDebit += row.Debit;
                detail.FyCredit += row.Credit;
            }
        }
        else
        {
            detail.Py += amount;
            if (includeMovement)
            {
                detail.PyOpening += NormalizeOpeningAmount(row, key, profile);
                detail.PyDebit += row.Debit;
                detail.PyCredit += row.Credit;
            }
        }
    }

    private static bool RequiresFinalClassification(FinancialStatementLedgerRow x)
    {
        // R9 FIXED16 R22 FIXED6 R34: final-output materiality follows the statement being
        // prepared. Balance Sheet / Debtor / Creditor rows are year-end positions, so a
        // ledger with an authoritative zero Closing balance must not block preparation merely
        // because it had Opening/Debit/Credit movement during the year. P&L rows remain
        // period-activity statements and therefore retain movement-based classification.
        const decimal tolerance = 0.004m;
        var statement = (x.StatementDisplay ?? string.Empty).Trim();
        var nature = (x.NatureDisplay ?? string.Empty).Trim();
        var isProfitAndLoss =
            statement.Equals("P&L Account", StringComparison.OrdinalIgnoreCase) ||
            nature.Equals("Income", StringComparison.OrdinalIgnoreCase) ||
            nature.Equals("Expenses", StringComparison.OrdinalIgnoreCase);

        if (isProfitAndLoss)
            return Math.Abs(x.Closing) > tolerance || Math.Abs(x.Opening) > tolerance ||
                   Math.Abs(x.Debit) > tolerance || Math.Abs(x.Credit) > tolerance;

        return Math.Abs(x.Closing) > tolerance;
    }

    private static void EnsureReadyForFinalOutput(PreparedMappings prepared, FinancialStatementBasicInfo basicInfo)
    {
        var fy = prepared.FyUnmapped.Where(RequiresFinalClassification).ToArray();
        var py = basicInfo.PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<FinancialStatementLedgerRow>()
            : prepared.PyUnmapped.Where(RequiresFinalClassification).ToArray();
        if (fy.Length == 0 && py.Length == 0) return;

        var review = fy.Select(x => $"FY: {x.Ledger} [{x.ParentGroup}]")
            .Concat(py.Select(x => $"PY: {x.Ledger} [{x.ParentGroup}]"))
            .Take(10)
            .ToArray();
        throw new InvalidOperationException(
            $"Final Financial Statements export is blocked because {fy.Length} FY and {py.Length} PY material ledger(s) still require classification. " +
            $"Balance Sheet/Debtor/Creditor rows require a non-zero year-end closing balance; P&L rows remain movement-based. " +
            $"Complete the TB mapping and, for Debtors/Creditors, the independent party schedule classification. Review: {string.Join(", ", review)}");
    }

    private static decimal NormalizeRawAmount(FinancialStatementLedgerRow row, decimal raw, string key, string profile)
    {
        if (row.AmountSign.Equals("As Is", StringComparison.OrdinalIgnoreCase)) return raw;
        if (row.AmountSign.Equals("Reverse", StringComparison.OrdinalIgnoreCase)) return -raw;
        return IsCreditNatureKey(key, profile) ? -raw : raw;
    }

    private static decimal NormalizeClosingAmount(FinancialStatementLedgerRow row, string key, string profile)
    {
        var raw = row.Closing;
        if (raw == 0m && (row.Debit != 0m || row.Credit != 0m)) raw = row.Debit - row.Credit;
        return NormalizeRawAmount(row, raw, key, profile);
    }

    private static decimal NormalizeOpeningAmount(FinancialStatementLedgerRow row, string key, string profile)
    {
        var raw = row.Opening;
        return IsCreditNatureKey(key, profile) ? -raw : raw;
    }

    private static bool IsCreditNatureKey(string key, string profile)
    {
        var k = key.ToUpperInvariant();
        if (profile == "NCE")
        {
            return (k.StartsWith("CAP_", StringComparison.Ordinal) && k != "CAP_DRAW") ||
                   (k.StartsWith("CURR_", StringComparison.Ordinal) && k != "CURR_DR") ||
                   k.StartsWith("RES_", StringComparison.Ordinal) || k.StartsWith("BORR_", StringComparison.Ordinal) ||
                   k.StartsWith("DTL_", StringComparison.Ordinal) || k.StartsWith("OLTL_", StringComparison.Ordinal) ||
                   k.StartsWith("PROV_LT_", StringComparison.Ordinal) || k.StartsWith("PROV_ST_", StringComparison.Ordinal) ||
                   k.StartsWith("TP_", StringComparison.Ordinal) || k.StartsWith("OCL_", StringComparison.Ordinal) ||
                   k.StartsWith("REV_", StringComparison.Ordinal) || k.StartsWith("OI_", StringComparison.Ordinal);
        }
        return k.StartsWith("SC_", StringComparison.Ordinal) || k.StartsWith("RES_", StringComparison.Ordinal) ||
               k.StartsWith("LTB_", StringComparison.Ordinal) || k.StartsWith("STB_", StringComparison.Ordinal) ||
               k.StartsWith("TP_", StringComparison.Ordinal) || k.StartsWith("OCL_", StringComparison.Ordinal) ||
               k.StartsWith("REV_", StringComparison.Ordinal) || k.StartsWith("OI_", StringComparison.Ordinal);
    }

    private static void WriteHiddenData(dynamic sheet, PreparedMappings prepared, FinancialStatementBasicInfo info)
    {
        sheet.Cells[1, 1].Value2 = "AAH internal statutory data — do not display";
        sheet.Cells[2, 1].Value2 = "Mapping Key";
        sheet.Cells[2, 2].Value2 = "Ledger";
        sheet.Cells[2, 3].Value2 = CurrentLabel(info);
        sheet.Cells[2, 4].Value2 = PreviousLabel(info);
        sheet.Cells[2, 5].Value2 = "FY Opening";
        sheet.Cells[2, 6].Value2 = "PY Opening";
        sheet.Cells[2, 7].Value2 = "FY Debit";
        sheet.Cells[2, 8].Value2 = "FY Credit";
        sheet.Cells[2, 9].Value2 = "PY Debit";
        sheet.Cells[2, 10].Value2 = "PY Credit";
        sheet.Cells[2, 11].Value2 = "Parent";
        var divisor = PresentationDivisor(info);
        var row = 3;
        foreach (var d in prepared.Details.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase))
        {
            sheet.Cells[row, 1].Value2 = d.Key;
            sheet.Cells[row, 2].Value2 = d.Ledger;
            sheet.Cells[row, 3].Value2 = (double)(d.Fy / divisor);
            sheet.Cells[row, 4].Value2 = (double)(d.Py / divisor);
            sheet.Cells[row, 5].Value2 = (double)(d.FyOpening / divisor);
            sheet.Cells[row, 6].Value2 = (double)(d.PyOpening / divisor);
            sheet.Cells[row, 7].Value2 = (double)(d.FyDebit / divisor);
            sheet.Cells[row, 8].Value2 = (double)(d.FyCredit / divisor);
            sheet.Cells[row, 9].Value2 = (double)(d.PyDebit / divisor);
            sheet.Cells[row, 10].Value2 = (double)(d.PyCredit / divisor);
            sheet.Cells[row, 11].Value2 = d.Parent;
            row++;
        }
    }

    private static void WriteNceNotes12(dynamic sheet, FinancialStatementBasicInfo info)
    {
        WriteTitleBlock(sheet, info, "NOTES TO THE FINANCIAL STATEMENTS — ENTITY AND ACCOUNTING POLICIES (NOTES 1 TO 2)");
        var row = 5;
        row = WriteNoteHeading(sheet, row, "1", "Brief about the Entity");
        var entityType = string.IsNullOrWhiteSpace(info.NceType) ? "non-corporate entity" : info.NceType.Trim();
        var entity = First(info.EntityName, info.MailingName, info.CurrentYearCompanyName);
        var narrative = $"{entity} is a {entityType}. The entity maintains its books of account and these financial statements comprise the Balance Sheet as at {FormatDateLabel(info.CurrentYearEnd)}, the Statement of Profit and Loss for the year then ended, and the related notes forming an integral part of the financial statements.";
        if (!string.IsNullOrWhiteSpace(info.Address)) narrative += $" The principal / mailing address recorded for these financial statements is {info.Address.Trim()}.";
        if (!string.IsNullOrWhiteSpace(info.Pan)) narrative += $" PAN: {info.Pan.Trim()}.";
        if (!string.IsNullOrWhiteSpace(info.Gstin)) narrative += $" GSTIN: {info.Gstin.Trim()}.";
        row = WriteNarrative(sheet, row, narrative) + 2;

        row = WriteNoteHeading(sheet, row, "2", "Summary of Significant Accounting Policies");
        var policies = new (string Heading, string Text)[]
        {
            ("Basis of preparation", "The financial statements are prepared on the accrual basis under the historical-cost convention, except where another measurement basis is specifically required or disclosed, and are presented in the selected presentation unit. The accounting policies are applied consistently to the periods presented."),
            ("Use of estimates", "Preparation of financial statements requires management to make estimates and assumptions affecting reported assets, liabilities, income, expenses and disclosures. Actual results may differ from those estimates; revisions are recognised prospectively when determined."),
            ("Revenue recognition", "Revenue from sale of goods or rendering of services is recognised when the significant performance / delivery conditions are satisfied, the amount can be measured reliably and collection is reasonably certain. Revenue is presented net of applicable indirect taxes and trade adjustments, where appropriate."),
            ("Property, Plant and Equipment", "Property, plant and equipment are carried at cost less accumulated depreciation and impairment, where applicable. Cost includes directly attributable expenditure necessary to bring an asset to its intended use."),
            ("Depreciation and amortisation", "Depreciation / amortisation is provided systematically over the useful lives or period of benefit of the respective assets in accordance with the accounting policy consistently followed by the entity. Additions and disposals are considered from the date they are available for use / disposed, as applicable."),
            ("Inventories", "Inventories are valued at the lower of cost and net realisable value. Cost includes purchase cost and other expenditure incurred in bringing inventories to their present location and condition, using the cost formula consistently followed by the entity."),
            ("Investments", "Investments are classified as current or non-current based on the intended holding period and are measured in accordance with the applicable accounting framework. Diminution other than temporary, where applicable, is recognised in the Statement of Profit and Loss."),
            ("Employee benefits", "Employee benefit obligations are recognised as expense and liability as services are rendered, in accordance with the terms of employment and applicable statutory requirements."),
            ("Borrowing costs", "Borrowing costs directly attributable to acquisition or construction of a qualifying asset are capitalised when the recognition criteria are met; other borrowing costs are recognised in the Statement of Profit and Loss."),
            ("Taxes on income", "Current tax is recognised based on taxable income under applicable tax law. Deferred tax is recognised on timing differences to the extent required by the applicable accounting framework and when recognition criteria are satisfied."),
            ("Provisions and contingencies", "A provision is recognised when there is a present obligation arising from a past event, an outflow of resources is probable and the amount can be estimated reliably. Contingent liabilities and contingent assets are disclosed / recognised in accordance with the applicable framework."),
            ("Foreign currency transactions", "Foreign-currency transactions, if any, are recorded at the exchange rate on the transaction date. Monetary items are restated at the reporting-date rate and resulting exchange differences are recognised in accordance with the applicable accounting policy."),
            ("Cash and cash equivalents", "Cash and cash equivalents comprise cash on hand and balances / short-term highly liquid deposits with banks that are readily convertible to known amounts of cash and subject to insignificant risk of change in value."),
            ("Other material accounting policies", "Other accounting policies are applied consistently with the nature of the entity's transactions and the applicable financial-reporting framework. Entity-specific departures or additional policies should be reviewed in Notes Preparation before final issue.")
        };
        foreach (var p in policies)
        {
            sheet.Cells[row, 1].Value2 = p.Heading;
            sheet.Cells[row, 1].Font.Bold = true;
            sheet.Range[$"B{row}:D{row}"].Merge();
            sheet.Cells[row, 2].Value2 = p.Text;
            sheet.Cells[row, 2].WrapText = true;
            row++;
        }
    }

    private static void WriteNceNotes36(dynamic sheet, FinancialStatementBasicInfo info,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs, IReadOnlyList<OwnerPartnerRow> owners)
    {
        WriteTitleBlock(sheet, info, "NOTES TO THE FINANCIAL STATEMENTS — NOTES 3 TO 6");
        var row = 5;
        row = WriteOwnersCapitalSchedule(sheet, row, info, prepared, owners, fsRefs);
        row += 2;
        row = WriteCatalogNote(sheet, row, "4", "Reserves and Surplus", catalog, prepared, fsRefs);
        row += 2;
        row = WriteCatalogNote(sheet, row, "5", "Long-term Borrowings", catalog.Where(x => x.Note == "5").ToArray(), prepared, fsRefs);
        row += 2;
        _ = WriteCatalogNote(sheet, row, "6", "Deferred Tax Balances", catalog.Where(x => x.Note == "6").ToArray(), prepared, fsRefs);
    }

    private static void WriteNceNotes712(dynamic sheet, FinancialStatementBasicInfo info,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        WriteTitleBlock(sheet, info, "NOTES TO THE FINANCIAL STATEMENTS — LIABILITIES (NOTES 7 TO 12)");
        var row = 5;
        row = WriteCatalogNote(sheet, row, "7", "Other Long-term Liabilities", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "8", "Long-term Provisions", catalog.Where(x => x.Note == "8").ToArray(), prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "9", "Short-term Borrowings", catalog.Where(x => x.Note == "9").ToArray(), prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "10", "Trade Payables", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "11", "Other Current Liabilities", catalog, prepared, fsRefs); row += 2;
        _ = WriteCatalogNote(sheet, row, "12", "Short-term Provisions", catalog.Where(x => x.Note == "12").ToArray(), prepared, fsRefs);
    }

    private static void WriteNceNotes1316(dynamic sheet, FinancialStatementBasicInfo info,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        WriteTitleBlock(sheet, info, "NOTES TO THE FINANCIAL STATEMENTS — NON-CURRENT ASSETS (NOTES 13 TO 16)");
        var row = 5;
        row = WritePpeSchedule(sheet, row, info, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "14", "Investments", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "15", "Long-term Loans and Advances", catalog.Where(x => x.Note == "15").ToArray(), prepared, fsRefs); row += 2;
        _ = WriteCatalogNote(sheet, row, "16", "Other Non-current Assets", catalog, prepared, fsRefs);
    }

    private static void WriteNceNotes1721(dynamic sheet, FinancialStatementBasicInfo info,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        WriteTitleBlock(sheet, info, "NOTES TO THE FINANCIAL STATEMENTS — CURRENT ASSETS (NOTES 17 TO 21)");
        var row = 5;
        row = WriteCatalogNote(sheet, row, "17", "Inventories", catalog, prepared, fsRefs); row += 2;
        row = WriteTradeReceivablesSchedule(sheet, row, catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "19", "Cash and Cash Equivalents", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "20", "Short-term Loans and Advances", catalog.Where(x => x.Note == "20").ToArray(), prepared, fsRefs); row += 2;
        _ = WriteCatalogNote(sheet, row, "21", "Other Current Assets", catalog, prepared, fsRefs);
    }

    private static int WriteTradeReceivablesSchedule(dynamic sheet, int row,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        sheet.Cells[row, 1].Value2 = "Note 18 - Trade Receivables";
        sheet.Cells[row, 1].Font.Bold = true;
        row++;
        string[] headers = ["Particulars", "FY > 6 Months", "FY < 6 Months", "FY Total", "PY > 6 Months", "PY < 6 Months", "PY Total"];
        for (var c = 0; c < headers.Length; c++) { sheet.Cells[row, c + 1].Value2 = headers[c]; sheet.Cells[row, c + 1].Font.Bold = true; }
        row++;
        var start = row;
        var details = prepared.Details.Where(x => x.Key.StartsWith("TR_", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var d in details)
        {
            sheet.Cells[row, 1].Value2 = d.Ledger;
            sheet.Cells[row, 2].Value2 = (double)d.FyAgeOver6Months;
            sheet.Cells[row, 3].Value2 = (double)d.FyAgeUnder6Months;
            sheet.Cells[row, 4].Value2 = (double)d.Fy;
            sheet.Cells[row, 5].Value2 = (double)d.PyAgeOver6Months;
            sheet.Cells[row, 6].Value2 = (double)d.PyAgeUnder6Months;
            sheet.Cells[row, 7].Value2 = (double)d.Py;
            row++;
        }
        if (row == start)
        {
            sheet.Cells[row, 1].Value2 = "Trade receivables";
            sheet.Cells[row, 4].Value2 = (double)prepared.Fy.Where(x => x.Key.StartsWith("TR_", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value);
            sheet.Cells[row, 7].Value2 = (double)prepared.Py.Where(x => x.Key.StartsWith("TR_", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value);
            row++;
        }
        sheet.Cells[row, 1].Value2 = "Total Trade Receivables";
        sheet.Cells[row, 1].Font.Bold = true;
        sheet.Cells[row, 2].Formula = $"=SUM(B{start}:B{row - 1})";
        sheet.Cells[row, 3].Formula = $"=SUM(C{start}:C{row - 1})";
        sheet.Cells[row, 4].Formula = $"=SUM(D{start}:D{row - 1})";
        sheet.Cells[row, 5].Formula = $"=SUM(E{start}:E{row - 1})";
        sheet.Cells[row, 6].Formula = $"=SUM(F{start}:F{row - 1})";
        sheet.Cells[row, 7].Formula = $"=SUM(G{start}:G{row - 1})";
        fsRefs["Trade Receivables"] = $"'{sheet.Name}'!D{row}";
        return row + 1;
    }

    private static void WriteNceNotes2228(dynamic sheet, FinancialStatementBasicInfo info,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        WriteTitleBlock(sheet, info, "NOTES TO THE FINANCIAL STATEMENTS — NOTES 22 TO 28");
        var row = 5;
        row = WriteCatalogNote(sheet, row, "22", "Revenue from Operations", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "23", "Other Income", catalog, prepared, fsRefs); row += 2;
        row = WriteCostOfGoodsSold(sheet, row, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "25", "Employee Benefits Expense", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "26", "Finance Costs", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "27", "Depreciation and Amortisation Expense", catalog, prepared, fsRefs); row += 2;
        row = WriteCatalogNote(sheet, row, "28", "Other Expenses", catalog, prepared, fsRefs); row += 3;
        WriteNceAdditionalPnlDisclosures(sheet, row, prepared, fsRefs);
    }

    private static void WriteNceAdditionalPnlDisclosures(dynamic sheet, int row, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        row = WriteNoteHeading(sheet, row, "", "Exceptional Items");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;
        sheet.Cells[row, 1].Value2 = "Exceptional items";
        sheet.Cells[row, 2].Formula = DataSumFormula("EXC", 3, prepared.Details.Count + 2);
        sheet.Cells[row, 3].Formula = DataSumFormula("EXC", 4, prepared.Details.Count + 2);
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs["Exceptional items"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";
        row += 3;

        row = WriteNoteHeading(sheet, row, "", "Extraordinary Items");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;
        sheet.Cells[row, 1].Value2 = "Extraordinary items";
        sheet.Cells[row, 2].Formula = DataSumFormula("EXTRAORDINARY", 3, prepared.Details.Count + 2);
        sheet.Cells[row, 3].Formula = DataSumFormula("EXTRAORDINARY", 4, prepared.Details.Count + 2);
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs["Extraordinary items"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";
        row += 3;

        row = WriteNoteHeading(sheet, row, "", "Partners' Remuneration");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;
        sheet.Cells[row, 1].Value2 = "Partners' remuneration";
        sheet.Cells[row, 2].Formula = DataSumFormula("PARTNER_REMUN", 3, prepared.Details.Count + 2);
        sheet.Cells[row, 3].Formula = DataSumFormula("PARTNER_REMUN", 4, prepared.Details.Count + 2);
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs["Partners' remuneration"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";
        row += 3;

        row = WriteNoteHeading(sheet, row, "", "Tax Expense");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;
        var taxStart = row;
        var currentTaxRow = row;
        row = WriteKeyLineSimple(sheet, row, "Current tax", "TAX_CURR", prepared);
        fsRefs["Current tax"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{currentTaxRow}";
        var deferredTaxRow = row;
        row = WriteKeyLineSimple(sheet, row, "Deferred tax", "TAX_DEFER", prepared);
        fsRefs["Deferred tax"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{deferredTaxRow}";
        var priorTaxRow = row;
        row = WriteKeyLineSimple(sheet, row, "Earlier-year tax adjustment", "TAX_PRIOR", prepared);
        fsRefs["Prior year tax"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{priorTaxRow}";
        sheet.Cells[row, 1].Value2 = "Total Tax Expense";
        sheet.Cells[row, 2].Formula = $"=SUM(B{taxStart}:B{row - 1})";
        sheet.Cells[row, 3].Formula = $"=SUM(C{taxStart}:C{row - 1})";
        sheet.Range[$"A{row}:C{row}"].Font.Bold = true;
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs["Tax expense"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";

        row += 3;
        row = WriteNoteHeading(sheet, row, "", "Discontinuing Operations");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;
        var discontinuingProfitRow = row;
        row = WriteKeyLineSimple(sheet, row, "Profit/(Loss) from Discontinuing Operations", "DISCONT_PROFIT", prepared);
        fsRefs["Profit/(Loss) from Discontinuing Operations"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{discontinuingProfitRow}";
        var discontinuingTaxRow = row;
        row = WriteKeyLineSimple(sheet, row, "Tax Expense of Discontinuing Operations", "DISCONT_TAX", prepared);
        fsRefs["Tax Expense of Discontinuing Operations"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{discontinuingTaxRow}";
    }

    private static int WriteOwnersCapitalSchedule(dynamic sheet, int row, FinancialStatementBasicInfo info,
        PreparedMappings prepared, IReadOnlyList<OwnerPartnerRow> owners, Dictionary<string, string> fsRefs)
    {
        row = WriteNoteHeading(sheet, row, "3", "Owners' / Partners' Capital and Current Accounts");
        sheet.Cells[row, 1].Value2 = "Name of Partner / Proprietor / Owner";
        sheet.Cells[row, 2].Value2 = "Share %";
        sheet.Cells[row, 3].Value2 = "Opening Balance";
        sheet.Cells[row, 4].Value2 = "Capital Introduced";
        sheet.Cells[row, 5].Value2 = "Remuneration";
        sheet.Cells[row, 6].Value2 = "Interest";
        sheet.Cells[row, 7].Value2 = "Withdrawals";
        sheet.Cells[row, 8].Value2 = "Share of Profit/(Loss)";
        sheet.Cells[row, 9].Value2 = "Closing Balance";
        StyleHeader(sheet.Range[$"A{row}:I{row}"]);
        row++;

        var list = owners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        if (list.Count == 0)
        {
            list.Add(new OwnerPartnerRow
            {
                Name = First(info.SignatoryName, info.EntityName, info.MailingName),
                ShareOfProfitLoss = 100m
            });
        }
        var shareTotal = list.Sum(x => x.ShareOfProfitLoss);
        if (shareTotal == 0m && list.Count == 1) list[0].ShareOfProfitLoss = 100m;

        var dataLast = Math.Max(3, prepared.Details.Count + 2);
        var start = row;
        for (var i = 0; i < list.Count; i++)
        {
            var owner = list[i];
            var share = owner.ShareOfProfitLoss;
            if (share == 0m && list.Count > 0) share = 100m / list.Count;
            sheet.Cells[row, 1].Value2 = owner.Name;
            sheet.Cells[row, 2].Value2 = (double)share / 100d;
            sheet.Cells[row, 2].NumberFormat = "0.00%";
            var ownerCriteria = EscapeFormulaText(owner.Name);

            if (list.Count == 1)
            {
                sheet.Cells[row, 3].Formula = DataSumFormula("CAP_OPEN", 5, dataLast);
                // Credits in the capital ledger plus explicitly mapped capital-addition ledgers.
                var capitalCredits = DataSumFormulaBody("CAP_OPEN", 8, dataLast);
                var capitalAdds = DataSumFormulaBody("CAP_ADD", 3, dataLast);
                sheet.Cells[row, 4].Formula = $"=SUM({capitalCredits},{capitalAdds})";
                sheet.Cells[row, 5].Formula = DataSumFormula("PARTNER_REMUN", 3, dataLast);
                sheet.Cells[row, 6].Value2 = 0d;
                var capitalDebits = DataSumFormulaBody("CAP_OPEN", 7, dataLast);
                var explicitDrawings = DataSumFormulaBody("CAP_DRAW", 3, dataLast);
                sheet.Cells[row, 7].Formula = $"=SUM({capitalDebits},{explicitDrawings})";
            }
            else
            {
                sheet.Cells[row, 3].Formula = DataLedgerContainsSumFormula("CAP_OPEN", owner.Name, 5, dataLast);
                var ownerCapitalCredits = DataLedgerContainsSumFormulaBody("CAP_OPEN", owner.Name, 8, dataLast);
                var ownerCapitalAdds = DataLedgerContainsSumFormulaBody("CAP_ADD", owner.Name, 3, dataLast);
                sheet.Cells[row, 4].Formula = $"=SUM({ownerCapitalCredits},{ownerCapitalAdds})";
                sheet.Cells[row, 5].Formula = DataLedgerContainsSumFormula("PARTNER_REMUN", owner.Name, 3, dataLast);
                sheet.Cells[row, 6].Value2 = 0d;
                var ownerCapitalDebits = DataLedgerContainsSumFormulaBody("CAP_OPEN", owner.Name, 7, dataLast);
                var ownerDrawings = DataLedgerContainsSumFormulaBody("CAP_DRAW", owner.Name, 3, dataLast);
                sheet.Cells[row, 7].Formula = $"=SUM({ownerCapitalDebits},{ownerDrawings})";
            }
            // Share of profit/(loss) is bound after the P&L has been created.
            sheet.Cells[row, 8].Value2 = 0d;
            sheet.Cells[row, 9].Formula = $"=C{row}+D{row}+E{row}+F{row}-G{row}+H{row}";
            sheet.Range[$"C{row}:I{row}"].NumberFormat = NumberFormat;
            row++;
        }
        var capitalTotalRow = row;
        sheet.Cells[row, 1].Value2 = "Total Owners' / Partners' Capital";
        for (var col = 3; col <= 9; col++)
            sheet.Cells[row, col].Formula = $"=SUM({ColumnName(col)}{start}:{ColumnName(col)}{row - 1})";
        sheet.Range[$"A{row}:I{row}"].Font.Bold = true;
        sheet.Range[$"C{row}:I{row}"].NumberFormat = NumberFormat;
        row += 2;

        var hasCurrentAccount = prepared.Details.Any(x => x.Key.StartsWith("CURR_", StringComparison.OrdinalIgnoreCase) &&
            (x.Fy != 0m || x.Py != 0m || x.FyOpening != 0m || x.PyOpening != 0m || x.FyDebit != 0m || x.FyCredit != 0m));
        int? currentTotalRow = null;
        if (hasCurrentAccount)
        {
            sheet.Cells[row, 1].Value2 = "Owners' / Partners' Current Accounts";
            sheet.Cells[row, 1].Font.Bold = true;
            row++;
            sheet.Cells[row, 1].Value2 = "Name";
            sheet.Cells[row, 2].Value2 = "Opening";
            sheet.Cells[row, 3].Value2 = "Credits";
            sheet.Cells[row, 4].Value2 = "Debits / Drawings";
            sheet.Cells[row, 5].Value2 = "Share of Profit/(Loss)";
            sheet.Cells[row, 6].Value2 = "Closing";
            StyleHeader(sheet.Range[$"A{row}:F{row}"]);
            row++;
            var currentStart = row;
            foreach (var owner in list)
            {
                sheet.Cells[row, 1].Value2 = owner.Name;
                if (list.Count == 1)
                {
                    sheet.Cells[row, 2].Formula = DataSumFormula("CURR_OPEN", 5, dataLast);
                    var currentCredits = DataSumFormulaBody("CURR_OPEN", 8, dataLast);
                    var explicitCurrentCredits = DataSumFormulaBody("CURR_CR", 3, dataLast);
                    sheet.Cells[row, 3].Formula = $"=SUM({currentCredits},{explicitCurrentCredits})";
                    var currentDebits = DataSumFormulaBody("CURR_OPEN", 7, dataLast);
                    var explicitCurrentDebits = DataSumFormulaBody("CURR_DR", 3, dataLast);
                    sheet.Cells[row, 4].Formula = $"=SUM({currentDebits},{explicitCurrentDebits})";
                    sheet.Cells[row, 5].Formula = DataSumFormula("CURR_SHAREPL", 3, dataLast);
                }
                else
                {
                    sheet.Cells[row, 2].Formula = DataLedgerContainsSumFormula("CURR_OPEN", owner.Name, 5, dataLast);
                    var ownerCurrentCredits = DataLedgerContainsSumFormulaBody("CURR_OPEN", owner.Name, 8, dataLast);
                    var ownerExplicitCredits = DataLedgerContainsSumFormulaBody("CURR_CR", owner.Name, 3, dataLast);
                    sheet.Cells[row, 3].Formula = $"=SUM({ownerCurrentCredits},{ownerExplicitCredits})";
                    var ownerCurrentDebits = DataLedgerContainsSumFormulaBody("CURR_OPEN", owner.Name, 7, dataLast);
                    var ownerExplicitDebits = DataLedgerContainsSumFormulaBody("CURR_DR", owner.Name, 3, dataLast);
                    sheet.Cells[row, 4].Formula = $"=SUM({ownerCurrentDebits},{ownerExplicitDebits})";
                    sheet.Cells[row, 5].Formula = DataLedgerContainsSumFormula("CURR_SHAREPL", owner.Name, 3, dataLast);
                }
                sheet.Cells[row, 6].Formula = $"=B{row}+C{row}-D{row}+E{row}";
                sheet.Range[$"B{row}:F{row}"].NumberFormat = NumberFormat;
                row++;
            }
            currentTotalRow = row;
            sheet.Cells[row, 1].Value2 = "Total Owners' / Partners' Current Accounts";
            for (var col = 2; col <= 6; col++)
                sheet.Cells[row, col].Formula = $"=SUM({ColumnName(col)}{currentStart}:{ColumnName(col)}{row - 1})";
            sheet.Range[$"A{row}:F{row}"].Font.Bold = true;
            sheet.Range[$"B{row}:F{row}"].NumberFormat = NumberFormat;
            row += 2;
        }

        sheet.Cells[row, 1].Value2 = "Total Owners' Funds represented by Note 3";
        sheet.Cells[row, 2].Formula = currentTotalRow.HasValue
            ? $"=I{capitalTotalRow}+F{currentTotalRow.Value}"
            : $"=I{capitalTotalRow}";
        var pyCapital = DataSumFormulaBody("CAP_OPEN", 4, dataLast);
        var pyCapitalAdd = DataSumFormulaBody("CAP_ADD", 4, dataLast);
        var pyDraw = DataSumFormulaBody("CAP_DRAW", 4, dataLast);
        var pyCurrOpen = DataSumFormulaBody("CURR_OPEN", 4, dataLast);
        var pyCurrCr = DataSumFormulaBody("CURR_CR", 4, dataLast);
        var pyCurrDr = DataSumFormulaBody("CURR_DR", 4, dataLast);
        var pyCurrPl = DataSumFormulaBody("CURR_SHAREPL", 4, dataLast);
        sheet.Cells[row, 3].Formula = $"=SUM({pyCapital},{pyCapitalAdd},-{pyDraw},{pyCurrOpen},{pyCurrCr},-{pyCurrDr},{pyCurrPl})";
        sheet.Range[$"A{row}:C{row}"].Font.Bold = true;
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        // Face-statement parity with FSforNCE.xlsm: capital and current accounts are
        // displayed on separate lines even though they share Note 3.
        fsRefs["Owners' capital account"] = $"'{EscapeSheetName((string)sheet.Name)}'!I{capitalTotalRow}";
        fsRefs["Owners' current account"] = currentTotalRow.HasValue
            ? $"'{EscapeSheetName((string)sheet.Name)}'!F{currentTotalRow.Value}"
            : "0";
        fsRefs["Owners' funds"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";
        return row + 1;
    }

    private static string DataSumFormulaBody(string key, int amountColumn, int lastRow)
        => $"SUMIFS('{DataSheetName}'!${ColumnName(amountColumn)}$3:${ColumnName(amountColumn)}${lastRow},'{DataSheetName}'!$A$3:$A${lastRow},\"{EscapeFormulaText(key)}\")";

    private static string DataLedgerContainsSumFormula(string key, string ledgerText, int amountColumn, int lastRow)
        => $"={DataLedgerContainsSumFormulaBody(key, ledgerText, amountColumn, lastRow)}";

    private static string DataLedgerContainsSumFormulaBody(string key, string ledgerText, int amountColumn, int lastRow)
        => $"SUMIFS('{DataSheetName}'!${ColumnName(amountColumn)}$3:${ColumnName(amountColumn)}${lastRow},'{DataSheetName}'!$A$3:$A${lastRow},\"{EscapeFormulaText(key)}\",'{DataSheetName}'!$B$3:$B${lastRow},\"*{EscapeFormulaText(ledgerText)}*\")";

    private static void BindNceProfitToCapital(dynamic notes36, dynamic pnl)
    {
        try
        {
            // R9 FIXED14: the previous renderer still linked Note 3 to H33, a row from an
            // older P&L layout. In the current NCE face H33 is not the final profit line, so
            // current-year profit/loss was omitted from owners' funds and a fully mapped TB
            // could fail the final Balance Sheet equality gate by exactly the P&L result.
            // Resolve the final face line from the P&L itself so future row movements cannot
            // silently break the capital bridge again.
            var pnlUsed = Convert.ToInt32(pnl.UsedRange.Rows.Count, CultureInfo.InvariantCulture);
            var profitRow = 0;
            for (var row = 1; row <= pnlUsed; row++)
            {
                var text = Convert.ToString(pnl.Cells[row, ProfitLossFace.ParticularColumn].Value2, CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.StartsWith("Profit/(Loss) for the Period", StringComparison.OrdinalIgnoreCase))
                {
                    profitRow = row;
                    break;
                }
            }
            if (profitRow <= 0)
                throw new InvalidOperationException("The final NCE Profit/(Loss) for the Period line could not be located for the owners' capital bridge.");

            var used = Convert.ToInt32(notes36.UsedRange.Rows.Count, CultureInfo.InvariantCulture);
            var headerRow = 0;
            var totalRow = 0;
            for (var row = 1; row <= used; row++)
            {
                var text = Convert.ToString(notes36.Cells[row, 1].Value2, CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.Equals("Name of Partner / Proprietor / Owner", StringComparison.OrdinalIgnoreCase)) headerRow = row;
                if (text.Equals("Total Owners' / Partners' Capital", StringComparison.OrdinalIgnoreCase))
                {
                    totalRow = row;
                    break;
                }
            }
            if (headerRow <= 0 || totalRow <= headerRow + 1)
                throw new InvalidOperationException("The owners' capital movement rows could not be located for the NCE Profit/(Loss) bridge.");

            var dataStart = headerRow + 1;
            var dataEnd = totalRow - 1;
            var pnlSheet = EscapeSheetName(Convert.ToString(pnl.Name, CultureInfo.InvariantCulture) ?? "Statement of Profit and Loss");
            var profitColumn = ColumnName(ProfitLossFace.FyColumn);
            for (var row = dataStart; row <= dataEnd; row++)
                notes36.Cells[row, 8].Formula = $"='{pnlSheet}'!{profitColumn}{profitRow}*B{row}";
            notes36.Cells[totalRow, 8].Formula = $"=SUM(H{dataStart}:H{dataEnd})";
            notes36.Cells[totalRow, 9].Formula = $"=SUM(I{dataStart}:I{dataEnd})";
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to bind the final NCE Profit/(Loss) into owners' capital before Balance Sheet validation.", ex);
        }
    }

    private static int WritePpeSchedule(dynamic sheet, int row, FinancialStatementBasicInfo info,
        PreparedMappings prepared, Dictionary<string, string> fsRefs)
    {
        row = WriteNoteHeading(sheet, row, "13", "Property, Plant and Equipment and Intangible Assets");
        sheet.Cells[row, 1].Value2 = "Asset / Ledger";
        sheet.Cells[row, 2].Value2 = "Opening Balance";
        sheet.Cells[row, 3].Value2 = "Additions / Debit";
        sheet.Cells[row, 4].Value2 = "Disposals / Credit";
        sheet.Cells[row, 5].Value2 = "Closing Balance";
        sheet.Cells[row, 6].Value2 = "Previous Year Closing";
        StyleHeader(sheet.Range[$"A{row}:F{row}"]);
        row++;
        var start = row;
        foreach (var d in prepared.Details.Where(x => x.Key.StartsWith("PPE_", StringComparison.OrdinalIgnoreCase) ||
                                                           x.Key.StartsWith("INTANG_", StringComparison.OrdinalIgnoreCase) ||
                                                           x.Key.Equals("CWIP", StringComparison.OrdinalIgnoreCase) ||
                                                           x.Key.Equals("IAUD", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase))
        {
            sheet.Cells[row, 1].Value2 = d.Ledger;
            sheet.Cells[row, 2].Value2 = (double)(d.FyOpening / PresentationDivisor(info));
            sheet.Cells[row, 3].Value2 = (double)(d.FyDebit / PresentationDivisor(info));
            sheet.Cells[row, 4].Value2 = (double)(d.FyCredit / PresentationDivisor(info));
            sheet.Cells[row, 5].Value2 = (double)(d.Fy / PresentationDivisor(info));
            sheet.Cells[row, 6].Value2 = (double)(d.Py / PresentationDivisor(info));
            sheet.Range[$"B{row}:F{row}"].NumberFormat = NumberFormat;
            row++;
        }
        if (row == start)
        {
            sheet.Cells[row, 1].Value2 = "No mapped PPE / intangible asset ledgers";
            row++;
        }
        sheet.Cells[row, 1].Value2 = "Total";
        sheet.Cells[row, 5].Formula = $"=SUM(E{start}:E{row - 1})";
        sheet.Cells[row, 6].Formula = $"=SUM(F{start}:F{row - 1})";
        sheet.Range[$"A{row}:F{row}"].Font.Bold = true;
        sheet.Range[$"B{row}:F{row}"].NumberFormat = NumberFormat;
        // The visible Total above is the combined Note 13 schedule total. Each Balance Sheet
        // face line must still link only to its own asset class; otherwise PPE would include
        // intangibles/CWIP and those lines would be counted again on the face statement.
        var helperRow = row + 1;
        var helperStartRow = helperRow;
        var dataLast = prepared.Details.Count + 2;
        foreach (var pair in new[] { ("Property, Plant and Equipment", "PPE_"), ("Intangible assets", "INTANG_") })
        {
            var prefixKeys = prepared.Details.Where(x => x.Key.StartsWith(pair.Item2, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            sheet.Cells[helperRow, 1].Value2 = pair.Item1;
            sheet.Cells[helperRow, 5].Formula = DataMultiKeySumFormula(prefixKeys, 3, dataLast);
            sheet.Cells[helperRow, 6].Formula = DataMultiKeySumFormula(prefixKeys, 4, dataLast);
            fsRefs[pair.Item1] = $"'{EscapeSheetName((string)sheet.Name)}'!E{helperRow}";
            helperRow++;
        }
        foreach (var pair in new[] { ("Capital work-in-progress", "CWIP"), ("Intangible assets under development", "IAUD") })
        {
            sheet.Cells[helperRow, 1].Value2 = pair.Item1;
            sheet.Cells[helperRow, 5].Formula = DataSumFormula(pair.Item2, 3, dataLast);
            sheet.Cells[helperRow, 6].Formula = DataSumFormula(pair.Item2, 4, dataLast);
            fsRefs[pair.Item1] = $"'{EscapeSheetName((string)sheet.Name)}'!E{helperRow}";
            helperRow++;
        }
        // These rows exist only so face-statement formulas have stable references. They are
        // deliberately hidden just like the helper mechanics in the supplied workbooks and
        // must never appear as extra lines in Excel/PDF Notes to Accounts.
        try { sheet.Rows[$"{helperStartRow}:{helperRow - 1}"].Hidden = true; } catch { }
        return helperRow;
    }

    private static int WriteCostOfGoodsSold(dynamic sheet, int row, PreparedMappings prepared, Dictionary<string, string> fsRefs)
    {
        row = WriteNoteHeading(sheet, row, "24", "Cost of Goods Sold");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;
        var start = row;
        var lastRow = prepared.Details.Count + 2;
        var inventoryKeys = new[] { "INV_RM", "INV_WIP", "INV_FG", "INV_SIT", "INV_STORES", "INV_PACKING", "INV_OTHER" };

        var openingRow = row;
        sheet.Cells[row, 1].Value2 = "Opening inventory";
        sheet.Cells[row, 2].Formula = DataMultiKeySumFormula(inventoryKeys, 5, lastRow);
        sheet.Cells[row, 3].Formula = DataMultiKeySumFormula(inventoryKeys, 6, lastRow);
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        row++;

        var purchaseRow = row;
        row = WriteKeyLineSimple(sheet, row, "Purchases of stock-in-trade", "PUR_SIT", prepared);
        fsRefs["Purchases of Stock-in-Trade"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{purchaseRow}";

        var materialRow = row;
        row = WriteKeyLineSimple(sheet, row, "Direct expenses / material consumption", "MAT_CONS", prepared);
        fsRefs["Cost of Materials Consumed"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{materialRow}";

        var closingRow = row;
        sheet.Cells[row, 1].Value2 = "Less: Closing inventory";
        sheet.Cells[row, 2].Formula = $"=-({DataMultiKeySumFormulaBody(inventoryKeys, 3, lastRow)})";
        sheet.Cells[row, 3].Formula = $"=-({DataMultiKeySumFormulaBody(inventoryKeys, 4, lastRow)})";
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        row++;

        var adjustmentRow = row;
        row = WriteKeyLineSimple(sheet, row, "Other inventory / cost adjustment", "CHG_INV", prepared);

        var changeRow = row;
        sheet.Cells[row, 1].Value2 = "Changes in inventories";
        sheet.Cells[row, 2].Formula = $"=SUM(B{openingRow},B{closingRow},B{adjustmentRow})";
        sheet.Cells[row, 3].Formula = $"=SUM(C{openingRow},C{closingRow},C{adjustmentRow})";
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs["Changes in Inventories"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{changeRow}";
        row++;

        sheet.Cells[row, 1].Value2 = "Total Cost of Goods Sold";
        sheet.Cells[row, 2].Formula = $"=SUM(B{purchaseRow},B{materialRow},B{changeRow})";
        sheet.Cells[row, 3].Formula = $"=SUM(C{purchaseRow},C{materialRow},C{changeRow})";
        sheet.Range[$"A{row}:C{row}"].Font.Bold = true;
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs["Cost of goods sold"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";
        return row + 1;
    }

    private static string DataMultiKeySumFormula(IEnumerable<string> keys, int amountColumn, int lastRow)
        => $"={DataMultiKeySumFormulaBody(keys, amountColumn, lastRow)}";

    private static string DataMultiKeySumFormulaBody(IEnumerable<string> keys, int amountColumn, int lastRow)
    {
        var parts = keys.Select(key => DataSumFormulaBody(key, amountColumn, lastRow)).ToArray();
        return parts.Length == 0 ? "0" : parts.Length == 1 ? parts[0] : $"SUM({string.Join(",", parts)})";
    }

    private static int WriteCatalogNote(dynamic sheet, int row, string note, string title,
        IEnumerable<FinancialStatementMappingOption> catalog, PreparedMappings prepared,
        Dictionary<string, string> fsRefs)
    {
        var options = catalog.Where(x => x.Note.Equals(note, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (options.Length == 0) return row;
        row = WriteNoteHeading(sheet, row, note, title);
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;

        foreach (var fsGroup in options.GroupBy(x => x.FsHead, StringComparer.OrdinalIgnoreCase))
        {
            var summaryRows = new List<int>();
            foreach (var option in fsGroup)
            {
                var details = prepared.Details
                    .Where(x => x.Key.Equals(option.MappingKey, StringComparison.OrdinalIgnoreCase) &&
                                (x.Fy != 0m || x.Py != 0m || x.FyOpening != 0m || x.PyOpening != 0m ||
                                 x.FyDebit != 0m || x.FyCredit != 0m || x.PyDebit != 0m || x.PyCredit != 0m))
                    .OrderBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var summaryRow = row;
                summaryRows.Add(summaryRow);
                sheet.Cells[row, 1].Value2 = option.NoteSubHead;
                sheet.Cells[row, 1].Font.Bold = true;
                sheet.Cells[row, 2].Formula = DataSumFormula(option.MappingKey, 3, prepared.Details.Count + 2);
                sheet.Cells[row, 3].Formula = DataSumFormula(option.MappingKey, 4, prepared.Details.Count + 2);
                sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
                // R15 R14: expose the exact mapping-key summary cell as a hidden renderer
                // reference so the face statements can reproduce the original workbook's
                // split lines (MSME/non-MSME, PPE/intangibles, etc.) without duplicating data.
                fsRefs[$"KEY:{option.MappingKey}"] = $"'{EscapeSheetName((string)sheet.Name)}'!B{summaryRow}";
                row++;

                // The example workbook dynamically inserts ledger detail rows below the relevant
                // note line. Reproduce that behaviour in the clean renderer so Notes contain the
                // actual ledgers, not only a list of note names / mapping buckets.
                foreach (var detail in details)
                {
                    sheet.Cells[row, 1].Value2 = detail.Ledger;
                    try { sheet.Cells[row, 1].IndentLevel = 1; } catch { }
                    sheet.Cells[row, 2].Formula = DataLedgerSumFormula(option.MappingKey, detail.Ledger, 3, prepared.Details.Count + 2);
                    sheet.Cells[row, 3].Formula = DataLedgerSumFormula(option.MappingKey, detail.Ledger, 4, prepared.Details.Count + 2);
                    sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
                    row++;
                }
            }

            var totalRow = row;
            sheet.Cells[totalRow, 1].Value2 = $"Total — {fsGroup.Key}";
            sheet.Cells[totalRow, 2].Formula = SumCellsFormula('B', summaryRows);
            sheet.Cells[totalRow, 3].Formula = SumCellsFormula('C', summaryRows);
            sheet.Range[$"A{totalRow}:C{totalRow}"].Font.Bold = true;
            sheet.Range[$"B{totalRow}:C{totalRow}"].NumberFormat = NumberFormat;
            fsRefs[fsGroup.Key] = $"'{EscapeSheetName((string)sheet.Name)}'!B{totalRow}";
            row++;
        }
        return row;
    }

    private static string SumCellsFormula(char column, IReadOnlyList<int> rows)
    {
        if (rows.Count == 0) return "=0";
        if (rows.Count == 1) return $"={column}{rows[0]}";
        return $"=SUM({string.Join(",", rows.Select(r => $"{column}{r}"))})";
    }

    private static int WriteKeyLine(dynamic sheet, int row, string label, string key, PreparedMappings prepared,
        Dictionary<string, string> fsRefs, string fsHead)
    {
        sheet.Cells[row, 1].Value2 = label;
        sheet.Cells[row, 2].Formula = DataSumFormula(key, 3, prepared.Details.Count + 2);
        sheet.Cells[row, 3].Formula = DataSumFormula(key, 4, prepared.Details.Count + 2);
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        fsRefs[fsHead] = fsRefs.TryGetValue(fsHead, out var existing)
            ? existing
            : $"'{EscapeSheetName((string)sheet.Name)}'!B{row}";
        return row + 1;
    }

    private static int WriteKeyLineSimple(dynamic sheet, int row, string label, string key, PreparedMappings prepared)
    {
        sheet.Cells[row, 1].Value2 = label;
        sheet.Cells[row, 2].Formula = DataSumFormula(key, 3, prepared.Details.Count + 2);
        sheet.Cells[row, 3].Formula = DataSumFormula(key, 4, prepared.Details.Count + 2);
        sheet.Range[$"B{row}:C{row}"].NumberFormat = NumberFormat;
        return row + 1;
    }

    private static int WriteNoteHeading(dynamic sheet, int row, string note, string title)
    {
        sheet.Range[$"A{row}:D{row}"].Merge();
        sheet.Cells[row, 1].Value2 = string.IsNullOrWhiteSpace(note) ? title : $"Note {note} — {title}";
        sheet.Cells[row, 1].Font.Bold = true;
        sheet.Cells[row, 1].Font.Size = 11;
        try { sheet.Range[$"A{row}:D{row}"].Borders[9].LineStyle = 1; } catch { }
        return row + 1;
    }

    private static int WriteNarrative(dynamic sheet, int row, string text)
    {
        sheet.Range[$"A{row}:D{row}"].Merge();
        sheet.Cells[row, 1].Value2 = text;
        sheet.Cells[row, 1].WrapText = true;
        try { sheet.Rows[row].RowHeight = 60; } catch { }
        return row + 1;
    }

    private static string DataSumFormula(string key, int amountColumn, int lastRow)
        => $"=SUMIFS('{DataSheetName}'!${ColumnName(amountColumn)}$3:${ColumnName(amountColumn)}${lastRow},'{DataSheetName}'!$A$3:$A${lastRow},\"{EscapeFormulaText(key)}\")";

    private static string DataLedgerSumFormula(string key, string ledger, int amountColumn, int lastRow)
        => $"=SUMIFS('{DataSheetName}'!${ColumnName(amountColumn)}$3:${ColumnName(amountColumn)}${lastRow},'{DataSheetName}'!$A$3:$A${lastRow},\"{EscapeFormulaText(key)}\",'{DataSheetName}'!$B$3:$B${lastRow},\"{EscapeFormulaText(ledger)}\")";

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + (column % 26)) + result;
            column /= 26;
        }
        return result;
    }

    private sealed record FaceLayout(int NumberColumn, int ParticularColumn, int NoteColumn, int FyColumn, int SpacerColumn, int PyColumn, int StartColumn, int EndColumn);

    private static readonly FaceLayout BalanceSheetFace = new(3, 4, 8, 9, 10, 11, 3, 11); // C,D,H,I,J,K - original workbooks
    private static readonly FaceLayout ProfitLossFace = new(3, 4, 7, 8, 9, 10, 3, 10);   // C,D,G,H,I,J - original workbooks

    private static void WriteNceBalanceSheet(dynamic sheet, FinancialStatementBasicInfo info, IReadOnlyDictionary<string, string> refs)
    {
        var l = BalanceSheetFace;
        WriteFaceTitleBlock(sheet, info, "Balance Sheet", l, false);

        // Exact face sequence from FSforNCE.xlsm / BS NCE.
        FaceSection(sheet, 7, "I.", "OWNERS' FUNDS AND LIABILITIES", l);

        FaceSection(sheet, 9, "", "Owners' Funds", l);
        FaceStaticNoteLine(sheet, 10, "", "Owners' Capital Account", "3", l);
        FaceRefLine(sheet, 11, "", "(i)  Owners' Capital Account", "", refs, "Owners' capital account", l);
        FaceRefLine(sheet, 12, "", "(ii)  Owners' Current Account", "", refs, "Owners' current account", l);
        FaceRefLine(sheet, 13, "", "Reserves and Surplus", "4", refs, "Reserves and surplus", l);

        FaceSection(sheet, 15, "", "Non-Current Liabilities", l);
        FaceRefLine(sheet, 16, "", "Long-term Borrowings", "5", refs, "Long-term borrowings", l);
        FaceRefLine(sheet, 17, "", "Deferred Tax Liabilities (Net)", "6", refs, "Deferred tax liabilities (Net)", l);
        FaceRefLine(sheet, 18, "", "Other Long-term Liabilities", "7", refs, "Other long-term liabilities", l);
        FaceRefLine(sheet, 19, "", "Long-term Provisions", "8", refs, "Long-term provisions", l);

        FaceSection(sheet, 21, "", "Current Liabilities", l);
        FaceRefLine(sheet, 22, "", "Short-term Borrowings", "9", refs, "Short-term borrowings", l);
        FaceRefLine(sheet, 23, "", "Trade Payables", "10", refs, "Trade payables", l);
        FaceRefLine(sheet, 24, "", "Other Current Liabilities", "11", refs, "Other current liabilities", l);
        FaceRefLine(sheet, 25, "", "Short-term Provisions", "12", refs, "Short-term provisions", l);

        FaceTotal(sheet, 27, "", "Total",
            "=SUM(I11:I13,I16:I19,I22:I25)",
            "=SUM(K11:K13,K16:K19,K22:K25)", l, true);

        FaceSection(sheet, 29, "II.", "ASSET", l);
        FaceSection(sheet, 31, "", "Non-Current Assets", l);
        FaceStaticNoteLine(sheet, 32, "", "Property, Plant and Equipment and Intangible Assets", "13", l);
        FaceRefLine(sheet, 33, "", "Property, Plant and Equipment", "", refs, "Property, Plant and Equipment", l);
        FaceRefLine(sheet, 34, "", "Intangible Assets", "", refs, "Intangible assets", l);
        FaceRefLine(sheet, 35, "", "Capital Work-in-Progress", "", refs, "Capital work-in-progress", l);
        FaceRefLine(sheet, 36, "", "Intangible Assets under Development", "", refs, "Intangible assets under development", l);
        FaceRefLine(sheet, 37, "", "Non-Current Investments", "14", refs, "Non-current investments", l);
        FaceRefLine(sheet, 38, "", "Deferred Tax Assets (Net)", "6", refs, "Deferred tax assets (Net)", l);
        FaceRefLine(sheet, 39, "", "Long-term Loans and Advances", "15", refs, "Long-term loans and advances", l);
        FaceRefLine(sheet, 40, "", "Other Non-Current Assets", "16", refs, "Other non-current assets", l);

        FaceSection(sheet, 42, "", "Current Assets", l);
        FaceRefLine(sheet, 43, "", "Current Investments", "14", refs, "Current investments", l);
        FaceRefLine(sheet, 44, "", "Inventories", "17", refs, "Inventories", l);
        FaceRefLine(sheet, 45, "", "Trade Receivables", "18", refs, "Trade Receivables", l);
        FaceRefLine(sheet, 46, "", "Cash and Cash Equivalents", "19", refs, "Cash and Cash Equivalents", l);
        FaceRefLine(sheet, 47, "", "Short-term Loans and Advances", "20", refs, "Short-term loans and advances", l);
        FaceRefLine(sheet, 48, "", "Other Current Assets", "21", refs, "Other current assets", l);

        FaceTotal(sheet, 50, "", "Total",
            "=SUM(I33:I40,I43:I48)",
            "=SUM(K33:K40,K43:K48)", l, true);
        FaceStaticNoteLine(sheet, 51, "", "Summary of Significant Accounting Policies", "2", l);
        WriteBalanceCheck(sheet, "I27", "I50", "K27", "K50");
        WriteFaceIntegralText(sheet, 53, l, "The Significant Accounting Policies and Notes form an integral part of the Financial Statements.");
    }

    private static void WriteNceProfitAndLoss(dynamic sheet, FinancialStatementBasicInfo info, IReadOnlyDictionary<string, string> refs)
    {
        var l = ProfitLossFace;
        WriteFaceTitleBlock(sheet, info, "Statement of Profit and Loss", l, true);

        FaceSection(sheet, 7, "", "INCOME", l);
        FaceRefLine(sheet, 8, "I", "Revenue from Operations", "22", refs, "Revenue from operations", l);
        FaceRefLine(sheet, 9, "II", "Other Income", "23", refs, "Other income", l);
        FaceTotal(sheet, 10, "III", "Total", "=SUM(H8:H9)", "=SUM(J8:J9)", l, true);

        FaceSection(sheet, 12, "IV", "EXPENSES", l);
        FaceRefLine(sheet, 13, "", "Cost of Materials Consumed", "24", refs, "Cost of Materials Consumed", l);
        FaceRefLine(sheet, 14, "", "Purchases of Stock-in-Trade", "24", refs, "Purchases of Stock-in-Trade", l);
        FaceRefLine(sheet, 15, "", "Changes in Inventories of Finished Goods, Work-in-Progress and Stock-in-Trade", "24", refs, "Changes in Inventories", l);
        FaceRefLine(sheet, 16, "", "Employee Benefits Expense", "25", refs, "Employee benefits expense", l);
        FaceRefLine(sheet, 17, "", "Finance Costs", "26", refs, "Finance costs", l);
        FaceRefLine(sheet, 18, "", "Depreciation and Amortisation", "27", refs, "Depreciation and Amortisation Expense", l);
        FaceRefLine(sheet, 19, "", "Other Expenses", "28", refs, "Other expenses", l);
        FaceTotal(sheet, 20, "", "Total", "=SUM(H13:H19)", "=SUM(J13:J19)", l, true);

        FaceTotal(sheet, 22, "V", "Profit/(Loss) before Exceptional and Extraordinary Items and Tax (III-IV)", "=H10-H20", "=J10-J20", l);
        FaceRefLine(sheet, 24, "VI", "Exceptional Items", "", refs, "Exceptional items", l);
        FaceTotal(sheet, 26, "VII", "Profit/(Loss) before Extraordinary Items and Tax (V - VI)", "=H22-H24", "=J22-J24", l);
        FaceRefLine(sheet, 28, "VIII", "Extraordinary Items", "", refs, "Extraordinary items", l);
        FaceTotal(sheet, 30, "IX", "Profit before, partners' remuneration and tax (VII-VIII)", "=H26-H28", "=J26-J28", l);
        FaceRefLine(sheet, 32, "X", "Partners’ Remuneration", "", refs, "Partners' remuneration", l);
        FaceTotal(sheet, 34, "XI", "Profit/(Loss) before Tax (IX - X)", "=H30-H32", "=J30-J32", l, true);

        FaceStaticNoteLine(sheet, 36, "XII", "Tax Expense", "", l);
        FaceRefLine(sheet, 37, "", "Current tax", "", refs, "Current tax", l);
        FaceRefLine(sheet, 38, "", "Deferred tax", "", refs, "Deferred tax", l);
        FaceRefLine(sheet, 39, "", "Adjustment of tax relating to earlier periods", "", refs, "Prior year tax", l);
        FaceTotal(sheet, 40, "", "Total", "=SUM(H37:H39)", "=SUM(J37:J39)", l, true);

        FaceTotal(sheet, 42, "XIII", "Profit/(Loss) for the period from Continuing Operations (XI-XII)", "=H34-H40", "=J34-J40", l);
        FaceRefLine(sheet, 44, "XIV", "Profit/(Loss) from Discontinuing Operations", "", refs, "Profit/(Loss) from Discontinuing Operations", l);
        FaceRefLine(sheet, 46, "XV", "Tax Expense of Discontinuing Operations", "", refs, "Tax Expense of Discontinuing Operations", l);
        FaceTotal(sheet, 48, "XVI", "Profit/(Loss) from Discontinuing Operations (after tax) (XIV-XV)", "=H44-H46", "=J44-J46", l);
        FaceTotal(sheet, 50, "XVII", "Profit/(Loss) for the Period (XIII + XVI)", "=H42+H48", "=J42+J48", l, true);

        FaceStaticNoteLine(sheet, 52, "", "Summary of Significant Accounting Policies", "2", l);
        WriteFaceIntegralText(sheet, 54, l, "The Significant Accounting Policies and Notes form an integral part of the Financial Statements.");
    }


    private static Dictionary<string, string> WriteCompanyNotes(dynamic sheet, FinancialStatementBasicInfo info,
        IReadOnlyList<FinancialStatementMappingOption> catalog, PreparedMappings prepared)
    {
        WriteTitleBlock(sheet, info, "NOTES TO ACCOUNTS");
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var row = 5;
        foreach (var note in catalog.Select(x => x.Note).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(ParseNote))
        {
            var options = catalog.Where(x => x.Note.Equals(note, StringComparison.OrdinalIgnoreCase)).ToArray();
            var title = options.FirstOrDefault()?.NoteHead ?? $"Note {note}";
            row = WriteCatalogNote(sheet, row, note, title, options, prepared, refs) + 2;
        }

        // Basic Info rows 39:49 in the original Schedule III workbook are the source for
        // Type of Shares / Face Value. They feed the Share Capital input/note generator but
        // do not themselves provide share-count or paid-up-value allocations. Preserve exactly
        // the information AAH actually has and never fabricate No. of Shares from TB amounts.
        var shareRows = (info.ShareCapitalDetails ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.TypeOfShares) || x.FaceValue != 0m)
            .Take(11)
            .ToArray();
        if (shareRows.Length > 0)
        {
            row += 1;
            row = WriteNoteHeading(sheet, row, "", "Share Capital Details");
            sheet.Cells[row, 1].Value2 = "Type of Shares";
            sheet.Cells[row, 2].Value2 = "Face Value";
            StyleHeader(sheet.Range[$"A{row}:B{row}"]);
            row++;
            foreach (var share in shareRows)
            {
                sheet.Cells[row, 1].Value2 = share.TypeOfShares;
                sheet.Cells[row, 2].Value2 = (double)share.FaceValue;
                sheet.Cells[row, 2].NumberFormat = NumberFormat;
                row++;
            }
            row += 1;
        }

        // The original Schedule III face contains several P&L rows that are not numbered
        // notes. Keep them in the generated Notes sheet as explicit schedules so face formulas
        // never fall back to zero merely because the line has no note number.
        row += 1;
        row = WriteNoteHeading(sheet, row, "", "Additional Statement of Profit and Loss Lines");
        sheet.Cells[row, 1].Value2 = "Particulars";
        sheet.Cells[row, 2].Value2 = "Current Year";
        sheet.Cells[row, 3].Value2 = "Previous Year";
        StyleHeader(sheet.Range[$"A{row}:C{row}"]);
        row++;

        void AddCompanyLine(string label, string key, string referenceName)
        {
            var lineRow = row;
            row = WriteKeyLineSimple(sheet, row, label, key, prepared);
            refs[referenceName] = $"'{EscapeSheetName((string)sheet.Name)}'!B{lineRow}";
        }

        AddCompanyLine("Exceptional Items", "EXC", "Exceptional Items");
        AddCompanyLine("Extraordinary Items", "EXTRAORD", "Extraordinary Items");
        AddCompanyLine("Current tax", "TAX_CURR", "Current tax");
        AddCompanyLine("Deferred tax", "TAX_DEFER", "Deferred tax");
        AddCompanyLine("Adjustment of tax relating to earlier periods", "TAX_PRIOR", "Earlier-year tax adjustment");
        refs["Prior year tax"] = refs["Earlier-year tax adjustment"];
        AddCompanyLine("Profit/(Loss) from Discontinuing Operations", "DISCONT_PROFIT", "Profit/(Loss) from Discontinuing Operations");
        AddCompanyLine("Tax Expense of Discontinuing Operations", "DISCONT_TAX", "Tax Expense of Discontinuing Operations");

        return refs;
    }

    private static void WriteDivisionIBalanceSheet(dynamic sheet, FinancialStatementBasicInfo info, IReadOnlyDictionary<string, string> refs)
    {
        var l = BalanceSheetFace;
        WriteFaceTitleBlock(sheet, info, "Balance Sheet", l, false);

        // Exact Schedule III Division I face sequence from FSforSchIIIDivI.xlsm / BS.
        FaceSection(sheet, 7, "I.", "EQUITY AND LIABILITIES", l);

        FaceSection(sheet, 9, "", "Shareholders' Fund", l);
        FaceRefLine(sheet, 10, "", "Share Capital", "1", refs, "Share Capital", l);
        FaceRefLine(sheet, 11, "", "Reserves and Surplus", "2", refs, "Reserves and Surplus", l);
        FaceRefLine(sheet, 12, "", "Money received against Share Warrants", "2A", refs, "Money received against Share Warrants", l);

        FaceRefLine(sheet, 14, "", "Share Application money pending allotment", "2B", refs, "Share Application Money pending allotment", l);

        FaceSection(sheet, 16, "", "Non-Current Liabilities", l);
        FaceRefLine(sheet, 17, "", "Long-term Borrowings", "3", refs, "Long-term Borrowings", l);
        FaceRefLine(sheet, 18, "", "Deferred Tax Liabilities (Net)", "4", refs, "Deferred Tax Liabilities (Net)", l);
        FaceRefLine(sheet, 19, "", "Other Long-term Liabilities", "5", refs, "Other Long-term Liabilities", l);
        FaceRefLine(sheet, 20, "", "Long-term Provisions", "6", refs, "Long-term Provisions", l);

        FaceSection(sheet, 22, "", "Current Liabilities", l);
        FaceRefLine(sheet, 23, "", "Short-term Borrowings", "7", refs, "Short-term Borrowings", l);
        FaceStaticNoteLine(sheet, 24, "", "Trade Payables", "8", l);
        FaceKeyLine(sheet, 25, "", "Total Outstanding Dues of MSME", "", refs, l, "TP_MSME", "TP_DISP_MSME");
        FaceKeyLine(sheet, 26, "", "Total Outstanding Dues of Creditors Other than MSME", "", refs, l, "TP_OTHER", "TP_DISP_OTHER");
        FaceRefLine(sheet, 27, "", "Other Current Liabilities", "9", refs, "Other Current Liabilities", l);
        FaceRefLine(sheet, 28, "", "Short-term Provisions", "10", refs, "Short-term Provisions", l);

        FaceTotal(sheet, 31, "", "Total",
            "=SUM(I10:I12,I14,I17:I20,I23,I25:I28)",
            "=SUM(K10:K12,K14,K17:K20,K23,K25:K28)", l, true);

        FaceSection(sheet, 33, "II.", "ASSET", l);
        FaceSection(sheet, 35, "", "Non-Current Assets", l);
        FaceStaticNoteLine(sheet, 36, "", "Property, Plant and Equipment and Intangible Assets", "11", l);
        FaceKeyLine(sheet, 37, "", "Property, Plant and Equipment", "", refs, l,
            "PPE_LAND", "PPE_BUILD", "PPE_PLANT", "PPE_FURN", "PPE_VEH", "PPE_OFFICE");
        FaceKeyLine(sheet, 38, "", "Intangible Assets", "", refs, l, "INTANG");
        FaceKeyLine(sheet, 39, "", "Capital Work-in-Progress", "", refs, l, "CWIP");
        FaceKeyLine(sheet, 40, "", "Intangible Assets under Development", "", refs, l, "IAUD");
        FaceRefLine(sheet, 41, "", "Non-Current Investments", "12", refs, "Non-current Investments", l);
        FaceRefLine(sheet, 42, "", "Deferred Tax Assets (Net)", "4", refs, "Deferred Tax Assets (Net)", l);
        FaceRefLine(sheet, 43, "", "Long-term Loans and Advances", "13", refs, "Long-term Loans and Advances", l);
        FaceRefLine(sheet, 44, "", "Other Non-Current Assets", "14", refs, "Other Non-current Assets", l);

        FaceSection(sheet, 46, "", "Current Assets", l);
        FaceRefLine(sheet, 47, "", "Current Investments", "15", refs, "Current Investments", l);
        FaceRefLine(sheet, 48, "", "Inventories", "16", refs, "Inventories", l);
        FaceRefLine(sheet, 49, "", "Trade Receivables", "17", refs, "Trade Receivables", l);
        FaceRefLine(sheet, 50, "", "Cash and Cash Equivalents", "18", refs, "Cash and Cash Equivalents", l);
        FaceRefLine(sheet, 51, "", "Short-term Loans and Advances", "19", refs, "Short-term Loans and Advances", l);
        FaceRefLine(sheet, 52, "", "Other Current Assets", "20", refs, "Other Current Assets", l);

        FaceTotal(sheet, 55, "", "Total",
            "=SUM(I37:I44,I47:I52)",
            "=SUM(K37:K44,K47:K52)", l, true);
        FaceStaticNoteLine(sheet, 56, "", "Summary of Significant Accounting Policies", "1", l);
        WriteBalanceCheck(sheet, "I31", "I55", "K31", "K55");
        WriteFaceIntegralText(sheet, 58, l, "The Significant Accounting Policies and Notes form an integral part of the Financial Statements.");
    }

    private static void WriteDivisionIProfitAndLoss(dynamic sheet, FinancialStatementBasicInfo info, IReadOnlyDictionary<string, string> refs)
    {
        var l = ProfitLossFace;
        WriteFaceTitleBlock(sheet, info, "Statement of Profit and Loss", l, true);

        FaceSection(sheet, 7, "", "INCOME", l);
        FaceRefLine(sheet, 8, "I", "Revenue from Operations", "21", refs, "Revenue from Operations", l);
        FaceRefLine(sheet, 9, "II", "Other Income", "22", refs, "Other Income", l);
        FaceTotal(sheet, 10, "III", "Total", "=SUM(H8:H9)", "=SUM(J8:J9)", l, true);

        FaceSection(sheet, 12, "IV", "EXPENSES", l);
        FaceRefLine(sheet, 13, "", "Cost of Materials Consumed", "23", refs, "Cost of Materials Consumed", l);
        FaceRefLine(sheet, 14, "", "Purchases of Stock-in-Trade", "24", refs, "Purchases of Stock-in-Trade", l);
        FaceRefLine(sheet, 15, "", "Changes in Inventories of Finished Goods, Work-in-Progress and Stock-in-Trade", "25", refs, "Changes in Inventories", l);
        FaceRefLine(sheet, 16, "", "Employee Benefits Expense", "26", refs, "Employee Benefits Expense", l);
        FaceRefLine(sheet, 17, "", "Finance Costs", "27", refs, "Finance Costs", l);
        FaceRefLine(sheet, 18, "", "Depreciation and Amortisation", "28", refs, "Depreciation and Amortisation", l);
        FaceRefLine(sheet, 19, "", "Other Expenses", "29", refs, "Other Expenses", l);
        FaceTotal(sheet, 20, "", "Total", "=SUM(H13:H19)", "=SUM(J13:J19)", l, true);

        FaceTotal(sheet, 22, "V", "Profit/(Loss) before Exceptional and Extraordinary Items and Tax (III-IV)", "=H10-H20", "=J10-J20", l);
        FaceRefLine(sheet, 24, "VI", "Exceptional Items", "", refs, "Exceptional Items", l);
        FaceTotal(sheet, 26, "VII", "Profit/(Loss) before Extraordinary Items and Tax (V - VI)", "=H22-H24", "=J22-J24", l);
        FaceRefLine(sheet, 28, "VIII", "Extraordinary Items", "", refs, "Extraordinary Items", l);
        FaceTotal(sheet, 30, "IX", "Profit/(Loss) before Tax (VII - VIII)", "=H26-H28", "=J26-J28", l, true);

        FaceStaticNoteLine(sheet, 32, "X", "Tax Expense", "", l);
        FaceRefLine(sheet, 33, "", "Current tax", "", refs, "Current tax", l);
        FaceRefLine(sheet, 34, "", "Deferred tax", "", refs, "Deferred tax", l);
        FaceRefLine(sheet, 35, "", "Adjustment of tax relating to earlier periods", "", refs, "Earlier-year tax adjustment", l);
        FaceTotal(sheet, 36, "", "Total", "=SUM(H33:H35)", "=SUM(J33:J35)", l, true);

        FaceTotal(sheet, 38, "XI", "Profit/(Loss) for the period from Continuing Operations (IX-X)", "=H30-H36", "=J30-J36", l);
        FaceRefLine(sheet, 40, "XII", "Profit/(Loss) from Discontinuing Operations", "", refs, "Profit/(Loss) from Discontinuing Operations", l);
        FaceRefLine(sheet, 42, "XIII", "Tax Expense of Discontinuing Operations", "", refs, "Tax Expense of Discontinuing Operations", l);
        FaceTotal(sheet, 44, "XIV", "Profit/(Loss) from Discontinuing Operations (after tax) (XII-XIII)", "=H40-H42", "=J40-J42", l);
        FaceTotal(sheet, 46, "XV", "Profit/(Loss) for the Period (XI + XIV)", "=H38+H44", "=J38+J44", l, true);

        FaceStaticNoteLine(sheet, 48, "XVI", "Earnings per Equity Share:", "", l);
        FaceRefLine(sheet, 49, "", "Basic", "", refs, "Basic EPS", l);
        FaceRefLine(sheet, 50, "", "Diluted", "", refs, "Diluted EPS", l);
        FaceStaticNoteLine(sheet, 52, "", "Summary of Significant Accounting Policies", "1", l);
        WriteFaceIntegralText(sheet, 54, l, "The Significant Accounting Policies and Notes form an integral part of the Financial Statements.");
    }


    private static void FaceRefLine(dynamic sheet, int row, string number, string particular, string note,
        IReadOnlyDictionary<string, string> refs, string fsHead, FaceLayout layout)
    {
        if (!string.IsNullOrWhiteSpace(number)) sheet.Cells[row, layout.NumberColumn].Value2 = number;
        sheet.Cells[row, layout.ParticularColumn].Value2 = particular;
        sheet.Cells[row, layout.NoteColumn].Value2 = note;
        if (refs.TryGetValue(fsHead, out var reference) && !string.IsNullOrWhiteSpace(reference))
        {
            sheet.Cells[row, layout.FyColumn].Formula = $"={reference}";
            sheet.Cells[row, layout.PyColumn].Formula = $"={AdjacentReference(reference)}";
        }
        else
        {
            sheet.Cells[row, layout.FyColumn].Value2 = 0d;
            sheet.Cells[row, layout.PyColumn].Value2 = 0d;
        }
        sheet.Cells[row, layout.FyColumn].NumberFormat = NumberFormat;
        sheet.Cells[row, layout.PyColumn].NumberFormat = NumberFormat;
    }

    private static void FaceStaticNoteLine(dynamic sheet, int row, string number, string particular, string note, FaceLayout layout)
    {
        if (!string.IsNullOrWhiteSpace(number)) sheet.Cells[row, layout.NumberColumn].Value2 = number;
        sheet.Cells[row, layout.ParticularColumn].Value2 = particular;
        if (!string.IsNullOrWhiteSpace(note)) sheet.Cells[row, layout.NoteColumn].Value2 = note;
    }

    private static void FaceKeyLine(dynamic sheet, int row, string number, string particular, string note,
        IReadOnlyDictionary<string, string> refs, FaceLayout layout, params string[] mappingKeys)
    {
        if (!string.IsNullOrWhiteSpace(number)) sheet.Cells[row, layout.NumberColumn].Value2 = number;
        sheet.Cells[row, layout.ParticularColumn].Value2 = particular;
        if (!string.IsNullOrWhiteSpace(note)) sheet.Cells[row, layout.NoteColumn].Value2 = note;

        var fyRefs = new List<string>();
        var pyRefs = new List<string>();
        foreach (var key in mappingKeys.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (!refs.TryGetValue($"KEY:{key}", out var reference) || string.IsNullOrWhiteSpace(reference)) continue;
            fyRefs.Add(reference);
            pyRefs.Add(AdjacentReference(reference));
        }

        sheet.Cells[row, layout.FyColumn].Formula = SumReferenceFormula(fyRefs);
        sheet.Cells[row, layout.PyColumn].Formula = SumReferenceFormula(pyRefs);
        sheet.Cells[row, layout.FyColumn].NumberFormat = NumberFormat;
        sheet.Cells[row, layout.PyColumn].NumberFormat = NumberFormat;
    }

    private static string SumReferenceFormula(IReadOnlyList<string> references)
    {
        if (references.Count == 0) return "=0";
        if (references.Count == 1) return $"={references[0]}";
        return $"=SUM({string.Join(",", references)})";
    }

    private static void FaceSection(dynamic sheet, int row, string number, string text, FaceLayout layout)
    {
        if (!string.IsNullOrWhiteSpace(number)) sheet.Cells[row, layout.NumberColumn].Value2 = number;
        sheet.Cells[row, layout.ParticularColumn].Value2 = text;
        sheet.Range[$"{ColumnName(layout.NumberColumn)}{row}:{ColumnName(layout.PyColumn)}{row}"].Font.Bold = true;
    }

    private static void FaceTotal(dynamic sheet, int row, string number, string text, string fyFormula, string pyFormula,
        FaceLayout layout, bool strong = false)
    {
        if (!string.IsNullOrWhiteSpace(number)) sheet.Cells[row, layout.NumberColumn].Value2 = number;
        sheet.Cells[row, layout.ParticularColumn].Value2 = text;
        sheet.Cells[row, layout.FyColumn].Formula = fyFormula;
        sheet.Cells[row, layout.PyColumn].Formula = pyFormula;
        sheet.Cells[row, layout.FyColumn].NumberFormat = NumberFormat;
        sheet.Cells[row, layout.PyColumn].NumberFormat = NumberFormat;
        var range = sheet.Range[$"{ColumnName(layout.NumberColumn)}{row}:{ColumnName(layout.PyColumn)}{row}"];
        range.Font.Bold = true;
        try { range.Borders[8].LineStyle = 1; } catch { }
        if (strong)
        {
            try { range.Borders[9].LineStyle = -4119; } catch { try { range.Borders[9].LineStyle = 1; } catch { } }
        }
    }

    private static void WriteFaceTitleBlock(dynamic sheet, FinancialStatementBasicInfo info, string title, FaceLayout layout, bool profitAndLoss)
    {
        var start = ColumnName(layout.StartColumn);
        var end = ColumnName(layout.EndColumn);
        var entity = First(info.EntityName, info.CurrentYearCompanyName, info.MailingName);
        var current = CurrentLabel(info);
        var previous = PreviousLabel(info);

        // Both supplied workbooks use a different title stack for the P&L face than for the
        // Balance Sheet. Do not collapse them into a generic three-row title: doing so moves
        // every subsequent print anchor and changes the PDF against the examples.
        if (profitAndLoss)
        {
            sheet.Range[$"{start}2:{end}2"].Merge();
            sheet.Range[$"{start}2"].Value2 = entity;
            sheet.Range[$"{start}2"].Font.Bold = true;
            sheet.Range[$"{start}2"].Font.Size = 11;
            sheet.Range[$"{start}2"].HorizontalAlignment = -4108;

            sheet.Range[$"{start}3:{end}3"].Merge();
            sheet.Range[$"{start}3"].Value2 = $"{title} for the year ended {current}";
            sheet.Range[$"{start}3"].Font.Bold = true;
            sheet.Range[$"{start}3"].Font.Size = 11;
            sheet.Range[$"{start}3"].HorizontalAlignment = -4108;

            sheet.Cells[4, layout.PyColumn].Value2 = PresentationUnitLabel(info);
            sheet.Cells[4, layout.PyColumn].Font.Size = 10;
            sheet.Cells[4, layout.PyColumn].HorizontalAlignment = -4152;
        }
        else
        {
            sheet.Range[$"{start}1:{end}1"].Merge();
            sheet.Range[$"{start}1"].Value2 = entity;
            sheet.Range[$"{start}1"].Font.Bold = true;
            sheet.Range[$"{start}1"].Font.Size = 11;
            sheet.Range[$"{start}1"].HorizontalAlignment = -4108;

            sheet.Range[$"{start}2:{end}2"].Merge();
            sheet.Range[$"{start}2"].Value2 = $"{title} as at {current}";
            sheet.Range[$"{start}2"].Font.Bold = true;
            sheet.Range[$"{start}2"].Font.Size = 11;
            sheet.Range[$"{start}2"].HorizontalAlignment = -4108;

            var registrationPrefix = FinancialStatementWorkbookService.ResolveProfileSheet(info.EntityType).Equals("C_AS", StringComparison.OrdinalIgnoreCase)
                ? "CIN"
                : "LLPIN";
            var identity = !string.IsNullOrWhiteSpace(info.LlpIn)
                ? $"{registrationPrefix} - {info.LlpIn.Trim()}"
                : !string.IsNullOrWhiteSpace(info.Pan) ? $"PAN - {info.Pan.Trim()}" : string.Empty;
            sheet.Range[$"{start}3:{end}3"].Merge();
            sheet.Range[$"{start}3"].Value2 = identity;
            sheet.Range[$"{start}3"].HorizontalAlignment = -4108;

            sheet.Cells[4, layout.PyColumn].Value2 = PresentationUnitLabel(info);
            sheet.Cells[4, layout.PyColumn].Font.Size = 10;
            sheet.Cells[4, layout.PyColumn].HorizontalAlignment = -4152;
        }

        var particularsEnd = profitAndLoss ? "F" : "G";
        sheet.Range[$"C5:{particularsEnd}6"].Merge();
        sheet.Range["C5"].Value2 = "Particulars";
        sheet.Range[$"{ColumnName(layout.NoteColumn)}5:{ColumnName(layout.NoteColumn)}6"].Merge();
        sheet.Cells[5, layout.NoteColumn].Value2 = "Notes";
        sheet.Range[$"{ColumnName(layout.FyColumn)}5:{ColumnName(layout.FyColumn)}6"].Merge();
        sheet.Cells[5, layout.FyColumn].Value2 = profitAndLoss ? $"For the year ended {current}" : $"As at {current}";
        sheet.Range[$"{ColumnName(layout.PyColumn)}5:{ColumnName(layout.PyColumn)}6"].Merge();
        sheet.Cells[5, layout.PyColumn].Value2 = profitAndLoss ? $"For the year ended {previous}" : $"As at {previous}";

        var header = sheet.Range[$"C5:{ColumnName(layout.PyColumn)}6"];
        header.Font.Bold = true;
        header.WrapText = true;
        header.HorizontalAlignment = -4108;
        header.VerticalAlignment = -4108;
        try { header.Borders[8].LineStyle = 1; } catch { }
        try { header.Borders[9].LineStyle = 1; } catch { }
    }

    private static void WriteFaceIntegralText(dynamic sheet, int row, FaceLayout layout, string text)
    {
        var start = ColumnName(layout.StartColumn);
        var end = ColumnName(layout.EndColumn);
        sheet.Range[$"{start}{row}:{end}{row}"].Merge();
        sheet.Range[$"{start}{row}"].Value2 = text;
        sheet.Range[$"{start}{row}"].Font.Italic = true;
        sheet.Range[$"{start}{row}"].WrapText = true;
    }

    private static string AdjacentReference(string reference)
    {
        var bang = reference.LastIndexOf('!');
        if (bang < 0 || bang + 1 >= reference.Length) return reference;
        var cell = reference[(bang + 1)..].Replace("$", string.Empty, StringComparison.Ordinal);
        var letters = new string(cell.TakeWhile(char.IsLetter).ToArray());
        var digits = new string(cell.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
        if (letters.Length == 0 || digits.Length == 0) return reference;
        var column = 0;
        foreach (var ch in letters.ToUpperInvariant()) column = column * 26 + (ch - 'A' + 1);
        return reference[..(bang + 1)] + ColumnName(column + 1) + digits;
    }

    private static void WriteBalanceCheck(dynamic sheet, string fyLiabilities, string fyAssets, string pyLiabilities, string pyAssets)
    {
        sheet.Range["M1"].Formula = $"={fyLiabilities}-{fyAssets}";
        sheet.Range["N1"].Formula = $"={pyLiabilities}-{pyAssets}";
        sheet.Range["M1:N1"].NumberFormat = NumberFormat;
        try { sheet.Columns[13].Hidden = true; sheet.Columns[14].Hidden = true; } catch { }
    }

    private static void ApplyExampleFaceZeroRowVisibility(dynamic balanceSheet, dynamic profitAndLoss, string profile)
    {
        // The supplied workbooks hide only these designated face rows when both CY and PY
        // amounts are zero. Section headings/totals outside these lists stay visible.
        var bsRows = profile.Equals("NCE", StringComparison.OrdinalIgnoreCase)
            ? new[] { 10, 11, 12, 13, 16, 17, 18, 19, 22, 23, 24, 25, 33, 34, 35, 36, 37, 38, 39, 40, 43, 44, 45, 46, 47, 48 }
            : new[] { 10, 11, 12, 14, 17, 18, 19, 20, 23, 25, 26, 27, 28, 37, 38, 39, 40, 41, 42, 43, 44, 47, 48, 49, 50, 51, 52 };
        var plRows = profile.Equals("NCE", StringComparison.OrdinalIgnoreCase)
            ? new[] { 8, 9, 13, 14, 15, 16, 17, 18, 19, 44, 46 }
            : new[] { 8, 9, 13, 14, 15, 16, 17, 18, 19, 33, 34, 35, 40, 42 };

        static decimal CellAmount(dynamic sheet, string address)
        {
            try
            {
                var value = sheet.Range[address].Value2;
                return value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch { return 0m; }
        }

        foreach (var row in bsRows)
        {
            var hide = Math.Abs(CellAmount(balanceSheet, $"I{row}")) < 0.005m &&
                       Math.Abs(CellAmount(balanceSheet, $"K{row}")) < 0.005m;
            try { balanceSheet.Rows[row].Hidden = hide; } catch { }
        }
        foreach (var row in plRows)
        {
            var hide = Math.Abs(CellAmount(profitAndLoss, $"H{row}")) < 0.005m &&
                       Math.Abs(CellAmount(profitAndLoss, $"J{row}")) < 0.005m;
            try { profitAndLoss.Rows[row].Hidden = hide; } catch { }
        }
    }

    private static void VerifyBalanceSheet(
        dynamic sheet,
        IReadOnlyList<FinancialStatementLedgerRow> fyRows,
        IReadOnlyList<FinancialStatementLedgerRow> pyRows)
    {
        try
        {
            var fy = Convert.ToDecimal(sheet.Range["M1"].Value2 ?? 0d, CultureInfo.InvariantCulture);
            var py = Convert.ToDecimal(sheet.Range["N1"].Value2 ?? 0d, CultureInfo.InvariantCulture);
            if (Math.Abs(fy) > 0.50m || Math.Abs(py) > 0.50m)
            {
                // R9 FIXED14: after the workbook-required P&L-to-capital bridge, a remaining
                // face difference is not cured by moving an amount between current/non-current
                // buckets on the same Balance Sheet side. Surface the signed source-TB residual
                // as separate evidence and never create a balancing plug.
                var fyTbResidual = fyRows.Sum(x => x.Closing);
                var pyTbResidual = pyRows.Sum(x => x.Closing);
                throw new InvalidOperationException(
                    $"Final Balance Sheet is not balanced after applying the NCE Profit/(Loss)-to-capital bridge. " +
                    $"FY difference: {fy:N2}; PY difference: {py:N2}. " +
                    $"Source TB signed closing residual — FY: {fyTbResidual:N2}; PY: {pyTbResidual:N2}. " +
                    "AAH will not create a balancing plug; review any remaining wrong-side mapping or source Trial Balance imbalance.");
            }
        }
        catch (InvalidOperationException) { throw; }
        catch { }
    }

    private static void WriteTitleBlock(dynamic sheet, FinancialStatementBasicInfo info, string title)
    {
        sheet.Range["A1:I1"].Merge();
        sheet.Range["A1"].Value2 = First(info.EntityName, info.CurrentYearCompanyName, info.MailingName);
        sheet.Range["A1"].Font.Bold = true;
        sheet.Range["A1"].Font.Size = 11;
        sheet.Range["A1"].HorizontalAlignment = -4108;
        sheet.Range["A2:I2"].Merge();
        sheet.Range["A2"].Value2 = title;
        sheet.Range["A2"].Font.Bold = true;
        sheet.Range["A2"].Font.Size = 11;
        sheet.Range["A2"].HorizontalAlignment = -4108;
        sheet.Range["A3:F3"].Merge();
        sheet.Range["A3"].Value2 = string.IsNullOrWhiteSpace(info.Address) ? string.Empty : info.Address.Trim();
        sheet.Range["A3"].WrapText = true;
        sheet.Range["G3:I3"].Merge();
        sheet.Range["G3"].Value2 = PresentationUnitLabel(info);
        sheet.Range["G3"].Font.Size = 10;
        sheet.Range["G3"].HorizontalAlignment = -4152;
    }


    private static void StyleHeader(dynamic range)
    {
        range.Font.Bold = true;
        try { range.Borders[9].LineStyle = 1; } catch { }
    }

    private static void ApplyCommonPresentation(dynamic sheet, bool noteRows, dynamic excel, FinancialStatementBasicInfo info)
    {
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(info.EntityType);
        var sheetName = ((string)sheet.Name ?? string.Empty).ToUpperInvariant();
        var isProfitAndLoss = sheetName.Contains("PROFIT", StringComparison.Ordinal);
        var isFace = !noteRows;

        try { sheet.Cells.Font.Name = "Calibri"; } catch { }
        try { sheet.Cells.Font.Size = isFace ? 11 : 10; } catch { }
        try { sheet.Cells.VerticalAlignment = -4108; } catch { } // xlCenter
        try { sheet.Cells.WrapText = false; } catch { }

        if (isFace)
        {
            // Exact face-statement widths from FSforNCE.xlsm and FSforSchIIIDivI.xlsm.
            // Never AutoFit the face sheets; the examples intentionally use narrow spacer
            // columns and fixed widths so the PDF breaks and amount columns stay stable.
            try
            {
                if (isProfitAndLoss)
                {
                    // Identical fixed PL widths in both original workbooks.
                    sheet.Columns[1].ColumnWidth = 3.42578125;
                    sheet.Columns[2].ColumnWidth = 3.42578125;
                    sheet.Columns[3].ColumnWidth = 3.5703125;
                    sheet.Columns[4].ColumnWidth = 41.5703125;
                    sheet.Columns[5].ColumnWidth = 3.5703125;
                    sheet.Columns[6].ColumnWidth = 4.42578125;
                    sheet.Columns[7].ColumnWidth = 7.42578125;
                    sheet.Columns[8].ColumnWidth = 18.5703125;
                    sheet.Columns[9].ColumnWidth = 1.42578125;
                    sheet.Columns[10].ColumnWidth = 18.5703125;
                    sheet.Columns[11].ColumnWidth = 13.42578125;
                    sheet.Range["H:J"].HorizontalAlignment = -4152; // xlRight
                    sheet.Range["C:G"].HorizontalAlignment = -4131; // xlLeft
                }
                else
                {
                    // Identical fixed BS widths in both original workbooks.
                    sheet.Columns[1].ColumnWidth = 2.5703125;
                    sheet.Columns[2].ColumnWidth = 2.5703125;
                    sheet.Columns[3].ColumnWidth = 2.42578125;
                    sheet.Columns[4].ColumnWidth = 3.0;
                    sheet.Columns[5].ColumnWidth = 3.5703125;
                    sheet.Columns[6].ColumnWidth = 23.140625;
                    sheet.Columns[7].ColumnWidth = 19.5703125;
                    sheet.Columns[8].ColumnWidth = 28.42578125;
                    sheet.Columns[9].ColumnWidth = 17.5703125;
                    sheet.Columns[10].ColumnWidth = 1.42578125;
                    sheet.Columns[11].ColumnWidth = 17.5703125;
                    sheet.Columns[12].ColumnWidth = 8.7109375;
                    sheet.Range["I:K"].HorizontalAlignment = -4152;
                    sheet.Range["C:H"].HorizontalAlignment = -4131;
                }

                sheet.Rows.RowHeight = 15;
                sheet.Rows[1].RowHeight = 30;

                // The face-statement row geometry is the same in both supplied workbooks.
                if (isProfitAndLoss)
                {
                    sheet.Rows[5].RowHeight = 15;
                    sheet.Range["D15:F15"].Merge();
                    sheet.Range["D15:F15"].WrapText = true;
                    sheet.Rows[15].RowHeight = 31.5;
                    foreach (var row in new[] { 11, 21, 23, 27, 29, 31, 37, 39, 41, 43, 45, 47, 51, 53, 55, 57 })
                        sheet.Rows[row].RowHeight = 7.15;
                    sheet.Rows[46].RowHeight = 15.75;
                }
                else
                {
                    foreach (var row in new[] { 8, 13, 15, 21, 29, 32, 34, 45, 53, 59, 61 })
                        sheet.Rows[row].RowHeight = 7.15;
                    sheet.Rows[30].RowHeight = 15;
                    sheet.Rows[31].RowHeight = 15.75;
                    sheet.Rows[54].RowHeight = 14.1;
                    sheet.Rows[55].RowHeight = 15.75;
                    sheet.Rows[56].RowHeight = 15.75;
                }
            }
            catch { }
        }
        else
        {
            // The original print engine uses portrait pages for normal narrative/detail notes
            // and landscape only for genuinely wide schedules. The merged A1:I title in AAH
            // must not itself make every note sheet landscape.
            try
            {
                sheet.Columns[1].ColumnWidth = 38;
                sheet.Columns[2].ColumnWidth = 16;
                sheet.Columns[3].ColumnWidth = 16;
                for (var column = 4; column <= 9; column++) sheet.Columns[column].ColumnWidth = 14;
                sheet.Range["B:I"].HorizontalAlignment = -4152;
                sheet.Range["A:A"].HorizontalAlignment = -4131;
                sheet.Cells.WrapText = true;
                sheet.Rows.AutoFit();
            }
            catch { }
        }

        try
        {
            var usedRows = Math.Max(1, Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture));
            var wideNote = noteRows &&
                (sheetName.Contains("3-6", StringComparison.Ordinal) ||
                 sheetName.Contains("13-16", StringComparison.Ordinal) ||
                 sheetName.Contains("17-21", StringComparison.Ordinal) ||
                 sheetName.Equals("NOTES TO ACCOUNTS", StringComparison.Ordinal));

            sheet.PageSetup.PaperSize = 9; // xlPaperA4
            sheet.PageSetup.Orientation = wideNote ? 2 : 1; // landscape only for wide schedules
            sheet.PageSetup.BlackAndWhite = true;
            sheet.PageSetup.Draft = false;
            sheet.PageSetup.CenterHorizontally = false;
            sheet.PageSetup.CenterVertically = false;
            sheet.PageSetup.PrintGridlines = false;
            sheet.PageSetup.Order = 1; // xlDownThenOver

            if (isFace)
            {
                // Exact face-statement scales read from the two supplied workbooks:
                // NCE: BS 83%, PL 88%; Schedule III Division I: BS 82%, PL 96%.
                sheet.PageSetup.Zoom = isProfitAndLoss
                    ? (profile == "NCE" ? 88 : 96)
                    : (profile == "NCE" ? 83 : 82);
            }
            else
            {
                sheet.PageSetup.Zoom = false;
                sheet.PageSetup.FitToPagesWide = 1;
                sheet.PageSetup.FitToPagesTall = false;
            }

            // Exact face-statement margins from the supplied workbooks. NCE face sheets use
            // symmetric 0.25 left/right and 0.50 top/bottom; Division I uses 0.50 left,
            // 0.25 right/bottom. Narrative notes keep the renderer's compact statutory margins.
            if (isFace && profile == "NCE")
            {
                sheet.PageSetup.LeftMargin = excel.InchesToPoints(0.25);
                sheet.PageSetup.RightMargin = excel.InchesToPoints(0.25);
                sheet.PageSetup.TopMargin = excel.InchesToPoints(0.50);
                sheet.PageSetup.BottomMargin = excel.InchesToPoints(0.50);
                sheet.PageSetup.HeaderMargin = excel.InchesToPoints(0.20);
                sheet.PageSetup.FooterMargin = excel.InchesToPoints(0.20);
            }
            else
            {
                sheet.PageSetup.LeftMargin = excel.InchesToPoints(0.50);
                sheet.PageSetup.RightMargin = excel.InchesToPoints(0.25);
                sheet.PageSetup.TopMargin = excel.InchesToPoints(0.50);
                sheet.PageSetup.BottomMargin = excel.InchesToPoints(0.25);
                sheet.PageSetup.HeaderMargin = excel.InchesToPoints(0.25);
                sheet.PageSetup.FooterMargin = excel.InchesToPoints(0.20);
            }

            sheet.PageSetup.LeftHeader = noteRows
                ? $"&\"Calibri,Bold\"&11Notes forming part of the Financial Statements for the year ended {CurrentLabel(info)}"
                : string.Empty;
            sheet.PageSetup.CenterHeader = string.Empty;
            sheet.PageSetup.RightHeader = noteRows ? PresentationUnitLabel(info) : string.Empty;
            sheet.PageSetup.LeftFooter = string.Empty;
            sheet.PageSetup.CenterFooter = "Page &P of &N";
            sheet.PageSetup.RightFooter = string.Empty;

            if (noteRows)
            {
                sheet.PageSetup.PrintTitleRows = "$1:$3";
                sheet.PageSetup.PrintArea = sheet.UsedRange.Address;
                ApplySmartNotePageBreaks(sheet, usedRows, wideNote);
            }
            else if (isProfitAndLoss)
            {
                sheet.PageSetup.PrintTitleRows = string.Empty;
                sheet.PageSetup.PrintArea = $"$C$2:$J${Math.Max(77, usedRows)}";
            }
            else
            {
                sheet.PageSetup.PrintTitleRows = string.Empty;
                sheet.PageSetup.PrintArea = $"$C$1:$K${Math.Max(81, usedRows)}";
            }
        }
        catch { }

        try
        {
            sheet.Activate();
            excel.ActiveWindow.DisplayGridlines = false;
            excel.ActiveWindow.DisplayHeadings = true;
            excel.ActiveWindow.Zoom = 100;
        }
        catch { }
    }

    private static void ApplySmartNotePageBreaks(dynamic sheet, int usedRows, bool landscape)
    {
        // Mirrors ModPageSettings.ApplySmartBreakOptimized: keep a note/table together when
        // possible instead of allowing Excel to break immediately after its heading.
        try { sheet.ResetAllPageBreaks(); } catch { }
        var targetRows = landscape ? 32 : 46;
        var lastBreak = 1;
        for (var row = 5; row <= usedRows; row++)
        {
            string text;
            try { text = (sheet.Cells[row, 1].Value2?.ToString() ?? string.Empty).Trim(); }
            catch { continue; }
            if (text.Length == 0) continue;
            var isHeading = text.StartsWith("Note ", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Exceptional Items", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Extraordinary Items", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Partners' Remuneration", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Tax Expense", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Discontinuing Operations", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("Additional Statement of Profit and Loss Lines", StringComparison.OrdinalIgnoreCase);
            if (!isHeading || row - lastBreak < targetRows) continue;
            try
            {
                sheet.HPageBreaks.Add(sheet.Rows[row]);
                lastBreak = row;
            }
            catch { }
        }
    }

    private static void AddSignatureBlock(dynamic sheet, FinancialStatementBasicInfo info,
        IEnumerable<EntitySignatoryRow> signatories, IEnumerable<OwnerPartnerRow> owners)
    {
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(info.EntityType);
        var name = ((string)sheet.Name ?? string.Empty);
        var isProfitAndLoss = name.Contains("Profit", StringComparison.OrdinalIgnoreCase);
        int used;
        try { used = Convert.ToInt32(sheet.UsedRange.Rows.Count, CultureInfo.InvariantCulture); }
        catch { used = 1; }

        // Exact anchor rows/columns from the supplied examples:
        // NCE BS D55/D57 + H57; NCE PL C56/C58 + G58;
        // Schedule III BS D60/D62 + H62; PL C56/C58 + G58.
        var reportRow = isProfitAndLoss ? 56 : (profile == "NCE" ? 55 : 60);
        reportRow = Math.Max(reportRow, used + 2);
        var auditorColumn = isProfitAndLoss ? 3 : 4; // C / D
        var entityColumn = isProfitAndLoss ? 7 : 8;  // G / H
        var blockRow = reportRow + 2;

        sheet.Cells[reportRow, auditorColumn].Value2 = "As per our Report of even date";
        sheet.Cells[reportRow, auditorColumn].Font.Bold = false;

        var entityName = First(info.EntityName, info.CurrentYearCompanyName, info.MailingName);
        sheet.Cells[blockRow, entityColumn].Value2 = "For and on behalf of the Board";
        sheet.Cells[blockRow, entityColumn].Font.Bold = true;
        sheet.Cells[blockRow + 1, entityColumn].Value2 = entityName;
        sheet.Cells[blockRow + 1, entityColumn].Font.Bold = true;

        if (!string.IsNullOrWhiteSpace(info.AuditFirmName) || !string.IsNullOrWhiteSpace(info.CaName))
        {
            sheet.Cells[blockRow, auditorColumn].Value2 = string.IsNullOrWhiteSpace(info.AuditFirmName)
                ? string.Empty
                : $"For {info.AuditFirmName.Trim()}";
            sheet.Cells[blockRow, auditorColumn].Font.Bold = true;
            sheet.Cells[blockRow + 1, auditorColumn].Value2 = "Chartered Accountants";
            sheet.Cells[blockRow + 2, auditorColumn].Value2 = string.IsNullOrWhiteSpace(info.FirmRegistrationNumber)
                ? string.Empty
                : $"FRN: {info.FirmRegistrationNumber.Trim()}";
            sheet.Cells[blockRow + 4, auditorColumn].Value2 = info.CaName;
            sheet.Cells[blockRow + 4, auditorColumn].Font.Bold = !string.IsNullOrWhiteSpace(info.CaName);
            sheet.Cells[blockRow + 5, auditorColumn].Value2 = info.CaDesignation;
            sheet.Cells[blockRow + 6, auditorColumn].Value2 = string.IsNullOrWhiteSpace(info.CaMembershipNumber)
                ? string.Empty
                : $"M. No.: {info.CaMembershipNumber.Trim()}";
            sheet.Cells[blockRow + 7, auditorColumn].Value2 = string.IsNullOrWhiteSpace(info.Udin)
                ? string.Empty
                : $"UDIN: {info.Udin.Trim()}";
            sheet.Cells[blockRow + 9, auditorColumn].Value2 = string.IsNullOrWhiteSpace(info.AuditPlace)
                ? string.Empty
                : $"Place: {info.AuditPlace.Trim()}";
            sheet.Cells[blockRow + 10, auditorColumn].Value2 = string.IsNullOrWhiteSpace(info.CaSigningDate)
                ? string.Empty
                : $"Date: {FormatDateLabel(info.CaSigningDate)}";
        }

        var people = signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToArray();
        if (people.Length == 0)
        {
            people = owners.Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new EntitySignatoryRow
                {
                    Name = x.Name,
                    Designation = x.Designation,
                    Din = string.Empty,
                    Place = info.SigningPlace,
                    SigningDate = info.SigningDate
                }).ToArray();
        }
        if (people.Length == 0 && !string.IsNullOrWhiteSpace(info.SignatoryName))
        {
            people =
            [
                new EntitySignatoryRow
                {
                    Name = info.SignatoryName,
                    Designation = info.SignatoryDesignation,
                    Place = info.SigningPlace,
                    SigningDate = info.SigningDate
                }
            ];
        }

        // Two side-by-side signatories where the face width permits, then continue below.
        for (var i = 0; i < people.Length; i++)
        {
            var person = people[i];
            var col = entityColumn + (i % 2) * 2;
            var row = blockRow + 4 + (i / 2) * 5;
            sheet.Cells[row, col].Value2 = person.Name;
            sheet.Cells[row, col].Font.Bold = true;
            sheet.Cells[row + 1, col].Value2 = person.Designation;
            if (!string.IsNullOrWhiteSpace(person.Din)) sheet.Cells[row + 2, col].Value2 = $"DIN: {person.Din}";
        }

        var signPlace = First(people.FirstOrDefault()?.Place ?? string.Empty, info.SigningPlace);
        var signDate = First(people.FirstOrDefault()?.SigningDate ?? string.Empty, info.SigningDate);
        var footerRow = blockRow + Math.Max(9, ((people.Length + 1) / 2) * 5 + 4);
        sheet.Cells[footerRow, entityColumn].Value2 = string.IsNullOrWhiteSpace(signPlace) ? string.Empty : $"Place: {signPlace}";
        sheet.Cells[footerRow + 1, entityColumn].Value2 = string.IsNullOrWhiteSpace(signDate) ? string.Empty : $"Date: {FormatDateLabel(signDate)}";

        try
        {
            sheet.Range[sheet.Cells[reportRow, auditorColumn], sheet.Cells[footerRow + 1, Math.Min(11, entityColumn + 2)]].Font.Name = "Calibri";
            sheet.Range[sheet.Cells[reportRow, auditorColumn], sheet.Cells[footerRow + 1, Math.Min(11, entityColumn + 2)]].Font.Size = 11;
        }
        catch { }
    }


    private static decimal PresentationDivisor(FinancialStatementBasicInfo info)
    {
        // Original workbooks use Basic Info "Round Off Amount to Nearest" as the amount
        // presentation authority. PresentationUnit remains only a backward-compatible AAH
        // fallback when the workbook control is Default.
        var round = (info.Rounding ?? string.Empty).Trim();
        if (!round.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
            !round.Equals("Default (without decimals)", StringComparison.OrdinalIgnoreCase) &&
            round.Length > 0)
        {
            if (round.Equals("Hundreds", StringComparison.OrdinalIgnoreCase)) return 100m;
            if (round.Equals("Thousands", StringComparison.OrdinalIgnoreCase)) return 1_000m;
            if (round.Equals("Ten Thousand", StringComparison.OrdinalIgnoreCase)) return 10_000m;
            if (round.Equals("Lakhs", StringComparison.OrdinalIgnoreCase)) return 100_000m;
            if (round.Equals("Ten Lakh", StringComparison.OrdinalIgnoreCase)) return 1_000_000m;
            if (round.Equals("Millions", StringComparison.OrdinalIgnoreCase)) return 1_000_000m;
            if (round.Equals("Ten Million", StringComparison.OrdinalIgnoreCase)) return 10_000_000m;
            if (round.Equals("Crores", StringComparison.OrdinalIgnoreCase)) return 10_000_000m;
        }

        var unit = (info.PresentationUnit ?? string.Empty).ToUpperInvariant();
        if (unit.Contains("CRORE", StringComparison.Ordinal)) return 10_000_000m;
        if (unit.Contains("LAKH", StringComparison.Ordinal)) return 100_000m;
        if (unit.Contains("THOUSAND", StringComparison.Ordinal)) return 1_000m;
        if (unit.Contains("HUNDRED", StringComparison.Ordinal)) return 100m;
        return 1m;
    }

    private static string PresentationUnitLabel(FinancialStatementBasicInfo info)
    {
        var round = (info.Rounding ?? string.Empty).Trim();
        if (round.Length > 0 &&
            !round.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
            !round.Equals("Default (without decimals)", StringComparison.OrdinalIgnoreCase))
            return $"(Amount in ₹ {round})";

        return string.IsNullOrWhiteSpace(info.PresentationUnit) || info.PresentationUnit == "₹"
            ? "(Amount in ₹)"
            : $"(Amount in {info.PresentationUnit})";
    }

    private static string PresentationNumberFormat(FinancialStatementBasicInfo info)
        => (info.Rounding ?? string.Empty).Trim().Equals("Default (without decimals)", StringComparison.OrdinalIgnoreCase)
            ? "#,##0;(#,##0);\"-\""
            : "#,##0.00;(#,##0.00);\"-\"";

    private static string CurrentLabel(FinancialStatementBasicInfo info)
        => string.IsNullOrWhiteSpace(info.CurrentYearEnd) ? "Current Year" : FormatDateLabel(info.CurrentYearEnd);

    private static string PreviousLabel(FinancialStatementBasicInfo info)
        => string.IsNullOrWhiteSpace(info.PreviousYearEnd) ? "Previous Year" : FormatDateLabel(info.PreviousYearEnd);

    private static string FormatDateLabel(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ||
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date))
            return date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
        return value?.Trim() ?? string.Empty;
    }

    private static int ParseNote(string note)
    {
        var digits = new string((note ?? string.Empty).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }

    private static string EscapeFormulaText(string value) => (value ?? string.Empty).Replace("\"", "\"\"");
    private static string EscapeSheetName(string value) => (value ?? string.Empty).Replace("'", "''");

    private static string Normalize(string value)
    {
        var chars = (value ?? string.Empty).ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string First(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
}
