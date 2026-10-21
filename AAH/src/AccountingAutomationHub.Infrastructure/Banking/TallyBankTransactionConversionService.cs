using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Converts exact live Tally financial-account ledger postings into the same
/// transaction contract consumed by Inter-Bank reconciliation: Date, Bank Ledger,
/// Debit, Credit, Narration, Reference and a stable source transaction ID.
/// No Tally mutation occurs here. The caller persists the stable ConversionId as sync authority.
/// </summary>
public sealed class TallyBankTransactionConversionService
{
    private const decimal Tolerance = 0.01m;

    public IReadOnlyList<TallyConvertedBankTransaction> Convert(
        IReadOnlyCollection<TallyVoucherIndexRecord> vouchers,
        IReadOnlySet<string> financialLedgerKeys,
        IReadOnlyCollection<TallyLedgerNameAuthority>? ledgerAuthorities = null)
    {
        ArgumentNullException.ThrowIfNull(vouchers);
        ArgumentNullException.ThrowIfNull(financialLedgerKeys);
        var authoritiesByName = (ledgerAuthorities ?? Array.Empty<TallyLedgerNameAuthority>())
            .Where(authority => !string.IsNullOrWhiteSpace(authority.MasterId))
            .GroupBy(authority => Normalize(authority.DisplayName.Length > 0 ? authority.DisplayName : authority.ExactName), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Select(item => item.MasterId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.AlterId).First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<TallyConvertedBankTransaction>();
        foreach (var voucher in vouchers)
        {
            if (voucher.VoucherDate is null) continue;
            foreach (var posting in voucher.LedgerPostings.OrderBy(x => x.Sequence))
            {
                var ledger = posting.LedgerName.Trim();
                if (ledger.Length == 0 || posting.Amount <= Tolerance) continue;
                if (!financialLedgerKeys.Contains(Normalize(ledger))) continue;

                // One and only one direction conversion is performed here.
                // Tally Bank DR means money moved into the Bank ledger, therefore the
                // Bank Statement-shaped row is Credit. Tally Bank CR becomes Debit.
                var convertedDebit = posting.IsDebit ? 0m : posting.Amount;
                var convertedCredit = posting.IsDebit ? posting.Amount : 0m;
                var identity = voucher.MasterId.Trim();
                if (identity.Length == 0) identity = voucher.Guid.Trim();
                if (identity.Length == 0) identity = voucher.VoucherNumber.Trim();
                if (identity.Length == 0) identity = $"{voucher.VoucherDate:yyyyMMdd}-{voucher.VoucherType}";

                authoritiesByName.TryGetValue(Normalize(ledger), out var ledgerAuthority);
                var ledgerMasterId = ledgerAuthority?.MasterId?.Trim() ?? string.Empty;
                var stablePostingIdentity = ledgerMasterId.Length > 0
                    ? $"TALLYBTX|MID:{identity}|P:{posting.Sequence}|LMID:{ledgerMasterId}"
                    : $"TALLYBTX|{identity}|{posting.Sequence}|{Normalize(ledger)}";

                result.Add(new TallyConvertedBankTransaction
                {
                    ConversionId = stablePostingIdentity,
                    CompanyKey = voucher.CompanyKey,
                    FinancialYear = voucher.FinancialYear,
                    TallyMasterId = voucher.MasterId,
                    TallyAlterId = voucher.AlterId,
                    TallyGuid = voucher.Guid,
                    VoucherType = voucher.VoucherType,
                    VoucherNumber = voucher.VoucherNumber,
                    PostingSequence = posting.Sequence,
                    BankLedgerName = ledger,
                    BankLedgerMasterId = ledgerMasterId,
                    BankLedgerGuid = ledgerAuthority?.Guid?.Trim() ?? string.Empty,
                    BankLedgerAlterId = ledgerAuthority?.AlterId ?? 0,
                    TransactionDate = voucher.VoucherDate.Value.Date,
                    Narration = (string.IsNullOrWhiteSpace(posting.Narration) ? voucher.Narration : posting.Narration).Trim(),
                    ReferenceNumber = voucher.Reference.Trim(),
                    Debit = convertedDebit,
                    Credit = convertedCredit,
                    TallySignedAmount = posting.SignedAmount,
                    TallyIsDebit = posting.IsDebit
                });
            }
        }

        return result
            .OrderBy(x => x.TransactionDate)
            .ThenBy(x => x.BankLedgerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.TallyMasterId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PostingSequence)
            .ToArray();
    }

    private static string Normalize(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
