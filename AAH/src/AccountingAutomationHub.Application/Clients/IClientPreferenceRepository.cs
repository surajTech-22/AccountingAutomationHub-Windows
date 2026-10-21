namespace AccountingAutomationHub.Application.Clients;

public interface IClientPreferenceRepository
{
    void Initialize();

    string Get(
        long clientId,
        string preferenceKey);

    void Set(
        long clientId,
        string preferenceKey,
        string preferenceValue);
}
