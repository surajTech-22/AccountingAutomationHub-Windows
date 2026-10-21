using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Application.Clients;

public interface IGstinProfileLookupService
{
    void Initialize();

    void Invalidate(string gstin);

    Task<GstinProfileLookupResult> LookupAsync(
        string gstin,
        string eReturnsRoot,
        CancellationToken cancellationToken = default);

    Task<GstinProfileLookupResult> LookupFolderAsync(
        string gstin,
        string folderPath,
        CancellationToken cancellationToken = default);

    Task<GstinProfileLookupResult> LookupAutomationLinkAsync(
        string gstin,
        string automationFolderPath,
        string eReturnsRoot,
        string financialYear,
        CancellationToken cancellationToken = default);
}
