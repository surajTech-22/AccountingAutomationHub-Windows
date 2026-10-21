using AccountingAutomationHub.Domain.Common;

namespace AccountingAutomationHub.Application.Masters;

/// <summary>A row identity captured when a Stock Item review is opened.</summary>
public sealed record StockItemMappingSourceRevision(
    string RecordJson, string UpdatedUtc, long BatchId, bool IsUserEdited);

public sealed record StockItemMappingSourceRecord(
    long PersistentId, string CurrentItemName, long StockItemPersistentId,
    string TallyStockMasterId, StockItemMappingSourceRevision? ExpectedRevision = null);

/// <summary>The exact alias revision visible when the review was opened.</summary>
public sealed record StockItemMappingAliasRevision(
    string StockItemName, string Source, DateTime UpdatedUtc);

/// <summary>One explicit user choice, applied to exactly the original source rows.</summary>
public sealed record StockItemMappingSelection(
    string ImportedName, string CurrentItemName, string FinalItemName,
    IReadOnlyList<StockItemMappingSourceRecord> SourceRecords,
    StockItemMappingAliasRevision? ExpectedAlias = null,
    bool AliasOverrideConfirmed = false);

public sealed record StockItemMappingResult(
    int SourceNames, int RecordsLinked, int RecordsChanged,
    int MastersCreated, int AliasesSaved, string Warning = "")
{
    public string Message =>
        $"Verified {SourceNames:N0} imported name(s) and {RecordsLinked:N0} saved row(s). " +
        $"{RecordsChanged:N0} row(s) changed, {MastersCreated:N0} Stock Master(s) created, " +
        $"{AliasesSaved:N0} explicit mapping(s) saved." +
        (Warning.Length == 0 ? string.Empty : " " + Warning);
}

/// <summary>A conflicting alias is returned without writing any Master, alias or row.
/// The user may explicitly confirm a replacement of this exact saved revision.</summary>
public sealed class StockItemMappingAliasConflictException : InvalidOperationException
{
    public string ImportedName { get; }
    public string RequestedName { get; }
    public StockItemMappingAliasRevision? CurrentAlias { get; }

    /// <summary>Bind a user-confirmed retry to the exact alias revision returned
    /// by the failed transaction. Other selections and source evidence are unchanged.</summary>
    public IReadOnlyList<StockItemMappingSelection> ConfirmSelections(
        IReadOnlyList<StockItemMappingSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var found = false;
        var result = selections.ToArray();
        for (var i = 0; i < selections.Count; i++)
        {
            var selection = selections[i];
            if (string.Equals(AccountingText.CleanWhitespace(selection.ImportedName),
                    AccountingText.CleanWhitespace(ImportedName), StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(AccountingText.CleanWhitespace(selection.FinalItemName),
                        AccountingText.CleanWhitespace(RequestedName), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected Stock Item changed during conflict confirmation. Review the selection again.");
                if (found) throw new InvalidOperationException("The Stock review contains duplicate imported names.");
                found = true;
                result[i] = selection with { ExpectedAlias = CurrentAlias, AliasOverrideConfirmed = true };
            }
            else result[i] = selection;
        }
        if (!found) throw new InvalidOperationException("The conflicting Stock Item is no longer selected. Reopen the review.");
        return result;
    }

    public StockItemMappingAliasConflictException(string importedName, string requestedName,
        StockItemMappingAliasRevision? currentAlias)
        : base($"The saved mapping for '{importedName}' now points to '{currentAlias?.StockItemName ?? "(No mapping)"}'. " +
            $"Your selected Stock Item is '{requestedName}'. No mapping was committed. You can confirm your selection to replace the current alias, or keep the saved mapping.")
    {
        ImportedName = importedName;
        RequestedName = requestedName;
        CurrentAlias = currentAlias;
    }
}
