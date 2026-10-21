using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Fix77: a Bank Ledger rename can represent a genuinely new financial Master.
/// The old Tally MASTERID remains a separate active Ledger; the new AAH Master stays
/// unbound until Tally CREATE returns a different MASTERID. Previous display names are
/// never used to silently reattach the new Bank Account to the old Tally Master.
/// </summary>
internal static class TallyIndependentBankLedgerPolicy
{
    public static int DetachExplicitIndependentRenames(
        IBankingRepository bankingRepository,
        IMasterRepository masterRepository,
        long clientId,
        string financialYear,
        IReadOnlyCollection<BankAccountRecord> accounts,
        IReadOnlyCollection<MasterRecord> masters)
    {
        var changed = 0;
        // Fix82: include archived rows here. A successfully-created independent Tally
        // Master can be hidden by stale alias history before the BankAccount binding is
        // promoted. MASTERID + exact requested current name must be allowed to reactivate
        // and bind that row in the same cycle.
        var authorityMasters = masterRepository.Get(clientId, financialYear, includeArchived: true, string.Empty).ToArray();

        foreach (var account in accounts)
        {
            var desiredName = account.AuthorityName.Trim();
            if (desiredName.Length == 0) continue;

            var exactLive = FindUniqueExactTallyMaster(desiredName, authorityMasters);
            if (exactLive is not null)
            {
                // Post-CREATE promotion: the exact requested independent name now owns one
                // concrete Tally MASTERID. That identity outranks the old BankAccount binding
                // and any stale archive/alias state. Duplicate exact names remain fail-closed.
                if (exactLive.IsArchived)
                {
                    masterRepository.Archive(exactLive.PersistentId, archived: false);
                    exactLive.IsArchived = false;
                    changed++;
                }

                if (!account.TallyLedgerMasterId.Trim().Equals(exactLive.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    !account.TallyLedgerGuid.Trim().Equals(exactLive.TallyGuid.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    account.TallyLedgerAlterId != exactLive.TallyAlterId ||
                    !account.TallyLedgerVerifiedName.Trim().Equals(exactLive.Name.Trim(), StringComparison.Ordinal) ||
                    !account.LedgerName.Trim().Equals(exactLive.Name.Trim(), StringComparison.Ordinal))
                {
                    account.LedgerName = exactLive.Name.Trim();
                    account.TallyLedgerMasterId = exactLive.TallyMasterId.Trim();
                    account.TallyLedgerGuid = exactLive.TallyGuid.Trim();
                    account.TallyLedgerAlterId = exactLive.TallyAlterId;
                    account.TallyLedgerVerifiedName = exactLive.Name.Trim();
                    bankingRepository.SaveAccount(account);
                    changed++;
                }
                continue;
            }

            // Fix78: historical Fix76/Fix77 attempts may already have detached the Bank
            // Account while leaving the new unbound user row labelled Source='Live Tally'.
            // No MASTERID means it is not live Tally authority. Normalize that pending
            // independent Master on every pass, not only while the account still carries
            // the old MASTERID. This guarantees the Master-first CREATE planner can see it.
            var independent = FindIndependentMaster(desiredName, authorityMasters);
            if (independent is not null &&
                MasterAuthority.IsTallySource(independent.Source) &&
                independent.TallyMasterId.Trim().Length == 0)
            {
                independent.Source = "Bank Statement";
                independent.IsUserEdited = true;
                masterRepository.Save(independent);
                changed++;
            }

            var boundMasterId = account.TallyLedgerMasterId.Trim();
            if (boundMasterId.Length == 0) continue;

            var bound = authorityMasters
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 master.TallyMasterId.Trim().Equals(boundMasterId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(master => master.TallyAlterId)
                .FirstOrDefault();
            if (bound is null || SameLedgerName(bound.Name, desiredName))
                continue; // same Ledger name: a Parent-only edit may still ALTER the bound MASTERID.

            independent ??= FindIndependentMaster(desiredName, authorityMasters);
            if (independent is null) continue;

            account.TallyLedgerMasterId = string.Empty;
            account.TallyLedgerGuid = string.Empty;
            account.TallyLedgerAlterId = 0;
            account.TallyLedgerVerifiedName = string.Empty;
            bankingRepository.SaveAccount(account);
            changed++;
        }

        return changed;
    }

    private static MasterRecord? FindUniqueExactTallyMaster(
        string? desiredName,
        IReadOnlyCollection<MasterRecord> masters)
    {
        var name = (desiredName ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        var matches = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             master.TallyMasterId.Trim().Length > 0 &&
                             SameLedgerName(master.Name, name))
            .GroupBy(master => master.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(master => master.TallyAlterId).First())
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static bool HasPendingIndependentMaster(
        BankAccountRecord account,
        IReadOnlyCollection<MasterRecord> masters)
    {
        if (account.TallyLedgerMasterId.Trim().Length > 0) return false;
        return FindIndependentMaster(account.AuthorityName, masters) is not null;
    }

    private static MasterRecord? FindIndependentMaster(string? desiredName, IReadOnlyCollection<MasterRecord> masters)
    {
        var name = (desiredName ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        var matches = masters
            .Where(master => IsActiveLedger(master) &&
                             master.TallyMasterId.Trim().Length == 0 &&
                             master.IsUserEdited &&
                             SameLedgerName(master.Name, name))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"More than one unbound local Ledger resolves to '{name}'. Resolve that local duplicate before Tally CREATE.");
        return matches.SingleOrDefault();
    }


    private static bool SameLedgerName(string? first, string? second) =>
        string.Equals((first ?? string.Empty).Trim(), (second ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveLedger(MasterRecord master) =>
        !master.IsArchived && master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase);
}
