using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Application.Purchases;

public interface IPurchaseImportService
{
    Task<PurchaseImportResult> ImportAsync(
        PurchaseImportRequest request,
        IProgress<PurchaseImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
