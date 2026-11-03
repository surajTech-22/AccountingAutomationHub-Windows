using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyConsolidatedPreparedXmlTests
{
    private const string Pan = "ABCDE1234F";
    private static readonly TallyBranchMasterIdentity Karnataka =
        new(101, "KA", "Karnataka", Pan, "29ABCDE1234F1Z5", "2025-26");
    private static readonly TallyBranchMasterIdentity TamilNadu =
        new(102, "TN", "Tamil Nadu", Pan, "33ABCDE1234F1Z5", "2025-26");

    private static MasterRecord Ledger(string name, string parent) =>
        new() { Name = name, Parent = parent, Type = "Ledger", Source = "Manual" };

    private static string Xml(string type, string number, string reference,
        string debitName, string creditName, string debit, string credit,
        string debitParent, string creditParent, bool altered = false) => $"""
        <ENVELOPE><HEADER><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER>
        <BODY><IMPORTDATA><REQUESTDESC><REPORTNAME>All Masters</REPORTNAME></REQUESTDESC>
        <REQUESTDATA>
          <TALLYMESSAGE><LEDGER NAME="{debitName}" ACTION="Create">
            <NAME>{debitName}</NAME><PARENT>{System.Security.SecurityElement.Escape(debitParent)}</PARENT></LEDGER></TALLYMESSAGE>
          <TALLYMESSAGE><LEDGER NAME="{creditName}" ACTION="Create">
            <NAME>{creditName}</NAME><PARENT>{System.Security.SecurityElement.Escape(creditParent)}</PARENT></LEDGER></TALLYMESSAGE>
          <TALLYMESSAGE><VOUCHER ACTION="{(altered ? "Alter" : "Create")}" VCHTYPE="{type}">
            <DATE>20250505</DATE><VOUCHERNUMBER>{number}</VOUCHERNUMBER>
            <VOUCHERTYPENAME>{type}</VOUCHERTYPENAME><REFERENCE>{reference}</REFERENCE>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>{debitName}</LEDGERNAME><AMOUNT>{debit}</AMOUNT></ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>{creditName}</LEDGERNAME><AMOUNT>{credit}</AMOUNT></ALLLEDGERENTRIES.LIST>
          </VOUCHER></TALLYMESSAGE>
        </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
        """;

    private static TallyBranchMasterPlan Plan() =>
        TallyBranchConsolidationMasterPlanner.Build(
            "Consolidated Books", "test-company", "2025-26", [Karnataka, TamilNadu],
            [], true);

    private static TallyPreparedBranchXml Prepared(TallyBranchMasterIdentity branch, string xml,
        MasterRecord a, MasterRecord b) => new(branch, xml, [a, b]);

    private static void Check(bool value, string why)
    {
        if (!value) throw new InvalidOperationException("Consolidated prepared XML: " + why);
    }
    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe XML accepted: " + why);
    }
    public static void Run()
    {
        var a = Prepared(Karnataka,
            Xml("Sales", "1", "KA-INV-1", "Buyer", "Local Sales", "-100.25", "100.25", "Sundry Debtors", "Sales Accounts"),
            Ledger("Buyer", "Sundry Debtors"), Ledger("Local Sales", "Sales Accounts"));
        // Tally has one global Ledger namespace even under different Groups.
        // The normal happy path uses two different legitimate source names.
        var b = Prepared(TamilNadu,
            Xml("Sales", "1", "TN-INV-1", "Tamil Buyer", "Tamil Sales", "-200.10", "200.10", "Sundry Debtors", "Sales Accounts"),
            Ledger("Tamil Buyer", "Sundry Debtors"), Ledger("Tamil Sales", "Sales Accounts"));

        var result = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a, b]);
        Check(result.BranchCount == 2 && result.SourceVouchers == 2 && result.FinalVouchers == 2,
            "Voucher quantity was changed.");
        var sourceAmounts = new[] { "-100.25", "100.25", "-200.10", "200.10" }.Order().ToArray();
        var finalDoc = XDocument.Parse(result.VouchersXml);
        var amounts = finalDoc.Descendants("AMOUNT").Select(x => x.Value).Order().ToArray();
        Check(sourceAmounts.SequenceEqual(amounts), "One branch amount was recalculated or replaced.");
        Check(finalDoc.Descendants("VOUCHERNUMBER").Select(x => x.Value).ToArray()
            .SequenceEqual(new[] { "KA-1", "TN-1" }), "Branch voucher identity collided.");
        Check(finalDoc.Descendants("LEDGERNAME").Select(x => x.Value).ToHashSet()
            .SetEquals(new[] { "Buyer", "Local Sales",
                "Tamil Buyer", "Tamil Sales" }), "Branch ledger identity was lost.");
        var masterDoc = XDocument.Parse(result.MastersXml);
        Check(masterDoc.Descendants("LEDGER").Count() == 4 &&
              masterDoc.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Buyer" &&
                  (string?)x.Element("PARENT") == "Karnataka Debtors"),
              "Existing ledger grouping was not carried into master XML.");

        // Live Mitansh consolidated failure: all 67 actual source
        // ledgers carried placeholder <TAXCLASSIFICATIONNAME> Not Applicable.
        // Tally rejects the first (Amazon): Created=0, Exceptions=1,
        // "Tax Classification 'Not Applicable' does not exist!".
        // Verify the sentinel value is disabled but its XML label remains,
        // including the original illegal ERP9 '&#4;' text encoding.
        const string legacySentinels =
            "<TAXCLASSIFICATIONNAME>&#4; Not Applicable</TAXCLASSIFICATIONNAME>" +
            "<GSTTYPE> Not Applicable</GSTTYPE>" +
            "<APPROPRIATEFOR> Not Applicable</APPROPRIATEFOR>" +
            "<SERVICECATEGORY> Not Applicable</SERVICECATEGORY>" +
            "<EXCISELEDGERCLASSIFICATION> Not Applicable</EXCISELEDGERCLASSIFICATION>" +
            "<EXCISEDUTYTYPE> Not Applicable</EXCISEDUTYTYPE>" +
            "<EXCISENATUREOFPURCHASE> Not Applicable</EXCISENATUREOFPURCHASE>" +
            "<LEDGERFBTCATEGORY> Not Applicable</LEDGERFBTCATEGORY>";
        var legacyTemplate = a.Xml.Replace(
            "<PARENT>Sundry Debtors</PARENT></LEDGER>",
            "<PARENT>Sundry Debtors</PARENT>" + legacySentinels + "</LEDGER>");
        var cleaned = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = legacyTemplate }, b
        ]);
        var cleanedBuyer = XDocument.Parse(cleaned.MastersXml).Descendants("LEDGER")
            .Single(x => (string?)x.Element("NAME") == "Buyer");
        Check(cleanedBuyer.Element("PARENT")?.Value == "Karnataka Debtors" &&
              new[] {
                  "TAXCLASSIFICATIONNAME", "GSTTYPE", "APPROPRIATEFOR",
                  "SERVICECATEGORY", "EXCISELEDGERCLASSIFICATION",
                  "EXCISEDUTYTYPE", "EXCISENATUREOFPURCHASE",
                  "LEDGERFBTCATEGORY"
              }.All(name => cleanedBuyer.Element(name)?.Value == string.Empty) &&
              !cleaned.MastersXml.Contains("Not Applicable", StringComparison.OrdinalIgnoreCase) &&
              XDocument.Parse(cleaned.VouchersXml).Descendants("AMOUNT")
                  .Select(x => x.Value).Order().SequenceEqual(sourceAmounts),
            "Placeholder labels were removed/not disabled, or branch voucher amounts changed.");

        // Do not remove genuine original tax-classification references:
        // only the special 'Not Applicable' placeholders lack a Master.
        var actualClassification = a.Xml.Replace(
            "<PARENT>Sundry Debtors</PARENT></LEDGER>",
            "<PARENT>Sundry Debtors</PARENT>" +
            "<TAXCLASSIFICATIONNAME>Original Valid Category</TAXCLASSIFICATIONNAME></LEDGER>");
        var classified = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = actualClassification }, b
        ]);
        Check(XDocument.Parse(classified.MastersXml).Descendants("LEDGER")
                  .Single(x => (string?)x.Element("NAME") == "Buyer")
                  .Element("TAXCLASSIFICATIONNAME")?.Value == "Original Valid Category",
            "A genuine source Tax Classification was stripped instead of preserving original metadata.");

        // The uploaded Mitansh GST-only master/voucher XML retains legacy
        // VATDEALERTYPE/ISVATDUTYPAID. Keep the labels but disable VAT values
        // only in the consolidated destination copy, without changing GSTIN
        // or DR/CR and without changing a genuine non-GST VAT source.
        var gstVatBranchXml = a.Xml
            .Replace("<PARENT>Sundry Debtors</PARENT></LEDGER>",
                "<PARENT>Sundry Debtors</PARENT>" +
                "<PARTYGSTIN>29ABCDE1234F1Z5</PARTYGSTIN>" +
                "<GSTREGISTRATIONTYPE>Regular</GSTREGISTRATIONTYPE>" +
                "<VATDEALERTYPE>Regular</VATDEALERTYPE></LEDGER>")
            .Replace("<VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>",
                "<VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>" +
                "<GSTREGISTRATIONTYPE>Regular</GSTREGISTRATIONTYPE>" +
                "<ISVATDUTYPAID>Yes</ISVATDUTYPAID>");
        var gstVatCopy = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = gstVatBranchXml }, b
        ]);
        var vatLedger = XDocument.Parse(gstVatCopy.MastersXml).Descendants("LEDGER")
            .Single(x => (string?)x.Element("NAME") == "Buyer");
        var vatVoucher = XDocument.Parse(gstVatCopy.VouchersXml).Descendants("VOUCHER")
            .Single(x => (string?)x.Element("VOUCHERNUMBER") == "KA-1");
        Check(vatLedger.Element("VATDEALERTYPE")?.Value == string.Empty &&
              vatLedger.Element("PARTYGSTIN")?.Value == "29ABCDE1234F1Z5" &&
              vatLedger.Element("GSTREGISTRATIONTYPE")?.Value == "Regular" &&
              vatVoucher.Element("ISVATDUTYPAID")?.Value == "No" &&
              vatVoucher.Element("GSTREGISTRATIONTYPE")?.Value == "Regular" &&
              XDocument.Parse(gstVatCopy.VouchersXml).Descendants("AMOUNT")
                  .Select(x => x.Value).Order().SequenceEqual(sourceAmounts),
            "GST consolidation removed VAT labels, left VAT activated, or modified GST/DR/CR.");

        var legacyVatBranchXml = a.Xml
            .Replace("<PARENT>Sundry Debtors</PARENT></LEDGER>",
                "<PARENT>Sundry Debtors</PARENT><VATDEALERTYPE>Regular</VATDEALERTYPE></LEDGER>")
            .Replace("<VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>",
                "<VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><ISVATDUTYPAID>Yes</ISVATDUTYPAID>");
        var legacyVatCopy = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = legacyVatBranchXml }, b
        ]);
        Check(XDocument.Parse(legacyVatCopy.MastersXml).Descendants("LEDGER")
                  .Single(x => (string?)x.Element("NAME") == "Buyer")
                  .Element("VATDEALERTYPE")?.Value == "Regular" &&
              XDocument.Parse(legacyVatCopy.VouchersXml).Descendants("VOUCHER")
                  .Single(x => (string?)x.Element("VOUCHERNUMBER") == "KA-1")
                  .Element("ISVATDUTYPAID")?.Value == "Yes",
            "A non-GST source VAT setting was changed without GST evidence.");

        Check(masterDoc.Descendants("GROUP").Any(x =>
                (string?)x.Element("NAME") == "Karnataka Debtors") &&
              masterDoc.Descendants("GROUP").Any(x =>
                (string?)x.Element("NAME") == "Tamil Nadu Debtors"),
            "Mandatory branch-named groups disappeared when Ledger names became state-free.");

        // Both branches have the same original names. Only THESE shared
        // names get branch suffixes; the two compulsory state/branch Groups
        // keep their original parent classification and all signed amounts.
        var sameNames = b with {
            Xml = a.Xml.Replace("KA-INV-1", "TN-INV-1")
        };
        var duplicated = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a, sameNames]);
        var duplicatedMasters = XDocument.Parse(duplicated.MastersXml);
        var duplicatedVouchers = XDocument.Parse(duplicated.VouchersXml);
        Check(duplicatedMasters.Descendants("LEDGER").Count() == 4 &&
              duplicatedMasters.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Buyer - Karnataka" &&
                  (string?)x.Element("PARENT") == "Karnataka Debtors") &&
              duplicatedMasters.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Buyer - Tamil Nadu" &&
                  (string?)x.Element("PARENT") == "Tamil Nadu Debtors") &&
              duplicatedMasters.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Local Sales - Karnataka" &&
                  (string?)x.Element("PARENT") == "Karnataka Sales") &&
              duplicatedMasters.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Local Sales - Tamil Nadu" &&
                  (string?)x.Element("PARENT") == "Tamil Nadu Sales") &&
              duplicatedVouchers.Descendants("LEDGERNAME").Select(x => x.Value)
                  .ToHashSet().SetEquals(new[] {
                    "Buyer - Karnataka", "Local Sales - Karnataka",
                    "Buyer - Tamil Nadu", "Local Sales - Tamil Nadu"
                  }) &&
              duplicatedVouchers.Descendants("AMOUNT").Select(x => x.Value)
                  .Order().SequenceEqual(new[] {
                    "-100.25", "100.25", "-100.25", "100.25"
                  }.Order()) &&
              !duplicated.MastersXml.Contains("<MASTERID>") &&
              !duplicated.VouchersXml.Contains("<MASTERID>"),
            "Shared source names were not independently mapped to correct branch groups/vouchers.");

        // Partial overlap: a shared party is suffixed, while unrelated
        // single-branch sales ledgers must NEVER acquire state suffixes.
        var partlyShared = b with {
            Xml = Xml("Sales", "1", "TN-INV-1", "Buyer", "Tamil Sales",
                "-200.10", "200.10", "Sundry Debtors", "Sales Accounts"),
            Masters = [Ledger("Buyer", "Sundry Debtors"), Ledger("Tamil Sales", "Sales Accounts")]
        };
        var partial = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a, partlyShared]);
        var partialMasters = XDocument.Parse(partial.MastersXml);
        Check(partialMasters.Descendants("LEDGER").Select(x => (string?)x.Element("NAME"))
                  .ToHashSet().SetEquals(new[] {
                    "Buyer - Karnataka", "Buyer - Tamil Nadu",
                    "Local Sales", "Tamil Sales"
                  }) &&
              XDocument.Parse(partial.VouchersXml).Descendants("LEDGERNAME")
                  .Select(x => x.Value).ToHashSet().SetEquals(new[] {
                    "Buyer - Karnataka", "Buyer - Tamil Nadu",
                    "Local Sales", "Tamil Sales"
                  }),
            "Ledger names unique to one branch were incorrectly suffixed.");

        // Same tax Ledger with different whitespace around '@' is ONE
        // cross-branch name. Both masters and all voucher posting references
        // must use one canonical spelling plus the owning branch name.
        var kaTax = Prepared(Karnataka,
            Xml("Journal", "98", "KA-GST-98", "Buyer", "Input IGST @ 18%",
                "-50.00", "50.00", "Sundry Debtors", "Duties & Taxes"),
            Ledger("Buyer", "Sundry Debtors"), Ledger("Input IGST @ 18%", "Duties & Taxes"));
        var tnTax = Prepared(TamilNadu,
            Xml("Journal", "99", "TN-GST-99", "Tamil Buyer", "Input IGST @18%",
                "-25.00", "25.00", "Sundry Debtors", "Duties & Taxes"),
            Ledger("Tamil Buyer", "Sundry Debtors"), Ledger("Input IGST @18%", "Duties & Taxes"));
        var spacing = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [kaTax, tnTax]);
        var spacingMasters = XDocument.Parse(spacing.MastersXml);
        var spacingVouchers = XDocument.Parse(spacing.VouchersXml);
        foreach (var (name, parent) in new[] {
                     ("Input IGST @ 18% - Karnataka", "Karnataka Taxes"),
                     ("Input IGST @ 18% - Tamil Nadu", "Tamil Nadu Taxes") })
        {
            var taxLedger = spacingMasters.Descendants("LEDGER").Single(x =>
                (string?)x.Element("NAME") == name);
            Check((string?)taxLedger.Element("PARENT") == parent &&
                  (string?)taxLedger.Element("TAXTYPE") == "GST" &&
                  (string?)taxLedger.Element("GSTDUTYHEAD") == "Integrated Tax" &&
                  (string?)taxLedger.Element("ISBEHAVEASDUTY") == "Yes" &&
                  spacingVouchers.Descendants("LEDGERNAME").Any(x => x.Value == name),
                "Equivalent GST tax Ledger spacing did not preserve original branch/group or GST Duty Head.");
        }
        var gstSourceWithSentinel = XDocument.Parse(kaTax.Xml);
        gstSourceWithSentinel.Descendants("LEDGER").Single(x =>
            (string?)x.Element("NAME") == "Input IGST @ 18%")
            .Add(new XElement("TAXCLASSIFICATIONNAME", " Not Applicable"));
        var gstClean = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            kaTax with { Xml = gstSourceWithSentinel.ToString() }, tnTax
        ]);
        var gstCleanLedger = XDocument.Parse(gstClean.MastersXml)
            .Descendants("LEDGER").Single(x =>
                (string?)x.Element("NAME") == "Input IGST @ 18% - Karnataka");
        Check(gstCleanLedger.Element("TAXCLASSIFICATIONNAME")?.Value == string.Empty &&
              gstCleanLedger.Element("TAXTYPE")?.Value == "GST" &&
              gstCleanLedger.Element("GSTDUTYHEAD")?.Value == "Integrated Tax",
            "Disabling invalid optional classification damaged real GST duty-head metadata.");

        Check(spacingVouchers.Descendants("AMOUNT").Select(x => x.Value)
              .Order().SequenceEqual(new[] { "-50.00", "50.00", "-25.00", "25.00" }.Order()),
            "GST ledger identity normalization recalculated original amounts.");

        // GST settlement and generic tax-group members are NOT presumed
        // to be a tax duty. Only original IGST/CGST/SGST/UTGST heads are
        // eligible for the corrected Tally GST ledger classification.
        var kaSettlement = Prepared(Karnataka,
            Xml("Journal", "90", "SET-90", "Buyer", "GST Payable / Receivable",
                "-10.00", "10.00", "Sundry Debtors", "Duties & Taxes"),
            Ledger("Buyer", "Sundry Debtors"),
            Ledger("GST Payable / Receivable", "Duties & Taxes"));
        var settlement = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [kaSettlement, b]);
        var settlementMaster = XDocument.Parse(settlement.MastersXml)
            .Descendants("LEDGER").Single(x => (string?)x.Element("NAME") == "GST Payable / Receivable");
        Check(settlementMaster.Element("GSTDUTYHEAD") is null &&
              settlementMaster.Element("ISBEHAVEASDUTY") is null,
            "GST settlement ledger was silently assigned a guessed IGST/CGST/SGST head.");

        // An explicit original GST duty head is authoritative. An
        // inconsistent one is rejected instead of being overwritten.
        var invalidHead = XDocument.Parse(kaTax.Xml);
        invalidHead.Descendants("LEDGER").Single(x =>
            (string?)x.Element("NAME") == "Input IGST @ 18%")
            .Add(new XElement("TAXTYPE", "GST"), new XElement("GSTDUTYHEAD", "Central Tax"));
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            kaTax with { Xml = invalidHead.ToString() }, tnTax
        ]), "conflicting original source tax duty head was overwritten");

        // A real source account may already be called "Buyer - Karnataka".
        // It cannot be merged with a generated alias for "Buyer"; fail closed.
        var aliasCollision = b with {
            Xml = Xml("Sales", "1", "TN-INV-1", "Buyer",
                "Buyer - Karnataka", "-200.10", "200.10",
                "Sundry Debtors", "Sales Accounts"),
            Masters = [Ledger("Buyer", "Sundry Debtors"),
                Ledger("Buyer - Karnataka", "Sales Accounts")]
        };
        try
        {
            TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a, aliasCollision]);
            throw new Exception("Generated branch suffix collided with an existing source ledger.");
        }
        catch (InvalidOperationException exception)
        {
            Check(exception.Message.Contains("after automatic branch suffixing", StringComparison.Ordinal) &&
                  exception.Message.Contains("Buyer - Karnataka", StringComparison.Ordinal),
                "An actual generated alias/name collision must remain fail-closed.");
        }

        // Formatting, not tax calculation: GST numeric state codes become
        // official names; party master State follows its own registration
        // prefix, while voucher place of supply remains source-owned.
        var stateData = XDocument.Parse(a.Xml);
        var stateVoucher = stateData.Descendants("VOUCHER").Single();
        stateVoucher.Add(new XElement("PLACEOFSUPPLY", "24"), new XElement("STATENAME", "29"));
        var party = stateData.Descendants("LEDGER").Single(x => (string?)x.Element("NAME") == "Buyer");
        party.Add(new XElement("PARTYGSTIN", "24ABCDE1234F1Z5"),
                  new XElement("STATENAME", "29"), new XElement("PRIORSTATENAME", "29"));
        var stateResult = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = stateData.ToString() }, b ]);
        var normalizedParty = XDocument.Parse(stateResult.MastersXml).Descendants("LEDGER")
            .Single(x => (string?)x.Element("NAME") == "Buyer");
        var normalizedVoucher = XDocument.Parse(stateResult.VouchersXml).Descendants("VOUCHER")
            .Single(x => (string?)x.Element("REFERENCE") == "KA-INV-1");
        Check((string?)normalizedParty.Element("STATENAME") == "Gujarat" &&
              (string?)normalizedParty.Element("PRIORSTATENAME") == "Gujarat" &&
              (string?)normalizedParty.Element("PARTYGSTIN") == "24ABCDE1234F1Z5" &&
              (string?)normalizedVoucher.Element("PLACEOFSUPPLY") == "Gujarat" &&
              (string?)normalizedVoucher.Element("STATENAME") == "Karnataka" &&
              normalizedVoucher.Descendants("AMOUNT").Select(x => x.Value)
                  .Order().SequenceEqual(new[] { "-100.25", "100.25" }.Order()),
            "GST state normalization altered source registration, voucher PoS or DR/CR.");

        // Same PAN across a party Ledger's default and one voucher
        // registration is valid (e.g. the same Amazon entity in two states).
        // The voucher-level registration must survive verbatim. A DIFFERENT
        // PAN cannot be silently attached to this same source party Ledger.
        var withPartyGstin = XDocument.Parse(a.Xml);
        withPartyGstin.Descendants("LEDGER").Single(x => (string?)x.Element("NAME") == "Buyer")
            .Add(new XElement("PARTYGSTIN", "29ABCDE1234F1Z5"));
        withPartyGstin.Descendants("VOUCHER").Single()
            .Add(new XElement("PARTYLEDGERNAME", "Buyer"),
                 new XElement("PARTYGSTIN", "24ABCDE1234F1Z5"));
        var safeOverride = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = withPartyGstin.ToString() }, b ]);
        Check(XDocument.Parse(safeOverride.VouchersXml)
                  .Descendants("VOUCHER").Single(x =>
                      (string?)x.Element("REFERENCE") == "KA-INV-1")
                  .Element("PARTYGSTIN")?.Value == "24ABCDE1234F1Z5",
            "Source voucher GST registration override was overwritten by Ledger's default GSTIN.");
        withPartyGstin.Descendants("VOUCHER").Single()
            .Element("PARTYGSTIN")!.Value = "24ZZZZZ9999Z1Z9";
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = withPartyGstin.ToString() }, b
        ]), "different legal PAN was silently joined to original party Ledger");

        // Existing AAH accounting export uses empty VOUCHERNUMBER when the
        // original Tally type assigns numbering automatically. Its REFERENCE,
        // including GST-owned journal references, is the stable source key.
        var autoNumbered = XDocument.Parse(a.Xml);
        autoNumbered.Descendants("VOUCHERNUMBER").Single().Value = "";
        var autoPrepared = a with { Xml = autoNumbered.ToString() };
        var autoA = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [autoPrepared, b]);
        var autoB = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [autoPrepared, b]);
        var autoVoucher = XDocument.Parse(autoA.VouchersXml).Descendants("VOUCHER")
            .Single(x => (string?)x.Element("REFERENCE") == "KA-INV-1");
        var automaticNumber = (string?)autoVoucher.Element("VOUCHERNUMBER") ?? "";
        Check(automaticNumber.StartsWith("AAH-", StringComparison.Ordinal) &&
              automaticNumber.Length == 28 &&
              automaticNumber == (string?)XDocument.Parse(autoB.VouchersXml)
                  .Descendants("VOUCHER")
                  .Single(x => (string?)x.Element("REFERENCE") == "KA-INV-1")
                  .Element("VOUCHERNUMBER") &&
              autoVoucher.Descendants("AMOUNT").Select(x => x.Value)
                  .Order().SequenceEqual(new[] { "-100.25", "100.25" }.Order()) &&
              (string?)autoVoucher.Element("REFERENCE") == "KA-INV-1",
            "Auto-numbered original branch voucher must retain reference and original DR/CR amounts with a deterministic target number.");
        var noIdentity = XDocument.Parse(autoPrepared.Xml);
        noIdentity.Descendants("REFERENCE").Single().Value = "";
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(
            Plan(), [a with { Xml = noIdentity.ToString() }, b]),
            "Original voucher with neither number nor reference must remain fail-closed.");
        var repeated = XDocument.Parse(autoPrepared.Xml);
        var originalMessage = repeated.Descendants("TALLYMESSAGE")
            .Single(x => x.Element("VOUCHER") is not null);
        originalMessage.AddAfterSelf(new XElement(originalMessage));
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(
            Plan(), [a with { Xml = repeated.ToString() }, b]),
            "Two blank-number vouchers with identical branch/date/type/reference must not silently coalesce.");

        // Real Gujarat private-branch regression: the original exporter can
        // stage a complete Ledger CREATE even when a separate branch Master
        // cache row was not hydrated. It is original source authority, not a
        // company-link or GSTIN-derived re-creation.
        var onlySalesMaster = a with
        {
            Masters = [Ledger("Local Sales", "Sales Accounts")]
        };
        var preparedMasterAuthority = TallyConsolidatedPreparedXmlBuilder.Build(
            Plan(), [onlySalesMaster, b]);
        Check(XDocument.Parse(preparedMasterAuthority.MastersXml).Descendants("LEDGER")
            .Any(x => (string?)x.Element("NAME") == "Buyer"),
            "Original prepared branch Ledger CREATE was ignored when the Master cache was absent.");

        // Never let a voucher name alone authorize an invented party/GST Ledger.
        var missingBuyerDeclaration = XDocument.Parse(a.Xml);
        missingBuyerDeclaration.Descendants("TALLYMESSAGE")
            .Where(x => (string?)x.Element("LEDGER")?.Attribute("NAME") == "Buyer").Remove();
        var missingBuyer = a with
        {
            Xml = missingBuyerDeclaration.ToString(),
            Masters = [Ledger("Local Sales", "Sales Accounts")]
        };
        try
        {
            TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [missingBuyer, b]);
            throw new Exception("Unverified Gujarat-style Customer Ledger was accepted without source authority.");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("Buyer", StringComparison.Ordinal) &&
                  ex.Message.Contains("unverified posting Ledger", StringComparison.Ordinal),
                "Unverified party ledger must be reported with exact branch source name.");
        }

        var missingBothDeclarations = XDocument.Parse(a.Xml);
        missingBothDeclarations.Descendants("TALLYMESSAGE")
            .Where(x => x.Element("LEDGER") is not null).Remove();
        var missingBoth = a with { Xml = missingBothDeclarations.ToString(), Masters = [] };
        try
        {
            TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [missingBoth, b]);
            throw new Exception("Two unverified Ledger names were silently treated as valid.");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("2 unverified posting Ledger(s)", StringComparison.Ordinal) &&
                  ex.Message.Contains("Buyer", StringComparison.Ordinal) &&
                  ex.Message.Contains("Local Sales", StringComparison.Ordinal),
                "Consolidation must report ALL missing ledgers together, never one by one.");
        }

        var originalWithoutBuyerMaster = a with
        {
            Xml = missingBuyerDeclaration.ToString(),
            Masters = [Ledger("Local Sales", "Sales Accounts")]
        };
        // Branch vouchers may refer to already-live Tally ledgers whose CREATE
        // declaration is absent from the normal prepared XML. The original
        // branch's full Tally master data is used without recreating GST data.
        var withoutBuyerDoc = XDocument.Parse(a.Xml);
        withoutBuyerDoc.Descendants("TALLYMESSAGE")
            .Where(x => (string?)x.Element("LEDGER")?.Attribute("NAME") == "Buyer")
            .Remove();
        const string fullBuyer = """
            <AAHMASTEREXPORT><RESPONSE><LEDGER NAME="Buyer">
              <NAME>Buyer</NAME><PARENT>Sundry Debtors</PARENT>
              <MASTERID>305</MASTERID><TAXTYPE>Others</TAXTYPE>
              <MAILINGNAME>Buyer Original</MAILINGNAME>
              <GSTIN>29ABCDE1234F1Z5</GSTIN><COUNTRYOFRESIDENCE>India</COUNTRYOFRESIDENCE>
              <STATE>KA</STATE><LEDGSTREGDETAILS.LIST><GSTIN>29ABCDE1234F1Z5</GSTIN></LEDGSTREGDETAILS.LIST>
            </LEDGER></RESPONSE></AAHMASTEREXPORT>
            """;
        var liveMasterCase = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = withoutBuyerDoc.ToString(), SourceLiveMastersXml = fullBuyer }, b ]);
        var liveOnlyAuthority = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            originalWithoutBuyerMaster with { SourceLiveMastersXml = fullBuyer }, b ]);
        Check(XDocument.Parse(liveOnlyAuthority.MastersXml).Descendants("LEDGER")
            .Any(x => (string?)x.Element("NAME") == "Buyer" &&
                      (string?)x.Element("PARENT") == "Karnataka Debtors"),
            "Original separately linked Tally's full Master export was not accepted as ledger authority.");
        var detailed = XDocument.Parse(liveMasterCase.MastersXml).Descendants("LEDGER").Single(x =>
            (string?)x.Element("NAME") == "Buyer");
        Check(detailed.Element("MASTERID") is null &&
              (string?)detailed.Element("PARENT") == "Karnataka Debtors" &&
              detailed.Descendants("GSTIN").Any(x => x.Value == "29ABCDE1234F1Z5"),
              "Original GST ledger metadata or branch Tally MASTERID handling failed.");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = withoutBuyerDoc.ToString(),
                SourceLiveMastersXml = @"<RESPONSE><LEDGER NAME=""Buyer""><NAME>Buyer</NAME><PARENT>Sundry Debtors</PARENT><MASTERID>305</MASTERID></LEDGER></RESPONSE>" }, b ]),
            "a thin live ledger collection cannot replace complete GST/party source metadata");

        // Inventory values and tax preparation are also source-owned. Only
        // the stock MASTER identity is namespaced for the destination company.
        string WithStock(string original) => original
            .Replace("<TALLYMESSAGE><VOUCHER", """
                <TALLYMESSAGE><STOCKITEM NAME="Rice" ACTION="Create"><NAME>Rice</NAME><PARENT>Primary</PARENT>
                <BASEUNITS>QTL</BASEUNITS></STOCKITEM></TALLYMESSAGE>
                <TALLYMESSAGE><VOUCHER
                """)
            .Replace("</VOUCHER></TALLYMESSAGE>", """
                <ALLINVENTORYENTRIES.LIST><STOCKITEMNAME>Rice</STOCKITEMNAME>
                <AMOUNT>100.25</AMOUNT></ALLINVENTORYENTRIES.LIST>
                </VOUCHER></TALLYMESSAGE>
                """);
        var inventory = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = WithStock(a.Xml) }, b with { Xml = WithStock(b.Xml) }]);
        var inventoryVoucher = XDocument.Parse(inventory.VouchersXml);
        var inventoryMaster = XDocument.Parse(inventory.MastersXml);
        Check(inventoryVoucher.Descendants("STOCKITEMNAME").Select(x => x.Value).ToHashSet()
              .SetEquals(new[] { "Rice - Karnataka", "Rice - Tamil Nadu" }) &&
              inventoryMaster.Descendants("STOCKITEM").Count() == 2 &&
              inventoryVoucher.Descendants("ALLINVENTORYENTRIES.LIST")
                  .All(x => x.Element("AMOUNT")?.Value == "100.25"),
              "Prepared stock item identity or item amount was regenerated.");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = a.Xml.Replace("</VOUCHER></TALLYMESSAGE>",
                "<ALLINVENTORYENTRIES.LIST><STOCKITEMNAME>Unverified Rice</STOCKITEMNAME></ALLINVENTORYENTRIES.LIST></VOUCHER></TALLYMESSAGE>") }, b ]),
            "inventory item without full original master must be blocked");

        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = a.Xml.Replace("<DATE>20250505</DATE>", "<DATE>20240405</DATE>") }, b ]),
            "an unrelated financial-year voucher was allowed into the consolidated XML");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [a]),
            "missing branch must stop the import");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a, b with { Branch = Karnataka }]), "wrong branch identity");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = a.Xml.Replace(@"<LEDGER NAME=""Buyer"" ACTION=""Create"">",
                @"<LEDGER NAME=""Not Buyer"" ACTION=""Create"">") }, b ]), "missing source ledger master");
        // The branch Alter operation is NOT the consolidated company.
        // Even an old source Alter without a valid branch Tally MASTERID must
        // be converted from its raw voucher postings into target CREATE.
        var rawAlterWithoutId = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = Xml("Sales", "1", "KA-INV-1", "Buyer", "Local Sales",
                "-100.25", "100.25", "Sundry Debtors", "Sales Accounts", altered: true) }, b]);
        Check(rawAlterWithoutId.RebasedBranchAlterCount == 1 &&
              XDocument.Parse(rawAlterWithoutId.VouchersXml).Descendants("VOUCHER")
                  .All(v => (string?)v.Attribute("ACTION") == "Create"),
              "Original branch Alter was incorrectly treated as destination Tally authority.");

        // A branch's live Tally MASTERID is source provenance, NEVER the
        // consolidated destination MASTERID. Rebase only in the offline XML.
        var confirmedAlterXml = Xml("Sales", "1", "KA-INV-1", "Buyer", "Local Sales",
            "-100.25", "100.25", "Sundry Debtors", "Sales Accounts", altered: true)
            .Replace(@"<VOUCHER ACTION=""Alter""",
                @"<VOUCHER ACTION=""Alter"" TAGNAME=""MASTER ID"" TAGVALUE=""912""");
        var rebased = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = confirmedAlterXml }, b ]);
        var rebasedVoucher = XDocument.Parse(rebased.VouchersXml).Descendants("VOUCHER").First();
        Check(rebased.RebasedBranchAlterCount == 1 &&
              (string?)rebasedVoucher.Attribute("ACTION") == "Create" &&
              rebasedVoucher.Attribute("TAGVALUE") is null &&
              rebasedVoucher.Attribute("TAGNAME") is null,
              "Foreign Tally MASTERID was reused in consolidated destination XML.");
        // The source number, reference, amounts and GST are business data.
        // Every source company ID/value is foreign and MUST be erased,
        // including nested IDs on the voucher, posting, source ledger and
        // inventory master XML. Two different branch IDs produce the SAME
        // consolidated XML.
        string SourceIdentities(string sourceXml, string id)
        {
            var doc = XDocument.Parse(sourceXml);
            var voucher = doc.Descendants("VOUCHER").Single();
            voucher.SetAttributeValue("ACTION", "Alter");
            voucher.SetAttributeValue("TAGNAME", "MASTER ID");
            voucher.SetAttributeValue("TAGVALUE", id);
            voucher.SetAttributeValue("GUID", "branch-voucher-" + id);
            voucher.Add(new XElement("MASTERID", id), new XElement("ALTERID", "88" + id),
                new XElement("VOUCHERID", "voucher-" + id), new XElement("REMOTEID", "remote-" + id));
            voucher.Descendants("ALLLEDGERENTRIES.LIST").First()
                .Add(new XElement("VCHKEY", "key-" + id));
            foreach (var ledger in doc.Descendants("LEDGER"))
            {
                ledger.SetAttributeValue("ALTERID", "22" + id);
                ledger.Add(new XElement("MASTERID", id),
                    new XElement("GUID", "branch-ledger-" + id));
            }
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        var foreign1 = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = SourceIdentities(a.Xml, "99123") }, b ]);
        var foreign2 = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = SourceIdentities(a.Xml, "99456") }, b ]);
        Check(foreign1.MastersXml == foreign2.MastersXml &&
              foreign1.VouchersXml == foreign2.VouchersXml &&
              foreign1.RebasedBranchAlterCount == 1 &&
              XDocument.Parse(foreign1.VouchersXml).Descendants("AMOUNT").Select(x => x.Value)
                  .Order().SequenceEqual(sourceAmounts) &&
              !foreign1.MastersXml.Contains("branch-ledger-") &&
              !foreign1.VouchersXml.Contains("branch-voucher-") &&
              !foreign1.VouchersXml.Contains("voucher-99123") &&
              !foreign1.VouchersXml.Contains("key-99123"),
              "Source Tally identity affected the consolidated voucher/master content.");

        var sourceInventory = WithStock(a.Xml)
            .Replace(@"<STOCKITEM NAME=""Rice"" ACTION=""Create"">",
                     @"<STOCKITEM NAME=""Rice"" ACTION=""Alter"" MASTERID=""815"">")
            .Replace("<BASEUNITS>QTL</BASEUNITS>",
                     "<MASTERID>815</MASTERID><GUID>stock-guid-815</GUID><BASEUNITS>QTL</BASEUNITS>");
        var foreignStock = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            a with { Xml = sourceInventory }, b with { Xml = WithStock(b.Xml) }]);
        Check(!foreignStock.MastersXml.Contains("stock-guid-815") &&
              !foreignStock.MastersXml.Contains("MASTERID") &&
              XDocument.Parse(foreignStock.MastersXml).Descendants("STOCKITEM")
                  .Any(x => (string?)x.Attribute("ACTION") == "Create"),
              "Branch stock MASTERID/Alter was imported instead of a new destination stock identity.");

        var customMasters = new MasterRecord[] {
            Ledger("Buyer", "Sundry Debtors"),
            Ledger("Local Sales", "Regional Sales"),
            new() { Name = "Regional Sales", Type = "Group", Parent = "Sales Accounts" }
        };
        var customBranch = new TallyPreparedBranchXml(Karnataka,
            a.Xml.Replace("<PARENT>Sales Accounts</PARENT>", "<PARENT>Regional Sales</PARENT>"),
            customMasters);
        var custom = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [customBranch, b]);
        var customDoc = XDocument.Parse(custom.MastersXml);
        Check(customDoc.Descendants("GROUP").Any(x =>
                  (string?)x.Element("NAME") == "Karnataka Regional Sales" &&
                  (string?)x.Element("PARENT") == "Karnataka Sales") &&
              customDoc.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Local Sales" &&
                  (string?)x.Element("PARENT") == "Karnataka Regional Sales"),
              "Custom source ledger classification lost its branch hierarchy.");

        // Regression: Mitansh v587 prepared all 296 Karnataka Bank rows but
        // consolidated XML stopped at "Circular or unknown branch accounting
        // group: Primary". The source Suspense Ledger is under Tally's built-in
        // Suspense A/c Group (itself under Primary), which is NOT a custom
        // Group that should be created under Primary in destination books.
        var suspenseBranch = new TallyPreparedBranchXml(Karnataka,
            Xml("Payment", "BANK-216", "BANK-REF-216",
                "Karnataka Bank", "Suspense", "-250.00", "250.00",
                "Bank Accounts", "Suspense A/c"),
            [
                Ledger("Karnataka Bank", "Bank Accounts"),
                Ledger("Suspense", "Suspense A/c"),
                new MasterRecord { Name = "Suspense A/c", Type = "Group", Parent = "Primary" }
            ]);
        var suspensePlan = Plan();
        Check(suspensePlan.Groups.Count(x =>
                  x.Parent.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase)) == 2 &&
              suspensePlan.Groups.Any(x => x.BranchId == "KA" &&
                  x.Name == "Karnataka Suspense A/c" && x.Create) &&
              XDocument.Parse(suspensePlan.GroupXml).Descendants("GROUP").Any(x =>
                  (string?)x.Element("NAME") == "Karnataka Suspense A/c" &&
                  (string?)x.Element("PARENT") == "Suspense A/c"),
            "Suspense A/c was not created by the ORIGINAL compulsory master-first branch plan.");
        var suspense = TallyConsolidatedPreparedXmlBuilder.Build(suspensePlan, [suspenseBranch, b]);
        var suspenseMasters = XDocument.Parse(suspense.MastersXml);
        var suspenseVouchers = XDocument.Parse(suspense.VouchersXml);
        Check(suspense.SourceVouchers == 2 && suspense.FinalVouchers == 2 &&
              suspenseMasters.Descendants("GROUP").Any(x =>
                  (string?)x.Element("NAME") == "Karnataka Suspense A/c" &&
                  (string?)x.Element("PARENT") == "Suspense A/c") &&
              suspenseMasters.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Suspense" &&
                  (string?)x.Element("PARENT") == "Karnataka Suspense A/c") &&
              !suspenseMasters.Descendants("GROUP").Any(x =>
                  (string?)x.Element("NAME") == "Primary") &&
              suspenseVouchers.Descendants("VOUCHER").Any(x =>
                  (string?)x.Element("VOUCHERNUMBER") == "KA-BANK-216") &&
              suspenseVouchers.Descendants("AMOUNT").Select(x => x.Value)
                  .Order().SequenceEqual(new[] { "-250.00", "250.00", "-200.10", "200.10" }.Order()) &&
              !suspense.MastersXml.Contains("MASTERID") &&
              !suspense.VouchersXml.Contains("MASTERID"),
            "Built-in Suspense A/c under Primary was not mapped to an isolated branch subgroup with unchanged original DR/CR.");

        // The same original hierarchy also supports saved custom subgroups
        // UNDER the canonical built-in group without losing nesting.
        var nestedSuspense = suspenseBranch with
        {
            Xml = suspenseBranch.Xml.Replace(
                "<PARENT>Suspense A/c</PARENT></LEDGER>",
                "<PARENT>Review Suspense</PARENT></LEDGER>"),
            Masters = [
                Ledger("Karnataka Bank", "Bank Accounts"),
                Ledger("Suspense", "Review Suspense"),
                new MasterRecord { Name = "Review Suspense", Type = "Group", Parent = "Suspense A/c" },
                new MasterRecord { Name = "Suspense A/c", Type = "Group", Parent = "Primary" }
            ]
        };
        var nestedXml = XDocument.Parse(TallyConsolidatedPreparedXmlBuilder.Build(
            Plan(), [nestedSuspense, b]).MastersXml);
        Check(nestedXml.Descendants("GROUP").Any(x =>
                  (string?)x.Element("NAME") == "Karnataka Review Suspense" &&
                  (string?)x.Element("PARENT") == "Karnataka Suspense A/c"),
            "Custom subgroup of a canonical Primary child lost its branch hierarchy.");

        // Unknown/custom Primary children require Tally's Group Nature;
        // neither the wrapper nor a fallback can invent that classification.
        var unsafeRoot = suspenseBranch with
        {
            Xml = suspenseBranch.Xml.Replace(
                "<PARENT>Suspense A/c</PARENT></LEDGER>",
                "<PARENT>Unverified Root Group</PARENT></LEDGER>"),
            Masters = [
                Ledger("Karnataka Bank", "Bank Accounts"),
                Ledger("Suspense", "Unverified Root Group"),
                new MasterRecord { Name = "Unverified Root Group", Type = "Group", Parent = "Primary" }
            ]
        };
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [unsafeRoot, b]),
            "Custom Group below Primary was assigned an invented Tally Group Nature.");

        var withCash = TallyBranchConsolidationMasterPlanner.Build(
            "Consolidated Books", "test-company", "2025-26", [Karnataka, TamilNadu], [],
            true, separateCashLedgers: true);
        var cashSource = Prepared(Karnataka,
            Xml("Receipt", "10", "Cash sale", "Cash", "Local Sales",
                "-10", "10", "Cash-in-Hand", "Sales Accounts"),
            Ledger("Cash", "Cash-in-Hand"), Ledger("Local Sales", "Sales Accounts"));
        var cashResult = TallyConsolidatedPreparedXmlBuilder.Build(withCash, [cashSource, b]);
        var cashMaster = XDocument.Parse(cashResult.MastersXml);
        Check(cashMaster.Descendants("LEDGER").Count(x =>
                (string?)x.Element("NAME") == "Cash (KA)") == 1 &&
              XDocument.Parse(cashResult.VouchersXml).Descendants("LEDGERNAME")
                  .Any(x => x.Value == "Cash (KA)"),
              "Optional branch Cash was duplicated instead of reusing planned master.");

        var outgoing = Prepared(Karnataka,
            Xml("Payment", "21", "AAH-INTERBANK:TRANSFER-10", "Bank", "Clearing", "-500.00", "500.00",
                "Bank Accounts", "Current Liabilities"),
            Ledger("Bank", "Bank Accounts"), Ledger("Clearing", "Current Liabilities"));
        var incoming = Prepared(TamilNadu,
            Xml("Receipt", "22", "AAH-INTERBANK:TRANSFER-10", "Bank TN", "Clearing TN", "500.00", "-500.00",
                "Bank Accounts", "Current Liabilities"),
            Ledger("Bank TN", "Bank Accounts"), Ledger("Clearing TN", "Current Liabilities"));
        var transfer = TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [outgoing, incoming]);
        Check(transfer.InterbankReconciled == 1 && transfer.SourceVouchers == 2 && transfer.FinalVouchers == 1,
            "Explicit interbank pair did not reconcile to one common Contra.");
        var bankDoc = XDocument.Parse(transfer.VouchersXml);
        Check(bankDoc.Descendants("AMOUNT").Select(x => x.Value).Order().SequenceEqual(
                  new[] { "-500.00", "500.00" }.Order()) &&
              bankDoc.Descendants("VOUCHERTYPENAME").Single().Value == "Contra",
            "Interbank reconciliation changed bank-side values or Contra type.");
        // Bank Ledger names shared across branches must be suffixed not only
        // in masters but also in the reconciled single common Contra lines.
        var matchingBankName = incoming with {
            Xml = incoming.Xml.Replace("Bank TN", "Bank"),
            Masters = [Ledger("Bank", "Bank Accounts"),
                Ledger("Clearing TN", "Current Liabilities")]
        };
        var sharedBanks = TallyConsolidatedPreparedXmlBuilder.Build(
            Plan(), [outgoing, matchingBankName]);
        var sharedBankMaster = XDocument.Parse(sharedBanks.MastersXml);
        var sharedBankVoucher = XDocument.Parse(sharedBanks.VouchersXml);
        Check(sharedBanks.InterbankReconciled == 1 &&
              sharedBankVoucher.Descendants("LEDGERNAME").Select(x => x.Value)
                  .ToHashSet().SetEquals(new[] { "Bank - Karnataka", "Bank - Tamil Nadu" }) &&
              sharedBankMaster.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Bank - Karnataka" &&
                  (string?)x.Element("PARENT") == "Karnataka Banks") &&
              sharedBankMaster.Descendants("LEDGER").Any(x =>
                  (string?)x.Element("NAME") == "Bank - Tamil Nadu" &&
                  (string?)x.Element("PARENT") == "Tamil Nadu Banks") &&
              sharedBankVoucher.Descendants("AMOUNT").Select(x => x.Value)
                  .Order().SequenceEqual(new[] { "-500.00", "500.00" }.Order()),
            "Common interbank Contra did not use shared-bank mapping without recalculating sides.");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [
            outgoing, incoming with { Xml = incoming.Xml.Replace("500.00", "499.00") }]),
            "mismatched interbank amount");
        Reject(() => TallyConsolidatedPreparedXmlBuilder.Build(Plan(), [outgoing, b]),
            "unmatched interbank reference");
        Console.WriteLine("PASS: branch-prepared XML, DR/CR immutability, group ledgers, explicit interbank and fail-closed identities.");
    }
}
