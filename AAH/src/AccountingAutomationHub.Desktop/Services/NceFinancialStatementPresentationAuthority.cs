using System;
using System.Collections.Generic;
using System.Linq;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R51 explicit NCE sheet-to-Note and Note-to-format authority.
///
/// A Note never discovers its physical owner from a row number, caption fragment or the old
/// worksheet tab name. Each Note has one declared owner sheet and one declared formatting
/// contract. Physical grouped sheets are layout containers only and may render only the Notes
/// declared here. The legacy template tab names remain internal until final rendering; user-facing
/// output is renamed to the canonical sheet names after all links/cross-checks are complete.
/// </summary>
internal static class NceFinancialStatementPresentationAuthority
{
    internal enum NoteLayoutKind
    {
        Narrative,
        OwnerCapital,
        Standard,
        PpeMovement
    }

    internal sealed record NoteFormatSpec(
        string FontName,
        int TitleFontSize,
        int HeaderFontSize,
        int BodyFontSize,
        double TitleRowHeight,
        double HeaderRowHeight,
        double BodyRowHeight,
        int FirstVisibleColumn,
        int LastVisibleColumn,
        int FyColumn,
        int PyColumn,
        bool RemovePreparationColumns,
        string NumberFormat,
        NoteLayoutKind LayoutKind);

    internal sealed record NotePresentationSpec(
        string Note,
        string Title,
        string OwnerSheet,
        string CanonicalOwnerSheet,
        string TotalLabel,
        IReadOnlyList<string> AdditionalTotalLabels,
        NoteFormatSpec Format)
    {
        public string ExactTitle => $"Note {Note} – {Title}";
    }

    internal sealed record SheetPresentationSpec(
        string TemplateSheet,
        string CanonicalSheet,
        IReadOnlyList<string> Notes,
        int FirstVisibleColumn,
        int LastVisibleColumn);

    internal const string BalanceSheetTemplate = "NCE Balance Sheet";
    internal const string ProfitLossTemplate = "NCE Notes 1-3";
    internal const string ProfitLossCanonical = "NCE Profit & Loss";

    private const string NumberFormat = "#,##0.00;(#,##0.00);\"-\"";

    private static NoteFormatSpec StandardFormat(int titleSize = 11, double bodyHeight = 21d)
        => new("Arial", titleSize, 10, 10, 22d, 30d, bodyHeight, 2, 4, 3, 4, true, NumberFormat, NoteLayoutKind.Standard);

    private static readonly NoteFormatSpec NarrativeFormat =
        new("Arial", 11, 10, 10, 21d, 21d, 21d, 2, 6, 0, 0, false, NumberFormat, NoteLayoutKind.Narrative);

    private static readonly NoteFormatSpec OwnerCapitalFormat =
        new("Arial", 11, 9, 10, 22d, 36d, 22d, 1, 11, 10, 11, false, NumberFormat, NoteLayoutKind.OwnerCapital);

    private static readonly NoteFormatSpec PpeFormat =
        new("Arial", 11, 9, 10, 21d, 38d, 21d, 2, 8, 7, 8, false, NumberFormat, NoteLayoutKind.PpeMovement);

