using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Application.Clients;

public interface IClientRepository
{
    void Initialize();

    IReadOnlyList<ClientProfile> GetAll(
        bool includeArchived);

    ClientProfile? GetById(
        long clientId);

    ClientProfile? GetCurrent();

    ClientProfile Save(
        ClientProfile client);

    ClientProfile ApplyImportedGstProfile(
        long clientId,
        GstinProfileLookupResult profile);

    void SetCurrent(
        long clientId);

    void ClearCurrent();

    void Archive(
        long clientId);

    void Restore(
        long clientId);

    // R39: permanent removal is intentionally separate from Archive/Restore and is
    // exposed by Desktop only after the record has first been archived.
    void Delete(
        long clientId);

    bool ClientCodeExists(
        string clientCode,
        long excludingClientId = 0);
}
