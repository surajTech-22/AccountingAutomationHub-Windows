using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Rebuilds the Ledger Mapping workbench using the same two-pass sequence as the
/// Excel workbook: first parse and count every exact Summary Name, then select the
/// canonical name and group transactions. Existing saved mappings are migrated to
/// the rebuilt canonical rows instead of leaving obsolete duplicate patterns behind.
/// </summary>
public sealed class BankLedgerMappingService(IBankingRepository repository, IMasterRepository masterRepository, SqliteGovernanceRepository? governanceRepository = null)
{
    private sealed record ParsedTransaction(
        BankTransactionRecord Transaction,
        string Bank,
        string VoucherType,
        string ExactSummary,
        string CanonicalSummary,
        string SourceLedgerName);

    public IReadOnlyList<BankLedgerMapping> Rebuild(long clientId, string financialYear, IReadOnlyCollection<long>? includedTransactionIds = null)
    {
        var accountNames = repository.GetAccounts(clientId, financialYear)
            .ToDictionary(item => item.Id, item => item.AuthorityName);
        var existingMappings = repository.GetLedgerMappings(clientId, financialYear).ToList();
        var allTransactions = repository.GetTransactions(clientId, financialYear, false).ToList();
        var includedIds = includedTransactionIds is null ? null : includedTransactionIds.Where(id => id > 0).ToHashSet();
        var sourceTransactions = allTransactions
            // Hub coverage rule: every active Bank Statement transaction must belong
            // to exactly one Ledger Mapping member set. Contra and reject/reversal
            // rows therefore remain represented (normally as already-mapped/system
            // rows) instead of disappearing from the workbench total.
            .Where(item => includedIds is null || includedIds.Contains(item.Id))
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
            .ToList();

        // Excel pass 1: calculate every cleaned Summary Name and its global count.
        var exactRows = sourceTransactions
            .Select(transaction =>
            {
                var voucherType = string.IsNullOrWhiteSpace(transaction.Mode) ? "All Types" : transaction.Mode.Trim();
                var exactSummary = ExcelLedgerMappingParser.BuildNormalizedSummary(transaction.Narration, voucherType);
                if (string.IsNullOrWhiteSpace(exactSummary) || BankNarrationParser.IsLedgerMappingNoiseSummary(exactSummary))
                    exactSummary = BankNarrationParser.BuildLedgerMappingFallbackSummary(transaction.Narration, voucherType);
                return new
                {
                    Transaction = transaction,
                    Bank = accountNames.TryGetValue(transaction.BankAccountId, out var bank) ? bank.Trim() : string.Empty,
                    VoucherType = voucherType,
                    ExactSummary = exactSummary.Trim()
                };
            })
            .Where(item => item.Bank.Length > 0 && item.ExactSummary.Length > 0)
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingNoiseSummary(item.ExactSummary))
            .ToList();

        var summaryCounts = exactRows
            .GroupBy(item => item.ExactSummary, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        // Excel pass 2: the CURRENT workbook chooses the best canonical name only
        // after the complete exact-count dictionary exists, using
        // GetMostFrequentSimilarName (quality + frequency).
        var groupMap = ExcelLedgerMappingParser.BuildFastGroupMap(summaryCounts);
        var parsedRows = exactRows
            .Select(item => new ParsedTransaction(
                item.Transaction,
                item.Bank,
                item.VoucherType,
                item.ExactSummary,
                groupMap.TryGetValue(item.ExactSummary, out var representative) ? representative : item.ExactSummary,
                item.Transaction.LedgerName?.Trim() ?? string.Empty))
            .Where(item => item.CanonicalSummary.Length > 0)
            .ToList();

        var masterLedgers = LedgerMappingMasterResolver.DistinctByMasterAuthority(masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived))
            .ToList();
        var masterNames = masterLedgers.Select(item => item.Name.Trim())
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var masterGroups = masterLedgers
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Parent?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        // CURRENT workbook BuildLedgerGroupCache: normalize punctuation/spacing and
        // disable any exact key that represents more than one genuinely different
        // Master ledger.  A Bank Statement ledger confirmed by this cache is already
        // valid and is therefore excluded from Ledger Mapping entirely.
        var masterByExactKey = new Dictionary<string, (string Name, string Group)>(StringComparer.OrdinalIgnoreCase);
        foreach (var master in masterLedgers)
        {
            var name = master.Name.Trim();
            var key = BankNarrationParser.NormalizeLedgerExactMatchKey(name);
            if (key.Length == 0) continue;
            var candidate = (Name: name, Group: master.Parent?.Trim() ?? string.Empty);
            if (!masterByExactKey.TryGetValue(key, out var existing))
            {
                masterByExactKey[key] = candidate;
            }
            else if (!string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                masterByExactKey[key] = (string.Empty, string.Empty);
            }
        }

