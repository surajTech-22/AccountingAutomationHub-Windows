using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Consolidation is a transport/identity wrapper, NOT another accounting engine.
/// The branch's prepared voucher is the sole authority for each DR/CR, tax, value,
/// inventory and narration field. No source-side journal or GST routine is called.
/// This produces two offline XML stages; it does not bypass the existing live
/// Tally import/readback/duplicate gate or imply that a voucher was imported.
/// </summary>
public static class TallyConsolidatedPreparedXmlBuilder
{
    public const string InterbankReferencePrefix = "AAH-INTERBANK:";

    public static TallyConsolidatedPreparedXmlResult Build(
        TallyBranchMasterPlan plan, IReadOnlyCollection<TallyPreparedBranchXml> prepared)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(prepared);
        var expected = plan.Groups.Select(x => x.BranchId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (expected.Length < 2 || prepared.Count != expected.Length ||
            prepared.GroupBy(x => x.Branch.BranchId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1) ||
            prepared.Any(x => !expected.Contains(x.Branch.BranchId, StringComparer.OrdinalIgnoreCase) ||
                              !x.Branch.FinancialYear.Equals(plan.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                              !x.Branch.Pan.Equals(plan.Pan, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Every verified same-PAN branch must supply its own prepared XML exactly once.");

        // Before rewriting ANY vouchers, inspect the exact posting/party
        // ledger names used by each prepared branch. Only a name present in
        // two or more distinct branches receives a destination suffix.
        // This is identity mapping, never an amount/GST recalculation.
        // Name-equivalent source ledgers can vary only in whitespace around
        // '@' (e.g. Input IGST @18% vs Input IGST @ 18%). This equivalence
        // is for consolidated identity mapping ONLY, not a branch rename.
        static string EquivalentLedgerName(string original)
        {
            var clean = original.Trim();
            return Regex.Replace(Regex.Replace(clean, @"\s*@\s*", " @ "),
                @"\s+", " ").Trim();
        }

        var parsedBranches = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        var ledgerOwners = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var originalBranch in prepared)
        {
            if (string.IsNullOrWhiteSpace(originalBranch.Xml))
                throw new InvalidOperationException(
                    "The branch has no prepared Tally XML: " + originalBranch.Branch.DisplayName);
            var originalDoc = TallyConsolidatedXmlTextSafety.ParseDocument(
                originalBranch.Xml, "ledger name preflight " + originalBranch.Branch.DisplayName,
                LoadOptions.PreserveWhitespace);
            parsedBranches.Add(originalBranch.Branch.BranchId, originalDoc);
            var usedNames = originalDoc.Descendants().Where(x =>
                    Is(x, "VOUCHER") && x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .SelectMany(v => v.Descendants().Where(x =>
                    Is(x, "LEDGERNAME") || Is(x, "PARTYLEDGERNAME") ||
                    Is(x, "PARTYNAME") || Is(x, "BASICBASEPARTYNAME")))
                .Select(x => x.Value.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceName in usedNames)
            {
                var equivalentName = EquivalentLedgerName(sourceName);
                if (!ledgerOwners.TryGetValue(equivalentName, out var owners))
                    ledgerOwners[equivalentName] = owners = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                owners.Add(originalBranch.Branch.BranchId);
            }
        }
        var duplicateAcrossBranches = ledgerOwners
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string MapBranchLedger(TallyBranchMasterIdentity owner, string originalName)
        {
            var clean = originalName.Trim();
            var equivalentName = EquivalentLedgerName(clean);
            return duplicateAcrossBranches.Contains(equivalentName)
                ? equivalentName + " - " + owner.DisplayName.Trim()
                : clean;
        }

        var masterMessages = new List<XElement>();
        var vouchers = new List<(TallyPreparedBranchXml Branch, XElement Voucher)>();
        var explicitlyMatched = new Dictionary<string, List<(TallyPreparedBranchXml Branch, XElement Voucher)>>(StringComparer.OrdinalIgnoreCase);
        var seenVoucherAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Tally ledger names are globally unique, even beneath different
        // Groups. Only shared source names receive branch-name suffixes.
        // Fail closed if a generated name still collides with a distinct
        // original name (or another generated name).
        var mappedLedgerMasters = new Dictionary<string, (string Branch, string Parent)>(
            StringComparer.OrdinalIgnoreCase);
        var destinationNameConflicts = new List<string>();
        var mappedStockMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonInventoryMasters = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var sourceVoucherCount = 0;
        var rawSourceAltersConverted = 0;

        foreach (var branch in prepared.OrderBy(x => x.Branch.BranchId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(branch.Xml))
                throw new InvalidOperationException("The branch has no prepared Tally XML: " + branch.Branch.DisplayName);
            // Reuse the strict, whitespace-preserving preflight XML parse:
            // no additional branch XML parsing on the hot collection path.
            var source = parsedBranches[branch.Branch.BranchId];
            if (!Is(source.Root, "ENVELOPE") ||
                !source.Descendants().Any(x => Is(x, "TALLYMESSAGE")))
                throw new InvalidOperationException("Invalid original Tally XML envelope for " + branch.Branch.DisplayName);
            // Boundary rule: accept branch accounting facts, never any
            // company-owned object IDs or Alter version from its XML.
            RemoveForeignTallyIdentity(source.Root!);
            // Branch-prepared XML deliberately omits Ledgers already live in
            // that branch's Tally company. Fill those master definitions only
            // from the original branch's freshly exported Tally masters, never
            // by guessing GSTIN, banking or party settings.
            var liveSource = string.IsNullOrWhiteSpace(branch.SourceLiveMastersXml)
                ? null : TallyConsolidatedXmlTextSafety.ParseDocument(
                    branch.SourceLiveMastersXml, "live source masters " + branch.Branch.DisplayName,
                    LoadOptions.PreserveWhitespace);
            // Source Tally's MASTERID/GUID/ALTERID and voucher IDs have no
            // meaning in consolidated books. Drop them at the ingestion
            // boundary, BEFORE inspecting or selecting any source master.
            if (liveSource?.Root is not null) RemoveForeignTallyIdentity(liveSource.Root);
            var originalLedgerXml = source.Descendants().Where(x => Is(x, "LEDGER") &&
                    x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .Concat(liveSource?.Descendants().Where(x => Is(x, "LEDGER")) ?? [])
                .GroupBy(x => Field(x, "NAME").Length > 0 ? Field(x, "NAME") : Value(x, "NAME"),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            // The normal branch exporter already verified any Ledger CREATE it
            // included in its own ClientId/FY XML. A private branch can also
            // have a separately linked Tally company whose fresh full Master
            // export contains exact Ledger authority. The old wrapper checked
            // ONLY SqliteMasterRepository rows, losing either valid source.
            // Never authorize a ledger solely because a voucher mentions its
            // name or GSTIN: require one exact Ledger master definition.
            var ledgerAuthority = branch.Masters
                .Where(m => m.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                            !m.IsArchived && !string.IsNullOrWhiteSpace(m.Name))
                .GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var (ledgerName, declarations) in originalLedgerXml)
            {
                if (ledgerName.Length == 0 || ledgerAuthority.ContainsKey(ledgerName))
                    continue;
                var preparedCreates = declarations.Where(x =>
                    x.Parent is not null && Is(x.Parent, "TALLYMESSAGE") &&
                    Field(x, "ACTION").Equals("Create", StringComparison.OrdinalIgnoreCase)).ToArray();
                var candidates = preparedCreates.Length > 0 ? preparedCreates
                    : declarations.Where(x => liveSource?.Root is not null &&
                        x.Ancestors().Contains(liveSource.Root) &&
                        Value(x, "PARENT").Length > 0).ToArray();
                // Conflicting or incomplete declarations cannot become a
                // second guessed party/tax/bank master.
                if (candidates.Length != 1) continue;
                var declared = candidates[0];
                var parentName = Value(declared, "PARENT");
                var intrinsicName = Value(declared, "NAME");
                if (parentName.Length == 0 ||
                    (intrinsicName.Length > 0 &&
                     !intrinsicName.Equals(ledgerName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                ledgerAuthority[ledgerName] = [new MasterRecord
                {
                    Name = ledgerName,
                    Parent = parentName,
                    Type = "Ledger",
                    Source = preparedCreates.Length > 0 ? "Original branch prepared CREATE" : "Verified source Tally master"
                    // Deliberately NEVER copy or compare a branch TallyMasterId.
                }];
            }
            // Inventory masters and units are reused, never regenerated from an
            // assumed VAT/GST rate. Shared units/groups are accepted only when
            // their source definitions are identical between branches.
            foreach (var rawMaster in source.Descendants().Where(x =>
                x.Parent is not null && Is(x.Parent, "TALLYMESSAGE") &&
                (Is(x, "UNIT") || Is(x, "STOCKGROUP") || Is(x, "STOCKCATEGORY") ||
                 Is(x, "GODOWN") || Is(x, "COSTCENTRE"))))
            {
                var kind = rawMaster.Name.LocalName.ToUpperInvariant();
                var name = Field(rawMaster, "NAME");
                if (name.Length == 0) name = Value(rawMaster, "NAME");
                if (name.Length == 0 || Field(rawMaster, "ACTION").Equals("Alter", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unverified inventory master or foreign ALTER: " + kind);
                var key = kind + "|" + name;
                var copy = new XElement(rawMaster);
                RemoveForeignTallyIdentity(copy);
                copy.SetAttributeValue("ACTION", "Create"); // new destination identity
                if (commonInventoryMasters.TryGetValue(key, out var existing))
                {
                    if (!XNode.DeepEquals(existing, copy))
                        throw new InvalidOperationException("Different branches use conflicting stock master definitions: " + key);
                }
                else commonInventoryMasters[key] = copy;
            }
            var originalStockXml = source.Descendants().Where(x => Is(x, "STOCKITEM") &&
                    x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .GroupBy(x => Field(x, "NAME").Length > 0 ? Field(x, "NAME") : Value(x, "NAME"),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var stockName in source.Descendants().Where(x => Is(x, "VOUCHER") &&
                         x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                         .SelectMany(x => x.Descendants().Where(y => Is(y, "STOCKITEMNAME")))
                         .Select(x => x.Value.Trim()).Where(x => x.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!originalStockXml.TryGetValue(stockName, out var definitions) || definitions.Length != 1)
                    throw new InvalidOperationException("Source stock item details missing for '" + stockName +
                        "'. Inventory amounts will not be recalculated or stock masters guessed.");
                var newName = stockName + " - " + branch.Branch.DisplayName.Trim();
                if (!mappedStockMasters.Add(newName))
                    throw new InvalidOperationException("Duplicate branch stock master destination: " + newName);
                var stock = new XElement(definitions[0]);
                RemoveForeignTallyIdentity(stock);
                stock.SetAttributeValue("NAME", newName);
                stock.SetAttributeValue("ACTION", "Create");
                Set(stock, "NAME", newName);
                masterMessages.Add(new XElement("TALLYMESSAGE", stock));
            }
            var customGroups = branch.Masters.Where(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

            var stagedCustomGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string ResolveParentGroup(string sourceParent, HashSet<string> ancestors)
            {
                var requirement = plan.Groups.SingleOrDefault(g =>
                    g.BranchId.Equals(branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase) &&
                    g.Parent.Equals(sourceParent, StringComparison.OrdinalIgnoreCase));
                if (requirement is not null) return requirement.Name;
                if (!ancestors.Add(sourceParent) ||
                    !customGroups.TryGetValue(sourceParent, out var groups) || groups.Length != 1 ||
                    string.IsNullOrWhiteSpace(groups[0].Parent))
                    throw new InvalidOperationException("Circular or unknown branch accounting group: " + sourceParent);
                var original = groups[0];
                var parent = ResolveParentGroup(original.Parent.Trim(), ancestors);
                var mapped = branch.Branch.DisplayName.Trim() + " " + original.Name.Trim();
                if (!stagedCustomGroups.Add(mapped)) return mapped;
                // Preserve the source hierarchy below the mandatory branch
                // classification. Group parent nesting is not a DR/CR calculation.
                var shell = new MasterRecord { Name = mapped, Parent = parent, Type = "Group" };
                var groupXml = TallyConsolidatedXmlTextSafety.ParseDocument(
                    TallyErp9ManualGroupImport.Build(plan.DestinationCompanyName, [shell]),
                    "planned nested branch group");
                foreach (var message in groupXml.Descendants().Where(x => Is(x, "TALLYMESSAGE")))
                    masterMessages.Add(new XElement(message));
                return mapped;
            }
            string AccountingParent(string ledger)
            {
                if (!ledgerAuthority.TryGetValue(ledger, out var sources) || sources.Length != 1)
                    throw new InvalidOperationException("Missing or ambiguous branch Ledger authority: " +
                        branch.Branch.DisplayName + "/" + ledger);
                return ResolveParentGroup(sources[0].Parent.Trim(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            string LedgerName(string sourceName)
            {
                if (!ledgerAuthority.TryGetValue(sourceName.Trim(), out var masters) || masters.Length != 1)
                    throw new InvalidOperationException("Voucher references an unverified branch ledger: " +
                        branch.Branch.DisplayName + "/" + sourceName);
                // Optional separately named cash uses the existing verified
                // master plan; it remains independent of auto name suffixing.
                var cash = plan.CashLedgers.SingleOrDefault(x =>
                    x.BranchId.Equals(branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase));
                if (cash is not null &&
                    masters[0].Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase))
                    return cash.Name;
                // The NORMAL individual Tally exporter uses its existing
                // Cash/Cash-in-Hand ledger for Bank Payment/Receipt/Contra.
                // Cash Entry module count can be zero while Bank vouchers
                // still use Cash. With optional branch cash DISABLED, reuse
                // that exact Tally ledger and never suffix/reparent it.
                if (sourceName.Trim().Equals("Cash", StringComparison.OrdinalIgnoreCase) &&
                    masters[0].Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase))
                    return "Cash";
                // Unique: unchanged. Shared name in 2+ different branches:
                // append that branch's display name ONLY to these duplicates.
                // The mandatory branch grouping and original master metadata
                // are preserved either way.
                return MapBranchLedger(branch.Branch, sourceName);
            }

            // Report every unresolved posting ledger before rewriting ANY source
            // voucher. This is a preparation/read-only gate; it never creates
            // a party ledger from the GSTIN or silently drops a posting.
            var unresolvedLedgers = source.Descendants().Where(x => Is(x, "VOUCHER") &&
                    x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .SelectMany(v => v.Descendants().Where(x =>
                    Is(x, "LEDGERNAME") || Is(x, "PARTYLEDGERNAME") ||
                    Is(x, "PARTYNAME") || Is(x, "BASICBASEPARTYNAME")))
                .Select(x => x.Value.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !ledgerAuthority.TryGetValue(name, out var entries) || entries.Length != 1)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unresolvedLedgers.Length > 0)
                throw new InvalidOperationException(
                    "Branch '" + branch.Branch.DisplayName + "' has " + unresolvedLedgers.Length +
                    " unverified posting Ledger(s): " + string.Join(" | ", unresolvedLedgers.Take(16)) +
                    (unresolvedLedgers.Length > 16 ? " | …" : "") +
                    ". These are used by original branch vouchers, but no unique active branch Ledger Master, " +
                    "original prepared Ledger CREATE, or full verified source-Tally Ledger exists. " +
                    "Resolve the exact ledgers in the branch Ledger Master and regenerate its prepared XML. " +
                    "No vouchers were removed, recalculated, or posted.");

            foreach (var raw in source.Descendants().Where(x => Is(x, "VOUCHER") &&
                       x.Parent is not null && Is(x.Parent, "TALLYMESSAGE")))
            {
                var v = new XElement(raw);
                var sourceAction = Field(v, "ACTION");
                // "Alter" is only an operation in the BRANCH'S Tally company.
                // The consolidated books get the raw postings, not that Alter
                // request, ID, GUID or version. AAH's independent document
                // anchor (date/type/reference) supplies source deduplication.
                if (!sourceAction.Equals("Create", StringComparison.OrdinalIgnoreCase) &&
                    !sourceAction.Equals("Alter", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsupported raw branch voucher action: " + sourceAction);
                if (sourceAction.Equals("Alter", StringComparison.OrdinalIgnoreCase))
                    rawSourceAltersConverted++;
                RemoveForeignTallyIdentity(v);
                v.SetAttributeValue("ACTION", "Create"); // new target voucher, no branch ID
                var originalNumber = Value(v, "VOUCHERNUMBER");
                var refValue = Value(v, "REFERENCE");
                var date = Value(v, "DATE");
                var type = Value(v, "VOUCHERTYPENAME");
                // The ORIGINAL AAH exporter deliberately leaves VOUCHERNUMBER
                // blank for Tally auto-numbered purchases, sales and GST journals.
                // A source REFERENCE is the durable document identity in that
                // existing flow. Never reject those already-prepared DR/CR lines
                // solely for having no Tally-owned voucher number.
                if (type.Length == 0 ||
                    (originalNumber.Length == 0 && refValue.Length == 0) ||
                    !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var voucherDate))
                    throw new InvalidOperationException("Prepared voucher lacks a stable source number/reference, valid date or type.");
                if (plan.FinancialYear.Length < 4 ||
                    !int.TryParse(plan.FinancialYear[..4], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var startYear) ||
                    voucherDate < new DateTime(startYear, 4, 1) ||
                    voucherDate >= new DateTime(startYear + 1, 4, 1))
                    throw new InvalidOperationException("Prepared branch voucher is outside the consolidated financial year.");
                var anchor = branch.Branch.BranchId + "|" + date + "|" + type + "|" +
                    (originalNumber.Length > 0 ? "NO:" + originalNumber : "REF:" + refValue);
                if (!seenVoucherAnchors.Add(anchor))
                    throw new InvalidOperationException("Duplicate source voucher identity within one branch's prepared XML: " + anchor);
                sourceVoucherCount++;
                if (refValue.StartsWith(InterbankReferencePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var transferId = refValue[InterbankReferencePrefix.Length..].Trim();
                    if (transferId.Length == 0)
                        throw new InvalidOperationException("Interbank transfer ID is blank.");
                    if (!explicitlyMatched.TryGetValue(transferId, out var pair))
                        explicitlyMatched[transferId] = pair = new();
                    pair.Add((branch, v));
                    continue;
                }

                // Verify before mapping PARTYLEDGERNAME so the original
                // branch's exact source Ledger remains the authority.
                // Do not replace a voucher's GSTIN with the Ledger default.
                TallyConsolidatedGstMasterPolicy.VerifyVoucherPartyRegistration(
                    v, originalLedgerXml, anchor);
                RewriteVoucher(v, branch.Branch, plan, LedgerName, anchor);
                TallyConsolidatedGstMasterPolicy.DisableLegacyVatOnGstVoucher(v);
                TallyConsolidatedStateNamePolicy.NormalizeVoucher(v);
                vouchers.Add((branch, v));
            }

            // Only the ledgers actually used by prepared vouchers are emitted.
            // Reuse original prepared Ledger XML where it exists; refuse to
            // invent GST/party/bank metadata when the source declaration is absent.
            var used = source.Descendants().Where(x => Is(x, "VOUCHER") &&
                x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .SelectMany(v => v.Descendants().Where(x => Is(x, "LEDGERNAME")))
                .Select(x => x.Value.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var original in used)
            {
                var destinationLedger = LedgerName(original);
                var cashPlanned = plan.CashLedgers.Any(x =>
                    x.BranchId.Equals(branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase) &&
                    x.Name.Equals(destinationLedger, StringComparison.OrdinalIgnoreCase));
                // The optional cash ledger was already included in the verified
                // master-first plan. Do not duplicate or recreate it in source XML.
                if (cashPlanned) continue;
                // The destination's standard Cash ledger is already owned by
                // Tally under Cash-in-Hand. The original individual Bank XML
                // uses it for physical cash Contra even when there are zero
                // Cash Entry module vouchers. Never stage a CREATE for Cash
                // under Karnataka Cash, nor silently ALTER its parent.
                // The existing importer will freshly read MASTERID + parent
                // of this Tally-owned Cash before any voucher is posted.
                if (original.Equals("Cash", StringComparison.OrdinalIgnoreCase) &&
                    ledgerAuthority.TryGetValue(original, out var cashSources) &&
                    cashSources.Length == 1 &&
                    cashSources[0].Parent.Trim().Equals("Cash-in-Hand",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var parent = AccountingParent(original);
                if (mappedLedgerMasters.TryGetValue(destinationLedger, out var mapped))
                {
                    // Example: a genuine source "Buyer - Gujarat" may
                    // collide with the generated alias for shared "Buyer".
                    // Never silently merge those separate accounts.
                    destinationNameConflicts.Add("'" + destinationLedger + "': " +
                        mapped.Branch + " / " + mapped.Parent + " and " +
                        branch.Branch.DisplayName + " / " + parent);
                    continue;
                }
                mappedLedgerMasters.Add(destinationLedger,
                    (branch.Branch.DisplayName.Trim(), parent));
                if (!originalLedgerXml.TryGetValue(original, out var sourceDeclarations) ||
                    sourceDeclarations.Length == 0)
                    throw new InvalidOperationException("Neither prepared branch XML nor fresh original-company Tally export " +
                        "contains full master details for '" + original + "'. Cannot guess party/GST/bank metadata.");
                // Prefer the original prepared CREATE. Otherwise use the
                // richest source-company Tally object, NOT a thin collection
                // stub that silently drops GST/party/bank details.
                var preparedDeclaration = sourceDeclarations.FirstOrDefault(x =>
                    x.Parent is not null && Is(x.Parent, "TALLYMESSAGE") &&
                    Field(x, "ACTION").Equals("Create", StringComparison.OrdinalIgnoreCase));
                var sourceLedger = preparedDeclaration ?? sourceDeclarations
                    .OrderByDescending(x => x.Descendants().Count()).First();
                var liveParent = Value(sourceLedger, "PARENT");
                // Source metadata authority is ledger name, classification and
                // complete business fields, never a foreign source MASTERID.
                if (preparedDeclaration is null &&
                    (sourceLedger.Descendants().Count() <= 7) &&
                    (liveParent.Equals("Duties & Taxes", StringComparison.OrdinalIgnoreCase) ||
                     liveParent.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase) ||
                     liveParent.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase) ||
                     liveParent.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Full original GST/party/bank master metadata was not returned for '" +
                        original + "'. A thin live collection is not a safe consolidated ledger template.");
                if (!liveParent.Equals(ledgerAuthority[original][0].Parent, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Branch Ledger parent differs from source Master authority: " + original);
                var ledger = new XElement(sourceLedger);
                // Strip foreign source identifiers at every depth. The
                // destination master-first import will create/find its own
                // Ledger and read back its new MASTERID from consolidated Tally.
                RemoveForeignTallyIdentity(ledger);
                ledger.SetAttributeValue("NAME", destinationLedger);
                ledger.SetAttributeValue("ACTION", "Create");
                Set(ledger, "NAME", destinationLedger);
                Set(ledger, "PARENT", parent);
                // Only definite IGST/CGST/SGST source tax ledgers under
                // mandatory branch Taxes groups are given Tally's GST duty
                // classification. Settlement/receivable/RCM clearing ledgers
                // without a single duty head remain source-owned.
                TallyConsolidatedGstMasterPolicy.ApplyKnownDutyHead(ledger, parent, original);
                // The individual branch exporter includes placeholder
                // Tax Classification / legacy GST enum values that Tally
                // cannot resolve as Master names. Preserve their labels with
                // inactive empty values in the destination copy before POST.
                TallyConsolidatedGstMasterPolicy.RemoveUnresolvedSourcePlaceholders(ledger);
                TallyConsolidatedGstMasterPolicy.DisableLegacyVatOnGstLedger(ledger);
                TallyConsolidatedStateNamePolicy.NormalizeLedger(ledger);
                foreach (var nameTag in ledger.Descendants().Where(x =>
                    Is(x, "NAME") && x.Parent is not null && Is(x.Parent, "NAME.LIST") &&
                    x.Value.Trim().Equals(original, StringComparison.OrdinalIgnoreCase)))
                    nameTag.Value = destinationLedger;
                masterMessages.Add(new XElement("TALLYMESSAGE", ledger));
            }
        }
        if (destinationNameConflicts.Count > 0)
            throw new InvalidOperationException(
                "Two different original branch Ledgers still resolve to one " +
                "consolidated Tally Ledger name after automatic branch suffixing: " +
                string.Join(" | ", destinationNameConflicts.Take(20)) +
                (destinationNameConflicts.Count > 20 ? " | ..." : "") +
                ". Give the conflicting source Ledgers distinct names; the " +
                "compulsory branch Groups and original DR/CR are unchanged. " +
                "No vouchers or masters were posted.");

        // Interbank is the ONE exception: reconcile two explicitly linked branch
        // bank sides into a common Contra without calculating or rewriting values.
        var reconciled = 0;
        foreach (var (transferId, sourcePair) in explicitlyMatched.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (sourcePair.Count != 2 || sourcePair[0].Branch.Branch.BranchId.Equals(
                    sourcePair[1].Branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Interbank transfer " + transferId +
                    " must be matched by exactly two different branches.");
            var sides = new List<XElement>();
            var amounts = new List<decimal>();
            foreach (var (branch, original) in sourcePair)
            {
                var sourceGroups = branch.Masters.Where(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
                bool IsBank(string parent)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (seen.Add(parent))
                    {
                        if (parent.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase)) return true;
                        if (!sourceGroups.TryGetValue(parent, out var groups) || groups.Length != 1)
                            return false;
                        parent = groups[0].Parent.Trim();
                    }
                    return false;
                }
                var bankNames = branch.Masters.Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    IsBank(x.Parent.Trim()))
                    .Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = original.Elements().Where(x => Is(x, "ALLLEDGERENTRIES.LIST") || Is(x, "LEDGERENTRIES.LIST")).ToArray();
                if (entries.Length != 2)
                    throw new InvalidOperationException("Interbank transfer must have one bank and one clearing ledger per branch.");
                var candidates = entries.Where(x => bankNames.Contains(Value(x, "LEDGERNAME"))).ToArray();
                if (candidates.Length != 1 ||
                    !decimal.TryParse(Value(candidates[0], "AMOUNT"), NumberStyles.Number | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var amount) || amount == 0)
                    throw new InvalidOperationException("Interbank bank-side amount or ledger is not verifiable.");
                var side = new XElement(candidates[0]);
                // Interbank is reconciled only after the same global
                // duplicate-only Ledger mapping has resolved both bank sides.
                // Keep the exact original signed amounts and common Contra.
                Set(side, "LEDGERNAME", MapBranchLedger(
                    branch.Branch, Value(side, "LEDGERNAME")));
                sides.Add(side);
                amounts.Add(amount);
            }
            if (amounts[0] + amounts[1] != 0 ||
                Value(sourcePair[0].Voucher, "DATE") != Value(sourcePair[1].Voucher, "DATE"))
                throw new InvalidOperationException("Interbank matching failed: dates or opposite bank movements differ.");
            var merged = new XElement(sourcePair[0].Voucher);
            foreach (var entry in merged.Elements().Where(x => Is(x, "ALLLEDGERENTRIES.LIST") || Is(x, "LEDGERENTRIES.LIST")).ToArray())
                entry.Remove();
            foreach (var field in merged.Elements().Where(x => Is(x, "PARTYLEDGERNAME") || Is(x, "PARTYNAME") || Is(x, "BASICBASEPARTYNAME")).ToArray())
                field.Remove();
            foreach (var side in sides) merged.Add(side);
            Set(merged, "VOUCHERTYPENAME", "Contra");
            Set(merged, "VOUCHERNUMBER", "IBT-" + transferId);
            merged.SetAttributeValue("VCHTYPE", "Contra");
            TallyConsolidatedStateNamePolicy.NormalizeVoucher(merged);
            vouchers.Add((sourcePair[0].Branch, merged));
            reconciled++;
        }
        if (vouchers.Count == 0)
            throw new InvalidOperationException("No final vouchers were prepared for the consolidated XML.");

        var masterBuilder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(masterBuilder, plan.DestinationCompanyName);
        // Reuse every existing master-plan XML stage in dependency order:
        // compulsory groups, optional Cash, optional Voucher Types.
        foreach (var planXml in new[] { plan.GroupXml, plan.CashLedgerXml, plan.VoucherTypeXml })
        {
            var source = TallyConsolidatedXmlTextSafety.ParseDocument(
                planXml, "planned consolidated master definitions");
            foreach (var message in source.Descendants().Where(x => Is(x, "TALLYMESSAGE")))
            {
                var rawPlannedMaster = new XElement(message);
                RemoveForeignTallyIdentity(rawPlannedMaster);
                masterBuilder.AppendLine(rawPlannedMaster.ToString(SaveOptions.DisableFormatting));
            }
        }
        foreach (var m in commonInventoryMasters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            masterBuilder.AppendLine(new XElement("TALLYMESSAGE", m.Value).ToString(SaveOptions.DisableFormatting));
        foreach (var m in masterMessages)
            masterBuilder.AppendLine(m.ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndAllMastersImport(masterBuilder);

        var voucherBuilder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginErp9DataImport(voucherBuilder, plan.DestinationCompanyName, "Vouchers");
        foreach (var (_, voucher) in vouchers)
            voucherBuilder.AppendLine(new XElement("TALLYMESSAGE", voucher).ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndErp9DataImport(voucherBuilder);
        // Positive assertion on FINAL wire payloads (includes inventory and
        // interbank) prevents a new feature from leaking foreign IDs later.
        RequireNoForeignTallyIdentity(masterBuilder.ToString(), "consolidated masters");
        RequireNoForeignTallyIdentity(voucherBuilder.ToString(), "consolidated vouchers");
        return new TallyConsolidatedPreparedXmlResult(
            masterBuilder.ToString(), voucherBuilder.ToString(),
            expected.Length, sourceVoucherCount, vouchers.Count, reconciled,
            "Original branch amounts, entries, tax values and journals were not recalculated. " +
            "XML is staged only; live Tally duplicate/readback checks are still mandatory.")
        {
            RebasedBranchAlterCount = rawSourceAltersConverted,
            // This is an AAH-only, verified PREPARED type map. It is never
            // serialized into Tally XML. The original base type remains the
            // durable source-binding identity when the user later enables
            // or disables branch-specific destination voucher types.
            VoucherTypeBaseByDestination = plan.VoucherTypes.ToDictionary(
                item => item.Name, item => item.BaseType,
                StringComparer.OrdinalIgnoreCase),
            // This AAH-only identity map never goes to Tally. It proves
            // "Cash - Karnataka" belongs to the exact B-... voucher scope
            // when an original Cash line is changed in one bound ALTER.
            BranchCashLedgerNamesById = plan.CashLedgers.ToDictionary(
                item => item.BranchId, item => item.Name,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static readonly HashSet<string> ForeignTallyIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "MASTERID", "ALTERID", "GUID", "OLDMASTERID", "OLDGUID",
        "VOUCHERID", "VOUCHERKEY", "VCHKEY", "REMOTEID", "UNIQUEREMOTEID",
        "TALLYGUID", "TAGNAME", "TAGVALUE"
    };

    private static bool IsForeignTallyIdentifier(string name) =>
        ForeignTallyIdentifiers.Contains(name);

    /// <summary>
    /// Branch company identities cannot be assigned in another Tally company.
    /// Preserve raw dates, amounts, original references, tax and party metadata.
    /// Deliberately does NOT remove VOUCHERNUMBER: it is a document/numbering
    /// label that RewriteVoucher replaces with a new branch-qualified target
    /// number for existing destination duplicate/readback gates.
    /// </summary>
    private static void RemoveForeignTallyIdentity(XElement source)
    {
        foreach (var element in source.DescendantsAndSelf().Where(x =>
                     IsForeignTallyIdentifier(x.Name.LocalName)).ToArray())
            element.Remove();
        foreach (var attr in source.DescendantsAndSelf()
                     .SelectMany(x => x.Attributes())
                     .Where(x => IsForeignTallyIdentifier(x.Name.LocalName)).ToArray())
            attr.Remove();
    }

    public static void RequireNoForeignTallyIdentity(string xml, string scope)
    {
        var doc = TallyConsolidatedXmlTextSafety.ParseDocument(xml, scope);
        var forbidden = doc.Descendants().FirstOrDefault(x =>
            IsForeignTallyIdentifier(x.Name.LocalName) ||
            x.Attributes().Any(a => IsForeignTallyIdentifier(a.Name.LocalName)));
        if (forbidden is not null)
            throw new InvalidOperationException(
                "Foreign branch Tally identity leaked into " + scope + ": " + forbidden.Name.LocalName);
    }

    private static void RewriteVoucher(
        XElement voucher, TallyBranchMasterIdentity branch,
        TallyBranchMasterPlan plan, Func<string, string> ledger, string sourceAnchor)
    {
        foreach (var item in voucher.Descendants().Where(x =>
            Is(x, "LEDGERNAME") || Is(x, "PARTYLEDGERNAME") || Is(x, "PARTYNAME") ||
            Is(x, "BASICBASEPARTYNAME")))
        {
            if (string.IsNullOrWhiteSpace(item.Value)) continue;
            item.Value = ledger(item.Value);
        }
        foreach (var item in voucher.Descendants().Where(x => Is(x, "STOCKITEMNAME")))
            if (!string.IsNullOrWhiteSpace(item.Value))
                item.Value = item.Value.Trim() + " - " + branch.DisplayName.Trim();
        var original = Value(voucher, "VOUCHERNUMBER");
        // The source REFERENCE remains unchanged. Only the target-side
        // voucher number is generated, deterministically from durable
        // branch/date/type/reference identity, when original AAH left it
        // blank for Tally's own automatic numbering. The 96-bit identity
        // digest avoids leaking invoice numbers into target numbering.
        var targetNumber = original.Length > 0
            ? branch.BranchId + "-" + original
            : "AAH-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(sourceAnchor)))[..24];
        Set(voucher, "VOUCHERNUMBER", targetNumber);
        var baseType = Value(voucher, "VOUCHERTYPENAME");
        if (plan.VoucherTypes.Count > 0)
        {
            var selected = plan.VoucherTypes.SingleOrDefault(x =>
                x.BranchId.Equals(branch.BranchId, StringComparison.OrdinalIgnoreCase) &&
                x.BaseType.Equals(baseType, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                throw new InvalidOperationException("Branch voucher type is not in the verified optional type catalog: " + baseType);
            Set(voucher, "VOUCHERTYPENAME", selected.Name);
            voucher.SetAttributeValue("VCHTYPE", selected.Name);
        }
    }

    private static bool Is(XElement? x, string name) =>
        x is not null && x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static string Field(XElement element, string field) =>
        element.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(field, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
    private static string Value(XElement element, string name) =>
        element.Elements().FirstOrDefault(x => Is(x, name))?.Value.Trim() ?? "";
    private static void Set(XElement element, string name, string value)
    {
        var match = element.Elements().FirstOrDefault(x => Is(x, name));
        if (match is null) element.Add(new XElement(name, value));
        else match.Value = value;
    }
}

public sealed record TallyPreparedBranchXml(
    TallyBranchMasterIdentity Branch, string Xml, IReadOnlyCollection<MasterRecord> Masters,
    string SourceLiveMastersXml = "");

public sealed record TallyConsolidatedPreparedXmlResult(
    string MastersXml, string VouchersXml, int BranchCount,
    int SourceVouchers, int FinalVouchers, int InterbankReconciled, string Status)
{
    // This is a source-company bound master, never the target company's MASTERID.
    // The target's old-version/read-back gate must make the final CREATE/ALTER decision.
    public int RebasedBranchAlterCount { get; init; }
    public IReadOnlyDictionary<string, string> VoucherTypeBaseByDestination { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> BranchCashLedgerNamesById { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
