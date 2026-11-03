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
        // Real CL019 03-Nov-2026 incident: Tally CREATED=1, LASTVCHID=2,
        // but the date-based collection missed it. The original normal
        // Purchase/Sales/Journal comparator MUST recognize the live source
        // even if Tally auto-numbered the voucher differently.
        var native = new TallyVoucherIndexRecord
        {
            MasterId = "2", VoucherDate = new DateTime(2026, 2, 28),
            VoucherType = "Debit Note", VoucherNumber = "TALLY-AUTO-2",
            Reference = "KA-C-26-1924192",
            PartyLedger = "Amazon Seller Services Pvt Ltd",
            Narration = "Debit Note KA-C-26-1924192", Amount = 7.08m,
            DebitLedgers = "Amazon Seller Services Pvt Ltd",
            CreditLedgers = "Purchase @ 18% - Gujarat | Input IGST @ 18% - Gujarat",
            LedgerPostings =
            [
                new TallyLedgerPostingRecord { LedgerName = "Amazon Seller Services Pvt Ltd", SignedAmount = -7.08m, IsDebit = true },
                new TallyLedgerPostingRecord { LedgerName = "Purchase @ 18% - Gujarat", SignedAmount = 6m },
                new TallyLedgerPostingRecord { LedgerName = "Input IGST @ 18% - Gujarat", SignedAmount = 1.08m }
            ]
        };
        (string Name, decimal Amount)[] original =
        [
            ("Amazon Seller Services Pvt Ltd", -7.08m),
            ("Purchase @ 18% - Gujarat", 6m),
            ("Input IGST @ 18% - Gujarat", 1.08m)
        ];
        static bool Match(TallyVoucherIndexRecord x,
            IReadOnlyList<(string Name, decimal Amount)> amounts,
            string reference = "KA-C-26-1924192") =>
            TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedSource(
                x, new DateTime(2026, 2, 28), "Debit Note", reference,
                "Amazon Seller Services Pvt Ltd", "Debit Note KA-C-26-1924192",
                amounts);
        if (!Match(native, original))
            throw new InvalidOperationException(
                "Original accounting duplicate rule did not find the Tally auto-numbered CL019 voucher.");
        if (Match(native, original, "DIFFERENT-ORIGINAL-REFERENCE") ||
            Match(native, [
                ("Amazon Seller Services Pvt Ltd", -7.08m),
                ("Purchase @ 18% - Gujarat", 6m),
                ("Input IGST @ 18% - Gujarat", 1.09m)
            ]))
            throw new InvalidOperationException(
                "Original accounting duplicate check ignored changed source reference or GST DR/CR.");
        Console.WriteLine("PASS: consolidated uses ORIGINAL strict Tally duplicate matcher and 3 signed DR/CR lines.");

        // The same original normal accounting matcher is reused for PURCHASE,
        // SALES and multi-line JOURNAL, not just the CL019 Debit Note.
        foreach (var (voucherType, number, reference, party, narration, postings, amount) in new[]
        {
            ("Purchase", "TALLY-AUTO-P-25", "PURCHASE-001", "Vendor One", "Purchase bill",
                new[] { ("Vendor One", -100m), ("Purchase 18%", 100m) }, 100m),
            ("Sales", "TALLY-AUTO-S-26", "SALES-001", "Customer One", "Sales bill",
                new[] { ("Customer One", 118m), ("Sales 18%", -118m) }, 118m),
            ("Journal", "TALLY-AUTO-J-27", "JOURNAL-001", "", "Journal allocation",
                new[] { ("Expense A", -120m), ("Income B", 120m) }, 120m)
        })
        {
            var liveTest = new TallyVoucherIndexRecord
            {
                MasterId = number, VoucherDate = new DateTime(2026, 2, 28),
                VoucherType = voucherType, VoucherNumber = number,
                Reference = reference, PartyLedger = party,
                Narration = narration, Amount = amount,
                DebitLedgers = string.Join(" | ", postings.Where(p => p.Item2 < 0m).Select(p => p.Item1)),
                CreditLedgers = string.Join(" | ", postings.Where(p => p.Item2 > 0m).Select(p => p.Item1)),
                LedgerPostings = postings.Select((p, index) => new TallyLedgerPostingRecord
                {
                    Sequence = index + 1,
                    LedgerName = p.Item1,
                    SignedAmount = p.Item2, IsDebit = p.Item2 < 0m
                }).ToArray()
            };
            if (!TallyAccountingVoucherExportService.VoucherStrictlyMatchesConsolidatedSource(
                    liveTest, new DateTime(2026, 2, 28), voucherType, reference,
                    party, narration, postings.Select(p => (p.Item1, p.Item2)).ToArray()))
                throw new InvalidOperationException(
                    "Existing normal Tally accounting matcher did not recognize " +
                    voucherType + " under an auto-numbered native MASTERID.");
        }
        Console.WriteLine("PASS: consolidated Purchase/Sales/Journal reuse normal exact signed ledger safety.");

        MustReject(Prepared(Voucher(action: "Alter")), "foreign ALTER");
        MustReject(Prepared(Voucher(extra: "TAGNAME=\"MASTER ID\" TAGVALUE=\"3\"")),
            "foreign source MASTERID");
        MustReject(Prepared(Voucher(extra: "GUID=\"foreign-identity\"")),
            "foreign source GUID");
        MustReject(Prepared(Voucher(postings: "")), "missing verified DR/CR");
        MustReject(Prepared(Voucher() + Voucher("KA-1", "20250506"), 2),
            "same FY voucher type and number on different dates");
        // Local selection guard shared with normal accounting: a different
        // branch prefix cannot cause a second CREATE of one original invoice.
        var invoiceA = Voucher("KA-1")
            .Replace("</VOUCHERNUMBER>", "</VOUCHERNUMBER><REFERENCE>SAME-INVOICE</REFERENCE>");
        var invoiceB = Voucher("GJ-2")
            .Replace("</VOUCHERNUMBER>", "</VOUCHERNUMBER><REFERENCE>SAME-INVOICE</REFERENCE>");
        MustReject(Prepared(invoiceA + invoiceB, 2),
            "same original invoice/DR-CR under two different branch voucher numbers");
        MustReject(Prepared(Voucher(), company: "Wrong Company"),
            "XML destination does not equal the confirmed link");
        MustReject(Prepared(Voucher(number: ""), 1), "voucher without stable number");
    }
}