        var workbenchRows = parsedRows
            .Select(item => new
            {
                Row = item,
                WorkbookGroup = BuildWorkbookMappingGroup(item.CanonicalSummary, item.SourceLedgerName, masterByExactKey)
            })
            .Where(item => item.WorkbookGroup.Length > 0)
            .ToList();

        var rebuilt = new List<BankLedgerMapping>();
        foreach (var group in workbenchRows
                     // The CURRENT workbook identity is canonical Summary Name +
                     // <BLANK> or <MISSING>normalized-current-ledger.  Bank remains a
                     // desktop persistence/filter boundary; Voucher Type is display/filter
                     // metadata and must not split the same mapping row.
                     .GroupBy(item => $"{item.Row.Bank.Trim()}\u001f{item.WorkbookGroup}", StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(item => item.Count())
                     .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First().Row;
            var groupRows = group.Select(item => item.Row).ToList();
            var voucherTypes = groupRows
                .Select(item => item.VoucherType.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sourceLedger = first.SourceLedgerName.Trim();
            var mapping = new BankLedgerMapping
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                BankFilter = first.Bank,
                VoucherType = voucherTypes.Count == 1 ? voucherTypes[0] : "Mixed",
                SummaryName = first.CanonicalSummary,
                NarrationPattern = first.CanonicalSummary,
                SourceLedgerName = sourceLedger,
                MatchCount = groupRows.Count,
                PaymentMatchCount = groupRows.Count(item => item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase)),
                ReceiptMatchCount = groupRows.Count(item => item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase)),
                TransactionIds = groupRows.Select(item => item.Transaction.Id).Where(id => id > 0).Distinct().ToList(),
                Status = "Pending",
                UpdatedUtc = DateTime.UtcNow
            };

