using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Desktop.Services;

internal sealed record BankExchangeConversionResult(string CsvPath, int TransactionCount, string Format, string BankName, string AccountNumber);

internal static class FinancialExchangeStatementConverters
{
    public static BankExchangeConversionResult ConvertOfx(string sourcePath)
    {
        var text = FlexibleWorkbookReader.ReadTextWithEncodingFallback(sourcePath);
        var account = Tag(text, "ACCTID");
        var bank = FirstNonBlank(Tag(text, "ORG"), Tag(text, "FID"), "Unknown Bank");
        var txs = new List<ExchangeTransaction>();
        foreach (Match match in Regex.Matches(text, @"(?is)<STMTTRN>(?<body>.*?)(?=<STMTTRN>|</BANKTRANLIST>|</STMTTRN>)"))
        {
            var body = match.Groups["body"].Value;
            var amount = ParseDecimal(Tag(body, "TRNAMT"));
            var date = ParseOfxDate(Tag(body, "DTPOSTED"));
            if (!date.HasValue || !amount.HasValue) continue;
            var narration = Join(Tag(body, "NAME"), Tag(body, "MEMO"));
            txs.Add(new ExchangeTransaction
            {
                Date = date.Value,
                ValueDate = date.Value,
                Amount = Math.Abs(amount.Value),
                IsDebit = amount.Value < 0,
                Narration = string.IsNullOrWhiteSpace(narration) ? Tag(body, "TRNTYPE") : narration,
                Reference = FirstNonBlank(Tag(body, "FITID"), Tag(body, "CHECKNUM"))
            });
        }
        var closing = ParseDecimal(Tag(text, "BALAMT"));
        return WriteCsv(sourcePath, "OFX", bank, account, txs, null, closing);
    }

