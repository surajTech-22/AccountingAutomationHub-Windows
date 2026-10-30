using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AccountingAutomationHub.Infrastructure.Tally;

internal static class OwnerGroupSampleExportTests
{
    private sealed record SampleCompany(
        string Company, string Pan, string Role,
        IReadOnlyDictionary<string, string> LedgerParents,
        TallyErp9BankVoucherExpectation[] Vouchers);

    internal static void Run()
    {
        var date = new DateTime(2026, 10, 30);
        static TallyErp9BankVoucherExpectation V(string type, DateTime d, string reference, string narration,
            string debitLedger, string creditLedger, decimal amount) =>
            new(d, type, reference, narration,
                [new TallyErp9BankLedgerLine(debitLedger, amount, true),
                 new TallyErp9BankLedgerLine(creditLedger, amount, false)]);
        static IReadOnlyDictionary<string, string> Masters(params (string ledger, string parent)[] items) =>
            items.ToDictionary(x => x.ledger, x => x.parent, StringComparer.OrdinalIgnoreCase);

        // Synthetic accounts only. Each Tally company receives its own payload.
        // A Tally Group Company is a reporting construct, never a posting destination.
        var companies = new[]
        {
            new SampleCompany("AAH SAMPLE Individual Books", "ABCDE1234F", "Individual", Masters(
                ("Individual Bank", "Bank Accounts"),
                ("Individual Cash", "Cash-in-Hand"),
                ("Proprietor Drawings", "Capital Account"),
                ("Due From PAN-B Entity", "Current Assets")),
                [
                    V("Contra", date, "IND-CASH-1", "Individual cash deposited to own bank",
                        "Individual Bank", "Individual Cash", 3000m),
                    V("Payment", date, "IND-DRAW-1", "Proprietor draws funds",
                        "Proprietor Drawings", "Individual Bank", 1500m),
                    V("Payment", date, "GROUP-XFER-1-A", "Funds lent to separate PAN entity",
                        "Due From PAN-B Entity", "Individual Bank", 8000m)
                ]),
            new SampleCompany("AAH SAMPLE Branch South", "ABCDE1234F", "Same PAN Branch", Masters(
                ("Bank A - South", "Bank Accounts"),
                ("Bank B - South", "Bank Accounts"),
                ("Cash - South", "Cash-in-Hand")),
                [
                    V("Contra", date, "SOUTH-BANK-1", "Interbank transfer inside Branch South",
                        "Bank B - South", "Bank A - South", 5500m),
                    V("Contra", date, "SOUTH-CASH-1", "Branch cash deposited to bank",
                        "Bank A - South", "Cash - South", 2000m)
                ]),
            new SampleCompany("AAH SAMPLE Branch North", "ABCDE1234F", "Same PAN Branch", Masters(
                ("Bank A - North", "Bank Accounts"),
                ("Cash - North", "Cash-in-Hand")),
                [V("Contra", date, "NORTH-CASH-1", "Cash withdrawn for Branch North",
                    "Cash - North", "Bank A - North", 900m)]),
            new SampleCompany("AAH SAMPLE Separate PAN Entity", "PQRST1234A", "Different PAN Same Owner", Masters(
                ("Bank - PAN-B", "Bank Accounts"),
                ("Due To Individual", "Current Liabilities")),
                [V("Receipt", date, "GROUP-XFER-1-B", "Funds received from individual entity",
                    "Bank - PAN-B", "Due To Individual", 8000m)])
        };

        var allReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summary = new List<object>();
        var outputDir = Environment.GetEnvironmentVariable("AAH_SAMPLE_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(outputDir)) Directory.CreateDirectory(outputDir);
        foreach (var company in companies)
        {
            var masterBuilder = new StringBuilder();
            TallyExcelParityXmlWriter.BeginAllMastersImport(masterBuilder, company.Company);
            foreach (var item in company.LedgerParents.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                TallyExcelParityXmlWriter.AppendErp9MinimalLedgerMaster(masterBuilder, item.Key, item.Value);
            TallyExcelParityXmlWriter.EndAllMastersImport(masterBuilder);
            var masterXml = masterBuilder.ToString();
            TallyBankXmlContractValidator.ValidateLedgerMasterImport(masterXml, company.Company, company.LedgerParents);

            // Exercise the actual production exact-name Bank voucher XML writer.
            var voucherXml = TallyErp9ExactBankVoucherXmlBuilder.Build(company.Company, company.Vouchers);
            TallyBankXmlContractValidator.ValidateVoucherImport(voucherXml, company.Company, company.Vouchers);
            var parsed = XDocument.Parse(voucherXml);
            var exportedVouchers = parsed.Descendants("VOUCHER").ToArray();
            if (exportedVouchers.Length != company.Vouchers.Length)
                throw new InvalidOperationException("Export count mismatch for " + company.Company);
            foreach (var voucher in exportedVouchers)
            {
                var reference = voucher.Element("REFERENCE")?.Value ?? "";
                if (!allReferences.Add(reference))
                    throw new InvalidOperationException("Duplicate voucher reference: " + reference);
                var lines = voucher.Elements("ALLLEDGERENTRIES.LIST").ToArray();
                var total = lines.Sum(x => decimal.Parse(x.Element("AMOUNT")!.Value, CultureInfo.InvariantCulture));
                if (total != 0m) throw new InvalidOperationException("Unbalanced voucher " + reference);
                if (voucher.Element("VOUCHERTYPENAME")?.Value == "Contra" &&
                    lines.Any(x => !company.LedgerParents.TryGetValue(x.Element("LEDGERNAME")!.Value, out var parent) ||
                        (!parent.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase) &&
                         !parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException("Invalid Contra line: " + reference);
            }
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                var name = company.Company.Replace(" ", "_");
                File.WriteAllText(Path.Combine(outputDir, name + "_masters.xml"), masterXml);
                File.WriteAllText(Path.Combine(outputDir, name + "_vouchers.xml"), voucherXml);
            }
            summary.Add(new { company.Company, company.Pan, company.Role,
                ledgerMasters = company.LedgerParents.Count, vouchers = company.Vouchers.Length });
            Console.WriteLine($"PASS: {company.Company} — {company.LedgerParents.Count} Ledger masters / {company.Vouchers.Length} vouchers.");
        }

        if (companies.Count(x => x.Pan == "ABCDE1234F") != 3 ||
            companies.Count(x => x.Pan == "PQRST1234A") != 1 ||
            allReferences.Count != 7 ||
            companies.SelectMany(x => x.Vouchers).Count(x => x.VoucherType == "Contra") != 4)
            throw new InvalidOperationException("Owner/PAN/voucher scenario counts changed.");

        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            var manifest = new
            {
                status = "XML_STRUCTURE_VALIDATED_ONLY_NOT_TALLY_ACCEPTANCE",
                syntheticOnly = true,
                ownerGroupCompany = "AAH SAMPLE Owner Group (zero vouchers or masters exported)",
                companyCount = companies.Length,
                ledgerMasterCount = companies.Sum(x => x.LedgerParents.Count),
                voucherCount = companies.Sum(x => x.Vouchers.Length),
                debitCreditLineCount = companies.Sum(x => x.Vouchers.Length) * 2,
                knownLimitations = new[]
                {
                    "No live Tally HTTP round-trip / MASTERID verification.",
                    "Custom branch-wise Contra voucher type is not implemented in the production Bank XML writer.",
                    "Production Owner Group transfer routing, intercompany matching and consolidation are not implemented.",
                    "Fixture verifies the production Bank XML writer / validator using synthetic prepared vouchers."
                },
                companies = summary
            };
            File.WriteAllText(Path.Combine(outputDir, "validation_manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: four synthetic Tally company XML exports, 11 masters, 7 vouchers, 14 balanced DR/CR lines; zero Group Company postings.");
        Console.WriteLine("NOT YET TESTED: Tally acceptance, read-back, custom branch voucher type and owner-group production routing.");
    }
}