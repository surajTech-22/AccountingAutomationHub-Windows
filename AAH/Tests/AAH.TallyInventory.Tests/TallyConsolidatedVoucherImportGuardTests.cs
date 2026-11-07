using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// No live Tally is required: these cases must reject unsafe XML BEFORE any
/// HTTP exchange, so they also protect accidental button enablement.
/// </summary>
internal static class TallyConsolidatedVoucherImportGuardTests
{
    private static readonly TallyClientLink Link = new()
    {
        ClientId = 1, CompanyKey = "destination-key",
        CompanyName = "Combined Books", FinancialYear = "2025-26"
    };
    private static readonly TallyCompanyInfo Company = new()
    {
        CompanyKey = "destination-key", Name = "Combined Books",
        FinancialYear = "2025-26"
    };
    private static string Masters(string company = "Combined Books") => $"""
        <ENVELOPE><BODY><IMPORTDATA><REQUESTDESC><STATICVARIABLES>
        <SVCURRENTCOMPANY>{company}</SVCURRENTCOMPANY>
        </STATICVARIABLES></REQUESTDESC><REQUESTDATA>
        <TALLYMESSAGE><GROUP NAME="Branch Debtors" ACTION="Create">
        <NAME>Branch Debtors</NAME><PARENT>Sundry Debtors</PARENT></GROUP></TALLYMESSAGE>
        </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
        """;
    private static string Voucher(string number = "KA-1", string date = "20250505",
        string action = "Create", string extra = "", string postings = """
        <ALLLEDGERENTRIES.LIST><LEDGERNAME>Customer - KA</LEDGERNAME><AMOUNT>-100.25</AMOUNT></ALLLEDGERENTRIES.LIST>
        <ALLLEDGERENTRIES.LIST><LEDGERNAME>Sales - KA</LEDGERNAME><AMOUNT>100.25</AMOUNT></ALLLEDGERENTRIES.LIST>
        """) => $"""
        <TALLYMESSAGE><VOUCHER ACTION="{action}" VCHTYPE="Sales" {extra}>
        <DATE>{date}</DATE><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>
        <VOUCHERNUMBER>{number}</VOUCHERNUMBER>{postings}
        </VOUCHER></TALLYMESSAGE>
        """;
    private static TallyConsolidatedPreparedXmlResult Prepared(
        string vouchers, int count = 1, string? company = null) => new(
        Masters(company ?? "Combined Books"),
        $"""
        <ENVELOPE><BODY><IMPORTDATA><REQUESTDESC><STATICVARIABLES>
        <SVCURRENTCOMPANY>Combined Books</SVCURRENTCOMPANY>
        </STATICVARIABLES></REQUESTDESC><REQUESTDATA>
        {vouchers}
        </REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
        """,
        2, count, count, 0, "Test");