    private static readonly IReadOnlyDictionary<string, NotePresentationSpec> NotesById =
        new[]
        {
            N("1", "Brief about the Entity", "NCE Notes 4-6", "NCE Notes 1-2", string.Empty, NarrativeFormat),
            N("2", "Summary of Significant Accounting Policies", "NCE Notes 4-6", "NCE Notes 1-2", string.Empty, NarrativeFormat),
            N("3", "Owners' Capital Account", "NCE Notes 7-10", "NCE Notes 3-6", "Total Owners' Capital", OwnerCapitalFormat),
            N("4", "Reserves and Surplus", "NCE Notes 7-10", "NCE Notes 3-6", "Total Reserves and Surplus", StandardFormat()),
            N("5", "Long-term Borrowings", "NCE Notes 7-10", "NCE Notes 3-6", "Total Long-term Borrowings", StandardFormat()),
            N("6", "Deferred Tax Balances", "NCE Notes 7-10", "NCE Notes 3-6", "Net Deferred Tax Balance", StandardFormat(), "Total Deferred Tax Liabilities", "Total Deferred Tax Assets"),
            N("7", "Other Long-term Liabilities", "NCE Note 11", "NCE Notes 7-14", "Total Other Long-term Liabilities", StandardFormat()),
            N("8", "Long-term Provisions", "NCE Note 11", "NCE Notes 7-14", "Total Long-term Provisions", StandardFormat()),
            N("9", "Short-term Borrowings", "NCE Note 11", "NCE Notes 7-14", "Total Short-term Borrowings", StandardFormat()),
            N("10", "Trade Payables", "NCE Note 11", "NCE Notes 7-14", "Total Trade Payables", StandardFormat()),
            N("11", "Other Current Liabilities", "NCE Note 11", "NCE Notes 7-14", "Total Other Current Liabilities", StandardFormat()),
            N("12", "Short-term Provisions", "NCE Note 11", "NCE Notes 7-14", "Total Short-term Provisions", StandardFormat()),
            N("13", "Property, Plant and Equipment and Intangible Assets", "NCE Note 11", "NCE Notes 7-14", "Total", PpeFormat),
            N("14", "Investments", "NCE Note 11", "NCE Notes 7-14", "Total Current Investments", StandardFormat(10, 19d), "Total Non-current Investments"),
            N("15", "Long-term Loans and Advances", "NCE Notes 12-18", "NCE Notes 15-28", "Total Long-term Loans and Advances", StandardFormat()),
            N("16", "Other Non-current Assets", "NCE Notes 12-18", "NCE Notes 15-28", "Total Other Non-current Assets", StandardFormat()),
            N("17", "Inventories", "NCE Notes 12-18", "NCE Notes 15-28", "Total Inventories", StandardFormat()),
            N("18", "Trade Receivables", "NCE Notes 12-18", "NCE Notes 15-28", "Total Trade Receivables", StandardFormat()),
            N("19", "Cash and Bank Balances", "NCE Notes 12-18", "NCE Notes 15-28", "Total Cash and Bank Balances", StandardFormat()),
            N("20", "Short-term Loans and Advances", "NCE Notes 12-18", "NCE Notes 15-28", "Total Short-term Loans and Advances", StandardFormat()),
            N("21", "Other Current Assets", "NCE Notes 12-18", "NCE Notes 15-28", "Total Other Current Assets", StandardFormat()),
            N("22", "Revenue from Operations", "NCE Notes 12-18", "NCE Notes 15-28", "Total Revenue from Operations", StandardFormat()),
            N("23", "Other Income", "NCE Notes 12-18", "NCE Notes 15-28", "Total Other Income", StandardFormat()),
            N("24", "Cost of Goods Sold", "NCE Notes 12-18", "NCE Notes 15-28", "Total Cost of Goods Sold", StandardFormat(10, 19d)),
            N("25", "Employee Benefits Expense", "NCE Notes 12-18", "NCE Notes 15-28", "Total Employee Benefits Expense", StandardFormat()),
            N("26", "Finance Costs", "NCE Notes 12-18", "NCE Notes 15-28", "Total Finance Costs", StandardFormat()),
            N("27", "Depreciation and Amortisation Expense", "NCE Notes 12-18", "NCE Notes 15-28", "Total Depreciation and Amortisation Expense", StandardFormat()),
            N("28", "Other Expenses", "NCE Notes 12-18", "NCE Notes 15-28", "Total Other Expenses", StandardFormat())
        }.ToDictionary(x => x.Note, StringComparer.OrdinalIgnoreCase);

    private static NotePresentationSpec N(
        string note, string title, string ownerSheet, string canonicalOwnerSheet, string totalLabel, NoteFormatSpec format, params string[] additionalTotalLabels)
        => new(note, title, ownerSheet, canonicalOwnerSheet, totalLabel, additionalTotalLabels ?? Array.Empty<string>(), format);

    internal static readonly IReadOnlyList<SheetPresentationSpec> NoteSheets = new[]
    {
        new SheetPresentationSpec("NCE Notes 4-6", "NCE Notes 1-2", new[] { "1", "2" }, 2, 6),
        new SheetPresentationSpec("NCE Notes 7-10", "NCE Notes 3-6", new[] { "3", "4", "5", "6" }, 1, 11),
        new SheetPresentationSpec("NCE Note 11", "NCE Notes 7-14", new[] { "7", "8", "9", "10", "11", "12", "13", "14" }, 2, 8),
        new SheetPresentationSpec("NCE Notes 12-18", "NCE Notes 15-28", new[] { "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28" }, 2, 4)
    };

    internal static NotePresentationSpec ForNote(string note)
        => NotesById.TryGetValue(note ?? string.Empty, out var value)
            ? value
            : throw new InvalidOperationException($"NCE Note {note} has no sheet/presentation authority.");

    internal static IReadOnlyList<NotePresentationSpec> ForSheet(string templateSheet)
    {
        var sheet = NoteSheets.FirstOrDefault(x => x.TemplateSheet.Equals(templateSheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"NCE physical sheet '{templateSheet}' has no Note authority.");
        return sheet.Notes.Select(ForNote).ToArray();
    }

    internal static SheetPresentationSpec Sheet(string templateSheet)
        => NoteSheets.FirstOrDefault(x => x.TemplateSheet.Equals(templateSheet, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"NCE physical sheet '{templateSheet}' has no presentation authority.");

    internal static IReadOnlyDictionary<string, string> CanonicalSheetNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ProfitLossTemplate] = ProfitLossCanonical,
            ["NCE Notes 4-6"] = "NCE Notes 1-2",
            ["NCE Notes 7-10"] = "NCE Notes 3-6",
            ["NCE Note 11"] = "NCE Notes 7-14",
            ["NCE Notes 12-18"] = "NCE Notes 15-28"
        };
}
