using System.Globalization;
using System.IO;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Desktop.Services;

public static class FinancialStatementWorkbookService
{
    public static string ResolveProfileSheet(string entityType)
    {
        var value = entityType ?? string.Empty;
        if (value.Contains("NBFC", StringComparison.OrdinalIgnoreCase) && value.Contains("Ind AS", StringComparison.OrdinalIgnoreCase)) return "NBFC_IND";
        if (value.Contains("NBFC", StringComparison.OrdinalIgnoreCase)) return "NBFC_AS";
        if (value.Contains("Company", StringComparison.OrdinalIgnoreCase) && value.Contains("Ind AS", StringComparison.OrdinalIgnoreCase)) return "C_IND";
        if (value.Contains("Company", StringComparison.OrdinalIgnoreCase)) return "C_AS";
        if (value.Contains("NCE", StringComparison.OrdinalIgnoreCase) || value.Contains("Proprietorship", StringComparison.OrdinalIgnoreCase) || value.Contains("Partnership", StringComparison.OrdinalIgnoreCase) || value.Contains("HUF", StringComparison.OrdinalIgnoreCase)) return "NCE";
        if (value.Equals("LLP", StringComparison.OrdinalIgnoreCase) || value.Contains("Limited Liability Partnership", StringComparison.OrdinalIgnoreCase)) return "LLP";
        if (value.Contains("NPO", StringComparison.OrdinalIgnoreCase) || value.Contains("Not-for-Profit", StringComparison.OrdinalIgnoreCase)) return "NPO";
        if (value.Contains("Co-operative Bank", StringComparison.OrdinalIgnoreCase) || value.Contains("Cooperative Bank", StringComparison.OrdinalIgnoreCase)) return "COOPBANK";
        if (value.Contains("Banking", StringComparison.OrdinalIgnoreCase) || value.Equals("Bank", StringComparison.OrdinalIgnoreCase)) return "BANK";
        if (value.Contains("Life Insurance", StringComparison.OrdinalIgnoreCase)) return "LIFE";
        if (value.Contains("General Insurance", StringComparison.OrdinalIgnoreCase)) return "GENINS";
        if (value.Contains("Reinsurance", StringComparison.OrdinalIgnoreCase)) return "REINS";
        if (value.Contains("Mutual Fund", StringComparison.OrdinalIgnoreCase)) return "MF";
        if (value.Equals("CIS", StringComparison.OrdinalIgnoreCase) || value.Contains("Collective Investment", StringComparison.OrdinalIgnoreCase)) return "CIS";
        if (value.Contains("REIT", StringComparison.OrdinalIgnoreCase)) return "REIT";
        if (value.Contains("InvIT", StringComparison.OrdinalIgnoreCase) || value.Contains("Infrastructure Investment", StringComparison.OrdinalIgnoreCase)) return "INVIT";
        return "C_AS";
    }

