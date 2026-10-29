using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Infrastructure.Clients;

var root = Path.Combine(Path.GetTempPath(), "AAH-GSTIN-REGRESSION-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new SqliteClientRepository(Path.Combine(root, "clients-test.sqlite"));
    store.Initialize();

    static ClientProfile Profile() => new()
    {
        ClientCode = "GSTIN-REG-001",
        ClientName = "Synthetic Multi-State Trader",
        FinancialYear = "2026-27",
        Gstin = "29ABCDE1234F1Z5",
        Pan = "ABCDE1234F",
        State = "Karnataka",
        AdditionalGstins = ["27ABCDE1234F1Z5", "33ABCDE1234F2Z5"]
    };

    var created = store.Save(Profile());
    if (created.Id <= 0) throw new Exception("New client not saved.");
    var fromDb = store.GetById(created.Id) ?? throw new Exception("Client not found after save.");
    if (fromDb.Gstin != "29ABCDE1234F1Z5" ||
        fromDb.AdditionalGstins?.Count != 2 ||
        !fromDb.AdditionalGstins.Contains("27ABCDE1234F1Z5") ||
        !fromDb.AdditionalGstins.Contains("33ABCDE1234F2Z5"))
        throw new Exception("Secondary GSTIN list not persisted or read correctly.");

    var clone = fromDb.Clone();
    if (clone.AdditionalGstins?.Count != 2) throw new Exception("Client Clone lost secondary GSTINs.");

    store.SetCurrent(created.Id);
    if (store.GetCurrent()?.AdditionalGstins?.Count != 2 ||
        store.GetAll(false).Single().AdditionalGstins?.Count != 2)
        throw new Exception("Client directory/current read did not hydrate GSTINs.");

    // Legacy/profile updates must not wipe registrations when callers don't
    // provide a new list (null means preserve; [] explicitly means clear).
    var legacyUpdate = store.GetById(created.Id)!;
    legacyUpdate.AdditionalGstins = null;
    legacyUpdate.Notes = "Legacy note update";
    store.Save(legacyUpdate);
    if (store.GetById(created.Id)?.AdditionalGstins?.Count != 2)
        throw new Exception("Legacy update removed extra GSTINs.");

    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Unsafe registration accepted: " + name);
    }

    var badPan = store.GetById(created.Id)!;
    badPan.AdditionalGstins = ["27ZZZZZ1234F1Z5"];
    Reject(() => store.Save(badPan), "different PAN");

    var duplicate = store.GetById(created.Id)!;
    duplicate.AdditionalGstins = ["27ABCDE1234F1Z5", "27ABCDE1234F1Z5"];
    Reject(() => store.Save(duplicate), "duplicate");

    var badPrimary = store.GetById(created.Id)!;
    badPrimary.AdditionalGstins = ["29ABCDE1234F1Z5"];
    Reject(() => store.Save(badPrimary), "same as primary");

    var afterRejection = store.GetById(created.Id)!;
    if (afterRejection.AdditionalGstins?.Count != 2)
        throw new Exception("Rejected update modified saved registrations.");

    var reduced = store.GetById(created.Id)!;
    reduced.AdditionalGstins = ["33ABCDE1234F2Z5"];
    store.Save(reduced);
    if (store.GetById(created.Id)?.AdditionalGstins?.Single() != "33ABCDE1234F2Z5")
        throw new Exception("Explicit removal of secondary GSTIN failed.");

    var deleted = store.GetById(created.Id)!;
    deleted.AdditionalGstins = [];
    store.Save(deleted);
    if (store.GetById(created.Id)?.AdditionalGstins?.Count != 0 ||
        store.GetById(created.Id)?.Gstin != "29ABCDE1234F1Z5")
        throw new Exception("Explicit secondary clear changed the primary GSTIN.");

    Console.WriteLine("PASS: 9/9 multi-GSTIN client preservation, retrieval, and integrity checks.");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch { /* Test SQLite WAL may be briefly locked by the runner. */ }
}