    public static BankExchangeConversionResult ConvertQif(string sourcePath)
    {
        var text = FlexibleWorkbookReader.ReadTextWithEncodingFallback(sourcePath);
        var txs = new List<ExchangeTransaction>();
        string dateText = "", amountText = "", payee = "", memo = "", number = "";
        void Flush()
        {
            var date = ParseFlexibleDate(dateText);
            var amount = ParseDecimal(amountText);
            if (date.HasValue && amount.HasValue)
            {
                txs.Add(new ExchangeTransaction
                {
                    Date = date.Value, ValueDate = date.Value, Amount = Math.Abs(amount.Value), IsDebit = amount.Value < 0,
                    Narration = Join(payee, memo), Reference = number
                });
            }
            dateText = amountText = payee = memo = number = "";
        }
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw == "^") { Flush(); continue; }
            if (raw.Length < 2) continue;
            var value = raw[1..].Trim();
            switch (raw[0])
            {
                case 'D': dateText = value; break;
                case 'T': amountText = value; break;
                case 'P': payee = value; break;
                case 'M': memo = value; break;
                case 'N': number = value; break;
            }
        }
        Flush();
        return WriteCsv(sourcePath, "QIF", "Unknown Bank", "Unknown", txs, null, null);
    }

    public static BankExchangeConversionResult ConvertCamt053(string sourcePath)
    {
        var document = XDocument.Load(sourcePath, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("The camt.053 XML has no document root.");
        XElement? First(XContainer node, string name) => node.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        string Value(XContainer node, string name) => First(node, name)?.Value.Trim() ?? string.Empty;

        var statement = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Stmt") ?? root;
        var accountNode = statement.Descendants().FirstOrDefault(e => e.Name.LocalName == "Acct");
        var account = accountNode is null ? string.Empty :
            accountNode.Descendants().FirstOrDefault(e => e.Name.LocalName is "IBAN" or "Id")?.Value.Trim() ?? string.Empty;
        var bank = accountNode?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Nm")?.Value.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bank))
            bank = accountNode?.Descendants().FirstOrDefault(e => e.Name.LocalName is "BICFI" or "BIC")?.Value.Trim() ?? "Unknown Bank";

        decimal? opening = null, closing = null;
        foreach (var balance in statement.Descendants().Where(e => e.Name.LocalName == "Bal"))
        {
            var code = balance.Descendants().FirstOrDefault(e => e.Name.LocalName == "Cd")?.Value.Trim().ToUpperInvariant() ?? string.Empty;
            var amountText = balance.Descendants().FirstOrDefault(e => e.Name.LocalName == "Amt")?.Value.Trim();
            var amount = ParseDecimal(amountText);
            if (!amount.HasValue) continue;
            var direction = balance.Descendants().FirstOrDefault(e => e.Name.LocalName == "CdtDbtInd")?.Value.Trim();
            var signed = direction?.StartsWith("DBIT", StringComparison.OrdinalIgnoreCase) == true ? -Math.Abs(amount.Value) : Math.Abs(amount.Value);
            if (code is "OPBD" or "PRCD") opening ??= signed;
            if (code is "CLBD" or "ITBD") closing = signed;
        }

        var txs = new List<ExchangeTransaction>();
        foreach (var entry in statement.Descendants().Where(e => e.Name.LocalName == "Ntry"))
        {
            var amount = ParseDecimal(entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Amt")?.Value);
            if (!amount.HasValue) continue;
            var direction = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "CdtDbtInd")?.Value.Trim() ?? string.Empty;
            var booking = ParseIsoDate(entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "BookgDt")?.Descendants().FirstOrDefault(e => e.Name.LocalName is "Dt" or "DtTm")?.Value);
            var valueDate = ParseIsoDate(entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "ValDt")?.Descendants().FirstOrDefault(e => e.Name.LocalName is "Dt" or "DtTm")?.Value) ?? booking;
            if (!booking.HasValue) continue;
            var reference = FirstNonBlank(Value(entry, "NtryRef"), Value(entry, "AcctSvcrRef"), Value(entry, "EndToEndId"));
            var partyNames = entry.Descendants().Where(e => e.Name.LocalName == "Nm").Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
            var narration = Join(string.Join(" / ", partyNames), Value(entry, "AddtlNtryInf"), Value(entry, "Ustrd"));
            txs.Add(new ExchangeTransaction
            {
                Date = booking.Value, ValueDate = valueDate ?? booking.Value, Amount = Math.Abs(amount.Value),
                IsDebit = direction.StartsWith("DBIT", StringComparison.OrdinalIgnoreCase), Narration = narration, Reference = reference
            });
        }
        return WriteCsv(sourcePath, "camt.053", bank, account, txs, opening, closing);
    }

    private static BankExchangeConversionResult WriteCsv(string sourcePath, string format, string bank, string account, IReadOnlyList<ExchangeTransaction> txs, decimal? opening, decimal? closing)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "BankExchange");
        Directory.CreateDirectory(tempFolder);
        var target = Path.Combine(tempFolder, $"AAH_{Regex.Replace(format, "[^A-Za-z0-9]+", "_")}_{Guid.NewGuid():N}.csv");
        decimal? running = opening;
        using var writer = new StreamWriter(target, false, new UTF8Encoding(true));
        writer.WriteLine($"Bank Name,{Csv(bank)}");
        writer.WriteLine($"Account Number,{Csv(account)}");
        if (opening.HasValue) writer.WriteLine($"Opening Balance,{opening.Value.ToString("0.00", CultureInfo.InvariantCulture)}");
        if (closing.HasValue) writer.WriteLine($"Closing Balance,{closing.Value.ToString("0.00", CultureInfo.InvariantCulture)}");
        writer.WriteLine();
        writer.WriteLine("Date,Value Date,Narration,Reference,Debit,Credit,Balance");
        foreach (var tx in txs.OrderBy(t => t.Date))
        {
            if (running.HasValue) running += tx.IsDebit ? -tx.Amount : tx.Amount;
            writer.Write(Csv(tx.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))); writer.Write(',');
            writer.Write(Csv(tx.ValueDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))); writer.Write(',');
            writer.Write(Csv(tx.Narration)); writer.Write(','); writer.Write(Csv(tx.Reference)); writer.Write(',');
            writer.Write(tx.IsDebit ? tx.Amount.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty); writer.Write(',');
            writer.Write(tx.IsDebit ? string.Empty : tx.Amount.ToString("0.00", CultureInfo.InvariantCulture)); writer.Write(',');
            writer.WriteLine(running?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return new BankExchangeConversionResult(target, txs.Count, format, bank, account);
    }

    private static string Tag(string text, string tag)
    {
        var match = Regex.Match(text, $@"(?is)<{Regex.Escape(tag)}>(?<v>[^<\r\n]+)");
        return match.Success ? match.Groups["v"].Value.Trim() : string.Empty;
    }
    private static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Replace(",", string.Empty).Trim();
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
    private static DateTime? ParseOfxDate(string text)
    {
        var digits = new string((text ?? string.Empty).TakeWhile(char.IsDigit).ToArray());
        if (digits.Length >= 8 && DateTime.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        return null;
    }
    private static DateTime? ParseFlexibleDate(string text)
    {
        var clean = (text ?? string.Empty).Replace("'", "/").Trim();
        var formats = new[] { "M/d/yyyy", "M/d/yy", "MM/dd/yyyy", "dd/MM/yyyy", "dd/MM/yy", "d-MMM-yy", "d-MMM-yyyy" };
        if (DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) return date;
        return DateTime.TryParse(clean, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date) ? date : null;
    }
    private static DateTime? ParseIsoDate(string? text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date.Date : null;
    private static string Join(params string[] values) => string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())).Trim();
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
    private static string Csv(string value)
    {
        value ??= string.Empty;
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private sealed class ExchangeTransaction
    {
        public DateTime Date { get; set; }
        public DateTime ValueDate { get; set; }
        public decimal Amount { get; set; }
        public bool IsDebit { get; set; }
        public string Narration { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }
}
