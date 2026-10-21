using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Application.Banking;

public interface IBankStatementImportService
{
    Task<BankStatementImportResult> ImportAsync(BankStatementImportRequest request, CancellationToken cancellationToken = default);
}
