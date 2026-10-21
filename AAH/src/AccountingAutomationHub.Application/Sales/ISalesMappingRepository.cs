namespace AccountingAutomationHub.Application.Sales;

public interface ISalesMappingRepository
{
    void Initialize();
    IReadOnlyDictionary<string, string>? Get(long clientId, string financialYear, string headerSignature);
    void Save(long clientId, string financialYear, string headerSignature, IReadOnlyDictionary<string, string> mapping, string gstTreatment, string taxColumnMode);
    void Delete(long clientId, string financialYear, string headerSignature);
    (string GstTreatment,string TaxColumnMode) GetOptions(long clientId,string financialYear,string headerSignature);
}
