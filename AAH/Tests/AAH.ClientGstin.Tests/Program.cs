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

    var legacyPlaceholder = store.Save(new ClientProfile
    {
        ClientCode = "GST-LEGACY-TRADE",
        ClientName = unresolvedGstin,
        Gstin = unresolvedGstin,
        TradeName = unresolvedGstin,
        TradeNameIsManual = true,
        LegalName = unresolvedGstin,
        LegalNameIsManual = true,
        Pan = "ABCDE1234F",
        FinancialYear = "2023-24",
        AdditionalGstins = ["27ABCDE1234F1Z5"]
    });
    var legacyRepaired = store.ApplyImportedGstProfile(
        legacyPlaceholder.Id,
        new GstinProfileLookupResult
        {
            Gstin = unresolvedGstin,
            TradeName = "SOURCE VERIFIED TRADERS",
            LegalName = "SOURCE VERIFIED TRADERS PRIVATE LIMITED"
        });
    if (legacyRepaired.TradeName != "SOURCE VERIFIED TRADERS" ||
        legacyRepaired.LegalName != "SOURCE VERIFIED TRADERS PRIVATE LIMITED" ||
        legacyRepaired.TradeNameIsManual || legacyRepaired.LegalNameIsManual ||
        store.GetById(legacyPlaceholder.Id)?.AdditionalGstins?.Single() != "27ABCDE1234F1Z5")
        throw new Exception("Legacy GSTIN-as-Trade/Legal-Name placeholders were not safely repaired.");

    var manualClient = store.Save(new ClientProfile
    {
        ClientCode = "GST-NAME-MANUAL",
        ClientName = "MY HAND-ENTERED COMPANY",
        Gstin = unresolvedGstin,
        TradeName = "MY SHOP TRADE NAME",
        TradeNameIsManual = true,
        LegalName = "MY COMPANY LEGAL NAME",
        LegalNameIsManual = true,
        Pan = "ABCDE1234F",
        FinancialYear = "2024-25",
        AdditionalGstins = ["27ABCDE1234F1Z5"]
    });
    store.ApplyImportedGstProfile(manualClient.Id,
        new GstinProfileLookupResult
        {
            Gstin = unresolvedGstin,
            TradeName = "Different GST Source Name",
            LegalName = "Different GST Legal Name"
        });
    var retainedManual = store.GetById(manualClient.Id);
    if (retainedManual?.ClientName != "MY HAND-ENTERED COMPANY" ||
        retainedManual.TradeName != "MY SHOP TRADE NAME" ||
        retainedManual.LegalName != "MY COMPANY LEGAL NAME" ||
        !retainedManual.TradeNameIsManual || !retainedManual.LegalNameIsManual)
        throw new Exception("GST profile lookup replaced genuine manual Client/Trade/Legal names.");
    // An editor may have looked up this GSTIN before the automation worker
    // moved its verified source into Auto Folder. A negative session cache must
    // not prevent recognition after that file move.
    var lookups = new GstinProfileLookupService(Path.Combine(root, "clients-test.sqlite"));
    lookups.Initialize();
    var noSource = await lookups.LookupAsync(unresolvedGstin, Path.Combine(root, "nonexistent-ereturns"));
    if (noSource.WasFound)
        throw new Exception("Unverified GSTIN was incorrectly treated as an actual business name.");

    // Match the real multi-GSTIN routing: only the linked client/primary GSTIN
    // may provide the shared client trade name.
    var linkedRoot = Path.Combine(root, "AutoFolder");
    var gstFolder = Path.Combine(linkedRoot, "GST", unresolvedGstin, "GSTR-3B");
    Directory.CreateDirectory(gstFolder);
    File.WriteAllText(Path.Combine(gstFolder, "profile.json"),
        """{"gstin":"29ABCDE1234F1Z5","lgnm":"SOURCE VERIFIED TRADERS PRIVATE LIMITED","trdnm":"SOURCE VERIFIED TRADERS"}""");
    var otherGstinFolder = Path.Combine(linkedRoot, "GST", "27ABCDE1234F1Z5", "GSTR-3B");
    Directory.CreateDirectory(otherGstinFolder);
    File.WriteAllText(Path.Combine(otherGstinFolder, "other-profile.json"),
        """{"gstin":"27ABCDE1234F1Z5","lgnm":"GUJARAT BRANCH LEGAL NAME","trdnm":"GUJARAT BRANCH TRADE NAME"}""");
    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={Path.Combine(root, "clients-test.sqlite")}"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AutomationFolders
            (
                ClientId INTEGER NOT NULL,
                FolderPath TEXT NOT NULL
            );
            INSERT INTO AutomationFolders (ClientId, FolderPath)
            VALUES ($ClientId, $FolderPath);
            """;
        command.Parameters.AddWithValue("$ClientId", placeholderClient.Id);
        command.Parameters.AddWithValue("$FolderPath", linkedRoot);
        command.ExecuteNonQuery();
    }
    var editorEvidence = await lookups.LookupAsync(
        unresolvedGstin, Path.Combine(root, "nonexistent-ereturns"));
    if (editorEvidence.TradeName != "SOURCE VERIFIED TRADERS" ||
        editorEvidence.LegalName != "SOURCE VERIFIED TRADERS PRIVATE LIMITED" ||
        editorEvidence.SourcePath.Length == 0)
        throw new Exception("Editor did not recover matching primary GSTIN from moved Auto Folder evidence.");

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
    Console.WriteLine("PASS: linked primary GSTIN editor, negative-cache retry, nested lookup and cross-registration isolation.");

    Console.WriteLine("PASS: 4/4 source-backed Client Name recovery and manual-name preservation checks.");

    // Guard against the regression where a new Additional GSTIN saves but
    // source discovery waits for a full Downloads scan before the first import.
    // This is a source-wiring contract check; live imports also require
    // the existing Auto Import policy and linked-folder prerequisites.
    var sourceRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (sourceRoot is not null &&
           !File.Exists(Path.Combine(sourceRoot.FullName, "AAH", "src",
               "AccountingAutomationHub.Desktop", "Services", "BackgroundAutomationService.cs")))
        sourceRoot = sourceRoot.Parent;
    if (sourceRoot is null)
        throw new Exception("AAH source root unavailable for Added GSTIN import-wiring regression.");

    var automationSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "Services", "BackgroundAutomationService.cs"));
    var editorSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "ViewModels", "ClientManagementViewModel.cs"));
    var scheduleAt = automationSource.IndexOf(
        "public bool ScheduleNewClientGstinSourceDiscovery(", StringComparison.Ordinal);
    var firstImportAt = automationSource.IndexOf(
        "QueueSalesPurchaseSourceSet(clientId, financialYear, \"Sales\", gstin, immediate: true);",
        scheduleAt < 0 ? 0 : scheduleAt, StringComparison.Ordinal);
    var historyScanAt = automationSource.IndexOf(
        "foreach (var root in recoveryRoots)",
        scheduleAt < 0 ? 0 : scheduleAt, StringComparison.Ordinal);
    if (scheduleAt < 0 || firstImportAt < scheduleAt || historyScanAt < firstImportAt ||
        !automationSource.Contains("_salesPurchaseActiveClientSnapshotUtc = DateTime.MinValue;", StringComparison.Ordinal) ||
        !automationSource.Contains("historicalReindex: true", StringComparison.Ordinal) ||
        !editorSource.Contains("_automationService?.ScheduleNewClientGstinSourceDiscovery(", StringComparison.Ordinal))
        throw new Exception("Added GSTIN no longer queues an immediate registration-scoped import before historical discovery.");
    Console.WriteLine("PASS: new GSTIN Save starts immediate source-set import and refreshes client ownership.");

    // Dashboard must display all saved client GSTINs, not only the primary,
    // and navigate directly to the existing editor without changing Client/FY.
    var dashboardSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "ViewModels", "DashboardViewModel.cs"));
    var dashboardXaml = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "Views", "DashboardView.xaml"));
    var dashboardCode = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "Views", "DashboardView.xaml.cs"));
    var mainSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "ViewModels", "MainWindowViewModel.cs"));
    var managementXaml = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "Views", "ClientManagementView.xaml"));
    var managementCode = File.ReadAllText(Path.Combine(sourceRoot.FullName, "AAH", "src",
        "AccountingAutomationHub.Desktop", "Views", "ClientManagementView.xaml.cs"));
    if (!dashboardSource.Contains("BuildClientGstinRegistrations(client)", StringComparison.Ordinal) ||
        !dashboardSource.Contains("client.AdditionalGstins ?? []", StringComparison.Ordinal) ||
        !dashboardSource.Contains("seen.Add(additional)", StringComparison.Ordinal) ||
        !dashboardSource.Contains("ClientGstinRegistrations = [];", StringComparison.Ordinal) ||
        !dashboardXaml.Contains("ItemsSource=\"{Binding ClientGstinRegistrations}\"", StringComparison.Ordinal) ||
        !dashboardXaml.Contains("Click=\"DashboardGstin_Click\"", StringComparison.Ordinal) ||
        !dashboardCode.Contains("GstinManagementPanel.Visibility = Visibility.Visible;", StringComparison.Ordinal) ||
        !dashboardCode.Contains("main.GetDashboardGstinSnapshot()", StringComparison.Ordinal) ||
        !dashboardCode.Contains("main.SaveDashboardGstinRegistrations(", StringComparison.Ordinal) ||
        dashboardCode.Contains("OpenDashboardGstinManagement(", StringComparison.Ordinal) ||
        !dashboardXaml.Contains("x:Name=\"GstinManagementPanel\"", StringComparison.Ordinal) ||
        !dashboardXaml.Contains("x:Name=\"GstinPanelPrimaryBox\"", StringComparison.Ordinal) ||
        !dashboardXaml.Contains("x:Name=\"GstinPanelAdditionalBox\"", StringComparison.Ordinal) ||
        !dashboardXaml.Contains("Click=\"GstinManagementSave_Click\"", StringComparison.Ordinal) ||
        !mainSource.Contains("public bool SaveDashboardGstinRegistrations(", StringComparison.Ordinal) ||
        !editorSource.Contains("public bool SaveDashboardGstinRegistrations(", StringComparison.Ordinal) ||
        !editorSource.Contains("var savedProfile = fresh.Clone();", StringComparison.Ordinal) ||
        !editorSource.Contains("savedProfile.AdditionalGstins = additional;", StringComparison.Ordinal) ||
        !editorSource.Contains("_automationService?.ScheduleNewClientGstinSourceDiscovery(", StringComparison.Ordinal) ||
        !managementXaml.Contains("Text=\"{Binding AdditionalGstinsText, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal) ||
        !managementCode.Contains("public void FocusClientCodeBox()", StringComparison.Ordinal))
        throw new Exception("Dashboard GSTIN-only panel not linked to existing Client/FY persistence and import rules.");
    Console.WriteLine("PASS: Dashboard displays every saved GSTIN and saves via the compact GSTIN-only panel without navigating.");


    // Disposable Company Group fixture: same legal PAN, three independent GST
    // registrations from both a multi-GSTIN profile and a second branch Client.
    // No real Client data or Tally company is touched.
    var branchKarnataka = store.GetById(created.Id)!;
    branchKarnataka.AdditionalGstins = ["33ABCDE1234F2Z5"];
    store.Save(branchKarnataka);
    var branchMaharashtra = store.Save(new ClientProfile
    {
        ClientCode = "BRANCH-MH-002", ClientName = "Same Proprietor Maharashtra",
        Gstin = "27ABCDE1234F1Z5", Pan = "ABCDE1234F",
        FinancialYear = "2026-27", State = "Maharashtra"
    });
    var companyGroupMembers = new[]
    {
        store.GetById(created.Id)!,
        store.GetById(branchMaharashtra.Id)!
    };
    var branchPlan = CompanyGroupBranchPlanner.Build(
        "synthetic-group-same-pan", "Company Group", "2026-27", companyGroupMembers);
    if (branchPlan.ClientCount != 2 || branchPlan.GstinCount != 3 ||
        branchPlan.Pan != "ABCDE1234F" ||
        branchPlan.Branches.Select(x => x.Gstin).Distinct().Count() != 3 ||
        !branchPlan.Branches.Select(x => x.State).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(new[] { "Karnataka", "Maharashtra", "Tamil Nadu" }))
        throw new Exception("Same-PAN Company Group branch scope lost or duplicated registrations.");
    if (!branchPlan.Branches.Any(x => x.Gstin == "29ABCDE1234F1Z5" && x.BranchName == "Karnataka Branch") ||
        !branchPlan.Branches.Any(x => x.Gstin == "27ABCDE1234F1Z5" && x.BranchName == "Maharashtra Branch") ||
        !branchPlan.Branches.Any(x => x.Gstin == "33ABCDE1234F2Z5" && x.BranchName == "Tamil Nadu Branch"))
        throw new Exception("GSTIN State codes did not produce the expected branch identities.");

    var karnatakaBranch = branchPlan.Branches.Single(x => x.State == "Karnataka").BranchName;
    var tnBranch = branchPlan.Branches.Single(x => x.State == "Tamil Nadu").BranchName;
    if (branchPlan.VoucherTypeFor(karnatakaBranch, "Cash Contra", true) != "Contra - Karnataka Branch" ||
        branchPlan.VoucherTypeFor(tnBranch, "Cash Contra", true) != "Contra - Tamil Nadu Branch" ||
        branchPlan.VoucherTypeFor(karnatakaBranch, "Bank-to-Bank", true) != "Contra" ||
        branchPlan.VoucherTypeFor(tnBranch, "Interbank Transfer", true) != "Contra" ||
        branchPlan.VoucherTypeFor(karnatakaBranch, "Sales", true) != "Sales - Karnataka Branch" ||
        branchPlan.VoucherTypeFor(tnBranch, "Sales", false) != "Sales")
        throw new Exception("Cash-vs-bank Contra or optional branch voucher type classification changed.");

    var masterXml = System.Xml.Linq.XDocument.Parse(branchPlan.BuildOfflineMastersPreviewXml(true));
    var masters = masterXml.Descendants("TALLYMESSAGE").SelectMany(m => m.Elements()).ToArray();
    if (masterXml.Descendants("REPORTNAME").Single().Value != "All Masters" ||
        masters.Count(m => m.Name.LocalName == "GROUP") != 27 ||
        masters.Count(m => m.Name.LocalName == "LEDGER") != 3 ||
        masters.Any(m => m.Element("NAME") is null || m.Element("PARENT") is null) ||
        !masters.Any(m => m.Name.LocalName == "GROUP" &&
                          m.Element("NAME")?.Value == "Karnataka Branch - Creditors" &&
                          m.Element("PARENT")?.Value == "Sundry Creditors") ||
        !masters.Any(m => m.Name.LocalName == "LEDGER" &&
                          m.Element("NAME")?.Value == "Cash - Karnataka Branch" &&
                          m.Element("PARENT")?.Value == "Cash-in-Hand") ||
        masters.Any(m => m.Name.LocalName == "GROUP" &&
                         m.Element("PARENT")?.Value == "Branch/Divisions"))
        throw new Exception("Offline branch master XML is malformed or has incorrect Tally parents.");
    var sharedCashXml = System.Xml.Linq.XDocument.Parse(branchPlan.BuildOfflineMastersPreviewXml(false));
    if (sharedCashXml.Descendants("LEDGER").Any())
        throw new Exception("Cash ledger option disabled but cash masters were generated.");

    var sameState = CompanyGroupBranchPlanner.Build(
        "synthetic-same-state", "Company Group", "2026-27",
        [companyGroupMembers[0], new ClientProfile
        {
            Id = 947, ClientCode = "SAME-STATE", ClientName = "Same State Second Branch",
            Gstin = "29ABCDE1234F2Z5", Pan = "ABCDE1234F", FinancialYear = "2026-27"
        }]);
    if (sameState.Branches.GroupBy(x => x.BranchName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
        throw new Exception("Two branches in one state were given colliding Tally master names.");

    static void ExpectGroupRejection(Action action, string reason)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Group branch isolation failed: " + reason);
    }
    var differentPanOwnerBusiness = new ClientProfile
    {
        Id = 9999, ClientCode = "OWNER-OTHER-PAN", ClientName = "Different PAN Enterprise",
        Gstin = "27PQRST5678X1Z5", Pan = "PQRST5678X", FinancialYear = "2026-27"
    };
    ExpectGroupRejection(
        () => CompanyGroupBranchPlanner.Build("owner-group", "Company Group", "2026-27",
            [companyGroupMembers[0], differentPanOwnerBusiness]),
        "different-PAN owner company mistaken as same-PAN branch");
    ExpectGroupRejection(
        () => CompanyGroupBranchPlanner.Build("ordinary-group", "Auditor", "2026-27", companyGroupMembers),
        "non-Company Group activated special import");
    ExpectGroupRejection(
        () => CompanyGroupBranchPlanner.Build("duplicate-group", "Company Group", "2026-27",
            [companyGroupMembers[0], new ClientProfile
            {
                Id = 349, ClientCode = "REPEATED-GSTIN", ClientName = "Collision",
                Gstin = "29ABCDE1234F1Z5", Pan = "ABCDE1234F", FinancialYear = "2026-27"
            }]),
        "duplicate GSTIN posted twice");
    ExpectGroupRejection(
        () => CompanyGroupBranchPlanner.Build("wrong-pan", "Company Group", "2026-27",
            [new ClientProfile
            {
                Id = 373, ClientCode = "MISMATCH", ClientName = "Mismatch",
                Gstin = "29ABCDE1234F1Z5", Pan = "PQRST5678X", FinancialYear = "2026-27"
            }]),
        "PAN mismatch was accepted");
    Console.WriteLine("PASS: synthetic Company Group with same-PAN multi-state branches; cash, bank, optional master XML and group isolation; different PAN deferred.");

    Console.WriteLine("PASS: 9/9 multi-GSTIN client preservation, retrieval, and integrity checks.");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch { /* Test SQLite WAL may be briefly locked by the runner. */ }
}
