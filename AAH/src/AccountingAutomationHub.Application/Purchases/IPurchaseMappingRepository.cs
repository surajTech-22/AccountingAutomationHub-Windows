namespace AccountingAutomationHub.Application.Purchases;

public sealed class PurchaseMappingProfile
{
    public string HeaderSignature { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
    public int HeaderRow { get; init; }
    public int DataStartRow { get; init; }
    public string GstTreatment { get; init; } = "Not shown in 2B";
    public IReadOnlyDictionary<string, string> Mapping { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public interface IPurchaseMappingRepository
{
    void Initialize();
    PurchaseMappingProfile? Get(long clientId, string financialYear, string headerSignature);
    IReadOnlyList<PurchaseMappingProfile> GetAll(long clientId, string financialYear);
    void Save(long clientId, string financialYear, PurchaseMappingProfile profile);
    void Delete(long clientId, string financialYear, string headerSignature);
}
