using System.Globalization;
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

        var masterMessages = new List<XElement>();
        var vouchers = new List<(TallyPreparedBranchXml Branch, XElement Voucher)>();
        var explicitlyMatched = new Dictionary<string, List<(TallyPreparedBranchXml Branch, XElement Voucher)>>(StringComparer.OrdinalIgnoreCase);
        var seenVoucherAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedLedgerMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedStockMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonInventoryMasters = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var sourceVoucherCount = 0;
        var sourceBoundAlters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var branch in prepared.OrderBy(x => x.Branch.BranchId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(branch.Xml))
                throw new InvalidOperationException("The branch has no prepared Tally XML: " + branch.Branch.DisplayName);
            var source = TallyConsolidatedXmlTextSafety.ParseDocument(
                branch.Xml, "prepared branch " + branch.Branch.DisplayName, LoadOptions.PreserveWhitespace);
            if (!Is(source.Root, "ENVELOPE") ||
                !source.Descendants().Any(x => Is(x, "TALLYMESSAGE")))
                throw new InvalidOperationException("Invalid original Tally XML envelope for " + branch.Branch.DisplayName);
            var ledgerAuthority = branch.Masters
                .Where(m => m.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Name))
                .GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            // Branch-prepared XML deliberately omits Ledgers already live in
            // that branch's Tally company. Fill those master definitions only
            // from the original branch's freshly exported Tally masters, never
            // by guessing GSTIN, banking or party settings.
            var liveSource = string.IsNullOrWhiteSpace(branch.SourceLiveMastersXml)
                ? null : TallyConsolidatedXmlTextSafety.ParseDocument(
                    branch.SourceLiveMastersXml, "live source masters " + branch.Branch.DisplayName,
                    LoadOptions.PreserveWhitespace);
            var originalLedgerXml = source.Descendants().Where(x => Is(x, "LEDGER") &&
                    x.Parent is not null && Is(x.Parent, "TALLYMESSAGE"))
                .Concat(liveSource?.Descendants().Where(x => Is(x, "LEDGER")) ?? [])
                .GroupBy(x => Field(x, "NAME").Length > 0 ? Field(x, "NAME") : Value(x, "NAME"),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
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
                if (Field(stock, "ACTION").Equals("Alter", StringComparison.OrdinalIgnoreCase) ||
                    stock.Descendants().Any(x => Is(x, "MASTERID") || Is(x, "GUID")))
                    throw new InvalidOperationException("Foreign stock MASTERID/ALTER cannot be imported into consolidated Tally.");
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
                // Optional cash uses the exact already-planned branch Cash
                // ledger; all other accounts remain branch-qualified.
                var cash = plan.CashLedgers.SingleOrDefault(x =>
                    x.BranchId.Equals(branch.Branch.BranchId, StringComparison.OrdinalIgnoreCase));
                if (cash is not null &&
                    masters[0].Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase))
                    return cash.Name;
                // Never merge taxes, parties, bank accounts or other ledgers by name.
                return sourceName.Trim() + " - " + branch.Branch.DisplayName.Trim();
            }

            foreach (var raw in source.Descendants().Where(x => Is(x, "VOUCHER") &&
                       x.Parent is not null && Is(x.Parent, "TALLYMESSAGE")))
            {
                var v = new XElement(raw);
                var sourceAction = Field(v, "ACTION");
                if (sourceAction.Equals("Alter", StringComparison.OrdinalIgnoreCase))
                {
                    var id = Field(v, "TAGVALUE");
                    if (!Field(v, "TAGNAME").Equals("MASTER ID", StringComparison.OrdinalIgnoreCase) ||
                        id.Length == 0 || !id.All(char.IsDigit) ||
                        !sourceBoundAlters.Add(branch.Branch.BranchId + "|" + id))
                        throw new InvalidOperationException(
                            "Unverified or repeated original branch Tally MASTERID; cannot rebase source voucher.");
                    // The original MASTERID is authority only inside its branch
                    // company. The consolidated target is a different company.
                    // Stage CREATE for review; no XML stage is allowed to post
                    // until the target's separate duplicate/version gate runs.
                    foreach (var attribute in v.Attributes().Where(x =>
                        x.Name.LocalName is "TAGNAME" or "TAGVALUE" or "GUID").ToArray())
                        attribute.Remove();
                    foreach (var identity in v.Elements().Where(x =>
                        Is(x, "MASTERID") || Is(x, "GUID") || Is(x, "ALTERID")).ToArray())
                        identity.Remove();
                    v.SetAttributeValue("ACTION", "Create");
                }
                else if (!sourceAction.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                         Field(v, "TAGNAME").Length > 0 || Field(v, "TAGVALUE").Length > 0 ||
                         v.Descendants().Any(x => Is(x, "MASTERID") || Is(x, "GUID")) ||
                         v.Attributes().Any(x => x.Name.LocalName.Equals("GUID", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "Branch voucher has a foreign or ambiguous Tally identity; final XML blocked.");
                var originalNumber = Value(v, "VOUCHERNUMBER");
                var date = Value(v, "DATE");
                var type = Value(v, "VOUCHERTYPENAME");
                if (originalNumber.Length == 0 || type.Length == 0 ||
                    !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var voucherDate))
                    throw new InvalidOperationException("Prepared voucher lacks a stable number, valid date or type.");
                if (plan.FinancialYear.Length < 4 ||
                    !int.TryParse(plan.FinancialYear[..4], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var startYear) ||
                    voucherDate < new DateTime(startYear, 4, 1) ||
                    voucherDate >= new DateTime(startYear + 1, 4, 1))
                    throw new InvalidOperationException("Prepared branch voucher is outside the consolidated financial year.");
                var anchor = branch.Branch.BranchId + "|" + date + "|" + type + "|" + originalNumber;
                if (!seenVoucherAnchors.Add(anchor))
                    throw new InvalidOperationException("Duplicate voucher within one branch's prepared XML: " + anchor);
                sourceVoucherCount++;
                var refValue = Value(v, "REFERENCE");
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

                RewriteVoucher(v, branch.Branch, plan, LedgerName, branch.Masters);
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
                var parent = AccountingParent(original);
                if (!mappedLedgerMasters.Add(destinationLedger))
                    throw new InvalidOperationException("Two branch ledgers map to one consolidated ledger: " + destinationLedger);
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
                var authority = ledgerAuthority[original][0];
                var foreignSourceId = Value(sourceLedger, "MASTERID");
                if (authority.TallyMasterId.Trim().Length > 0 &&
                    foreignSourceId.Length > 0 &&
                    !foreignSourceId.Equals(authority.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Original branch Ledger MASTERID changed before consolidation: " + original);
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
                // Foreign branch MASTERID / GUID / ALTERID are never applied to
                // the new consolidated Tally company.
                foreach (var item in ledger.Elements().Where(x =>
                    Is(x, "MASTERID") || Is(x, "GUID") || Is(x, "ALTERID")).ToArray())
                    item.Remove();
                foreach (var attribute in ledger.Attributes().Where(x =>
                    x.Name.LocalName is "MASTERID" or "GUID" or "ALTERID" or "TAGNAME" or "TAGVALUE").ToArray())
                    attribute.Remove();
                if (ledger.Descendants().Any(x => Is(x, "MASTERID") || Is(x, "GUID")))
                    throw new InvalidOperationException("Nested foreign identity in Ledger master needs manual review.");
                ledger.SetAttributeValue("NAME", destinationLedger);
                ledger.SetAttributeValue("ACTION", "Create");
                Set(ledger, "NAME", destinationLedger);
                Set(ledger, "PARENT", parent);
                foreach (var nameTag in ledger.Descendants().Where(x =>
                    Is(x, "NAME") && x.Parent is not null && Is(x.Parent, "NAME.LIST") &&
                    x.Value.Trim().Equals(original, StringComparison.OrdinalIgnoreCase)))
                    nameTag.Value = destinationLedger;
                masterMessages.Add(new XElement("TALLYMESSAGE", ledger));
            }
        }

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
                Set(side, "LEDGERNAME", Value(side, "LEDGERNAME") + " - " + branch.Branch.DisplayName.Trim());
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
                masterBuilder.AppendLine(message.ToString(SaveOptions.DisableFormatting));
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
        return new TallyConsolidatedPreparedXmlResult(
            masterBuilder.ToString(), voucherBuilder.ToString(),
            expected.Length, sourceVoucherCount, vouchers.Count, reconciled,
            "Original branch amounts, entries, tax values and journals were not recalculated. " +
            "XML is staged only; live Tally duplicate/readback checks are still mandatory.")
        {
            RebasedBranchAlterCount = sourceBoundAlters.Count
        };
    }

    private static void RewriteVoucher(
        XElement voucher, TallyBranchMasterIdentity branch,
        TallyBranchMasterPlan plan, Func<string, string> ledger, IReadOnlyCollection<MasterRecord> authority)
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
        Set(voucher, "VOUCHERNUMBER", branch.BranchId + "-" + original);
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
}
