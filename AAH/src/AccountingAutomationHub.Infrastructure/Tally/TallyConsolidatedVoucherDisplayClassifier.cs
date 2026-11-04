using System;
using System.Collections.Generic;
using System.Linq;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Read-only presentation projection for the ORIGINAL Dashboard Tally module tiles.
/// A branch subtype is still the destination voucher's actual Tally type; only its
/// visible parent module and stable original source anchor are normalized.
/// No voucher XML, Tally master, source-binding state or posting is modified.
/// </summary>
public static class TallyConsolidatedVoucherDisplayClassifier
{
    public static TallyConsolidatedVoucherDisplayType Classify(
        string destinationType,
        string preparedVoucherNumber,
        IReadOnlyDictionary<string, string>? parentByDestinationType = null,
        IReadOnlyCollection<string>? configuredBranchNames = null)
    {
        var type = (destinationType ?? string.Empty).Trim();
        var number = (preparedVoucherNumber ?? string.Empty).Trim();
        if (type.Length == 0 || number.Length == 0)
            throw new InvalidOperationException("Consolidated voucher display requires a real type and source number.");

        // The current in-memory plan is the most authoritative subtype->parent
        // mapping. On navigation the saved Vouchers XML is display-only, so
        // recover the naming rule from CURRENT configured branch display names
        // instead of losing all parent types and source identity on reload.
        var mapped = parentByDestinationType is not null &&
                     parentByDestinationType.TryGetValue(type, out var explicitParent) &&
                     !string.IsNullOrWhiteSpace(explicitParent);
        var parent = mapped ? explicitParent!.Trim() : type;
        var branch = string.Empty;
        var suffix = (configuredBranchNames ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => type.EndsWith(" - " + x, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Length)
            .FirstOrDefault();
        if (suffix is not null)
        {
            var inferredParent = type[..^(suffix.Length + 3)].Trim();
            if (inferredParent.Length > 0 &&
                (!mapped || inferredParent.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            {
                branch = suffix;
                if (!mapped) parent = inferredParent;
            }
        }

        // Type is the original parent for layout/reporting only. Bank is
        // established by the original prepared BANK-row number, not by
        // Payment/Receipt/Contra, which also occur in the Cash module.
        var bank = number.StartsWith("BANK-", StringComparison.OrdinalIgnoreCase) ||
                   number.Contains("-BANK-", StringComparison.OrdinalIgnoreCase);
        var module = bank ? "Bank" :
            parent.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? "Purchase" :
            parent.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales" :
            parent.Equals("Journal", StringComparison.OrdinalIgnoreCase) ? "Journal" :
            parent.Equals("Debit Note", StringComparison.OrdinalIgnoreCase) ? "Debit Note" :
            parent.Equals("Credit Note", StringComparison.OrdinalIgnoreCase) ? "Credit Note" :
            parent.Equals("Contra", StringComparison.OrdinalIgnoreCase) ||
            parent.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ||
            parent.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? "Cash" : parent;

        return new TallyConsolidatedVoucherDisplayType(
            type, parent, module, branch,
            TallyConsolidatedVoucherImportService.BuildConsolidatedSourceAnchor(parent, number));
    }
}

public sealed record TallyConsolidatedVoucherDisplayType(
    string DestinationType, string ParentType, string Module,
    string BranchName, string SourceAnchor);