    private static void MustReject(TallyConsolidatedPreparedXmlResult data, string reason)
    {
        var writer = new TallyConsolidatedVoucherImportService(new TallyHttpService(port: 1));
        try
        {
            writer.ImportAsync(Link, Company, data).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe consolidated export passed local guard: " + reason);
    }

    public static void Run()
    {
        // CL019 2026-11-03: Tally CREATE returned LASTVCHID=2, but
        // diagnostic voucher index returned 0. On a later run a Tally-owned
        // auto-number must not authorize creating the same original invoice.
        var original = new (string Name, decimal Amount)[]
        {
            ("Creditor KA", -7.08m), ("Taxable KA", 6.00m), ("IGST KA", 1.08m)
        };
        var live = new TallyVoucherIndexRecord
        {
            MasterId = "2",
            AlterId = "12",
            VoucherDate = new DateTime(2026, 2, 28),
            VoucherType = "Debit Note",
            VoucherNumber = "TALLY-AUTO-002",
            Reference = "KA-C-26-1924192",
            Narration = "Imported from branch",
            LedgerPostings = new TallyLedgerPostingRecord[]
            {
                new() { LedgerName = "Creditor KA", SignedAmount = -7.08m },
                new() { LedgerName = "Taxable KA", SignedAmount = 6.00m },
                new() { LedgerName = "IGST KA", SignedAmount = 1.08m }
            }
        };
        static bool Match(TallyVoucherIndexRecord actual, string refNumber,
            IReadOnlyList<(string Name, decimal Amount)> posts) =>
            TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedPrepared(
                actual, new DateTime(2026, 2, 28), "Debit Note",
                refNumber, "Imported from branch", posts);
        if (!Match(live, "KA-C-26-1924192", original))
            throw new InvalidOperationException(
                "Existing normal Tally strict classifier failed to recognise live " +
                "auto-numbered same-source and complete DR/CR.");
        if (Match(live, "DIFFERENT-INVOICE", original) ||
            Match(live, "KA-C-26-1924192",
                new (string Name, decimal Amount)[]
                {
                    ("Creditor KA", -7.08m), ("Taxable KA", 6.00m),
                    ("IGST KA", 1.09m)
                }))
            throw new InvalidOperationException(
                "Tally strict classifier ignored different original reference " +
                "or altered the source GST posting.");
        var noMasterId = new TallyVoucherIndexRecord
        {
            VoucherDate = live.VoucherDate,
            VoucherType = live.VoucherType,
            VoucherNumber = live.VoucherNumber,
            Reference = live.Reference,
            Narration = live.Narration,
            LedgerPostings = live.LedgerPostings
        };
        if (Match(noMasterId, live.Reference, original))
            throw new InvalidOperationException(
                "A live voucher with no durable destination MASTERID " +
                "must never be confirmed exact.");
        Console.WriteLine("PASS: CL019 same-source Tally auto-number and signed DR/CR strict duplicate comparator.");

        // CL019 2026-11-07: NORMAL Tally Link already normalizes PARTYNAME
        // as PartyLedger and BASICNARRATION as Narration in its saved index.
        // The consolidated Previous->Desired wrapper MUST use those SAME
        // normal index values and the SAME native full-XML DR/CR reader.
        // Requiring only raw PARTYLEDGERNAME/NARRATION would reject this
        // perfectly unchanged linked voucher despite the same 359/382.
        const string normalGuid = "8784a1d3-991b-41fd-b05e-404565f76a87-00000167";
        const string normalFullXml = """
            <VOUCHER>
              <MASTERID>359</MASTERID><ALTERID>382</ALTERID>
              <GUID>8784a1d3-991b-41fd-b05e-404565f76a87-00000167</GUID>
              <DATE>20250531</DATE><VOUCHERTYPENAME>Payment</VOUCHERTYPENAME>
              <REFERENCE>BANK-REFERENCE-01</REFERENCE>
              <PARTYNAME>Original Supplier</PARTYNAME>
              <BASICNARRATION>Payment to original supplier</BASICNARRATION>
              <NATIVELEDGER.LIST>
                <LEDGERNAME>Original Supplier</LEDGERNAME><AMOUNT>-40000.00</AMOUNT>
                <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
              </NATIVELEDGER.LIST>
              <NATIVELEDGER.LIST>
                <LEDGERNAME>Bank Account</LEDGERNAME><AMOUNT>40000.00</AMOUNT>
                <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              </NATIVELEDGER.LIST>
            </VOUCHER>
            """;
        var originalDate = new DateTime(2025, 5, 31);
        var normalizedIndex = new TallyVoucherIndexRecord
        {
            MasterId = "359", AlterId = "382", Guid = normalGuid,
            VoucherDate = originalDate, VoucherType = "Payment",
            Reference = "BANK-REFERENCE-01", PartyLedger = "Original Supplier",
            Narration = "Payment to original supplier",
            LedgerPostings = new TallyLedgerPostingRecord[]
            {
                new() { LedgerName = "Original Supplier", SignedAmount = -40000m },
                new() { LedgerName = "Bank Account", SignedAmount = 40000m }
            }
        };
        var normalSnapshot = new TallyVoucherXmlSnapshot(
            "359", normalGuid, "Payment", originalDate, normalFullXml) { AlterId = "382" };
        var oldSigned = "Bank Account:40000.00|Original Supplier:-40000.00";
        var linkedHash = TallyVoucherFingerprint.BuildContentHash(
            originalDate, "Payment", "BANK-REFERENCE-01", "Original Supplier",
            40000m, "Payment to original supplier|" + oldSigned);
        var linkedVersion = "CONSOLIDATED|20250531|Payment|BANK-REFERENCE-01|" +
            oldSigned + "|" + linkedHash;
        var previousLinkedVersion = new TallySourceBindingVersionState
        {
            State = "Verified", MasterId = "359", Guid = normalGuid,
            VerifiedContentHash = linkedHash, VerifiedVersion = linkedVersion,
            LastVerifiedAlterId = 382
        };
        var nativeOldVersion = TallyConsolidatedVoucherImportService
            .ReadVerifiedLiveConsolidatedVersion(normalizedIndex, [normalSnapshot]);
        if (nativeOldVersion is null ||
            !nativeOldVersion.Value.Hash.Equals(linkedHash, StringComparison.OrdinalIgnoreCase) ||
            !TallyConsolidatedVoucherImportService.PreviousConsolidatedVersionMatches(
                normalizedIndex, [normalSnapshot], previousLinkedVersion))
            throw new InvalidOperationException(
                "Normal Tally Link PARTYNAME/BASICNARRATION normalization was not reused by " +
                "the consolidated wrapper; unchanged Payment 359/382 incorrectly blocked.");
        var changedLiveXml = normalFullXml.Replace(
            "<AMOUNT>-40000.00</AMOUNT>", "<AMOUNT>-40001.00</AMOUNT>", StringComparison.Ordinal);
        var changedXml = new TallyVoucherXmlSnapshot(
            "359", normalGuid, "Payment", originalDate, changedLiveXml) { AlterId = "382" };
        if (TallyConsolidatedVoucherImportService.ReadVerifiedLiveConsolidatedVersion(
                normalizedIndex, [changedXml]) is not null)
            throw new InvalidOperationException(
                "Consolidated wrapper accepted full Tally XML with changed signed DRCR.");
        var wrongAlter = new TallyVoucherXmlSnapshot(
            "359", normalGuid, "Payment", originalDate, normalFullXml) { AlterId = "383" };
        if (TallyConsolidatedVoucherImportService.ReadVerifiedLiveConsolidatedVersion(
                normalizedIndex, [wrongAlter]) is not null)
            throw new InvalidOperationException(
                "Consolidated wrapper accepted full Tally XML with changed native ALTERID.");
        var wrongGuidXml = normalFullXml.Replace(normalGuid,
            "different-native-voucher-guid", StringComparison.Ordinal);
        var wrongGuid = new TallyVoucherXmlSnapshot(
            "359", normalGuid, "Payment", originalDate, wrongGuidXml) { AlterId = "382" };
        if (TallyConsolidatedVoucherImportService.ReadVerifiedLiveConsolidatedVersion(
                normalizedIndex, [wrongGuid]) is not null)
            throw new InvalidOperationException(
                "Consolidated wrapper ignored changed GUID inside original full Voucher XML.");
        Console.WriteLine(
            "PASS: consolidated Previous->Desired uses original individual Tally normalized index " +
            "and native signed DRCR; PARTYNAME/BASICNARRATION aliases work; changed XML/ALTERID/GUID block.");


        // Another CL019 regression: 645 normal synchronized Voucher objects
        // had full signed XML, but the SQLite index did not persist the
        // individual posting objects. Exercise the EXACT normal Tally reader
        // for every row (including ERP9-compatible nonstandard entry tags).
        // No private customer books or diagnostic data are stored in CI.
        var oldBankDate = new DateTime(2025, 5, 31);
        var oldBankReference = "BANK-TRANSACTION-001";
        var oldBankNarration = "ORIGINAL PAYMENT";
        var oldBankXml = """
            <VOUCHER>
              <DATE>20250531</DATE>
              <VOUCHERTYPENAME>Payment</VOUCHERTYPENAME>
              <REFERENCE>BANK-TRANSACTION-001</REFERENCE>
              <PARTYLEDGERNAME></PARTYLEDGERNAME>
              <NARRATION>ORIGINAL PAYMENT</NARRATION>
              <NATIVELEDGER.LIST>
                <LEDGERNAME>Original Supplier</LEDGERNAME>
                <AMOUNT>-40000.00</AMOUNT><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
              </NATIVELEDGER.LIST>
              <NATIVELEDGER.LIST>
                <LEDGERNAME>Bank Account</LEDGERNAME>
                <AMOUNT>40000.00</AMOUNT><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              </NATIVELEDGER.LIST>
              <NATIVELEDGER.LIST>
                <LEDGERNAME>Ignored Zero Line</LEDGERNAME>
                <AMOUNT>0.00</AMOUNT>
              </NATIVELEDGER.LIST>
            </VOUCHER>
            """;
        var nativeDrcr = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "ReadNormalTallySignedPostings",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Consolidated did not link the original normal Tally DR/CR reader.");
        var oldBankNative = (IReadOnlyList<(string Name, decimal Amount)>)nativeDrcr.Invoke(null,
            [System.Xml.Linq.XElement.Parse(oldBankXml)])!;
        if (oldBankNative.Count != 2 ||
            oldBankNative[0] != ("Original Supplier", -40000m) ||
            oldBankNative[1] != ("Bank Account", 40000m))
            throw new InvalidOperationException(
                "Original normal Tally signed DR/CR parser did not accept ERP9 Ledger wrapper or ignored zero posting.");
        var oldBankGuid = "9cd81761-ef4a-4a10-b707-111111111111-00000167";
        var oldBankIndex = new List<TallyVoucherIndexRecord>(645);
        var oldBankFull = new List<TallyVoucherXmlSnapshot>(645);
        for (var i = 1; i <= 645; i++)
        {
            oldBankIndex.Add(new TallyVoucherIndexRecord {
                MasterId = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AlterId = "382", Guid = oldBankGuid,
                VoucherDate = oldBankDate, VoucherType = "Payment",
                Reference = oldBankReference, VoucherNumber = i.ToString(),
                PartyLedger = "", Narration = oldBankNarration,
                DebitLedgers = "Original Supplier", CreditLedgers = "Bank Account",
                Amount = 40000m, LedgerPostings = Array.Empty<TallyLedgerPostingRecord>()
            });
            oldBankFull.Add(new TallyVoucherXmlSnapshot(
                i.ToString(), oldBankGuid, "Payment", oldBankDate, oldBankXml)
                { AlterId = "382" });
        }
        var hydrateAll = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "HydrateSynchronizedVoucherPostings",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original synchronized FY full XML materializer missing.");
        var materialized = (IReadOnlyList<TallyVoucherIndexRecord>)hydrateAll.Invoke(null,
            [oldBankIndex, oldBankFull])!;
        if (materialized.Count != 645 ||
            materialized.Any(v => v.LedgerPostings.Count != 2))
            throw new InvalidOperationException(
                "Full 645-Voucher synchronized DR/CR batch was not populated before consolidated matching.");
        var oldSignedBank = "Bank Account:40000.00|Original Supplier:-40000.00";
        var oldHashBank = TallyVoucherFingerprint.BuildContentHash(
            oldBankDate, "Payment", oldBankReference, "",
            40000m, oldBankNarration + "|" + oldSignedBank);
        var oldVersionBank = "CONSOLIDATED|20250531|Payment|" + oldBankReference +
            "|" + oldSignedBank + "|" + oldHashBank;
        var boundBank = new TallySourceBindingVersionState {
            State = "Verified", MasterId = "359", Guid = oldBankGuid,
            VerifiedContentHash = oldHashBank,
            VerifiedVersion = oldVersionBank, LastVerifiedAlterId = 382
        };
        var knownIndex = new TallyVoucherIndexRecord {
            MasterId = "359", AlterId = "382", Guid = oldBankGuid,
            VoucherDate = oldBankDate, VoucherType = "Payment", VoucherNumber = "2",
            Reference = oldBankReference, PartyLedger = "", Narration = oldBankNarration,
            DebitLedgers = "Original Supplier", CreditLedgers = "Bank Account",
            Amount = 40000m, LedgerPostings = materialized[0].LedgerPostings
        };
        var knownFull = new[] {
            new TallyVoucherXmlSnapshot(
                "359", oldBankGuid, "Payment", oldBankDate, oldBankXml)
                { AlterId = "382" }
        };
        var oldProof = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "PreviousConsolidatedVersionMatches",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original previous-voucher proof missing.");
        bool ProvesOld() => (bool)oldProof.Invoke(null,
            [knownIndex, knownFull, boundBank])!;
        if (!ProvesOld()) throw new InvalidOperationException(
            "CL019 old full DRCR could not verify same 359/382 through the normal native Tally ledger parser.");
        boundBank.LastVerifiedAlterId = 383;
        if (ProvesOld()) throw new InvalidOperationException(
            "Previous signed DRCR proof ignored a different native Tally ALTERID.");
        boundBank.LastVerifiedAlterId = 382;
        boundBank.Guid = "different-tally-book";
        if (ProvesOld()) throw new InvalidOperationException(
            "Previous signed DRCR proof accepted different destination Voucher GUID.");
        boundBank.Guid = oldBankGuid;
        var changedDesiredBankHash = TallyVoucherFingerprint.BuildContentHash(
            oldBankDate, "Payment", oldBankReference, "", 40000m,
            oldBankNarration + "|Bank Account:40000.00|Renamed Supplier:-40000.00");
        if (changedDesiredBankHash.Equals(oldHashBank, StringComparison.OrdinalIgnoreCase) ||
            !ProvesOld())
            throw new InvalidOperationException(
                "A new accounting Ledger did not retain original bound Previous->Desired ALTER authority.");
        DirectoryInfo? boundPreflightRoot = new(Environment.CurrentDirectory);
        while (boundPreflightRoot is not null &&
               !File.Exists(Path.Combine(boundPreflightRoot.FullName, "AAH", "src",
                   "AccountingAutomationHub.Infrastructure", "Tally",
                   "TallyConsolidatedVoucherImportService.cs")))
            boundPreflightRoot = boundPreflightRoot.Parent;
        if (boundPreflightRoot is null)
            throw new InvalidOperationException("Original consolidated source checkout missing.");
        var sourceCode = File.ReadAllText(Path.Combine(
            boundPreflightRoot.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally",
            "TallyConsolidatedVoucherImportService.cs"));
        if (!sourceCode.Contains("var preflightBoundConflicts = new List<string>()", StringComparison.Ordinal) ||
            !sourceCode.Contains("preflightBoundConflicts.Add(", StringComparison.Ordinal) ||
            !sourceCode.Contains("if (preflightBoundConflicts.Count > 0)", StringComparison.Ordinal) ||
            !sourceCode.Contains("No Tally Voucher CREATE or ALTER was sent.", StringComparison.Ordinal) ||
            !sourceCode.Contains("expected.ContentHash, expected.DesiredVersion,", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "645-voucher pre-write conflict audit or canonical source hash persistence missing.");
        Console.WriteLine(
            "PASS: original native Tally DRCR for 645 Vouchers, exact old 359/382 " +
            "Previous->Desired proof, zero-value/wrapper tolerance, conflicting ALT/GUID block, " +
            "all-bound pre-write classification and canonical AAH source hash persistence.");


        // CL019 2026-11-06 v621: NORMAL Tally synchronization persists the
        // index without individual LedgerPostings and stores complete XML in
        // a separate authority table. Consolidated MUST join both by exact
        // MASTERID/ALTERID before applying the ordinary strict classifier.
        const string debitNoteGuid = "8784a1d3-991b-41fd-b05e-404565f76a87-00000005";
        const string debitNoteFull = """
            <VOUCHER>
              <MASTERID>5</MASTERID><ALTERID>7</ALTERID>
              <GUID>8784a1d3-991b-41fd-b05e-404565f76a87-00000005</GUID>
              <DATE>20260228</DATE><VOUCHERTYPENAME>Debit Note</VOUCHERTYPENAME>
              <VOUCHERNUMBER>3</VOUCHERNUMBER>
              <REFERENCE>KA-C-26-1924192</REFERENCE>
              <PARTYLEDGERNAME>Amazon Seller Services Pvt Ltd</PARTYLEDGERNAME>
              <NARRATION>Debit Note KA-C-26-1924192</NARRATION>
              <ALLLEDGERENTRIES.LIST>
                <LEDGERNAME>Amazon Seller Services Pvt Ltd</LEDGERNAME>
                <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-7.08</AMOUNT>
              </ALLLEDGERENTRIES.LIST>
              <ALLLEDGERENTRIES.LIST>
                <LEDGERNAME>Purchase @ 18% - Gujarat</LEDGERNAME>
                <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>6.00</AMOUNT>
              </ALLLEDGERENTRIES.LIST>
              <ALLLEDGERENTRIES.LIST>
                <LEDGERNAME>Input IGST @ 18% - Gujarat</LEDGERNAME>
                <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>1.08</AMOUNT>
              </ALLLEDGERENTRIES.LIST>
            </VOUCHER>
            """;
        var debitIndex = new TallyVoucherIndexRecord
        {
            MasterId = "5", AlterId = "7", Guid = debitNoteGuid,
            VoucherDate = new DateTime(2026, 2, 28),
            VoucherType = "Debit Note", VoucherNumber = "3",
            Reference = "KA-C-26-1924192",
            PartyLedger = "Amazon Seller Services Pvt Ltd",
            DebitLedgers = "Amazon Seller Services Pvt Ltd",
            CreditLedgers = "Purchase @ 18% - Gujarat | Input IGST @ 18% - Gujarat",
            Amount = 7.08m, Narration = "Debit Note KA-C-26-1924192",
            ContentHash = "INDEX-ONLY-NOT-SIGNED-SOURCE-HASH",
            LedgerPostings = Array.Empty<TallyLedgerPostingRecord>()
        };
        var debitXml = new TallyVoucherXmlSnapshot(
            "5", debitNoteGuid, "Debit Note", debitIndex.VoucherDate, debitNoteFull)
            { AlterId = "7" };
        var hydrate = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "HydrateSynchronizedVoucherPostings",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing reuse of normal synchronized full Voucher DR/CR.");
        IReadOnlyList<TallyVoucherIndexRecord> Hydrate(
            IReadOnlyList<TallyVoucherIndexRecord> indexes,
            IReadOnlyList<TallyVoucherXmlSnapshot> snapshots) =>
            (IReadOnlyList<TallyVoucherIndexRecord>)hydrate.Invoke(null, [indexes, snapshots])!;
        var hydratedDebit = Hydrate(new[] { debitIndex }, new[] { debitXml }).Single();
        var debitPostings = new (string Name, decimal Amount)[] {
            ("Amazon Seller Services Pvt Ltd", -7.08m),
            ("Purchase @ 18% - Gujarat", 6.00m),
            ("Input IGST @ 18% - Gujarat", 1.08m)
        };
        if (hydratedDebit.LedgerPostings.Count != 3 ||
            !TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedPrepared(
                hydratedDebit, debitIndex.VoucherDate!.Value, debitIndex.VoucherType,
                debitIndex.Reference, debitIndex.Narration, debitPostings))
            throw new InvalidOperationException(
                "Previously accepted CL019 Debit Note MASTERID 5 was not recognized from existing synchronized full DR/CR.");
        if (TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedPrepared(
                hydratedDebit, debitIndex.VoucherDate!.Value, debitIndex.VoucherType,
                debitIndex.Reference, debitIndex.Narration,
                debitPostings.Select(x => x.Name == "Input IGST @ 18% - Gujarat"
                    ? (x.Name, 1.09m) : x).ToArray()))
            throw new InvalidOperationException(
                "Hydrated Tally DR/CR ignored an actual input tax posting change.");
        var (savedSourceHash, originalSigned) =
            TallyConsolidatedVoucherImportService.ComputePreparedContentIdentity(
                System.Xml.Linq.XElement.Parse(debitNoteFull));
        var originalBinding = new TallySourceBindingVersionState {
            State = "Verified", MasterId = "5", Guid = debitNoteGuid,
            VerifiedContentHash = savedSourceHash,
            VerifiedVersion = "CONSOLIDATED|20260228|Debit Note|" +
                debitIndex.Reference + "|" + originalSigned + "|" + savedSourceHash,
            LastVerifiedAlterId = 7
        };
        var unchanged = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "IsVerifiedUnchangedBoundSource",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original source-hash + bound MASTERID/ALTERID no-op proof missing.");
        bool NoOp(TallySourceBindingVersionState binding, string hash) =>
            (bool)unchanged.Invoke(null, [binding, hydratedDebit, hash])!;
        if (!NoOp(originalBinding, savedSourceHash))
            throw new InvalidOperationException(
                "Same CL019 source hash, bound MASTERID 5, ALTERID 7 and GUID was not skipped immediately.");
        if (NoOp(originalBinding, new string('F', 64)))
            throw new InvalidOperationException(
                "Changed source hash was skipped using MASTERID/ALTERID without comparing previous DR/CR.");
        originalBinding.LastVerifiedAlterId = 8;
        if (NoOp(originalBinding, savedSourceHash))
            throw new InvalidOperationException(
                "Changed external ALTERID was ignored for an unchanged prepared source.");
        originalBinding.LastVerifiedAlterId = 7;
        originalBinding.Guid = "different-book-guid";
        if (NoOp(originalBinding, savedSourceHash))
            throw new InvalidOperationException(
                "Different destination GUID was silently adopted by unchanged fast path.");
        originalBinding.Guid = debitNoteGuid;
        var prev = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "PreviousConsolidatedVersionMatches",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original previous->desired DR/CR verification missing.");
        if (!(bool)prev.Invoke(null, [hydratedDebit, new[] { debitXml }, originalBinding])!)
            throw new InvalidOperationException(
                "Actual previous signed DR/CR readback did not match verified CL019 Debit Note.");
        var tamperedXml = new TallyVoucherXmlSnapshot(
            "5", debitNoteGuid, "Debit Note", debitIndex.VoucherDate, debitNoteFull)
            { AlterId = "8" };
        try {
            Hydrate(new[] { debitIndex }, new[] { tamperedXml });
            throw new InvalidOperationException(
                "Different synchronized ALTERID incorrectly allowed Ledger hydration.");
        } catch (System.Reflection.TargetInvocationException ex)
            when (ex.InnerException is InvalidOperationException) { }
        Console.WriteLine("PASS: all synchronized Tally Vouchers hydrate exact signed DR/CR; unchanged same-source MASTERID/ALTERID skips; changed source goes to original previous DRCR gate.");


        // CL019 2026-11-03 13:42:40: the 333rd CREATE had LASTVCHID
        // 337, while earlier branch voucher 26 shared the SAME original
        // B2CS-202601-24-18-2 reference and date/type. Reference-only
        // matching falsely found 2 and quarantined a successful Tally CREATE.
        var b2cDate = new DateTime(2026, 1, 31);
        const string b2cReference = "B2CS-202601-24-18-2";
        const string earlierBranchNumber =
            "B-92E9C2807A128C8B141395FA-B2CS-202601-24-18-2";
        const string acceptedBranchNumber =
            "B-BFF10AAEA9D095199C45F469-B2CS-202601-24-18-2";
        var acceptedPostings = new (string Name, decimal Amount)[]
        {
            ("B2C Sales - 24 - Karnataka", -49499.82m),
            ("Sales @ 18% - Karnataka", 41949.00m),
            ("Output IGST @ 18% - Karnataka", 7550.82m)
        };
        var unrelatedPostings = new (string Name, decimal Amount)[]
        {
            ("B2C Sales - 24 - Gujarat", -11120.32m),
            ("Sales @ 18% - Gujarat", 9424.00m),
            ("Output CGST @ 9% - Gujarat", 848.16m),
            ("Output SGST @ 9% - Gujarat", 848.16m)
        };
        static TallyVoucherIndexRecord B2cIndex(string id, string number,
            IReadOnlyList<(string Name, decimal Amount)> postings) => new()
        {
            MasterId = id, AlterId = "231", VoucherDate = new DateTime(2026, 1, 31),
            VoucherType = "Sales", VoucherNumber = number,
            Reference = "B2CS-202601-24-18-2",
            Narration = "Sales B2CS-202601-24-18-2",
            LedgerPostings = postings.Select(x => new TallyLedgerPostingRecord
            {
                LedgerName = x.Name, SignedAmount = x.Amount
            }).ToArray()
        };
        var earlier = B2cIndex("26", earlierBranchNumber, unrelatedPostings);
        var accepted = B2cIndex("337", acceptedBranchNumber, acceptedPostings);
        var sameReferenceCandidates = new[] { earlier, accepted };
        if (sameReferenceCandidates.Count(x =>
                x.VoucherType == "Sales" && x.VoucherDate == b2cDate &&
                x.Reference == b2cReference) != 2)
            throw new InvalidOperationException("CL019 two-branch reference collision fixture is invalid.");
        var exact = sameReferenceCandidates.Where(x =>
            TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedPrepared(
                x, b2cDate, "Sales", b2cReference,
                "Sales B2CS-202601-24-18-2", acceptedPostings)).ToArray();
        if (exact.Length != 1 || exact[0].MasterId != "337")
            throw new InvalidOperationException(
                "Same B2C reference across branches matched wrong destination MASTERID or DR/CR.");
        var differentScopes = typeof(TallyConsolidatedVoucherImportService)
            .GetMethod("HasDifferentPreparedBranchScopes",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Consolidated branch-scope classifier is missing.");
        bool DistinctScopes(string a, string b) =>
            (bool)differentScopes.Invoke(null, new object[] { a, b })!;
        if (!DistinctScopes(earlierBranchNumber, acceptedBranchNumber) ||
            DistinctScopes(acceptedBranchNumber, acceptedBranchNumber) ||
            DistinctScopes("TALLY-AUTO-337", acceptedBranchNumber))
            throw new InvalidOperationException(
                "Only two distinct proven prepared branch scopes may share a source reference; " +
                "unscoped or same-branch conflicts must stay blocked.");
        Console.WriteLine(
            "PASS: CL019 MASTERID 337 selected over same-reference branch MASTERID 26 " +
            "only by exact branch-signed DR/CR; no resend or unsafe cross-branch rebind.");

        // The new user-requested recovery must ONLY Export fresh live
        // Tally vouchers and confirm existing AcceptedAwaitingVerification
        // source bindings. Never permit an ImportXml POST in that method.
        DirectoryInfo? sourceRoot = new(Environment.CurrentDirectory);
        while (sourceRoot is not null &&
               !File.Exists(Path.Combine(sourceRoot.FullName, "AAH", "src",
                   "AccountingAutomationHub.Infrastructure", "Tally",
                   "TallyConsolidatedVoucherImportService.cs")))
            sourceRoot = sourceRoot.Parent;
        if (sourceRoot is null)
            throw new InvalidOperationException("Consolidated writer source missing.");
        var importerSource = File.ReadAllText(Path.Combine(
            sourceRoot.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally",
            "TallyConsolidatedVoucherImportService.cs"));
        var readonlyStart = importerSource.IndexOf(
            "public async Task<int> ReconcileAcceptedReadOnlyAsync(", StringComparison.Ordinal);
        var normalStart = importerSource.IndexOf(
            "public async Task<TallyConsolidatedVoucherImportResult> ImportAsync(", StringComparison.Ordinal);
        if (readonlyStart <= 0 || normalStart <= readonlyStart)
            throw new InvalidOperationException("Read-only accepted reconciliation route is missing.");
        var readOnlyBody = importerSource.Substring(readonlyStart, normalStart - readonlyStart);
        if (readOnlyBody.Contains("ImportWithEvidenceAsync(", StringComparison.Ordinal) ||
            readOnlyBody.Contains("ImportXmlAsync(", StringComparison.Ordinal) ||
            !readOnlyBody.Contains("AcceptedAwaitingVerification", StringComparison.Ordinal) ||
            !readOnlyBody.Contains("FullVoucherMatches(", StringComparison.Ordinal) ||
            !readOnlyBody.Contains("candidate.MasterId, item.AcknowledgedMasterId", StringComparison.Ordinal) ||
            !readOnlyBody.Contains("state.PendingContentHash", StringComparison.Ordinal) ||
            !readOnlyBody.Contains("RequireUniqueLiveDestination(", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Accepted-voucher read-only reconciliation can mutate Tally or ignore original MASTERID/DRCR/version gates.");
        Console.WriteLine("PASS: CL019 accepted voucher recovery is read-only and original-binding exact MASTERID/DRCR gated.");
        // CL019 B2CS-202601-29-18-1: two branches, one already-imported
        // voucher (MASTERID 27, Tally auto-number 3). The second branch
        // may proceed only if the first one is PROVEN owned by another
        // persisted consolidated source anchor at that same MASTERID.
        if (!importerSource.Contains("IsAlreadyOwnedByOtherVerifiedBranch(", StringComparison.Ordinal) ||
            !importerSource.Contains(
                "IgnoreCase.Equals(otherBinding.MasterId, live.MasterId)", StringComparison.Ordinal) ||
            !importerSource.Contains(
                "otherBinding.State.Equals(\"Verified\"", StringComparison.Ordinal) ||
            !importerSource.Contains("exactMatches.Any(candidate =>", StringComparison.Ordinal) ||
            !importerSource.Contains(
                "!IsAlreadyOwnedByOtherVerifiedBranch(live, planned)", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "CL019 auto-numbered shared B2C reference is still classified " +
                "without original AAH source-to-MASTERID authority.");
        Console.WriteLine(
            "PASS: CL019 other branch ownership requires its original verified " +
            "source binding; same-ref auto-number does not authorize cross-branch rebind.");


        // CL019: user changed the accounting ledger IN AAH after the
        // original consolidated voucher was verified in Tally. The correct
        // operation is the established bound MASTERID Previous->Desired ALTER,
        // never a second CREATE or a new posting/calculation engine.
        var preparedAlterSource = System.Xml.Linq.XElement.Parse(Voucher(
            postings: """
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>B2C Sales - Gujarat</LEDGERNAME><AMOUNT>-11120.32</AMOUNT></ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>Sales @ 18% - Gujarat</LEDGERNAME><AMOUNT>9424.00</AMOUNT></ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output CGST @ 9% - Gujarat</LEDGERNAME><AMOUNT>848.16</AMOUNT></ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output SGST @ 9% - Gujarat</LEDGERNAME><AMOUNT>848.16</AMOUNT></ALLLEDGERENTRIES.LIST>
            """).Replace("<TALLYMESSAGE>", "").Replace("</TALLYMESSAGE>", ""));
        var boundAlterXml = TallyAccountingVoucherExportService.BuildBoundConsolidatedVoucherAlterXml(
            "Combined Books", preparedAlterSource, "26", "2");
        var alterDocument = System.Xml.Linq.XDocument.Parse(boundAlterXml);
        var alteredVoucher = alterDocument.Descendants("VOUCHER").Single();
        if (!string.Equals((string?)alteredVoucher.Attribute("ACTION"), "Alter", StringComparison.Ordinal) ||
            !string.Equals((string?)alteredVoucher.Attribute("TAGNAME"), "MASTER ID", StringComparison.Ordinal) ||
            !string.Equals((string?)alteredVoucher.Attribute("TAGVALUE"), "26", StringComparison.Ordinal) ||
            alteredVoucher.Element("VOUCHERNUMBER")?.Value != "2" ||
            alteredVoucher.Elements("ALLLEDGERENTRIES.LIST").Count() != 4 ||
            alteredVoucher.Descendants("LEDGERNAME").First().Value != "B2C Sales - Gujarat" ||
            alteredVoucher.Descendants("AMOUNT").First().Value != "-11120.32")
            throw new InvalidOperationException(
                "Consolidated ledger edit did not reuse the normal bound-MASTERID ALTER writer " +
                "and preserve the branch's original signed DR/CR and Tally-owned number.");
        // Optional separate destination voucher types MUST reuse the original
        // source anchor and original destination MASTERID: 645 already-live
        // CL019 vouchers must ALTER, never enter a new CREATE queue.
        var separateTypes = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Sales - Karnataka"] = "Sales",
            ["Purchase - Gujarat"] = "Purchase",
            ["Payment - Karnataka"] = "Payment"
        };
        var oldSalesAnchor = TallyConsolidatedVoucherImportService
            .BuildConsolidatedSourceAnchor("Sales", "KA-INV-001");
        var branchSalesAnchor = TallyConsolidatedVoucherImportService
            .BuildConsolidatedSourceAnchor(
                "Sales - Karnataka", "KA-INV-001", separateTypes);
        if (!string.Equals(oldSalesAnchor, branchSalesAnchor, StringComparison.Ordinal) ||
            !string.Equals(oldSalesAnchor,
                TallyConsolidatedVoucherImportService.BuildConsolidatedSourceAnchor(
                    "Sales", "KA-INV-001", separateTypes), StringComparison.Ordinal) ||
            !string.Equals(
                TallyConsolidatedVoucherImportService.BuildConsolidatedSourceAnchor(
                    "Payment", "KA-BANK-123"),
                TallyConsolidatedVoucherImportService.BuildConsolidatedSourceAnchor(
                    "Payment - Karnataka", "KA-BANK-123", separateTypes),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Optional branch voucher type changed the durable source binding; duplicate CREATE risk.");
        var desiredBranchType = new System.Xml.Linq.XElement(preparedAlterSource);
        desiredBranchType.Element("VOUCHERTYPENAME")!.Value = "Sales - Karnataka";
        desiredBranchType.SetAttributeValue("VCHTYPE", "Sales - Karnataka");
        var branchTypeAlter = System.Xml.Linq.XDocument.Parse(
            TallyAccountingVoucherExportService.BuildBoundConsolidatedVoucherAlterXml(
                "Combined Books", desiredBranchType, "26", "2"))
            .Descendants("VOUCHER").Single();
        if (!string.Equals((string?)branchTypeAlter.Attribute("ACTION"),
                "Alter", StringComparison.Ordinal) ||
            !string.Equals((string?)branchTypeAlter.Attribute("TAGVALUE"),
                "26", StringComparison.Ordinal) ||
            !string.Equals((string?)branchTypeAlter.Attribute("VCHTYPE"),
                "Sales - Karnataka", StringComparison.Ordinal) ||
            branchTypeAlter.Element("VOUCHERTYPENAME")?.Value != "Sales - Karnataka" ||
            branchTypeAlter.Element("VOUCHERNUMBER")?.Value != "2" ||
            !branchTypeAlter.Descendants("AMOUNT").Select(e => e.Value).SequenceEqual(
                preparedAlterSource.Descendants("AMOUNT").Select(e => e.Value)) ||
            !branchTypeAlter.Descendants("LEDGERNAME").Select(e => e.Value).SequenceEqual(
                preparedAlterSource.Descendants("LEDGERNAME").Select(e => e.Value)))
            throw new InvalidOperationException(
                "Separate destination voucher type did not preserve bound MASTERID and full DR/CR.");
        // Unticking the same UI option must use precisely the same bound
        // MASTERID ALTER writer in reverse, with the original parent type
        // restored and no change to Tally's current voucher number or DR/CR.
        var restoredParent = new System.Xml.Linq.XElement(desiredBranchType);
        restoredParent.Element("VOUCHERTYPENAME")!.Value = "Sales";
        restoredParent.SetAttributeValue("VCHTYPE", "Sales");
        var restoreXml = System.Xml.Linq.XDocument.Parse(
            TallyAccountingVoucherExportService.BuildBoundConsolidatedVoucherAlterXml(
                "Combined Books", restoredParent, "26", "2"));
        var restoredVoucher = restoreXml.Descendants("VOUCHER").Single();
        if ((string?)restoredVoucher.Attribute("ACTION") != "Alter" ||
            (string?)restoredVoucher.Attribute("TAGVALUE") != "26" ||
            (string?)restoredVoucher.Attribute("VCHTYPE") != "Sales" ||
            restoredVoucher.Element("VOUCHERTYPENAME")?.Value != "Sales" ||
            restoredVoucher.Element("VOUCHERNUMBER")?.Value != "2" ||
            !restoredVoucher.Descendants("AMOUNT").Select(e => e.Value).SequenceEqual(
                desiredBranchType.Descendants("AMOUNT").Select(e => e.Value)) ||
            !restoredVoucher.Descendants("LEDGERNAME").Select(e => e.Value).SequenceEqual(
                desiredBranchType.Descendants("LEDGERNAME").Select(e => e.Value)))
            throw new InvalidOperationException(
                "Unchecking separate voucher types did not restore the parent using " +
                "the same bound-MASTERID ALTER while preserving the full old DR/CR.");
        if (TallyConsolidatedVoucherImportService
                .ComputePreparedContentIdentity(desiredBranchType).ContentHash ==
            TallyConsolidatedVoucherImportService
                .ComputePreparedContentIdentity(restoredParent).ContentHash)
            throw new InvalidOperationException(
                "Changing only voucher type failed to stage a new desired version for ALTER.");
        if (!importerSource.Contains(
                "type, number, prepared.VoucherTypeBaseByDestination", StringComparison.Ordinal) ||
            !importerSource.Contains(
                "The consolidated branch voucher type '", StringComparison.Ordinal) ||
            !importerSource.Contains(
                "PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) ||
            !importerSource.Contains(
                "alterResponse.Altered != 1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Separate voucher type bypasses master-first or existing bound ALTER safety gate.");
        Console.WriteLine("PASS: optional branch voucher-type toggle uses same source MASTERID, " +
                          "original source anchor and signed DR/CR under normal bound ALTER.");

        var alterWriterSource = File.ReadAllText(Path.Combine(
            sourceRoot!.FullName, "AAH", "src",
            "AccountingAutomationHub.Infrastructure", "Tally",
            "TallyAccountingVoucherExportService.cs"));
        var alterWriterStart = alterWriterSource.IndexOf(
            "public static string BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal);
        var normalWriterStart = alterWriterSource.IndexOf(
            "private static void BeginVoucherAlter(", alterWriterStart, StringComparison.Ordinal);
        if (alterWriterStart < 0 || normalWriterStart < alterWriterStart ||
            !alterWriterSource.Substring(alterWriterStart, normalWriterStart - alterWriterStart)
                .Contains("BeginVoucherAlter(", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Consolidated ledger edit bypasses the original normal Tally ALTER XML writer.");
        if (!importerSource.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal) ||
            !importerSource.Contains("StageSourceBindingChange(", StringComparison.Ordinal) ||
            !importerSource.Contains("BuildBoundConsolidatedVoucherAlterXml(", StringComparison.Ordinal) ||
            !importerSource.Contains("ConfirmedAlter", StringComparison.OrdinalIgnoreCase) ||
            !importerSource.Contains("AcceptedAwaitingVerification", StringComparison.Ordinal) ||
            !importerSource.Contains("alterResponse.Altered != 1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Consolidated changed ledger bypasses normal previous-version/accepted/live verification.");
        Console.WriteLine("PASS: consolidated changed ledger uses original bound MASTERID ALTER " +
            "with previous DRCR/ALTERID, accepted-write barrier and post-write verification.");
        // Same actual destination voucher BEFORE the user renamed its
        // assigned debtor ledger in the branch. AAH saved its previous exact
        // source version + content hash + destination MASTERID/ALTERID.
        // Reconstruct from the old full Tally XML; never invent a prior value
        // from the new branch voucher being exported.
        var oldPostings = new (string Name, decimal Amount)[]
        {
            ("B2C Sales - 24 - Gujarat", -11120.32m),
            ("Sales @ 18% - Gujarat", 9424m),
            ("Output CGST @ 9% - Gujarat", 848.16m),
            ("Output SGST @ 9% - Gujarat", 848.16m)
        };
        var oldIndex = new TallyVoucherIndexRecord
        {
            MasterId = "26", AlterId = "28", VoucherType = "Sales",
            VoucherDate = new DateTime(2026, 1, 31),
            VoucherNumber = "2", Reference = "B2CS-202601-24-18-2",
            PartyLedger = "B2C Sales - 24 - Gujarat",
            Narration = "Sales B2CS-202601-24-18-2",
            LedgerPostings = oldPostings.Select(posting =>
                new TallyLedgerPostingRecord
                {
                    LedgerName = posting.Name, SignedAmount = posting.Amount
                }).ToArray()
        };
        var previousXml = new System.Xml.Linq.XElement("VOUCHER",
            new System.Xml.Linq.XElement("DATE", "20260131"),
            new System.Xml.Linq.XElement("VOUCHERTYPENAME", "Sales"),
            new System.Xml.Linq.XElement("REFERENCE", oldIndex.Reference),
            new System.Xml.Linq.XElement("PARTYLEDGERNAME", oldIndex.PartyLedger),
            new System.Xml.Linq.XElement("NARRATION", oldIndex.Narration),
            oldPostings.Select(p => new System.Xml.Linq.XElement(
                "ALLLEDGERENTRIES.LIST",
                new System.Xml.Linq.XElement("LEDGERNAME", p.Name),
                new System.Xml.Linq.XElement("AMOUNT",
                    p.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))));
        var fullOld = new[] {
            new TallyVoucherXmlSnapshot("26", "old-guid", "Sales",
                oldIndex.VoucherDate, previousXml.ToString(
                    System.Xml.Linq.SaveOptions.DisableFormatting))
        };
        var oldSigned = string.Join("|", oldPostings
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Amount).Select(x => x.Name + ":" +
                x.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        var previousHash = TallyVoucherFingerprint.BuildContentHash(
            oldIndex.VoucherDate, oldIndex.VoucherType, oldIndex.Reference,
            oldIndex.PartyLedger, 11120.32m, oldIndex.Narration + "|" + oldSigned);
        var previousVersion = "CONSOLIDATED|20260131|Sales|" +
            oldIndex.Reference + "|" + oldSigned + "|" + previousHash;
        var oldBinding = new AccountingAutomationHub.Domain.Tally.TallySourceBindingVersionState
        {
            State = "Verified", MasterId = "26", VerifiedContentHash = previousHash,
            VerifiedVersion = previousVersion, LastVerifiedAlterId = 28
        };
        var verifier = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "PreviousConsolidatedVersionMatches",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original previous-version verification gate missing.");
        bool OldMatches(
            TallyVoucherIndexRecord oldLive,
            AccountingAutomationHub.Domain.Tally.TallySourceBindingVersionState binding,
            IReadOnlyList<TallyVoucherXmlSnapshot>? xml = null) =>
            (bool)verifier.Invoke(null, new object[] {
                oldLive, xml ?? fullOld, binding })!;
        if (!OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Original AAH-saved PreviousVersion + full DR/CR is not accepted for normal ALTER.");
        oldIndex.AlterId = "29";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "External Tally ALTERID change was allowed to overwrite previous version.");
        oldIndex.AlterId = "28";
        oldBinding.VerifiedContentHash = "WRONG";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Incorrect previous content fingerprint authorized bound ALTER.");
        // AAH previously saved the verified REQUEST fingerprint while Tally's
        // full readback can serialize the same voucher differently. A saved,
        // self-consistent previous version and identical live MASTERID/ALTERID
        // must still enter the normal bound ALTER path, without reassigning ID.
        var savedRequestHash = new string('A', 64);
        oldBinding.VerifiedContentHash = savedRequestHash;
        oldBinding.VerifiedVersion = previousVersion[..^previousHash.Length] + savedRequestHash;
        if (!OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Verified original request fingerprint did not reconcile unchanged Tally MASTERID/ALTERID.");
        oldIndex.AlterId = "29";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Different live Tally ALTERID was allowed to rebase original request.");
        oldIndex.AlterId = "28";
        // Only a Ledger rename may differ in the saved previous version:
        // its signed DR/CR amounts and occurrence count must stay identical.
        oldBinding.VerifiedVersion = "CONSOLIDATED|20260131|Sales|" +
            oldIndex.Reference + "|Old Ledger:-3333.33|Different Ledger:3333.33|" + savedRequestHash;
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Different prior signed DR/CR amounts authorized bound ALTER.");
        oldBinding.VerifiedVersion = previousVersion.Replace(
            oldSigned, oldSigned.Replace("B2C Sales - 24 - Gujarat", "Renamed Debtor"),
            StringComparison.Ordinal);
        oldBinding.VerifiedVersion = oldBinding.VerifiedVersion[..^previousHash.Length] + savedRequestHash;
        if (!OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Ledger Master display rename blocked exact previous signed DR/CR amounts.");
        oldBinding.VerifiedVersion = previousVersion;
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Inconsistent original request hash/version falsely authorized ALTER.");
        oldBinding.VerifiedContentHash = previousHash;
        oldBinding.MasterId = "337";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Different branch's destination MASTERID authorized incorrect source ALTER.");
        oldBinding.MasterId = "26";
        oldBinding.State = "AcceptedAwaitingVerification";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Unverified accepted voucher was allowed a new second ALTER.");
        Console.WriteLine("PASS: previous bound source version + full old DRCR + " +
            "same destination MASTERID/ALTERID mandatory before changed ledger ALTER.");

        // Reuse the NORMAL AAH previous-version reconciliation for legacy
        // consolidated bindings. An earlier AAH build can serialize a
        // representation/version differently without Tally changing its
        // exact old voucher. A VERIFIED previously saved content hash,
        // unique MASTERID, fresh live ALTERID, original DATE/TYPE/REF/PARTY/
        // NARRATION and complete signed DR/CR must still agree.
        oldBinding.State = "Verified";
        oldBinding.VerifiedVersion = "CONSOLIDATED|OLD-SERIALIZATION|" + previousHash;
        oldBinding.LastVerifiedAlterId = 28;
        if (!OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Legacy saved representation blocked unchanged verified previous Tally voucher.");
        oldIndex.AlterId = "29";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Legacy representation recovery ignored a changed nonzero Tally ALTERID.");
        oldIndex.AlterId = "28";
        oldBinding.LastVerifiedAlterId = 0; // Legacy AAH verified source with no ALTERID captured.
        if (!OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Legacy missing saved ALTERID blocked an exact SAVED hash and current live full DR/CR.");
        oldBinding.VerifiedContentHash = "WRONG";
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Legacy missing ALTERID incorrectly authorized an unverified prior content hash.");
        oldBinding.VerifiedContentHash = previousHash;
        oldIndex.LedgerPostings = oldPostings.Select(x =>
            new TallyLedgerPostingRecord { LedgerName = x.Name, SignedAmount = x.Amount + 1m }).ToArray();
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Legacy previous-version reconciliation accepted altered live DR/CR.");
        oldIndex.LedgerPostings = oldPostings.Select(x =>
            new TallyLedgerPostingRecord { LedgerName = x.Name, SignedAmount = x.Amount }).ToArray();
        oldBinding.VerifiedVersion = "DIFFERENT-SOURCE|" + previousHash;
        if (OldMatches(oldIndex, oldBinding))
            throw new InvalidOperationException(
                "Legacy recovery crossed into an unrelated previous-version namespace.");

        // CL019 BANK-56032 real import evidence: live MASTERID 354,
        // ALTERID 377; prior Receipt with HDFC/Suspense 50,000 and exact
        // imported content fingerprint. Separate voucher type is a DESIRED
        // Receipt - Karnataka, not a reason to lose the old MASTERID.
        var bankPostings = new (string Name, decimal Amount)[] {
            ("HDFC Bank CA - 50200110437141", -50000m),
            ("Suspense", 50000m)
        };
        var bankLive = new TallyVoucherIndexRecord {
            MasterId = "354", AlterId = "377", VoucherType = "Receipt",
            VoucherDate = new DateTime(2025, 5, 21),
            VoucherNumber = "1", Reference = "0000000000000001",
            PartyLedger = "", Narration = "CU1906763241MITANSH TECHNOLOGY INDIA PRI",
            LedgerPostings = bankPostings.Select(x =>
                new TallyLedgerPostingRecord { LedgerName = x.Name, SignedAmount = x.Amount }).ToArray()
        };
        var bankVoucher = new System.Xml.Linq.XElement("VOUCHER",
            new System.Xml.Linq.XElement("DATE", "20250521"),
            new System.Xml.Linq.XElement("VOUCHERTYPENAME", "Receipt"),
            new System.Xml.Linq.XElement("REFERENCE", bankLive.Reference),
            new System.Xml.Linq.XElement("PARTYLEDGERNAME", ""),
            new System.Xml.Linq.XElement("NARRATION", bankLive.Narration),
            bankPostings.Select(x => new System.Xml.Linq.XElement("ALLLEDGERENTRIES.LIST",
                new System.Xml.Linq.XElement("LEDGERNAME", x.Name),
                new System.Xml.Linq.XElement("AMOUNT",
                    x.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))));
        var fullBank = new[] { new TallyVoucherXmlSnapshot("354", "bank-guid", "Receipt",
            bankLive.VoucherDate, bankVoucher.ToString(
                System.Xml.Linq.SaveOptions.DisableFormatting)) };
        var bankSigned = string.Join("|", bankPostings.OrderBy(x => x.Name,
            StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Amount).Select(x =>
                x.Name + ":" + x.Amount.ToString("0.00",
                    System.Globalization.CultureInfo.InvariantCulture)));
        var bankHash = TallyVoucherFingerprint.BuildContentHash(
            bankLive.VoucherDate, bankLive.VoucherType, bankLive.Reference,
            bankLive.PartyLedger, 50000m, bankLive.Narration + "|" + bankSigned);
        if (!bankHash.Equals("8F490BC17F450D67F6273657DC0D4048C4344AAA70F985A02F012FE27225DFC6",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CL019 old Receipt proof does not match imported source hash.");
        var bankBinding = new AccountingAutomationHub.Domain.Tally.TallySourceBindingVersionState {
            MasterId = "354", State = "Verified", VerifiedContentHash = bankHash,
            VerifiedVersion = "CONSOLIDATED|LEGACY|" + bankHash, LastVerifiedAlterId = 377
        };
        if (!OldMatches(bankLive, bankBinding, fullBank))
            throw new InvalidOperationException(
                "CL019 BANK-56032 exact old voucher blocked by legacy saved version format.");
        bankLive.AlterId = "378";
        if (OldMatches(bankLive, bankBinding, fullBank))
            throw new InvalidOperationException(
                "CL019 Bank external Tally ALTERID change passed previous-version safety.");
        bankLive.AlterId = "377";
        var readVerifiedVersion = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "ReadVerifiedLiveConsolidatedVersion",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Original consolidated readback helper missing.");
        if (readVerifiedVersion.Invoke(null, new object[] { bankLive, fullBank })
                is not ValueTuple<string, string> acceptedReadback ||
            !acceptedReadback.Item1.Equals(bankHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Original destination full voucher DR/CR readback did not produce the prior saved fingerprint.");
        Console.WriteLine("PASS: CL019 original Receipt + verified fingerprint and live MASTERID/ALTERID " +
            "reuse normal previous-version reconciliation; new type requires normal bound ALTER.");

        // Exact Nov 3 CL019 live diagnostic: existing MASTERID 337 / ALTERID
        // 339 has the SAME Karnataka Sales B2C ref, date, signed tax/amounts,
        // but its former debtor name includes an old two-digit B2C code.
        // The desired branch ledger name changed; this is ONE verified
        // PREVIOUS->DESIRED ALTER, never an additional Tally CREATE.
        var kaOldPosts = new (string Name, decimal Amount)[]
        {
            ("B2C Sales - 24 - Karnataka", -49499.82m),
            ("Sales @ 18% - Karnataka", 41949.00m),
            ("Output IGST @ 18% - Karnataka", 7550.82m)
        };
        var kaDate = new DateTime(2026, 1, 31);
        const string kaRef = "B2CS-202601-24-18-2";
        const string kaOldName = "B2C Sales - 24 - Karnataka";
        const string kaNewName = "B2C Sales - Karnataka";
        var kaOldXml = new System.Xml.Linq.XElement("VOUCHER",
            new System.Xml.Linq.XElement("DATE", "20260131"),
            new System.Xml.Linq.XElement("VOUCHERTYPENAME", "Sales"),
            new System.Xml.Linq.XElement("VOUCHERNUMBER", "27"),
            new System.Xml.Linq.XElement("REFERENCE", kaRef),
            new System.Xml.Linq.XElement("NARRATION", "Sales " + kaRef),
            new System.Xml.Linq.XElement("PARTYLEDGERNAME", kaOldName),
            kaOldPosts.Select(p => new System.Xml.Linq.XElement("ALLLEDGERENTRIES.LIST",
                new System.Xml.Linq.XElement("LEDGERNAME", p.Name),
                new System.Xml.Linq.XElement("AMOUNT",
                    p.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))));
        var kaNewXml = new System.Xml.Linq.XElement(kaOldXml);
        kaNewXml.SetAttributeValue("ACTION", "Create");
        kaNewXml.Element("VOUCHERNUMBER")!.Value =
            "B-BFF10AAEA9D095199C45F469-" + kaRef;
        kaNewXml.Element("PARTYLEDGERNAME")!.Value = kaNewName;
        kaNewXml.Elements("ALLLEDGERENTRIES.LIST").First()
            .Element("LEDGERNAME")!.Value = kaNewName;
        var kaOldIndex = new TallyVoucherIndexRecord
        {
            MasterId = "337", AlterId = "339", Guid = "unique-guid-337",
            VoucherDate = kaDate, VoucherType = "Sales",
            VoucherNumber = "27", Reference = kaRef,
            Narration = "Sales " + kaRef, PartyLedger = kaOldName,
            LedgerPostings = kaOldPosts.Select(p => new TallyLedgerPostingRecord
            {
                LedgerName = p.Name, SignedAmount = p.Amount
            }).ToArray()
        };
        var kaSnapshot = new[]
        {
            new TallyVoucherXmlSnapshot(
                "337", "unique-guid-337", "Sales", kaDate,
                kaOldXml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting))
                { AlterId = "339" }
        };
        var renameMethod = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "TryReadFirstUnboundB2cLedgerRename",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "First-unbound B2C previous-version recovery guard is missing.");
        bool B2cRename(
            System.Xml.Linq.XElement newXml,
            TallyVoucherIndexRecord existing,
            IReadOnlyList<TallyVoucherXmlSnapshot> snapshots,
            out string hash, out string version)
        {
            var args = new object?[] { newXml, existing, snapshots, "", "" };
            var result = (bool)renameMethod.Invoke(null, args)!;
            hash = (string)args[3]!;
            version = (string)args[4]!;
            return result;
        }
        if (!B2cRename(kaNewXml, kaOldIndex, kaSnapshot, out var kaHash, out var kaVersion))
            throw new InvalidOperationException(
                "Exact CL019 B2C old debtor name to latest branch ledger was rejected.");
        var kaBaseline = new TallySourceBindingVersionState
        {
            State = "Verified", MasterId = "337",
            LastVerifiedAlterId = 339,
            VerifiedContentHash = kaHash, VerifiedVersion = kaVersion
        };
        if (!OldMatches(kaOldIndex, kaBaseline, kaSnapshot))
            throw new InvalidOperationException(
                "Recovered old exact B2C version did not pass ORIGINAL " +
                "same-MASTERID old full DRCR/ALTERID gate.");
        var kaAlter = System.Xml.Linq.XDocument.Parse(
            TallyAccountingVoucherExportService.BuildBoundConsolidatedVoucherAlterXml(
                "Combined Books", kaNewXml, "337", "27"));
        var kaAlterVoucher = kaAlter.Descendants("VOUCHER").Single();
        if ((string?)kaAlterVoucher.Attribute("TAGVALUE") != "337" ||
            !string.Equals((string?)kaAlterVoucher.Attribute("ACTION"), "Alter",
                StringComparison.Ordinal) ||
            kaAlterVoucher.Element("VOUCHERNUMBER")?.Value != "27" ||
            kaAlterVoucher.Descendants("LEDGERNAME").First().Value != kaNewName ||
            !kaAlterVoucher.Descendants("AMOUNT").Select(e => e.Value)
                .SequenceEqual(new[] { "-49499.82", "41949.00", "7550.82" }))
            throw new InvalidOperationException(
                "Recovered B2C invoice did not use original bound-MASTERID ALTER " +
                "with unchanged tax and signed amounts.");
        var wrongDate = new System.Xml.Linq.XElement(kaNewXml);
        wrongDate.Element("DATE")!.Value = "20260130";
        var wrongAmount = new System.Xml.Linq.XElement(kaNewXml);
        wrongAmount.Elements("ALLLEDGERENTRIES.LIST").Last()
            .Element("AMOUNT")!.Value = "7550.83";
        var wrongBranch = new System.Xml.Linq.XElement(kaNewXml);
        wrongBranch.Element("PARTYLEDGERNAME")!.Value = "B2C Sales - Gujarat";
        wrongBranch.Elements("ALLLEDGERENTRIES.LIST").First()
            .Element("LEDGERNAME")!.Value = "B2C Sales - Gujarat";
        var wrongNumber = new System.Xml.Linq.XElement(kaNewXml);
        wrongNumber.Element("VOUCHERNUMBER")!.Value = "27";
        var wrongTax = new System.Xml.Linq.XElement(kaNewXml);
        wrongTax.Elements("ALLLEDGERENTRIES.LIST").Last()
            .Element("LEDGERNAME")!.Value = "Output CGST @ 9% - Karnataka";
        foreach (var disallowed in new[] {
            (wrongDate, "date"), (wrongAmount, "IGST amount"),
            (wrongBranch, "cross-branch party"), (wrongNumber, "unscoped number"),
            (wrongTax, "changed tax ledger")
        })
            if (B2cRename(disallowed.Item1, kaOldIndex, kaSnapshot, out _, out _))
                throw new InvalidOperationException(
                    "Unsafe B2C ALTER recovery ignored " + disallowed.Item2);
        kaOldIndex.AlterId = "340";
        if (OldMatches(kaOldIndex, kaBaseline, kaSnapshot))
            throw new InvalidOperationException(
                "Recovered B2C ALTER bypassed changed destination ALTERID.");
        kaOldIndex.AlterId = "339";
        kaOldIndex.Guid = "different-guid";
        if (B2cRename(kaNewXml, kaOldIndex, kaSnapshot, out _, out _))
            throw new InvalidOperationException(
                "Recovered B2C source accepted a different destination GUID.");
        // Guard source-level workflow wiring as well as the pure matcher.
        if (!importerSource.Contains("acceptedOldSource", StringComparison.Ordinal) ||
            !importerSource.Contains("state!.PendingContentHash, oldHash", StringComparison.Ordinal) ||
            !importerSource.Contains("state.PendingVersion, oldVersion", StringComparison.Ordinal) ||
            !importerSource.Contains("IsTallyMasterIdClaimedByAnotherSource(", StringComparison.Ordinal) ||
            !importerSource.Contains("alterTargets.Add(planned.SourceAnchor, priorLive);", StringComparison.Ordinal) ||
            !importerSource.Contains("PreviousConsolidatedVersionMatches(", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "B2C previous-accepted/first-unbound source skipped original " +
                "Tally owner/pending-version/previous-MASTERID ALTER gates.");
        Console.WriteLine(
            "PASS: CL019 MASTERID 337 old ledger to desired ledger is strict " +
            "same-MASTERID ALTER, with full GST/DRCR and prior-accepted " +
            "version guards; cross-branch/amount/date/GUID changes rejected.");

        // CL019 v589 20:30 real Tally: Cash Entry module=0 but Bank
        // Contra vouchers refer to the standard destination Cash Ledger,
        // live MASTERID 31 and original parent Cash-in-Hand. The preview
        // previously staged a conflicting Cash/Create under Karnataka Cash.
        // Verify the ORIGINAL read-only Tally MASTERID/parent, never silently
        // reparent, CREATE, or bypass live master-first before voucher POST.
        var cashGuard = typeof(TallyConsolidatedVoucherImportService).GetMethod(
            "RequireExistingDefaultCashForBankVouchers",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Default Cash master-first live readback guard missing.");
        var bankContra = System.Xml.Linq.XElement.Parse(Voucher(
            number: "B-BFF10AAEA9D095199C45F469-BANK-56033",
            postings: """
                <ALLLEDGERENTRIES.LIST><LEDGERNAME>KA Bank</LEDGERNAME><AMOUNT>-500000.00</AMOUNT></ALLLEDGERENTRIES.LIST>
                <ALLLEDGERENTRIES.LIST><LEDGERNAME>Cash</LEDGERNAME><AMOUNT>500000.00</AMOUNT></ALLLEDGERENTRIES.LIST>
                """).Replace("<TALLYMESSAGE>", "").Replace("</TALLYMESSAGE>", ""));
        var liveCash = new System.Xml.Linq.XElement("LEDGER",
            new System.Xml.Linq.XAttribute("NAME", "Cash"),
            new System.Xml.Linq.XElement("NAME", "Cash"),
            new System.Xml.Linq.XElement("PARENT", "Cash-in-Hand"),
            new System.Xml.Linq.XElement("MASTERID", "31"));
        void RequireCash(
            System.Xml.Linq.XElement[] voucherItems,
            System.Xml.Linq.XElement[] stagedItems,
            System.Xml.Linq.XElement? live)
        {
            var available = new Dictionary<string, System.Xml.Linq.XElement>(
                StringComparer.OrdinalIgnoreCase);
            if (live is not null) available.Add("LEDGER|CASH", live);
            try
            {
                cashGuard.Invoke(null, new object[] {
                    voucherItems, stagedItems, available
                });
            }
            catch (System.Reflection.TargetInvocationException error)
                when (error.InnerException is InvalidOperationException failure)
            {
                throw failure;
            }
        }
        RequireCash([bankContra], [], liveCash);
        RequireCash([], [], null); // Nothing from Cash/BANK needs a live Cash check.
        void MustRejectCash(
            System.Xml.Linq.XElement[] voucherItems,
            System.Xml.Linq.XElement[] stagedItems,
            System.Xml.Linq.XElement? live, string reason)
        {
            try { RequireCash(voucherItems, stagedItems, live); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(
                "Unsafe zero-Cash-Entry consolidated bank master gate accepted " + reason);
        }
        MustRejectCash([bankContra],
            [new System.Xml.Linq.XElement("LEDGER",
                new System.Xml.Linq.XAttribute("NAME", "Cash"),
                new System.Xml.Linq.XElement("NAME", "Cash"),
                new System.Xml.Linq.XElement("PARENT", "Karnataka Cash"))],
            liveCash, "the old conflicting Cash CREATE");
        MustRejectCash([bankContra], [], null, "missing live Cash");
        var movedCash = new System.Xml.Linq.XElement(liveCash);
        movedCash.Element("PARENT")!.Value = "Karnataka Cash";
        MustRejectCash([bankContra], [], movedCash, "reparented live Cash");
        var anonymousCash = new System.Xml.Linq.XElement(liveCash);
        anonymousCash.Element("MASTERID")!.Remove();
        MustRejectCash([bankContra], [], anonymousCash,
            "destination Cash without exact MASTERID");
        Console.WriteLine(
            "PASS: zero Cash Entry module still keeps original Bank Contra " +
            "Cash-in-Hand/MASTERID, no duplicate Cash CREATE or unsafe reparent.");

        MustReject(Prepared(Voucher(action: "Alter")), "foreign ALTER");
        MustReject(Prepared(Voucher(extra: "TAGNAME=\"MASTER ID\" TAGVALUE=\"3\"")),
            "foreign source MASTERID");
        MustReject(Prepared(Voucher(extra: "GUID=\"foreign-identity\"")),
            "foreign source GUID");
        MustReject(Prepared(Voucher(postings: "")), "missing verified DR/CR");
        MustReject(Prepared(Voucher() + Voucher("KA-1", "20250506"), 2),
            "same FY voucher type and number on different dates");
        MustReject(Prepared(Voucher(), company: "Wrong Company"),
            "XML destination does not equal the confirmed link");
        MustReject(Prepared(Voucher(number: ""), 1), "voucher without stable number");
    }
}
