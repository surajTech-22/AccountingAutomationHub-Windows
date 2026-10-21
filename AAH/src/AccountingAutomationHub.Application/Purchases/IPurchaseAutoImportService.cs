using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Application.Purchases;

public interface IPurchaseAutoImportService
{
    Task<PurchaseAutoImportResult> ImportAsync(
        PurchaseAutoImportRequest request,
        IProgress<PurchaseImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