    public static IReadOnlyList<FinancialStatementNoteTemplateRow> ReadNoteTemplate(string templatePath, string entityType)
    {
        if (!File.Exists(templatePath)) return [];
        try
        {
            var workbook = new OpenXmlWorkbookReader().Read(templatePath, CancellationToken.None);
            if (!workbook.TryGetWorksheet(ResolveProfileSheet(entityType), out var sheet)) return [];
            var headerRow = 0;
            for (var row = 1; row <= sheet.MaxRow; row++)
            {
                if (sheet.GetCell(row, 4).Equals("AAH Mapping Key", StringComparison.OrdinalIgnoreCase))
                {
                    headerRow = row;
                    break;
                }
            }
            if (headerRow == 0) return [];

            var result = new List<FinancialStatementNoteTemplateRow>();
            for (var row = headerRow + 1; row <= sheet.MaxRow; row++)
            {
                var note = sheet.GetCell(row, 1).Trim();
                var title = sheet.GetCell(row, 2).Trim();
                var line = sheet.GetCell(row, 3).Trim();
                var key = sheet.GetCell(row, 4).Trim();
                if (key.Length == 0) continue;
                if (note.Length == 0 || title.Length == 0) continue;
                if (key.Equals("AAH Mapping Key", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("Current Period", StringComparison.OrdinalIgnoreCase) || key.Equals("Previous Period", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("Result", StringComparison.OrdinalIgnoreCase) || key.Equals("PASS", StringComparison.OrdinalIgnoreCase) || key.Equals("READY FOR REVIEW", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new FinancialStatementNoteTemplateRow(note, title, line, key, row));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> BuildNoteSelector(string templatePath, string entityType)
    {
        var result = new List<string> { "All Notes" };
        if (FinancialStatementPresentationExportService.HasPresentationMappingCatalog(entityType))
        {
            foreach (var group in FinancialStatementPresentationExportService.BuildPresentationMappingCatalog(entityType)
                         .GroupBy(x => new { x.Note, x.NoteHead })
                         .Select(g => g.Key))
            {
                var display = $"Note {group.Note} - {group.NoteHead}";
                if (!result.Contains(display, StringComparer.OrdinalIgnoreCase)) result.Add(display);
            }
            return result;
        }
        foreach (var group in ReadNoteTemplate(templatePath, entityType)
                     .GroupBy(x => new { x.Note, x.Title })
                     .Select(g => g.Key))
        {
            var display = $"{NotePrefix(entityType)} {group.Note} - {group.Title}".Trim();
            if (!result.Contains(display, StringComparer.OrdinalIgnoreCase)) result.Add(display);
        }
        return result;
    }

    public static IReadOnlyList<FinancialStatementMappingOption> BuildMappingCatalog(string templatePath, string entityType)
    {
        if (FinancialStatementPresentationExportService.HasPresentationMappingCatalog(entityType))
            return FinancialStatementPresentationExportService.BuildPresentationMappingCatalog(entityType);
        if (!File.Exists(templatePath)) return [];
        try
        {
            var profileSheet = ResolveProfileSheet(entityType);
            var workbook = new OpenXmlWorkbookReader().Read(templatePath, CancellationToken.None);
            if (!workbook.TryGetWorksheet(profileSheet, out var sheet)) return [];
            var headerRow = 0;
            for (var row = 1; row <= sheet.MaxRow; row++)
            {
                if (sheet.GetCell(row, 4).Equals("AAH Mapping Key", StringComparison.OrdinalIgnoreCase))
                {
                    headerRow = row;
                    break;
                }
            }
            if (headerRow == 0) return [];

            // R15 R2: C1 is built only from the selected profile's actual face-statement
            // rows. Disclosure-only notes such as "Brief about the Entity" and accounting
            // policies must never appear as FS Heads simply because they exist in the notes
            // section. The workbook row order is authoritative and is preserved end-to-end.
            var faceByNote = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var faceOrder = new List<(string Note, string FsHead)>();
            for (var row = 1; row < headerRow; row++)
            {
                var fsHead = sheet.GetCell(row, 1).Trim();
                var note = sheet.GetCell(row, 2).Trim();
                if (fsHead.Length == 0 || note.Length == 0 || note.Equals("Note No.", StringComparison.OrdinalIgnoreCase) || note.Equals("Schedule", StringComparison.OrdinalIgnoreCase) || note == "—") continue;
                if (!faceByNote.TryGetValue(note, out var heads)) faceByNote[note] = heads = [];
                if (!heads.Contains(fsHead, StringComparer.OrdinalIgnoreCase)) heads.Add(fsHead);
                if (!faceOrder.Any(x => x.Note.Equals(note, StringComparison.OrdinalIgnoreCase) && x.FsHead.Equals(fsHead, StringComparison.OrdinalIgnoreCase)))
                    faceOrder.Add((note, fsHead));
            }

            // Drive the dropdown catalog from the face-statement order, not note-number or
            // alphabetical order. This is what keeps C1 identical to the selected statutory
            // Balance Sheet / P&L format even where one note is shared by current/non-current
            // heads (for example NCE Borrowings, Provisions, Investments or Loans/Advances).
            var noteRows = ReadNoteTemplate(templatePath, entityType)
                .Where(x => !x.MappingKey.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var result = new List<FinancialStatementMappingOption>();
            foreach (var face in faceOrder)
            {
                if (!faceByNote.TryGetValue(face.Note, out var heads) || heads.Count == 0) continue;
                foreach (var row in noteRows.Where(x => x.Note.Equals(face.Note, StringComparison.OrdinalIgnoreCase)))
                {
                    var selectedHead = SelectFaceHead(heads, row);
                    if (!selectedHead.Equals(face.FsHead, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new FinancialStatementMappingOption(
                        face.FsHead, row.Title, row.LineItem, row.MappingKey, row.Note,
                        "Financial Statements", face.FsHead, row.Title, row.LineItem));
                }
            }
            if (profileSheet.Equals("C_AS", StringComparison.OrdinalIgnoreCase))
            {
                // The original Schedule III workbook exposes these face lines in its factory
                // mapping table even though the older AAH template library had no durable keys.
                result.Add(new FinancialStatementMappingOption("Money received against Share Warrants", "Money received against Share Warrants", "Money received against Share Warrants", "SHARE_WARRANTS", "2A",
                    "Balance Sheet", "Liabilities", "Money received against Share Warrants", string.Empty));
                result.Add(new FinancialStatementMappingOption("Share Application Money pending allotment", "Share Application Money pending allotment", "Share Application Money pending allotment", "SHARE_APP_MONEY", "2B",
                    "Balance Sheet", "Liabilities", "Share Application Money pending allotment", string.Empty));
                result.Add(new FinancialStatementMappingOption("Exceptional Items", "Exceptional Items", "Exceptional items", "EXC", string.Empty,
                    "P&L Account", "Expenses", "Other Profit and Loss Items", "Exceptional Items"));
                result.Add(new FinancialStatementMappingOption("Extraordinary Items", "Extraordinary Items", "Extraordinary items", "EXTRAORD", string.Empty,
                    "P&L Account", "Expenses", "Other Profit and Loss Items", "Extraordinary Items"));
                result.Add(new FinancialStatementMappingOption("Profit/(Loss) from Discontinuing Operations", "Discontinuing Operations", "Profit/(Loss) from Discontinuing Operations", "DISCONT_PROFIT", string.Empty,
                    "P&L Account", "Expenses", "Other Profit and Loss Items", "Profit/(Loss) from Discontinuing Operations"));
                result.Add(new FinancialStatementMappingOption("Tax Expense of Discontinuing Operations", "Discontinuing Operations", "Tax Expense of Discontinuing Operations", "DISCONT_TAX", string.Empty,
                    "P&L Account", "Expenses", "Other Profit and Loss Items", "Tax Expense of Discontinuing Operations"));
                result.Add(new FinancialStatementMappingOption("Tax expense", "Tax Expense", "Current tax", "TAX_CURR", string.Empty,
                    "P&L Account", "Expenses", "Tax Expenses (Income Tax)", "Current Tax"));
                result.Add(new FinancialStatementMappingOption("Tax expense", "Tax Expense", "Deferred tax", "TAX_DEFER", string.Empty,
                    "P&L Account", "Expenses", "Tax Expenses (Income Tax)", "Deferred Tax"));
                result.Add(new FinancialStatementMappingOption("Tax expense", "Tax Expense", "Earlier-year tax adjustment", "TAX_PRIOR", string.Empty,
                    "P&L Account", "Expenses", "Tax Expenses (Income Tax)", "Adjustment of Tax relating to earlier periods"));
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var distinct = result
                .Where(x => seen.Add($"{x.FsHead}\u001f{x.NoteHead}\u001f{x.NoteSubHead}\u001f{x.MappingKey}"))
                .ToArray();
            return FinancialStatementClassificationService.NormalizeProfileCatalog(entityType, distinct);
        }
        catch { return []; }
    }

    private static string SelectFaceHead(IReadOnlyList<string> heads, FinancialStatementNoteTemplateRow row)
    {
        if (heads.Count == 1) return heads[0];
        var best = heads[0];
        var bestScore = int.MinValue;
        foreach (var head in heads)
        {
            var score = ScoreFaceHead(head, row);
            if (score <= bestScore) continue;
            best = head;
            bestScore = score;
        }
        return best;
    }

    private static int ScoreFaceHead(string head, FinancialStatementNoteTemplateRow row)
    {
        var headText = NormalizeMatchText(head);
        var detailText = NormalizeMatchText($"{row.Title} {row.LineItem} {row.MappingKey}");
        var score = 0;

        var detailNonCurrent = ContainsPhrase(detailText, "NON CURRENT") || ContainsPhrase(detailText, "LONG TERM") || ContainsPhrase(detailText, " LT ");
        var detailCurrent = !detailNonCurrent && (ContainsPhrase(detailText, "CURRENT") || ContainsPhrase(detailText, "SHORT TERM") || ContainsPhrase(detailText, " ST "));
        var headNonCurrent = ContainsPhrase(headText, "NON CURRENT") || ContainsPhrase(headText, "LONG TERM");
        var headCurrent = !headNonCurrent && (ContainsPhrase(headText, "CURRENT") || ContainsPhrase(headText, "SHORT TERM"));
        if (detailNonCurrent) score += headNonCurrent ? 100 : headCurrent ? -100 : 0;
        if (detailCurrent) score += headCurrent ? 100 : headNonCurrent ? -100 : 0;

        var detailAsset = ContainsPhrase(detailText, " ASSET") || ContainsPhrase(detailText, "DTA");
        var detailLiability = ContainsPhrase(detailText, "LIABIL") || ContainsPhrase(detailText, "DTL");
        var headAsset = ContainsPhrase(headText, " ASSET");
        var headLiability = ContainsPhrase(headText, "LIABIL");
        if (detailAsset) score += headAsset ? 80 : headLiability ? -80 : 0;
        if (detailLiability) score += headLiability ? 80 : headAsset ? -80 : 0;

        foreach (var token in headText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 4 || token is "OTHER" or "TOTAL" or "NET") continue;
            if (detailText.Contains($" {token} ", StringComparison.Ordinal)) score += 3;
        }
        return score;
    }

    private static string NormalizeMatchText(string value)
    {
        var chars = (value ?? string.Empty).ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return $" {string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))} ";
    }

    private static bool ContainsPhrase(string normalized, string phrase)
        => normalized.Contains(phrase.StartsWith(' ') ? phrase : $" {phrase}", StringComparison.Ordinal);

    public static IReadOnlyList<FinancialStatementNotePreviewRow> BuildNotePreview(
        string templatePath,
        string entityType,
        string selectedNote,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows)
    {
        if (FinancialStatementPresentationExportService.HasPresentationMappingCatalog(entityType))
        {
            var catalog = FinancialStatementPresentationExportService.BuildPresentationMappingCatalog(entityType);
            var fyAmounts = AggregateMappings(fyRows);
            var pyAmounts = AggregateMappings(pyRows);
            var fyCountMap = AggregateCounts(fyRows);
            var pyCountMap = AggregateCounts(pyRows);
            var preview = new List<FinancialStatementNotePreviewRow>();
            foreach (var option in catalog)
            {
                var display = $"Note {option.Note} - {option.NoteHead}";
                if (!selectedNote.Equals("All Notes", StringComparison.OrdinalIgnoreCase) &&
                    !display.Equals(selectedNote, StringComparison.OrdinalIgnoreCase)) continue;
                preview.Add(new FinancialStatementNotePreviewRow
                {
                    Note = option.Note,
                    NoteTitle = option.NoteHead,
                    StatutoryLineItem = option.NoteSubHead,
                    MappingKey = option.MappingKey,
                    CurrentAmount = fyAmounts.GetValueOrDefault(option.MappingKey),
                    PreviousAmount = pyAmounts.GetValueOrDefault(option.MappingKey),
                    MappedLedgerCount = fyCountMap.GetValueOrDefault(option.MappingKey) + pyCountMap.GetValueOrDefault(option.MappingKey),
                    IsTotal = false
                });
            }
            return preview;
        }

        var template = ReadNoteTemplate(templatePath, entityType);
        var fy = AggregateMappings(fyRows);
        var py = AggregateMappings(pyRows);
        var fyCounts = AggregateCounts(fyRows);
        var pyCounts = AggregateCounts(pyRows);
        var prefix = NotePrefix(entityType);
        var output = new List<FinancialStatementNotePreviewRow>();
        foreach (var row in template)
        {
            var display = $"{prefix} {row.Note} - {row.Title}".Trim();
            if (!selectedNote.Equals("All Notes", StringComparison.OrdinalIgnoreCase) &&
                !display.Equals(selectedNote, StringComparison.OrdinalIgnoreCase)) continue;

            decimal current;
            decimal previous;
            int mappedCount;
            if (row.MappingKey.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase))
            {
                var noteKeys = template.Where(x => x.Note.Equals(row.Note, StringComparison.OrdinalIgnoreCase) &&
                                                    !x.MappingKey.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.MappingKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                current = noteKeys.Sum(key => fy.GetValueOrDefault(key));
                previous = noteKeys.Sum(key => py.GetValueOrDefault(key));
                mappedCount = noteKeys.Sum(key => fyCounts.GetValueOrDefault(key) + pyCounts.GetValueOrDefault(key));
            }
            else
            {
                current = fy.GetValueOrDefault(row.MappingKey);
                previous = py.GetValueOrDefault(row.MappingKey);
                mappedCount = fyCounts.GetValueOrDefault(row.MappingKey) + pyCounts.GetValueOrDefault(row.MappingKey);
            }
            output.Add(new FinancialStatementNotePreviewRow
            {
                Note = row.Note,
                NoteTitle = row.Title,
                StatutoryLineItem = row.LineItem,
                MappingKey = row.MappingKey,
                CurrentAmount = current,
                PreviousAmount = previous,
                MappedLedgerCount = mappedCount,
                IsTotal = row.MappingKey.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase)
            });
        }
        return output;
    }

    public static void Export(
        string templatePath,
        string destinationPath,
        FinancialStatementBasicInfo basicInfo,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        IEnumerable<EntitySignatoryRow> signatories,
        IEnumerable<OwnerPartnerRow> ownerPartners,
        bool keepLinks,
        string? pdfPath = null)
    {
        if (!File.Exists(templatePath)) throw new FileNotFoundException("Financial Statement template library is missing.", templatePath);
        var excelType = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException("Microsoft Excel is not installed.");
        dynamic? excel = null;
        dynamic? book = null;
        try
        {
            excel = Activator.CreateInstance(excelType)!;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.ScreenUpdating = false;
            book = excel.Workbooks.Open(destinationPath);

            var profileName = ResolveProfileSheet(basicInfo.EntityType);
            dynamic profile = book.Worksheets[profileName];
            dynamic basic;
            try { basic = book.Worksheets["AAH Basic Info"]; }
            catch { basic = book.Worksheets.Add(); basic.Name = "AAH Basic Info"; }

            WriteBasicInfo(basic, basicInfo, signatories, ownerPartners);
            WriteMappedAmounts(profile, fyRows, pyRows);
            try { excel.CalculateFull(); } catch { }

            // Keep exactly the active profile plus its hidden Basic Info authority.
            for (var index = book.Worksheets.Count; index >= 1; index--)
            {
                dynamic ws = book.Worksheets[index];
                var name = Convert.ToString(ws.Name, CultureInfo.InvariantCulture) ?? string.Empty;
                if (name.Equals(profileName, StringComparison.OrdinalIgnoreCase) || name.Equals("AAH Basic Info", StringComparison.OrdinalIgnoreCase)) continue;
                ws.Delete();
            }

            profile.Name = "Financial Statements";
            basic.Visible = 2; // xlSheetVeryHidden; formulas stay live for linked export.
            profile.Activate();
            ConfigurePrint(profile);

            if (!keepLinks)
            {
                dynamic used = profile.UsedRange;
                used.Value2 = used.Value2;
            }

            if (!string.IsNullOrWhiteSpace(pdfPath))
            {
                profile.ExportAsFixedFormat(0, pdfPath);
            }
            else
            {
                book.Save();
            }
        }
        finally
        {
            try { book?.Close(false); } catch { }
            try { excel?.Quit(); } catch { }
        }
    }

    private static void WriteMappedAmounts(dynamic profile, IEnumerable<FinancialStatementLedgerRow> fyRows, IEnumerable<FinancialStatementLedgerRow> pyRows)
    {
        var fy = AggregateMappings(fyRows);
        var py = AggregateMappings(pyRows);
        dynamic used = profile.UsedRange;
        var rowCount = Convert.ToInt32(used.Rows.Count, CultureInfo.InvariantCulture);
        for (var row = 1; row <= rowCount; row++)
        {
            object? rawKey = profile.Cells[row, 4].Value2;
            var key = Convert.ToString(rawKey, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (key.Length == 0 || key.Equals("__TOTAL__", StringComparison.OrdinalIgnoreCase) || key.Equals("AAH Mapping Key", StringComparison.OrdinalIgnoreCase)) continue;
            if (fy.TryGetValue(key, out decimal current)) profile.Cells[row, 5].Value2 = (double)current;
            else profile.Cells[row, 5].Value2 = 0d;
            if (py.TryGetValue(key, out decimal previous)) profile.Cells[row, 6].Value2 = (double)previous;
            else profile.Cells[row, 6].Value2 = 0d;
        }
    }

    private static void WriteBasicInfo(dynamic sheet, FinancialStatementBasicInfo info, IEnumerable<EntitySignatoryRow> signatories, IEnumerable<OwnerPartnerRow> owners)
    {
        var rows = new (string Label, string Value)[]
        {
            ("Entity Name", info.EntityName), ("Mailing Name", info.MailingName), ("Address", info.Address), ("PAN", info.Pan),
            ("GSTIN", info.Gstin), ("Entity Type", info.EntityType), ("Current Year End", info.CurrentYearEnd), ("Previous Year End", info.PreviousYearEnd),
            ("Rounding", info.Rounding), ("PPE / FA Block", info.PpeBlock), ("Entity Signatory", info.SignatoryName), ("Designation", info.SignatoryDesignation),
            ("DIN", info.Din), ("Signing Date", info.SigningDate), ("Signing Place", info.SigningPlace), ("Audit Firm", info.AuditFirmName),
            ("FRN", info.FirmRegistrationNumber), ("Signing CA", info.CaName), ("CA Designation", info.CaDesignation), ("Membership No.", info.CaMembershipNumber),
            ("UDIN", info.Udin), ("Audit Place", info.AuditPlace), ("Presentation Unit", info.PresentationUnit), ("Standalone / Consolidated", info.StandaloneConsolidated),
            ("Previous Year Tally Company", info.PreviousYearTallyCompany), ("LLPIN / Registration ID", info.LlpIn),
            ("Current Year Company Name", info.CurrentYearCompanyName), ("Previous Year Company Name", info.PreviousYearCompanyName),
            ("CA Signing Date", info.CaSigningDate), ("NCE Type", info.NceType)
        };
        sheet.Cells[1, 1].Value2 = "AAH Financial Statements - Basic Information";
        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cells[i + 3, 1].Value2 = rows[i].Label;
            sheet.Cells[i + 3, 2].Value2 = rows[i].Value;
        }

        var start = rows.Length + 5;
        sheet.Cells[start, 1].Value2 = "Entity Signatories";
        sheet.Cells[start + 1, 1].Value2 = "Name";
        sheet.Cells[start + 1, 2].Value2 = "Designation";
        sheet.Cells[start + 1, 3].Value2 = "DIN / ID";
        var signRow = start + 2;
        foreach (var item in signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            sheet.Cells[signRow, 1].Value2 = item.Name;
            sheet.Cells[signRow, 2].Value2 = item.Designation;
            sheet.Cells[signRow, 3].Value2 = item.Din;
            signRow++;
        }

        var ownerStart = Math.Max(signRow + 2, 42);
        sheet.Cells[ownerStart, 1].Value2 = "Owner / Partners Details";
        sheet.Cells[ownerStart + 1, 1].Value2 = "Name";
        sheet.Cells[ownerStart + 1, 2].Value2 = "Share of Profit/(Loss)";
        var ownerRow = ownerStart + 2;
        foreach (var item in owners.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            sheet.Cells[ownerRow, 1].Value2 = item.Name;
            sheet.Cells[ownerRow, 2].Value2 = (double)item.ShareOfProfitLoss;
            ownerRow++;
        }
    }

    private static void ConfigurePrint(dynamic sheet)
    {
        try
        {
            dynamic page = sheet.PageSetup;
            page.Zoom = false;
            page.FitToPagesWide = 1;
            page.FitToPagesTall = false;
            page.Orientation = 1; // xlPortrait
            page.CenterHorizontally = true;
            page.PrintArea = sheet.UsedRange.Address;
            page.LeftMargin = 18;
            page.RightMargin = 18;
            page.TopMargin = 24;
            page.BottomMargin = 24;
        }
        catch { }
    }

    private static Dictionary<string, decimal> AggregateMappings(IEnumerable<FinancialStatementLedgerRow> rows)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.Mapping?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            var amount = row.AmountSign switch
            {
                "As Is" => row.Closing,
                "Reverse" => -row.Closing,
                _ => Math.Abs(row.Closing)
            };
            result[key] = result.GetValueOrDefault(key) + amount;
        }
        return result;
    }

    private static Dictionary<string, int> AggregateCounts(IEnumerable<FinancialStatementLedgerRow> rows)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.Mapping?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static string NotePrefix(string entityType)
    {
        var sheet = ResolveProfileSheet(entityType);
        return sheet is "BANK" or "COOPBANK" or "LIFE" or "GENINS" or "REINS" ? "Schedule" : "Note";
    }
}

public sealed record FinancialStatementNoteTemplateRow(string Note, string Title, string LineItem, string MappingKey, int WorkbookRow);
public sealed record FinancialStatementMappingOption(
    string FsHead,
    string NoteHead,
    string NoteSubHead,
    string MappingKey,
    string Note,
    string Class1 = "",
    string Class2 = "",
    string Class3 = "",
    string Class4 = "",
    string Class5 = "",
    string StableHierarchyId = "");
