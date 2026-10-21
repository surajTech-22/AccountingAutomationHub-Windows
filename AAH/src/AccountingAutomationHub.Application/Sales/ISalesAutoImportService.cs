using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Application.Sales;

public interface ISalesAutoImportService
{
    Task<SalesAutoImportResult> ImportAsync(
        SalesAutoImportRequest request,
        IProgress<SalesImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
