namespace AccountingAutomationHub.Application.Masters;

/// <summary>A detached source row with its exact persisted revision and ownership metadata.</summary>
public sealed record StockProjectionSnapshot<T>(
    T? Record, string OriginalRecordJson, string OriginalUpdatedUtc,
    long OriginalBatchId, bool OriginalIsUserEdited) where T : class;

/// <summary>Only the captured source revision may receive a calculated inventory projection.</summary>
public sealed record StockProjectionChange(
    long PersistentId, string ExpectedRecordJson, string ExpectedUpdatedUtc,
    long ExpectedBatchId, bool ExpectedIsUserEdited, string ProjectedRecordJson);

public sealed record StockProjectionWriteResult(int Updated, int Unchanged, int Conflicted, int Missing, int Invalid = 0)
{
    public bool NeedsRetry => Conflicted > 0;
}
