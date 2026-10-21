using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Tally;

public static class TallyVoucherFingerprint
{
    public static string BuildContentHash(
        DateTime? date,
        string voucherType,
        string reference,
        string partyLedger,
        decimal amount,
        string narration)
    {
        var payload = string.Join(
            "|",
            date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(voucherType),
            Normalize(reference),
            Normalize(partyLedger),
            Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture),
            Normalize(narration));

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(payload)));
    }

    // Fix74: Bank first-pass duplicate identity includes every stable Bank-source field,
    // including Voucher Type and the BANK ACCOUNT Ledger. The only accounting field
    // deliberately excluded is the user-selected/top/counterpart Ledger; a change to that
    // field is a treatment modification of the same Bank transaction, not a new source.
    public static string BuildBankSourceIdentityHash(
        DateTime? date,
        string sourceKey,
        string voucherType,
        string bankLedger,
        decimal amount,
        string reference,
        string narration)
    {
        var payload = string.Join(
            "|",
            "BANK-V2",
            date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(sourceKey),
            Normalize(voucherType),
            Normalize(bankLedger),
            Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture),
            Normalize(reference),
            Normalize(narration));

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(payload)));
    }

    public static string BuildSourceKey(
        long clientId,
        string financialYear,
        string transactionId,
        long persistentId)
    {
        var identity = string.IsNullOrWhiteSpace(transactionId)
            ? persistentId.ToString(CultureInfo.InvariantCulture)
            : transactionId.Trim();

        return $"AAH-BANK:{clientId}:{Normalize(financialYear)}:{identity}";
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
