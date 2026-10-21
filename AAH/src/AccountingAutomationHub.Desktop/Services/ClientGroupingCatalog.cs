using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

public static class ClientGroupingCatalog
{
    public const string PreferenceKey = "Clients.GroupFolders.V1";

    // Historical synthetic roots retained only as migration identifiers. R20 no longer
    // creates or displays fixed Firm/Company placeholder folders. Every saved Group is a
    // real user-created Group and a blank ParentId means that Group is topmost.
    public const string RootFirmId = "root-firm";
    public const string RootCompanyId = "root-company";

    private static readonly object CacheGate = new();
    private static string _cachedJson = string.Empty;
    private static ClientGroupCatalogData? _cachedData;

    public static ClientGroupCatalogData Load(string? json)
    {
        var source = json ?? string.Empty;
        lock (CacheGate)
        {
            if (_cachedData is not null && source.Equals(_cachedJson, StringComparison.Ordinal))
                return Clone(_cachedData);
        }

        ClientGroupCatalogData data;
        try { data = JsonSerializer.Deserialize<ClientGroupCatalogData>(source) ?? new ClientGroupCatalogData(); }
        catch { data = new ClientGroupCatalogData(); }
        Normalize(data);

        lock (CacheGate)
        {
            _cachedJson = source;
            _cachedData = Clone(data);
        }
        return data;
    }

    public static string Save(ClientGroupCatalogData data)
    {
        Normalize(data);
        var json = JsonSerializer.Serialize(data);
        lock (CacheGate)
        {
            _cachedJson = json;
            _cachedData = Clone(data);
        }
        return json;
    }

    private static ClientGroupCatalogData Clone(ClientGroupCatalogData source) => new()
    {
        Folders = source.Folders.Select(x => new ClientGroupFolderRecord
        {
            Id = x.Id,
            Name = x.Name,
            Type = x.Type,
            ParentId = x.ParentId,
            LinkedCaProfileId = x.LinkedCaProfileId
        }).ToList(),
        Assignments = source.Assignments.Select(x => new ClientGroupAssignmentRecord
        {
            ClientId = x.ClientId,
            FolderId = x.FolderId
        }).ToList()
    };

    public static void Normalize(ClientGroupCatalogData data)
    {
        data.Folders ??= [];
        data.Assignments ??= [];

        foreach (var folder in data.Folders)
        {
            folder.Id = string.IsNullOrWhiteSpace(folder.Id) ? Guid.NewGuid().ToString("N") : folder.Id.Trim();
            folder.Name = (folder.Name ?? string.Empty).Trim();
            folder.Type = ClientGroupFolderTypes.Normalize(folder.Type);
            folder.ParentId = (folder.ParentId ?? string.Empty).Trim();
            folder.LinkedCaProfileId = (folder.LinkedCaProfileId ?? string.Empty).Trim();
        }

        // R20 migration: old builds injected two synthetic placeholder roots named Firm and
        // Company. They were not real user Groups. Detach their children to top level and
        // remove the placeholders. Direct Client assignments to those placeholders become
        // ungrouped when assignments are validated below.
        var legacyRootIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RootFirmId,
            RootCompanyId
        };
        foreach (var folder in data.Folders)
        {
            if (legacyRootIds.Contains(folder.ParentId)) folder.ParentId = string.Empty;
        }
        data.Folders.RemoveAll(x => legacyRootIds.Contains(x.Id));

        // Remove duplicate IDs conservatively, keeping the last saved record.
        data.Folders = data.Folders
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        var folderIds = data.Folders
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in data.Folders)
        {
            // Blank ParentId is valid topmost authority for Firm, Company, CA, Auditor and
            // Company Group. Missing/self parents fail back to topmost instead of inventing
            // a Firm/Company parent.
            if (!string.IsNullOrWhiteSpace(folder.ParentId) &&
                (!folderIds.Contains(folder.ParentId) || folder.ParentId.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)))
                folder.ParentId = string.Empty;

            // Firm is optional, but whenever used it is the highest container and cannot be
            // persisted below another Group.
            if (folder.Type.Equals(ClientGroupFolderTypes.Firm, StringComparison.OrdinalIgnoreCase))
                folder.ParentId = string.Empty;

            if (!folder.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase))
                folder.LinkedCaProfileId = string.Empty;
        }

        // Repair any legacy parent cycle so every saved Group remains reachable. New moves
        // are already cycle-guarded in the ViewModel; this is only durable recovery for old data.
        var byId = data.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in data.Folders)
        {
            var cursor = folder;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(cursor.ParentId) && byId.TryGetValue(cursor.ParentId, out var parent))
            {
                if (!visited.Add(cursor.Id) || visited.Contains(parent.Id))
                {
                    folder.ParentId = string.Empty;
                    break;
                }
                cursor = parent;
            }
        }

        data.Assignments = data.Assignments
            .Where(x => x.ClientId > 0 && folderIds.Contains(x.FolderId))
            .GroupBy(x => x.ClientId)
            .Select(x => x.Last())
            .ToList();
    }

    public static string FindLinkedCaProfileId(ClientGroupCatalogData data, long clientId)
    {
        Normalize(data);
        var assignment = data.Assignments.LastOrDefault(x => x.ClientId == clientId);
        if (assignment is null) return string.Empty;
        var byId = data.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var currentId = assignment.FolderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId) && byId.TryGetValue(currentId, out var folder))
        {
            if (folder.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(folder.LinkedCaProfileId))
                return folder.LinkedCaProfileId;
            currentId = folder.ParentId;
        }
        return string.Empty;
    }
}

public static class ClientGroupFolderTypes
{
    public const string Firm = "Firm";
    public const string Company = "Company";
    public const string Ca = "CA";
    public const string Auditor = "Auditor";
    public const string CompanyGroup = "Company Group";

    // Compatibility aliases for older source gates/callers. These are now ordinary creatable
    // Group types, not synthetic fixed roots.
    public const string RootFirm = Firm;
    public const string RootCompany = Company;

    public static IReadOnlyList<string> CreatableTypes { get; } = [Firm, Company, Ca, Auditor, CompanyGroup];
    public static IReadOnlyList<string> ChildTypes { get; } = CreatableTypes;

    // There are no fixed Group records in R20. A blank ParentId means topmost.
    public static bool IsRoot(string? value) => false;

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals(Firm, StringComparison.OrdinalIgnoreCase)) return Firm;
        if (text.Equals(Company, StringComparison.OrdinalIgnoreCase)) return Company;
        if (text.Equals(Ca, StringComparison.OrdinalIgnoreCase)) return Ca;
        if (text.Equals(Auditor, StringComparison.OrdinalIgnoreCase)) return Auditor;
        if (text.Equals(CompanyGroup, StringComparison.OrdinalIgnoreCase)) return CompanyGroup;
        return CompanyGroup;
    }
}

public sealed class ClientGroupCatalogData
{
    public List<ClientGroupFolderRecord> Folders { get; set; } = [];
    public List<ClientGroupAssignmentRecord> Assignments { get; set; } = [];
}

public sealed class ClientGroupFolderRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = ClientGroupFolderTypes.CompanyGroup;
    public string ParentId { get; set; } = string.Empty;
    public string LinkedCaProfileId { get; set; } = string.Empty;
}

public sealed class ClientGroupAssignmentRecord
{
    public long ClientId { get; set; }
    public string FolderId { get; set; } = string.Empty;
}
