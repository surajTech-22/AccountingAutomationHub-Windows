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
