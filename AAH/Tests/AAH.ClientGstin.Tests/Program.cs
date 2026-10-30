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

    // AAH previously saved some GSTIN-only clients with the GSTIN as
    // Client Name. Source name recognition must repair those placeholders
    // without modifying a manually entered business name or GSTIN list.
    var unresolvedGstin = "29ABCDE1234F1Z5";
    if (!GstClientNameRecognitionPolicy.IsUnresolvedName(unresolvedGstin, unresolvedGstin) ||
        !GstClientNameRecognitionPolicy.IsUnresolvedName(" 29-ABCDE1234F1Z5 ", unresolvedGstin) ||
        GstClientNameRecognitionPolicy.IsUnresolvedName("Registered Business", unresolvedGstin) ||
        GstClientNameRecognitionPolicy.HasSourceBusinessName(unresolvedGstin, unresolvedGstin) ||
        GstClientNameRecognitionPolicy.HasSourceBusinessName("ABCDE1234F", unresolvedGstin) ||
        !GstClientNameRecognitionPolicy.HasSourceBusinessName("SOURCE VERIFIED TRADERS", unresolvedGstin))
        throw new Exception("Client-name source recognition accepted GSTIN placeholders or rejected valid names.");

    var placeholderClient = store.Save(new ClientProfile
    {
        ClientCode = "GST-NAME-RECOVER",
        ClientName = unresolvedGstin,
        Gstin = unresolvedGstin,
        Pan = "ABCDE1234F",
        FinancialYear = "2025-26",
        AdditionalGstins = ["27ABCDE1234F1Z5"]
    });
    store.ApplyImportedGstProfile(placeholderClient.Id,
        new GstinProfileLookupResult { Gstin = unresolvedGstin, TradeName = unresolvedGstin });
    if (store.GetById(placeholderClient.Id)!.ClientName != unresolvedGstin)
        throw new Exception("GSTIN placeholder presented as authoritative trade name.");

    var profileRecovered = store.ApplyImportedGstProfile(placeholderClient.Id,
        new GstinProfileLookupResult
        {
            Gstin = unresolvedGstin,
            TradeName = "SOURCE VERIFIED TRADERS",
            LegalName = "SOURCE VERIFIED TRADERS PRIVATE LIMITED"
        });
    if (!profileRecovered.ClientName.Equals("Source Verified Traders", StringComparison.Ordinal) ||
        profileRecovered.TradeName != "SOURCE VERIFIED TRADERS" ||
        profileRecovered.LegalName != "SOURCE VERIFIED TRADERS PRIVATE LIMITED" ||
        store.GetById(placeholderClient.Id)?.AdditionalGstins?.Single() != "27ABCDE1234F1Z5")
        throw new Exception($"Source name recovery lost business identity or additional GSTIN: ClientName={profileRecovered.ClientName}; TradeName={profileRecovered.TradeName}; LegalName={profileRecovered.LegalName}; Additional={string.Join(",", store.GetById(placeholderClient.Id)?.AdditionalGstins ?? [])}.");

    var manualClient = store.Save(new ClientProfile
    {
        ClientCode = "GST-NAME-MANUAL",
        ClientName = "MY HAND-ENTERED COMPANY",
        Gstin = unresolvedGstin,
        Pan = "ABCDE1234F",
        FinancialYear = "2024-25",
        AdditionalGstins = ["27ABCDE1234F1Z5"]
    });
    store.ApplyImportedGstProfile(manualClient.Id,
        new GstinProfileLookupResult { Gstin = unresolvedGstin, TradeName = "Different GST Source Name" });
    if (store.GetById(manualClient.Id)?.ClientName != "MY HAND-ENTERED COMPANY")
        throw new Exception("GST profile lookup replaced user's manual Client Name.");
    // The new Auto Folder moves GSTIN evidence away from Downloads.
    // Keep GSTIN-specific nested JSON discoverable for the existing profile
    // lookup; do not cross-assign a different registration's trade name.
    var gstFolder = Path.Combine(root, "AutoFolder", "GST", unresolvedGstin, "GSTR-3B");
    Directory.CreateDirectory(gstFolder);
    File.WriteAllText(Path.Combine(gstFolder, "profile.json"),
        """{"gstin":"29ABCDE1234F1Z5","lgnm":"SOURCE VERIFIED TRADERS PRIVATE LIMITED","trdnm":"SOURCE VERIFIED TRADERS"}""");
    var lookups = new GstinProfileLookupService(Path.Combine(root, "clients-test.sqlite"));
    lookups.Initialize();
    var folderEvidence = await lookups.LookupFolderAsync(
        unresolvedGstin, Path.Combine(root, "AutoFolder", "GST", unresolvedGstin));
    if (folderEvidence.TradeName != "SOURCE VERIFIED TRADERS" ||
        folderEvidence.LegalName != "SOURCE VERIFIED TRADERS PRIVATE LIMITED" ||
        folderEvidence.SourcePath.Length == 0)
        throw new Exception("Nested GSTIN Auto Folder no longer supplies business-name evidence.");
    var differentGstin = await lookups.LookupFolderAsync(
        "27ABCDE1234F1Z5", Path.Combine(root, "AutoFolder", "GST", unresolvedGstin));
    if (differentGstin.TradeName.Length > 0 || differentGstin.LegalName.Length > 0)
        throw new Exception("A source from another GST registration was used as client-name evidence.");
    Console.WriteLine("PASS: 2/2 nested GSTIN name lookup and cross-registration isolation checks.");

    Console.WriteLine("PASS: 4/4 source-backed Client Name recovery and manual-name preservation checks.");

    Console.WriteLine("PASS: 9/9 multi-GSTIN client preservation, retrieval, and integrity checks.");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch { /* Test SQLite WAL may be briefly locked by the runner. */ }
}
