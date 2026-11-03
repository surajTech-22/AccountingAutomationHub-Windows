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
