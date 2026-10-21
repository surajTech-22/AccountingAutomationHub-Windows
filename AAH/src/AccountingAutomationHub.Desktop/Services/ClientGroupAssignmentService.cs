using AccountingAutomationHub.Application.Settings;

namespace AccountingAutomationHub.Desktop.Services;

public static class ClientGroupAssignmentService
{
    public static void Assign(IApplicationPreferenceRepository preferences, long clientId, string? folderId)
    {
        if (clientId <= 0) return;
        var data = ClientGroupingCatalog.Load(preferences.Get(ClientGroupingCatalog.PreferenceKey));
        data.Assignments.RemoveAll(x => x.ClientId == clientId);
        var id = (folderId ?? string.Empty).Trim();
        if (id.Length > 0 && data.Folders.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            data.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = clientId, FolderId = id });
        preferences.Set(ClientGroupingCatalog.PreferenceKey, ClientGroupingCatalog.Save(data));
    }
}
