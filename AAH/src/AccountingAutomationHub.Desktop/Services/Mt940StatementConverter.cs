using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Desktop.Services;

internal sealed record Mt940ConversionResult(
    string CsvPath,
    string BankName,
    string AccountNumber,
    int TransactionCount,
    decimal? OpeningBalance,
    decimal? ClosingBalance);

/// <summary>
/// Minimal, deterministic SWIFT MT940 reader for Automation Folder bank statements.
/// It converts :61: transaction lines plus :86: details to the Hub's normal Bank CSV
/// contract so the existing BankStatementImportService remains the accounting authority.
/// </summary>
internal static class Mt940StatementConverter
{
    private static readonly Dictionary<string, string> BicBankNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HDFCINBB"] = "HDFC Bank",
        ["ICICINBB"] = "ICICI Bank",
        ["SBININBB"] = "State Bank of India",
        ["UTIBINBB"] = "Axis Bank",
        ["CNRBINBB"] = "Canara Bank",
        ["BARBINBB"] = "Bank of Baroda",
        ["KKBKINBB"] = "Kotak Mahindra Bank",
        ["YESBINBB"] = "YES Bank",
        ["IDFBINBB"] = "IDFC FIRST Bank",
        ["INDBINBB"] = "IndusInd Bank",
        ["PUNBINBB"] = "Punjab National Bank",
        ["BKIDINBB"] = "Bank of India",
        ["IOBAINBB"] = "Indian Overseas Bank",
        ["UCBAINBB"] = "UCO Bank",
        ["FDRLINBB"] = "Federal Bank",
        ["KVBLINBB"] = "Karur Vysya Bank"
    };

    public static Mt940ConversionResult Convert(string sourcePath)
    {
        var text = FlexibleWorkbookReader.ReadTextWithEncodingFallback(sourcePath);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var account = lines.FirstOrDefault(line => line.StartsWith(":25:", StringComparison.OrdinalIgnoreCase))?[4..].Trim() ?? string.Empty;
        account = NormalizeAccount(account);
        var bank = DetectBank(text);
        var opening = ParseBalance(lines.FirstOrDefault(line => line.StartsWith(":60F:", StringComparison.OrdinalIgnoreCase) || line.StartsWith(":60M:", StringComparison.OrdinalIgnoreCase)));
        var closing = ParseBalance(lines.LastOrDefault(line => line.StartsWith(":62F:", StringComparison.OrdinalIgnoreCase) || line.StartsWith(":62M:", StringComparison.OrdinalIgnoreCase)));

        var transactions = new List<Mt940Transaction>();
        Mt940Transaction? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith(":61:", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) transactions.Add(current);
                current = ParseTransaction(line[4..]);
                continue;
            }
            if (current is not null && line.StartsWith(":86:", StringComparison.OrdinalIgnoreCase))
            {
                current.Narration = JoinNarration(current.Narration, line[4..]);
                continue;
            }
            if (current is not null && !line.StartsWith(':') && !string.IsNullOrWhiteSpace(line))
                current.Narration = JoinNarration(current.Narration, line);
        }
        if (current is not null) transactions.Add(current);
        transactions = transactions.Where(item => item.Date.HasValue && item.Amount > 0m).ToList();

        decimal? running = opening;
        foreach (var transaction in transactions)
        {
            if (running.HasValue)
            {
                running += transaction.IsDebit ? -transaction.Amount : transaction.Amount;
                transaction.RunningBalance = running;
            }
        }
        if (closing.HasValue && transactions.Count > 0)
            transactions[^1].RunningBalance = closing;

        var tempFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "BankExchange");
        Directory.CreateDirectory(tempFolder);
        var target = Path.Combine(tempFolder, $"AAH_MT940_{Guid.NewGuid():N}.csv");
        using (var writer = new StreamWriter(target, false, new UTF8Encoding(true)))
        {
            writer.WriteLine($"Bank Name,{Csv(bank)}");
            writer.WriteLine($"Account Number,{Csv(account)}");
            if (opening.HasValue) writer.WriteLine($"Opening Balance,{opening.Value.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (closing.HasValue) writer.WriteLine($"Closing Balance,{closing.Value.ToString("0.00", CultureInfo.InvariantCulture)}");
            writer.WriteLine();
            writer.WriteLine("Date,Narration,Debit,Credit");
            foreach (var transaction in transactions)
            {
                writer.Write(Csv(transaction.Date!.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
                writer.Write(',');
                writer.Write(Csv(string.IsNullOrWhiteSpace(transaction.Narration) ? transaction.Reference : transaction.Narration));
                writer.Write(',');
                writer.Write(transaction.IsDebit ? transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty);
                writer.Write(',');
                writer.WriteLine(transaction.IsDebit ? string.Empty : transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }

        return new Mt940ConversionResult(target, bank, account, transactions.Count, opening, closing);
    }

    private static Mt940Transaction ParseTransaction(string payload)
    {
        // YYMMDD[MMDD]D/C[funds code]amount,decimal[transaction code]reference
        var match = Regex.Match(payload,
            @"^(?<date>\d{6})(?<entry>\d{4})?(?<dc>R?[CD])(?<fund>[A-Z])?(?<amount>\d+(?:,\d{0,2})?)(?<rest>.*)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return new Mt940Transaction { Narration = payload.Trim() };

        DateTime? date = null;
        if (DateTime.TryParseExact(match.Groups["date"].Value, "yyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
            date = parsedDate;
        var dc = match.Groups["dc"].Value.ToUpperInvariant();
        var isDebit = dc.EndsWith('D');
        var amountText = match.Groups["amount"].Value.Replace(',', '.');
        decimal.TryParse(amountText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount);
        var rest = match.Groups["rest"].Value.Trim();
        var reference = rest;
        if (reference.StartsWith("N", StringComparison.OrdinalIgnoreCase) && reference.Length > 4)
            reference = reference[4..];
        reference = reference.Replace("//", " ", StringComparison.Ordinal).Trim();
        return new Mt940Transaction
        {
            Date = date,
            IsDebit = isDebit,
            Amount = Math.Abs(amount),
            Reference = reference,
            Narration = reference
        };
    }

    private static decimal? ParseBalance(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var payload = line.Length > 5 ? line[5..] : line;
        var match = Regex.Match(payload, @"^(?<dc>[CD])(?<date>\d{6})(?<currency>[A-Z]{3})(?<amount>\d+(?:,\d{0,2})?)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!decimal.TryParse(match.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            return null;
        return match.Groups["dc"].Value.Equals("D", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(amount) : Math.Abs(amount);
    }

    private static string DetectBank(string text)
    {
        var bicMatch = Regex.Match(text, @"\{1:F01(?<bic>[A-Z0-9]{8,12})", RegexOptions.IgnoreCase);
        if (bicMatch.Success)
        {
            var bic = bicMatch.Groups["bic"].Value.ToUpperInvariant();
            foreach (var pair in BicBankNames)
                if (bic.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        var known = new (string Needle, string Name)[]
        {
            ("HDFC BANK", "HDFC Bank"), ("ICICI BANK", "ICICI Bank"), ("STATE BANK OF INDIA", "State Bank of India"),
            ("AXIS BANK", "Axis Bank"), ("CANARA BANK", "Canara Bank"), ("BANK OF BARODA", "Bank of Baroda"),
            ("KOTAK MAHINDRA", "Kotak Mahindra Bank"), ("IDFC FIRST", "IDFC FIRST Bank"), ("INDUSIND", "IndusInd Bank")
        };
        foreach (var item in known)
            if (text.Contains(item.Needle, StringComparison.OrdinalIgnoreCase)) return item.Name;
        return "Unknown Bank";
    }

    private static string NormalizeAccount(string value)
    {
        var clean = value.Trim();
        var slash = clean.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < clean.Length) clean = clean[(slash + 1)..];
        var compact = new string(clean.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length > 0 ? compact : "Unknown";
    }

    private static string JoinNarration(string left, string right)
    {
        left = left.Trim(); right = right.Trim();
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        return $"{left} {right}";
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private sealed class Mt940Transaction
    {
        public DateTime? Date { get; set; }
        public bool IsDebit { get; set; }
        public decimal Amount { get; set; }
        public string Reference { get; set; } = string.Empty;
        public string Narration { get; set; } = string.Empty;
        public decimal? RunningBalance { get; set; }
    }
}
