using System.Reflection;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class Program
{
    private static int _passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Run(string name, Action test)
    {
        test(); _passed++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Reject(Action test)
    {
        try { test(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected fail-closed inventory dependency rejection.");
    }
    private static StockSimpleMasterRecord Category(string name, string parent = "Primary", bool archived = false) =>
        new() { Name = name, Parent = parent, Type = "Stock Category", IsArchived = archived, Source = "Manual" };
    private static XElement[] Messages(StringBuilder b) =>
        XElement.Parse("<ROOT>" + b + "</ROOT>").Elements("TALLYMESSAGE").ToArray();
    private static string WriteItem(string name, string category, string group = "Primary")
    {
        var method = typeof(TallyAccountingVoucherExportService).GetMethod("AppendStockItemMaster", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production Stock Item writer is missing.");
        var item = new StockItemRecord { PersistentId = 42, Name = name, StockGroup = group, StockCategory = category,
            BaseUnit = "QTL", Source = "Manual" };
        var builder = new StringBuilder();
        try
        {
            method.Invoke(null, new object?[] { builder, name, "QTL", item,
                Array.Empty<StockItemUnitProfileRecord>(), new DateTime(2025, 4, 1) });
        }
        catch (TargetInvocationException exception)
        {
            if (exception.InnerException is Exception inner)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        return builder.ToString();
    }
    private static object Record(Type type, Dictionary<string, object?> values)
    {
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Any(p => p.Name == "SourceType") || c.GetParameters().Any(p => p.Name == "LedgerName") || c.GetParameters().Any(p => p.Name == "ItemName"));
        var args = ctor.GetParameters().Select(p => values.TryGetValue(p.Name!, out var value) ? value :
            p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(string) ? string.Empty :
            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return ctor.Invoke(args);
    }

    private static void Main()
    {
        Run("missing recovery excludes verified, deleted and rebound identities", () =>
        {
            var service = typeof(TallyAccountingVoucherExportService);
            var eligibility = service.GetMethod("CanOfferMissingRecovery", BindingFlags.NonPublic | BindingFlags.Static)!;
            var snapshot = service.GetMethod("RecoveryStateSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
            var state = new AccountingAutomationHub.Domain.Tally.TallySourceBindingVersionState { State = "NeedsReview" };
            bool Eligible() => (bool)eligibility.Invoke(null, new object?[] { state })!;
            Check(Eligible(), "Unbound uncertain entry cannot be reviewed.");
            var original = (string)snapshot.Invoke(null, new object?[] { state })!;
            state.PendingContentHash = "changed";
            Check(original != (string)snapshot.Invoke(null, new object?[] { state })!, "Concurrent pending edit escaped snapshot.");
            state.MasterId = "3106"; Check(!Eligible(), "Bound MASTERID offered for CREATE.");
            state.MasterId = ""; state.Guid = "known-guid"; Check(!Eligible(), "Known GUID offered for CREATE.");
            state.Guid = ""; state.VerifiedVersion = "verified"; Check(!Eligible(), "Verified history offered for CREATE.");
            state.VerifiedVersion = ""; state.LastVerifiedAlterId = 123; Check(!Eligible(), "Verified ALTERID offered for CREATE.");
            state.LastVerifiedAlterId = 0;
            foreach (var status in new[] { "DeletedInTally", "RebindRequired", "StaleMissingLive", "ExternalDeleteConflict", "Verified", "ModifyPending" })
            { state.State = status; Check(!Eligible(), "Protected state offered for CREATE: " + status); }
        });
        Run("inventory invoices recover exact live identity with stock accounting ledger sides", () =>
        {
            var type = typeof(TallyAccountingVoucherExportService);
            var entryType = type.GetNestedType("VoucherLedgerEntry", BindingFlags.NonPublic)!;
            var inventoryType = type.GetNestedType("PreparedInventoryEntry", BindingFlags.NonPublic)!;
            var voucherType = type.GetNestedType("PreparedVoucher", BindingFlags.NonPublic)!;
            var match = type.GetMethod("VoucherStrictlyMatchesPrepared", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var purchase in new[] { false, true })
            {
                var ledger = purchase ? "Purchase" : "Sales";
                var entries = Array.CreateInstance(entryType, 1);
                entries.SetValue(Record(entryType, new() { ["LedgerName"] = "Mahesh", ["Amount"] = 6000m, ["IsDebit"] = !purchase, ["IsParty"] = true }), 0);
                var inventory = Array.CreateInstance(inventoryType, 1);
                inventory.SetValue(Record(inventoryType, new() { ["ItemName"] = "Rice", ["AccountingLedger"] = ledger, ["AccountingAmount"] = 6000m, ["IsDeemedPositive"] = purchase }), 0);
                var prepared = Record(voucherType, new() {
                    ["SourceType"] = purchase ? "Purchase Voucher" : "Sales Voucher", ["VoucherType"] = ledger,
                    ["Date"] = new DateTime(2025, 6, 2), ["VoucherNumber"] = "81", ["ReferenceNumber"] = "81",
                    ["PartyLedger"] = "Mahesh", ["Narration"] = ledger + " 81", ["Entries"] = entries, ["InventoryEntries"] = inventory });
                var live = new AccountingAutomationHub.Domain.Tally.TallyVoucherIndexRecord {
                    MasterId = "3106", VoucherDate = new DateTime(2025, 6, 2), VoucherType = ledger,
                    VoucherNumber = "35", Reference = "81", PartyLedger = "Mahesh", Narration = ledger + " 81",
                    Amount = 6000m, DebitLedgers = purchase ? ledger : "Mahesh", CreditLedgers = purchase ? "Mahesh" : ledger };
                bool Matches() => (bool)match.Invoke(null, new object?[] { live, prepared, null })!;
                Check(Matches(), "Stock accounting allocation missing from identity verification.");
                var desired = type.GetMethod("BuildPreparedVoucherVersion", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object?[] { prepared, null, live.VoucherNumber });
                var actual = type.GetMethod("BuildLiveVoucherVersion", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object?[] { live, null });
                Check(Equals(desired, actual), "Desired version omitted stock allocation ledger sides.");
                live.CreditLedgers = "Wrong Ledger"; Check(!Matches(), "Wrong ledger accepted.");
                live.CreditLedgers = purchase ? "Mahesh" : ledger;
                live.Amount = 6001m; Check(!Matches(), "Different total accepted.");
                live.Amount = 6000m; live.Reference = "82"; Check(!Matches(), "Different source reference accepted.");
            }
        });
        Run("failed batches isolate two rejected entries and retain successful suffix batches", () =>
        {
            var split = typeof(TallyAccountingVoucherExportService)
                .GetMethod("SplitFailedVoucherBatch", BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(int));
            var stack = new Stack<int[]>();
            stack.Push(Enumerable.Range(1, 80).ToArray());
            var created = new HashSet<int>();
            var rejected = new HashSet<int>();
            var bad = new HashSet<int> { 35, 60 };
            var requests = 0;
            var suffixBatches = 0;
            while (stack.Count > 0)
            {
                Check(++requests < 40, "Recovery did not converge.");
                var batch = stack.Pop();
                Check(!batch.Any(created.Contains), "Recovery resent an accepted voucher.");
                if (batch.Length > 1 && batch[0] > 35) suffixBatches++;
                // Simulate ERP9: accept the prefix, then stop at the first rejected entry.
                foreach (var item in batch)
                {
                    if (bad.Contains(item)) break;
                    Check(created.Add(item), "Duplicate CREATE.");
                }
                var absent = batch.Where(item => !created.Contains(item)).ToArray();
                if (absent.Length == 0) continue;
                if (batch.Length == 1) { rejected.Add(batch[0]); continue; }
                var halves = (int[][])split.Invoke(null, new object[] { absent })!;
                for (var i = halves.Length - 1; i >= 0; i--) stack.Push(halves[i]);
            }
            Check(created.Count == 78 && rejected.SetEquals(bad), "Valid entries were lost or bad entries imported.");
            Check(suffixBatches > 0, "Recovery converted the suffix to individual imports.");
            Check(((int[][])split.Invoke(null, new object[] { Array.Empty<int>() })!).Length == 0, "Empty recovery queued a request.");
        });
        Run("ledger-only Sales voucher 35 emits a balanced accounting voucher", () =>
        {
            var type = typeof(TallyAccountingVoucherExportService);
            var ledgerType = type.GetNestedType("VoucherLedgerEntry", BindingFlags.NonPublic) ?? throw new InvalidOperationException("Ledger record missing.");
            var voucherType = type.GetNestedType("PreparedVoucher", BindingFlags.NonPublic) ?? throw new InvalidOperationException("Voucher record missing.");
            var entries = Array.CreateInstance(ledgerType, 2);
            entries.SetValue(Record(ledgerType, new() { ["LedgerName"] = "Surash-Rakash", ["Amount"] = -2000m, ["IsDebit"] = true, ["IsParty"] = true }), 0);
            entries.SetValue(Record(ledgerType, new() { ["LedgerName"] = "Sales", ["Amount"] = 2000m, ["IsDebit"] = false, ["IsParty"] = false }), 1);
            foreach (var kind in new[] { "Sales", "Credit Note", "Purchase", "Debit Note" })
            {
                var voucher = Record(voucherType, new() { ["SourceType"] = kind is "Purchase" or "Debit Note" ? "Purchase Voucher" : "Sales Voucher", ["VoucherType"] = kind,
                    ["Date"] = new DateTime(2025,4,26), ["VoucherNumber"] = "35", ["ReferenceNumber"] = "35", ["PartyLedger"] = "Surash-Rakash", ["Entries"] = entries });
                var method = type.GetMethod("AppendVoucher", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Voucher writer missing.");
                var output = new StringBuilder();
                method.Invoke(null, new object[] { output, voucher });
                var xml = output.ToString();
                Check(xml.Contains("Accounting Voucher View") && xml.Contains("<VCHENTRYMODE>As Voucher</VCHENTRYMODE>") && xml.Contains("<ISINVOICE>No</ISINVOICE>"), "Ledger-only invoice mode was not corrected.");
                Check(xml.Contains("<AMOUNT>-2000.00</AMOUNT>") && xml.Contains("<AMOUNT>2000.00</AMOUNT>"), "Ledger amounts were lost.");
                Check(!xml.Contains("<ALLINVENTORYENTRIES.LIST>"), "Invented stock lines.");
            }
        });

        Run("Stock export spelling uses durable identity with canonical whitespace", () =>
        {
            var item = new StockItemRecord { PersistentId = 42, Name = "BROKEN RICE", TallyMasterId = "321", BaseUnit = "QTL" };
            var resolver = new StockExportIdentityResolver(new[] { item }, Array.Empty<StockItemAliasRecord>());
            foreach (var name in new[] { "BROKEN   RICE", "BROKEN\u00a0 RICE", "BROKEN\tRICE" })
                Check(ReferenceEquals(resolver.Resolve(42, name, "321"), item), "Whitespace-only source name failed to resolve.");
            Reject(() => resolver.Resolve(0, "BROKEN RICE", "321"));
            Reject(() => resolver.Resolve(99, "BROKEN RICE", "321"));
            Reject(() => resolver.Resolve(42, "BROKEN RICE", "999"));
            Reject(() => resolver.Resolve(42, "B.Rice", "321"));
        });
        Run("Explicit rename alias must agree with durable Stock ID", () =>
        {
            var item = new StockItemRecord { PersistentId = 42, Name = "B.Rice", TallyMasterId = "321" };
            var alias = new StockItemAliasRecord { ImportedAlias = "BROKEN RICE", StockItemName = "B.Rice", Source = "Manual Sales Item Mapping" };
            var resolver = new StockExportIdentityResolver(new[] { item }, new[] { alias });
            Check(resolver.Resolve(42, "BROKEN   RICE", "321").Name == "B.Rice", "Saved explicit alias was ignored.");
            var wrong = new StockExportIdentityResolver(new[] { item }, new[] { new StockItemAliasRecord { ImportedAlias = "BROKEN RICE", StockItemName = "Other", Source = "Manual" } });
            Reject(() => wrong.Resolve(42, "BROKEN RICE", "321"));
            var automatic = new StockExportIdentityResolver(new[] { item }, new[] { new StockItemAliasRecord { ImportedAlias = "BROKEN RICE", StockItemName = "B.Rice", Source = "Auto compact" } });
            Reject(() => automatic.Resolve(42, "BROKEN RICE", "321"));
            var archived = new StockExportIdentityResolver(new[] { new StockItemRecord { PersistentId = 42, Name = "B.Rice", IsArchived = true } }, new[] { alias });
            Reject(() => archived.Resolve(42, "BROKEN RICE", "321"));
            var ambiguous = new StockExportIdentityResolver(new[] { item, new StockItemRecord { PersistentId = 43, Name = "B.Rice" } }, new[] { alias });
            Reject(() => ambiguous.Resolve(42, "BROKEN RICE", "321"));
        });
        Run("Master import detector recognizes real tags after writes", () =>
        {
            var method = typeof(TallyHttpService).GetMethod("ContainsErp9MasterMutation", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Master mutation detector missing.");
            Check((bool)method.Invoke(null, new object[] { "<LEDGER NAME=\"Bank\" ACTION=\"Create\"/>" })!, "Ledger write did not invalidate master cache.");
            Check(!(bool)method.Invoke(null, new object[] { "<LEDGERENTRIES.LIST/>" })!, "Voucher entries mistaken for masters.");
        });

        Run("reserved Stock Category names normalize to no category", () =>
        {
            foreach (var value in new[] { "", " ", "Primary", " primary ", "Not Applicable", " not applicable ",
                         "\u0004 Not Applicable", " \u0004 Not Applicable", "\u0004\u0004 Not Applicable" })
                Check(TallyInventoryRootPolicy.CategoryForExport(value) == "", "Root category not recognized: " + value);
            Check(TallyInventoryRootPolicy.CategoryForExport("Rice & Grains") == "Rice & Grains", "Custom category changed.");
        });
        Run("root categories emit no CREATE Masters", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendRequiredCategories(b, new[] { "Primary", "Not Applicable", "\u0004 Not Applicable", "" },
                Array.Empty<StockSimpleMasterRecord>());
            Check(b.Length == 0, "A reserved category was created.");
            TallyInventoryRootPolicy.AppendSimpleInventoryMaster(b, "STOCKCATEGORY", "Not Applicable", "Primary", "Manual");
            Check(b.Length == 0, "Direct reserved category creation was not blocked.");
        });
        Run("real top-level category omits fictitious Primary parent", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendRequiredCategories(b, new[] { "Rice & Grains" }, new[] { Category("Rice & Grains") });
            var item = Messages(b).Single().Element("STOCKCATEGORY")!;
            Check(item.Attribute("NAME")?.Value == "Rice & Grains" && item.Element("PARENT") is null, "Invalid category root parent.");
            Check(item.Element("NAME")?.Value == "Rice & Grains", "Category display name changed.");
        });
        Run("custom category hierarchy is emitted parent-first", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendRequiredCategories(b, new[] { "Basmati" }, new[]
            {
                Category("Basmati", "Rice"), Category("Rice", "Grains"), Category("Grains")
            });
            var items = Messages(b).Select(x => x.Element("STOCKCATEGORY")!).ToArray();
            Check(items.Select(x => x.Attribute("NAME")!.Value).SequenceEqual(new[] { "Grains", "Rice", "Basmati" }), "Category dependency order is wrong.");
            Check(items[0].Element("PARENT") is null && items[1].Element("PARENT")?.Value == "Grains" &&
                  items[2].Element("PARENT")?.Value == "Rice", "Custom category hierarchy was changed.");
        });
        Run("duplicate requested category is emitted only once", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendRequiredCategories(b, new[] { "Rice", "RICE", " Rice " }, new[] { Category("Rice") });
            Check(Messages(b).Length == 1, "Duplicate category Masters emitted.");
        });
        Run("missing category blocks before any XML is appended", () =>
        {
            var b = new StringBuilder("KEEP");
            Reject(() => TallyInventoryRootPolicy.AppendRequiredCategories(b, new[] { "Unknown" }, Array.Empty<StockSimpleMasterRecord>()));
            Check(b.ToString() == "KEEP", "Missing category produced a partial payload.");
        });
        Run("missing custom parent blocks before any XML is appended", () =>
        {
            var b = new StringBuilder("KEEP");
            Reject(() => TallyInventoryRootPolicy.AppendRequiredCategories(b, new[] { "Child" }, new[] { Category("Child", "Missing Parent") }));
            Check(b.ToString() == "KEEP", "Missing parent produced a partial payload.");
        });
        Run("cyclic category hierarchy is rejected", () =>
        {
            Reject(() => TallyInventoryRootPolicy.OrderRequiredCategories(new[] { "A" }, new[] { Category("A", "B"), Category("B", "A") }));
        });
        Run("archived category is not recreated", () =>
        {
            Reject(() => TallyInventoryRootPolicy.OrderRequiredCategories(new[] { "A" }, new[] { Category("A", archived: true) }));
        });
        Run("duplicate category authority requires review", () =>
        {
            Reject(() => TallyInventoryRootPolicy.OrderRequiredCategories(new[] { "A" }, new[] { Category("A"), Category(" a ") }));
        });
        Run("actual Stock Item writer omits reserved categories", () =>
        {
            foreach (var category in new[] { "", "Primary", "Not Applicable", "\u0004 Not Applicable" })
            {
                var item = Messages(new StringBuilder(WriteItem("B.Rice", category))).Single().Element("STOCKITEM")!;
                Check(item.Element("CATEGORY") is null, "Actual Stock Item writer emitted a reserved category.");
                Check(item.Element("PARENT") is null && item.Element("BASEUNITS")?.Value == "QTL", "Stock Item root or Base Unit changed.");
            }
        });
        Run("actual Stock Item writer preserves real category and group", () =>
        {
            var item = Messages(new StringBuilder(WriteItem("Rice", "Rice & Grains", "Food Grains"))).Single().Element("STOCKITEM")!;
            Check(item.Element("CATEGORY")?.Value == "Rice & Grains" && item.Element("PARENT")?.Value == "Food Grains", "Real inventory classification was lost.");
            Check(item.Attribute("ACTION")?.Value == "Create" && item.Element("BASEUNITS")?.Value == "QTL", "Stock Item action/unit changed.");
        });
        Run("Stock Group root parent is omitted", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendSimpleInventoryMaster(b, "STOCKGROUP", "Food Grains", "Primary", "Manual");
            Check(Messages(b).Single().Element("STOCKGROUP")!.Element("PARENT") is null, "Fictitious Stock Group parent emitted.");
        });
        Run("custom Stock Group parent is retained", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendSimpleInventoryMaster(b, "STOCKGROUP", "Rice", "Food Grains", "Manual");
            Check(Messages(b).Single().Element("STOCKGROUP")!.Element("PARENT")?.Value == "Food Grains", "Custom Stock Group parent lost.");
        });
        Run("Godown parent and source-derived action are retained", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendSimpleInventoryMaster(b, "GODOWN", "Warehouse", "Main Location", "Live Tally");
            var item = Messages(b).Single().Element("GODOWN")!;
            Check(item.Element("PARENT")?.Value == "Main Location" && item.Attribute("ACTION")?.Value == "Alter", "Unrelated Godown behavior changed.");
        });
        Run("unsupported simple inventory Master is rejected", () =>
        {
            try { TallyInventoryRootPolicy.AppendSimpleInventoryMaster(new StringBuilder(), "LEDGER", "X", "Primary", "Manual"); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Unsupported Master type was accepted.");
        });
        Run("partial ERP9 Master response remains a failure with accepted counts", () =>
        {
            const string response = "<RESPONSE><LINEERROR>Stock Category &apos;Primary&apos; does not exist!</LINEERROR><CREATED>63</CREATED><ALTERED>0</ALTERED><DELETED>0</DELETED><IGNORED>1</IGNORED><ERRORS>1</ERRORS></RESPONSE>";
            var result = TallyHttpService.ParseImportResponse(response);
            Check(result.Created == 63 && result.Ignored == 1 && result.Errors == 1 &&
                  result.LineError.Contains("Stock Category", StringComparison.Ordinal), "Partial failure was mistaken for a clean import.");
        });
        Run("clean Master response is not misclassified as an error", () =>
        {
            var result = TallyHttpService.ParseImportResponse("<RESPONSE><CREATED>24</CREATED><ALTERED>0</ALTERED><IGNORED>0</IGNORED><ERRORS>0</ERRORS></RESPONSE>");
            Check(result.Created == 24 && result.Errors == 0 && result.LineError.Length == 0, "Clean Master response was rejected.");
        });
        Run("a Bank-only dependency pass has no inventory payload", () =>
        {
            var b = new StringBuilder();
            TallyInventoryRootPolicy.AppendRequiredCategories(b, Array.Empty<string>(), Array.Empty<StockSimpleMasterRecord>());
            Check(b.Length == 0, "An empty inventory dependency set created a Master.");
        });
        TallyBranchMasterPlannerTests.Run();
        TallyBranchMasterImportContractTests.Run();
        TallyConsolidatedPreparedXmlTests.Run();
        TallyConsolidatedBranchSourceTests.Run();
        Console.WriteLine($"PASS: {_passed}/{_passed} production Tally inventory regressions.");
    }
}
