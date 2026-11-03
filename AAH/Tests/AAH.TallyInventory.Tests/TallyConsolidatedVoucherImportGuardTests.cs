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
        // CL019 regression (03-Nov-2026): Tally reported CREATED=1,
        // LASTVCHID=2, but the normal live FY index returned 0. A rerun
        // must identify an existing voucher even if Tally has auto-numbered
        // it differently from the original branch voucher.
        var live = new TallyVoucherIndexRecord
        {
            VoucherDate = new DateTime(2026, 2, 28),
            VoucherType = "Debit Note",
            VoucherNumber = "Tally-Auto-Number-2",
            Reference = "INV-DEBIT-001",
            MasterId = "2",
            LedgerPostings =
            [
                new TallyLedgerPostingRecord { LedgerName = "Vendor A", SignedAmount = -7.08m, IsDebit = true },
                new TallyLedgerPostingRecord { LedgerName = "Purchase 18%", SignedAmount = 6.00m },
                new TallyLedgerPostingRecord { LedgerName = "Input IGST 18%", SignedAmount = 1.08m }
            ]
        };
        (string Name, decimal Amount)[] branch =
        [
            ("Vendor A", -7.08m), ("Purchase 18%", 6.00m), ("Input IGST 18%", 1.08m)
        ];
        void RequireDuplicate(bool expected, TallyVoucherIndexRecord row,
            DateTime date, string voucherType, string reference,
            IReadOnlyList<(string Name, decimal Amount)> postings, string caseName)
        {
            var detected = TallyConsolidatedVoucherImportService.IsStrongOriginalReferenceDuplicate(
                row, date, voucherType, reference, postings);
            if (detected != expected)
                throw new InvalidOperationException("Consolidated live duplicate test failed: " + caseName);
        }
        RequireDuplicate(true, live, new DateTime(2026, 2, 28),
            "Debit Note", "INV-DEBIT-001", branch,
            "same original reference + 3 signed DR/CR with different Tally number");
        RequireDuplicate(false, live, new DateTime(2026, 2, 27),
            "Debit Note", "INV-DEBIT-001", branch, "different date");
        RequireDuplicate(false, live, new DateTime(2026, 2, 28),
            "Credit Note", "INV-DEBIT-001", branch, "different voucher type");
        RequireDuplicate(false, live, new DateTime(2026, 2, 28),
            "Debit Note", "OTHER-INVOICE", branch, "different original reference");
        RequireDuplicate(false, live, new DateTime(2026, 2, 28),
            "Debit Note", "INV-DEBIT-001",
            [("Vendor A", -7.08m), ("Purchase 18%", 6.00m), ("Input IGST 18%", 1.09m)],
            "changed GST posting");
        RequireDuplicate(false, live, new DateTime(2026, 2, 28),
            "Debit Note", "INV-DEBIT-001",
            [("Vendor A", -7.08m), ("Purchase 18%", 6.00m), ("Different IGST", 1.08m)],
            "different GST ledger");
        RequireDuplicate(false, live, new DateTime(2026, 2, 28),
            "Debit Note", "", branch, "blank reference must not imply a duplicate");
        Console.WriteLine("PASS: consolidated original-reference duplicate detection honors full DR/CR even after Tally auto-numbering.");

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