            // Keep every active transaction represented in the desktop workbench.
            // A populated source ledger that is uniquely confirmed in Master is retained
            // as an already-Mapped row instead of disappearing from Ledger Mapping. This
            // lets Pending + Mapped coverage reconcile back to the Bank Statement total.
            if (sourceLedger.Length > 0)
            {
                var sourceKey = BankNarrationParser.NormalizeLedgerExactMatchKey(sourceLedger);
                if (sourceKey.Length > 0 &&
                    masterByExactKey.TryGetValue(sourceKey, out var confirmedMaster) &&
                    confirmedMaster.Name.Length > 0)
                {
                    mapping.LedgerName = confirmedMaster.Name;
                    mapping.MasterGroup = confirmedMaster.Group;
                    mapping.Status = "Mapped";

                    // Completion 64: a user-created partial expanded-selection split is
                    // displayed under its Final Ledger name. Preserve that display identity
                    // on later Rebuild Mapping runs by matching exact frozen membership +
                    // current source ledger. NarrationPattern remains the parser identity.
                    var groupIds = mapping.TransactionIds.ToHashSet();
                    var preservedSplit = existingMappings
                        .Where(existing => existing.TransactionIds.Any(groupIds.Contains))
                        .Where(existing => BankNarrationParser.NormalizeLedgerExactMatchKey(existing.SourceLedgerName) == sourceKey)
                        .Where(existing => BankNarrationParser.NormalizeLedgerExactMatchKey(existing.LedgerName) == sourceKey)
                        .Where(existing => BankNarrationParser.NormalizeLedgerExactMatchKey(existing.SummaryName) == sourceKey)
                        .OrderByDescending(existing => existing.UpdatedUtc)
                        .FirstOrDefault();
                    if (preservedSplit is not null)
                        mapping.SummaryName = confirmedMaster.Name;
                }
                else
                {
                    // Missing/ambiguous source ledger remains editable and requires a
                    // Master group exactly as before.
                    mapping.LedgerName = sourceLedger;
                    mapping.MasterGroup = string.Empty;
                    mapping.Status = "Suggested";
                }
            }
            else
            {
                var saved = ResolveExistingMapping(mapping, groupRows, existingMappings);
                if (saved is not null && !string.IsNullOrWhiteSpace(saved.LedgerName))
                {
                    mapping.LedgerName = ResolveMasterSpelling(saved.LedgerName, masterNames);
                    mapping.MasterGroup = !string.IsNullOrWhiteSpace(saved.MasterGroup)
                        ? saved.MasterGroup.Trim()
                        : masterGroups.GetValueOrDefault(mapping.LedgerName, string.Empty);
                    mapping.Status = saved.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase) && masterNames.Contains(mapping.LedgerName)
                        ? "Mapped"
                        : "Suggested";
                }
                // Excel leaves an unmatched canonical Summary Name in Suggested Match.
                // Final Ledger stays blank until the user double-clicks the suggestion
                // or types/selects a ledger in column F. Do not silently pre-stage it.

            }

            rebuilt.Add(mapping);
        }

        // Replace, rather than merely upsert, so obsolete month/bank/branch variants
        // cannot remain visible beside their new canonical row.
        repository.ReplaceLedgerMappings(clientId, financialYear, rebuilt);

        var mappedRows = rebuilt.Where(item =>
                item.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.LedgerName))
            .ToList();
        if (mappedRows.Count > 0)
            repository.ApplyLedgerMappings(clientId, financialYear, mappedRows);

        return repository.GetLedgerMappings(clientId, financialYear);
    }

    public static bool HasCoverageIntegrityIssue(
        IEnumerable<BankLedgerMapping> mappings,
        IEnumerable<long> activeTransactionIds)
    {
        var activeIds = activeTransactionIds.Where(id => id > 0).ToHashSet();
        var membershipCounts = new Dictionary<long, int>();
        var hasStaleMembership = false;

        foreach (var mapping in mappings)
        {
            foreach (var transactionId in mapping.TransactionIds.Where(id => id > 0).Distinct())
            {
                if (!activeIds.Contains(transactionId))
                {
                    hasStaleMembership = true;
                    continue;
                }

                membershipCounts[transactionId] = membershipCounts.GetValueOrDefault(transactionId) + 1;
            }
        }

        if (activeIds.Count == 0) return hasStaleMembership || membershipCounts.Count > 0;
        if (hasStaleMembership || membershipCounts.Count != activeIds.Count) return true;
        return activeIds.Any(id => membershipCounts.GetValueOrDefault(id) != 1);
    }

    public LedgerSuggestionEvidence ResolveSuggestionEvidence(
        long clientId,
        string financialYear,
        string summaryName,
        string existingLedgerMatch)
    {
        var displaySummary = AccountingText.DisplayName(summaryName);
        if (!string.IsNullOrWhiteSpace(existingLedgerMatch))
        {
            return new LedgerSuggestionEvidence
            {
                SummaryName = displaySummary,
                SuggestedLedger = string.Empty,
                Confidence = 100d,
                Reason = "Existing Master match",
                Source = "Master"
            };
        }

        var summaryKey = BankNarrationParser.NormalizeLedgerExactMatchKey(displaySummary);
        var learned = governanceRepository?.GetLearnedLedger(clientId, financialYear, summaryKey);
        if (learned is not null && learned.Value.AcceptCount > 0)
        {
            var masters = masterRepository.Get(clientId, financialYear, false, string.Empty)
                .Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !x.IsArchived)
                .ToArray();
            var learnedKey = TallyMasterIdentity.Key(learned.Value.Ledger);
            var matches = masters.Where(x => TallyMasterIdentity.Key(x.Name) == learnedKey).Take(2).ToArray();

            // Fix131: Suggested Match is only a proposal for a genuinely new Ledger name.
            // A learned name that is already an active Master belongs in Existing Ledger Match
            // only when the full Summary/explicit identity matches it; never repeat an existing
            // Master in Suggested Match merely because it was approved for a different summary.
            if (matches.Length == 0 && learnedKey.Length > 0)
            {
                return new LedgerSuggestionEvidence
                {
                    SummaryName = displaySummary,
                    SuggestedLedger = AccountingText.DisplayName(learned.Value.Ledger),
                    SuggestedGroup = AccountingText.DisplayName(learned.Value.Group),
                    Confidence = Math.Min(99d, 92d + Math.Min(7, learned.Value.AcceptCount)),
                    Reason = $"Previously approved new-ledger name ({learned.Value.AcceptCount:N0} time(s))",
                    Source = "Learning",
                    PreviousAcceptCount = learned.Value.AcceptCount
                };
            }
        }

        // Fix131: never place an already existing Master in Suggested Match. This also
        // protects a user-rejected Existing Ledger Match from reappearing immediately in the
        // neighboring Suggested column under the same parsed name.
        var displayKey = TallyMasterIdentity.Key(displaySummary);
        if (displayKey.Length > 0 && masterRepository.Get(clientId, financialYear, false, string.Empty)
                .Any(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !x.IsArchived && TallyMasterIdentity.Key(x.Name) == displayKey))
        {
            return new LedgerSuggestionEvidence
            {
                SummaryName = displaySummary,
                SuggestedLedger = string.Empty,
                Confidence = 0d,
                Reason = "Existing Master names are not suggestions",
                Source = "Master"
            };
        }

        if (governanceRepository?.IsLedgerSuggestionRejected(clientId, financialYear, summaryKey, displaySummary) == true)
        {
            return new LedgerSuggestionEvidence
            {
                SummaryName = displaySummary,
                SuggestedLedger = string.Empty,
                Confidence = 0d,
                Reason = "User rejected this automatic suggestion",
                Source = "User"
            };
        }

        return new LedgerSuggestionEvidence
        {
            SummaryName = displaySummary,
            SuggestedLedger = displaySummary,
            Confidence = 60d,
            Reason = "Parsed party / narration summary; review before mapping",
            Source = "Parser"
        };
    }

    public int Apply(BankLedgerMapping mapping, bool createMaster)
    {
        DemandPermission(GovernancePermissions.LedgerMapping, "apply Ledger Mapping");
        if (createMaster) DemandPermission(GovernancePermissions.CreateMaster, "create a Ledger Master from Ledger Mapping");
        if (mapping.ClientId <= 0 || mapping.LedgerName.Trim().Length == 0)
            throw new InvalidOperationException("Select a ledger before applying the mapping.");

        var ledgerGroups = masterRepository.Get(mapping.ClientId, mapping.FinancialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => TallyMasterIdentity.Key(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var requested = AccountingText.DisplayName(mapping.LedgerName);
        var key = TallyMasterIdentity.Key(requested);
        ledgerGroups.TryGetValue(key, out var matches);
        if (matches?.Length > 1)
            throw new InvalidOperationException($"Final Ledger '{requested}' matches duplicate Masters. Resolve the duplicate Masters first.");

        if (matches is null && createMaster)
        {
            masterRepository.Save(new MasterRecord
            {
                ClientId = mapping.ClientId,
                FinancialYear = mapping.FinancialYear,
                Name = requested,
                Parent = mapping.MasterGroup.Trim().Length > 0 ? AccountingText.DisplayName(mapping.MasterGroup) : "Indirect Expenses",
                Type = "Ledger",
                Source = "Bank Ledger Mapping",
                IsUserEdited = true
            });
            matches = masterRepository.Get(mapping.ClientId, mapping.FinancialYear, false, string.Empty)
                .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
                .Where(item => TallyMasterIdentity.Key(item.Name) == key)
                .Take(2)
                .ToArray();
        }

        if (matches is null || matches.Length != 1)
            throw new InvalidOperationException("The selected ledger does not exist in Master. Select Create missing Master ledger or choose an existing ledger.");

        mapping.LedgerName = matches[0].Name.Trim();
        mapping.MasterGroup = matches[0].Parent?.Trim() ?? mapping.MasterGroup;
        mapping.Status = "Mapped";
        mapping.UpdatedUtc = DateTime.UtcNow;
        repository.UpsertLedgerMapping(mapping);
        var applied = repository.ApplyLedgerMapping(mapping.ClientId, mapping.FinancialYear, mapping);
        if (applied > 0 && governanceRepository is not null)
        {
            governanceRepository.RecordLedgerSuggestionDecision(
                mapping.ClientId,
                mapping.FinancialYear,
                BankNarrationParser.NormalizeLedgerExactMatchKey(mapping.SummaryName),
                mapping.LedgerName,
                mapping.MasterGroup,
                true,
                "Ledger Mapping applied");
        }
        return applied;
    }


    public bool Clear(BankLedgerMapping mapping)
    {
        DemandPermission(GovernancePermissions.LedgerMapping, "clear Ledger Mapping");
        ArgumentNullException.ThrowIfNull(mapping);
        var oldLedger = mapping.LedgerName?.Trim() ?? string.Empty;
        repository.ClearLedgerMapping(mapping);
        return TryArchiveUnusedAutoCreatedLedger(mapping.ClientId, mapping.FinancialYear, oldLedger);
    }

    public (int ClearedTransactions, int ArchivedMasters) ClearMany(
        long clientId,
        string financialYear,
        IReadOnlyCollection<long> transactionIds)
    {
        DemandPermission(GovernancePermissions.LedgerMapping, "bulk remove Final Ledger");
        ArgumentNullException.ThrowIfNull(transactionIds);
        var ids = transactionIds.Where(id => id > 0).Distinct().ToHashSet();
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || ids.Count == 0) return (0, 0);

        var oldLedgers = repository.GetTransactions(clientId, financialYear, true)
            .Where(item => ids.Contains(item.Id))
            .Select(item => item.LedgerName?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cleared = repository.ClearLedgerMappingsForTransactions(clientId, financialYear, ids);
        var archived = 0;
        foreach (var ledger in oldLedgers)
            if (TryArchiveUnusedAutoCreatedLedger(clientId, financialYear, ledger)) archived++;
        return (cleared, archived);
    }

    private bool TryArchiveUnusedAutoCreatedLedger(long clientId, string financialYear, string ledgerName)
    {
        var oldLedger = ledgerName?.Trim() ?? string.Empty;
        if (oldLedger.Length == 0) return false;

        // Only retire a Master that this Ledger Mapping feature itself created and
        // that is no longer referenced anywhere in Banking/Cash. Genuine/manual/Tally
        // Masters are never archived by clearing a mapping.
        var key = TallyMasterIdentity.Key(oldLedger);
        var master = masterRepository.Get(clientId, financialYear, true, string.Empty)
            .FirstOrDefault(item => !item.IsArchived &&
                                    item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                    TallyMasterIdentity.Key(item.Name) == key &&
                                    item.Source.Contains("Bank Ledger Mapping", StringComparison.OrdinalIgnoreCase));
        if (master is null) return false;

        var stillMapped = repository.GetLedgerMappings(clientId, financialYear)
            .Any(item => !string.IsNullOrWhiteSpace(item.LedgerName) && TallyMasterIdentity.Key(item.LedgerName) == key);
        var usedByBank = repository.GetTransactions(clientId, financialYear, true)
            .Any(item => !string.IsNullOrWhiteSpace(item.LedgerName) && TallyMasterIdentity.Key(item.LedgerName) == key);
        var usedByCash = repository.GetCashEntries(clientId, financialYear)
            .Any(item => TallyMasterIdentity.Key(item.EntryLedger) == key || TallyMasterIdentity.Key(item.CommonLedger) == key);
        if (stillMapped || usedByBank || usedByCash) return false;

        masterRepository.Archive(master.PersistentId, true);
        return true;
    }

    public int ApplyMany(IEnumerable<BankLedgerMapping> mappings, bool createMaster, bool exactTransactionScope = false)
    {
        DemandPermission(GovernancePermissions.LedgerMapping, "apply Ledger Mapping");
        if (createMaster) DemandPermission(GovernancePermissions.CreateMaster, "create Ledger Masters from Ledger Mapping");
        ArgumentNullException.ThrowIfNull(mappings);
        var rows = mappings
            .Where(item => item.ClientId > 0 && !string.IsNullOrWhiteSpace(item.LedgerName))
            .ToList();
        if (rows.Count == 0) return 0;
        if (exactTransactionScope && rows.Any(item => item.TransactionIds.Count == 0))
            throw new InvalidOperationException("Amount-filtered Bulk Mapping requires exact Bank transaction membership for every selected mapping row.");

        var clientId = rows[0].ClientId;
        var financialYear = rows[0].FinancialYear;
        if (rows.Any(item => item.ClientId != clientId || !item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Map All can only process one client and Financial Year at a time.");

        var masterLedgers = LedgerMappingMasterResolver.DistinctByMasterAuthority(masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name)))
            .ToList();
        var byKey = masterLedgers
            .GroupBy(item => TallyMasterIdentity.Key(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var key = TallyMasterIdentity.Key(row.LedgerName);
            if (byKey.TryGetValue(key, out var duplicateMatches) && duplicateMatches.Length > 1)
                throw new InvalidOperationException($"Final Ledger '{AccountingText.DisplayName(row.LedgerName)}' matches duplicate Masters. Resolve the duplicate Masters before Map All.");
        }

        var missing = rows
            .Where(item => !byKey.ContainsKey(TallyMasterIdentity.Key(item.LedgerName)))
            .GroupBy(item => TallyMasterIdentity.Key(item.LedgerName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (missing.Count > 0 && !createMaster)
            throw new InvalidOperationException("Some Ready rows use new ledgers. Enable Create missing Master ledger or select existing ledgers.");
        if (missing.Any(item => string.IsNullOrWhiteSpace(item.MasterGroup)))
            throw new InvalidOperationException("Every new Final Ledger must have a Ledger Group before Map All.");

        foreach (var item in missing)
        {
            masterRepository.Save(new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                Name = AccountingText.DisplayName(item.LedgerName),
                Parent = AccountingText.DisplayName(item.MasterGroup),
                Type = "Ledger",
                Source = "Bank Ledger Mapping",
                IsUserEdited = true
            });
        }

        // Re-read after inserts so every mapped row uses the actual Master spelling/group.
        byKey = LedgerMappingMasterResolver.DistinctByMasterAuthority(masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name)))
            .GroupBy(item => TallyMasterIdentity.Key(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in rows)
        {
            var matches = byKey.GetValueOrDefault(TallyMasterIdentity.Key(mapping.LedgerName), []);
            if (matches.Length != 1)
                throw new InvalidOperationException($"Final Ledger '{mapping.LedgerName}' could not be resolved uniquely in Master.");
            mapping.LedgerName = matches[0].Name.Trim();
            mapping.MasterGroup = matches[0].Parent?.Trim() ?? mapping.MasterGroup;
            mapping.Status = "Mapped";
            mapping.UpdatedUtc = DateTime.UtcNow;
            // Amount-filtered Bulk Mapping is an exact transaction operation, not a new summary rule.
            // Persisting this partial membership as the normal mapping identity would let the next
            // rebuild expand the ledger to same-summary transactions outside the requested amount range.
            if (!exactTransactionScope) repository.UpsertLedgerMapping(mapping);
        }

        var applied = repository.ApplyLedgerMappings(clientId, financialYear, rows);
        if (governanceRepository is not null && !exactTransactionScope)
        {
            foreach (var mapping in rows)
            {
                governanceRepository.RecordLedgerSuggestionDecision(
                    mapping.ClientId, mapping.FinancialYear,
                    BankNarrationParser.NormalizeLedgerExactMatchKey(mapping.SummaryName),
                    mapping.LedgerName, mapping.MasterGroup, true, "Ledger Mapping batch applied");
            }
        }
        return applied;
    }


    public bool IsSuggestionRejected(long clientId, string financialYear, string summaryKey, string ledgerName) =>
        governanceRepository?.IsLedgerSuggestionRejected(clientId, financialYear, summaryKey, ledgerName) == true;

    public void RecordSuggestionRejection(long clientId, string financialYear, string summaryKey, string ledgerName, string ledgerGroup, string reason)
    {
        if (governanceRepository is null || clientId <= 0 || string.IsNullOrWhiteSpace(summaryKey) || string.IsNullOrWhiteSpace(ledgerName)) return;
        governanceRepository.RecordLedgerSuggestionDecision(clientId, financialYear, summaryKey, ledgerName, ledgerGroup, false, reason);
    }

    private void DemandPermission(string permission, string action)
    {
        if (governanceRepository is not null &&
            !governanceRepository.HasPermission(Environment.UserName, permission))
            throw new UnauthorizedAccessException($"The current Windows user does not have permission to {action}.");
    }

    public IReadOnlyList<MasterRecord> GetLedgerSuggestions(long clientId, string financialYear, string text)
    {
        var query = text.Trim();
        return masterRepository.Get(clientId, financialYear, false, query)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();
    }

    private static BankLedgerMapping? ResolveExistingMapping(
        BankLedgerMapping rebuilt,
        IReadOnlyList<ParsedTransaction> groupRows,
        IReadOnlyList<BankLedgerMapping> existingMappings)
    {
        var candidates = existingMappings
            .Where(item => AccountMatches(item.BankFilter, rebuilt.BankFilter))
            .Where(item => VoucherMatches(item.VoucherType, rebuilt.VoucherType))
            .Select(item => new { Mapping = item, Score = ExistingMappingScore(item, rebuilt, groupRows) })
            .Where(item => item.Score > 0 && !string.IsNullOrWhiteSpace(item.Mapping.LedgerName))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Mapping.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Mapping.UpdatedUtc)
            .ToList();

        if (candidates.Count == 0) return null;
        var highestScore = candidates[0].Score;
        var top = candidates.Where(item => item.Score == highestScore).ToList();
        var distinctLedgers = top.Select(item => item.Mapping.LedgerName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Do not silently migrate two conflicting ledgers into one canonical row.
        return distinctLedgers.Count == 1 ? top[0].Mapping : null;
    }

    private static int ExistingMappingScore(
        BankLedgerMapping existing,
        BankLedgerMapping rebuilt,
        IReadOnlyList<ParsedTransaction> groupRows)
    {
        var existingSummary = existing.SummaryName?.Trim() ?? string.Empty;
        var existingPattern = existing.NarrationPattern?.Trim() ?? string.Empty;
        if (existingSummary.Equals(rebuilt.SummaryName, StringComparison.OrdinalIgnoreCase) ||
            existingPattern.Equals(rebuilt.NarrationPattern, StringComparison.OrdinalIgnoreCase)) return 100;

        // Parser/noise repairs are allowed to rename or merge workbench Summary Names,
        // but they must never discard a user's existing Ledger decision. Transaction
        // membership is the strongest migration identity after exact summary equality.
        // If several old noisy rows collapse into one clean party row, migration occurs
        // only when their top-scoring saved Ledger is unambiguous (ResolveExistingMapping
        // already rejects conflicting ledgers).
        var groupTransactionIds = groupRows.Select(item => item.Transaction.Id).Where(id => id > 0).ToHashSet();
        if (existing.TransactionIds.Any(id => id > 0 && groupTransactionIds.Contains(id))) return 98;

        // Parser repairs may remove a proven bank reference token (for example
        // UTILITY CT0AB... MS SOWMYA -> UTILITY MS SOWMYA). Treat that as the same
        // workbench identity so a saved mapping is migrated instead of discarded.
        var rebuiltComparable = BankNarrationParser.NormalizeLedgerMappingComparisonName(rebuilt.SummaryName);
        var existingSummaryComparable = BankNarrationParser.NormalizeLedgerMappingComparisonName(existingSummary);
        var existingPatternComparable = BankNarrationParser.NormalizeLedgerMappingComparisonName(existingPattern);
        if ((existingSummaryComparable.Length > 0 && existingSummaryComparable.Equals(rebuiltComparable, StringComparison.OrdinalIgnoreCase)) ||
            (existingPatternComparable.Length > 0 && existingPatternComparable.Equals(rebuiltComparable, StringComparison.OrdinalIgnoreCase))) return 95;

        // Fix131: never migrate a saved mapping by fuzzy/partial party similarity.
        // Only exact normalized identity or proven frozen transaction membership may carry
        // an earlier Ledger decision into a rebuilt Summary.
        if (groupRows.Any(item =>
                item.ExactSummary.Equals(existingSummary, StringComparison.OrdinalIgnoreCase) ||
                item.ExactSummary.Equals(existingPattern, StringComparison.OrdinalIgnoreCase) ||
                item.CanonicalSummary.Equals(existingSummary, StringComparison.OrdinalIgnoreCase) ||
                item.CanonicalSummary.Equals(existingPattern, StringComparison.OrdinalIgnoreCase))) return 80;

        // Do not migrate by narration Contains. The workbook cache migrates only
        // the parsed/canonical group; broad substring matching can attach a saved
        // ledger to a different party.
        return 0;
    }

    private static bool AccountMatches(string filter, string bank)
    {
        if (string.IsNullOrWhiteSpace(filter) ||
            filter.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase) ||
            filter.Equals(bank, StringComparison.OrdinalIgnoreCase))
            return true;

        var filterIdentity = BankAccountNumberIdentity(filter);
        var bankIdentity = BankAccountNumberIdentity(bank);
        return filterIdentity.Length >= 6 &&
               filterIdentity.Equals(bankIdentity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Bank Account rename (for example stale OD text to the current CA Master) must
    /// not orphan saved Ledger Mapping decisions. The long account-number token is the
    /// migration identity; the rebuilt row is always persisted with the current
    /// BankAccountRecord.AuthorityName.
    /// </summary>
    public static string BankAccountNumberIdentity(string? value)
    {
        var best = string.Empty;
        var current = new List<char>();
        foreach (var character in value ?? string.Empty)
        {
            if (char.IsDigit(character))
            {
                current.Add(character);
                continue;
            }
            if (current.Count >= 6 && current.Count > best.Length)
                best = new string(current.ToArray());
            current.Clear();
        }
        if (current.Count >= 6 && current.Count > best.Length)
            best = new string(current.ToArray());
        return best;
    }

    private static bool VoucherMatches(string filter, string voucher) =>
        string.IsNullOrWhiteSpace(filter) ||
        filter.Equals("ALL TYPES", StringComparison.OrdinalIgnoreCase) ||
        filter.Equals("Mixed", StringComparison.OrdinalIgnoreCase) ||
        voucher.Equals("Mixed", StringComparison.OrdinalIgnoreCase) ||
        filter.Equals(voucher, StringComparison.OrdinalIgnoreCase);

    private static string ResolveMasterSpelling(string requested, IReadOnlySet<string> masterNames)
    {
        var value = requested.Trim();
        return masterNames.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) ??
               masterNames.FirstOrDefault(item => TallyMasterIdentity.Equivalent(item, value)) ??
               value;
    }

    private static (string Ledger, string Group) SuggestAutomaticLedger(string narration)
    {
        var normalized = BankNarrationParser.Normalize(narration);
        if (BankNarrationParser.IsBankCharge(normalized)) return ("Bank Charges", "Indirect Expenses");
        if (normalized.Contains("INTEREST CREDIT") || normalized.Contains("INT CR")) return ("Interest Received", "Indirect Incomes");
        if (normalized.Contains("INTEREST DEBIT") || normalized.Contains("INT DR")) return ("Interest Paid", "Indirect Expenses");
        if (normalized.Contains("GST") && (normalized.Contains("PAY") || normalized.Contains("CPIN") || normalized.Contains("CHALLAN")))
            return ("GST Payable / Receivable", "Duties & Taxes");
        return (string.Empty, string.Empty);
    }

    private static string BuildWorkbookMappingGroup(
        string summaryName,
        string sourceLedgerName,
        IReadOnlyDictionary<string, (string Name, string Group)> masterByExactKey)
    {
        var summary = summaryName.Trim();
        var existingLedger = sourceLedgerName.Trim();
        if (summary.Length == 0) return string.Empty;
        if (existingLedger.Length == 0) return $"{summary}\u001e<BLANK>";

        var ledgerKey = BankNarrationParser.NormalizeLedgerExactMatchKey(existingLedger);
        if (ledgerKey.Length > 0 &&
            masterByExactKey.TryGetValue(ledgerKey, out var masterInfo) &&
            masterInfo.Name.Length > 0)
        {
            // Desktop coverage extension: keep confirmed source-ledger transactions as
            // Mapped rows so they remain visible under the Mapped filter and the complete
            // workbench membership reconciles to active Bank Statement transactions.
            return $"{summary}\u001e<VALID>{ledgerKey}";
        }

        return $"{summary}\u001e<MISSING>{ledgerKey}";
    }
}
