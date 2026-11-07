using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Executes only the first (master) stage of a same-PAN consolidated import.
/// Reuses the application's existing Tally HTTP transport and single-flight gate.
/// It deliberately never treats an XML preview or Tally's CREATED counter as
/// evidence of voucher success; that needs independently verified source rows.
/// </summary>
public sealed class TallyBranchConsolidationImportService(TallyHttpService tally)
{
    /// <summary>
    /// The saved Client/FY Tally Link is authoritative for the selected
    /// accounting period. Tally itself may return no FinancialYear metadata
    /// for an otherwise uniquely open linked company. Reject an explicit
    /// contradictory year, not a missing optional metadata field.
    /// </summary>
    public static string RequireConfirmedDestination(
        TallyClientLink link, TallyCompanyInfo destination, string? accountingFinancialYear = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(destination);
        // SQL TallyClientLinks.FinancialYear is Tally-discovery metadata (possibly
        // empty, or the FIRST year of company books), not the AAH Client/FY.
        // The consolidated parent workspace supplies its explicit accounting FY.
        var effectiveFinancialYear = accountingFinancialYear ?? link.FinancialYear;
        if (link.ClientId <= 0 ||
            string.IsNullOrWhiteSpace(link.CompanyKey) ||
            string.IsNullOrWhiteSpace(destination.CompanyKey) ||
            !link.CompanyKey.Trim().Equals(destination.CompanyKey.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(link.CompanyName) ||
            string.IsNullOrWhiteSpace(destination.Name))
            throw new InvalidOperationException(
                "The selected consolidated destination does not match the saved Tally company identity. " +
                "No XML or import was sent; inspect the original parent Tally link.");
        if (string.IsNullOrWhiteSpace(effectiveFinancialYear))
            throw new InvalidOperationException(
                "The consolidated accounting FY is missing. " +
                "Use the parent workspace financial year; Tally discovery FY may be blank.");

        // Unlike ordinary branch export, consolidated import uses the saved parent
        // CompanyKey to identify the destination. Tally may subsequently rename
        // that SAME company (e.g. add 'Consolidated') without changing its
        // GSTIN/company-number-based key. The stored display name is not an
        // independent accounting identity and must not veto a valid linked key.
        // Never accept contradictory nonempty strong metadata.
        static bool Conflicting(string saved, string selected) =>
            !string.IsNullOrWhiteSpace(saved) &&
            !string.IsNullOrWhiteSpace(selected) &&
            !saved.Trim().Equals(selected.Trim(), StringComparison.OrdinalIgnoreCase);
        if (Conflicting(link.CompanyNumber, destination.CompanyNumber) ||
            Conflicting(TallyCompanyInfo.NormalizeGstin(link.Gstin),
                        TallyCompanyInfo.NormalizeGstin(destination.Gstin)))
            throw new InvalidOperationException(
                "The selected consolidated Tally company number or GSTIN contradicts the saved parent link.");
        var linkedBookStart = TallyCompanyInfo.ParseTallyDate(link.BooksFrom);
        var selectedBookStart = TallyCompanyInfo.ParseTallyDate(destination.BooksFrom);
        if (linkedBookStart.HasValue && selectedBookStart.HasValue &&
            linkedBookStart.Value.Date != selectedBookStart.Value.Date)
            throw new InvalidOperationException(
                "The selected consolidated Tally BooksFrom differs from the saved parent link.");

        RequireLinkedFinancialYearCoverage(effectiveFinancialYear, destination);
        return TallyCompanyInfo.NormalizeFinancialYear(effectiveFinancialYear);
    }

    /// <summary>
    /// Tally's Company.FinancialYear in the existing discovery path is derived
    /// from BooksFrom/StartingFrom. It is the company's FIRST book year, not a
    /// restriction that the company only contains that financial year.
    /// The selected AAH Client/FY link remains the period authority.
    /// </summary>
    private static void RequireLinkedFinancialYearCoverage(string financialYear, TallyCompanyInfo company)
    {
        var linkedYear = TallyCompanyInfo.NormalizeFinancialYear(financialYear);
        if (linkedYear.Length != 7 || linkedYear[4] != '-' ||
            !int.TryParse(linkedYear[..4], out var startYear) ||
            startYear < 1900 || startYear > 9998 ||
            !linkedYear.Equals(
                $"{startYear:0000}-{(startYear + 1) % 100:00}",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved consolidated Client/FY is invalid.");

        var linkedEnd = new DateTime(startYear + 1, 3, 31);
        var bookStart = TallyCompanyInfo.ParseTallyDate(company.BooksFrom);
        var startingFrom = TallyCompanyInfo.ParseTallyDate(company.StartingFrom);
        if ((bookStart is not null && bookStart.Value.Date > linkedEnd) ||
            (startingFrom is not null && startingFrom.Value.Date > linkedEnd))
            throw new InvalidOperationException(
                "The linked financial year precedes the Tally company's books/start date.");

        var reported = TallyCompanyInfo.NormalizeFinancialYear(company.FinancialYear);
        var inferredBookYear = TallyCompanyInfo.NormalizeFinancialYear(
            "", !string.IsNullOrWhiteSpace(company.BooksFrom)
                ? company.BooksFrom : company.StartingFrom);
        if (reported.Length > 0 &&
            !reported.Equals(linkedYear, StringComparison.OrdinalIgnoreCase) &&
            !reported.Equals(inferredBookYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Tally reported a financial year not explained by its books/start date or the saved Client/FY link.");
    }

    /// <summary>
    /// The coordinator preserves the confirmed CompanyKey for the top dropdown.
    /// A second raw HTTP discovery can derive a DIFFERENT key from the book-start
    /// FY; verify the exact single live company by independent saved identity
    /// instead of treating that computed key as a new unlink/relink request.
    /// No source/client switching or write is allowed on an ambiguous identity.
    /// </summary>
    public static TallyCompanyInfo RequireUniqueLiveDestination(
        TallyClientLink link, TallyCompanyInfo destination,
        IReadOnlyCollection<TallyCompanyInfo> discovered, string? accountingFinancialYear = null)
    {
        RequireConfirmedDestination(link, destination, accountingFinancialYear);
        ArgumentNullException.ThrowIfNull(discovered);
        var matchesByName = discovered.Where(company =>
            company.Name.Trim().Equals(destination.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchesByName.Length != 1)
            throw new InvalidOperationException(
                "The exact linked consolidated Tally company is not uniquely open; no import sent.");

        var live = matchesByName[0];
        static bool HasValue(string value) => !string.IsNullOrWhiteSpace(value);
        static bool Same(string a, string b) =>
            a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
        var expectedNumber = HasValue(link.CompanyNumber)
            ? link.CompanyNumber : destination.CompanyNumber;
        var expectedGstin = HasValue(link.Gstin)
            ? TallyCompanyInfo.NormalizeGstin(link.Gstin)
            : TallyCompanyInfo.NormalizeGstin(destination.Gstin);
        var actualGstin = TallyCompanyInfo.NormalizeGstin(live.Gstin);
        if ((HasValue(expectedNumber) && HasValue(live.CompanyNumber) &&
             !Same(expectedNumber, live.CompanyNumber)) ||
            (HasValue(expectedGstin) && HasValue(actualGstin) &&
             !Same(expectedGstin, actualGstin)))
            throw new InvalidOperationException(
                "Live Tally company number or GSTIN differs from the saved consolidated link.");

        var savedBookDate = TallyCompanyInfo.ParseTallyDate(link.BooksFrom) ??
                            TallyCompanyInfo.ParseTallyDate(destination.BooksFrom);
        var liveBookDate = TallyCompanyInfo.ParseTallyDate(live.BooksFrom);
        if (savedBookDate is not null && liveBookDate is not null &&
            savedBookDate.Value.Date != liveBookDate.Value.Date)
            throw new InvalidOperationException(
                "Live Tally BooksFrom differs from the linked consolidated company.");

        var sameKey = Same(link.CompanyKey, live.CompanyKey);
        var independentlyConfirmed =
            (HasValue(expectedNumber) && HasValue(live.CompanyNumber) &&
             Same(expectedNumber, live.CompanyNumber)) ||
            (HasValue(expectedGstin) && HasValue(actualGstin) &&
             Same(expectedGstin, actualGstin)) ||
            (savedBookDate is not null && liveBookDate is not null &&
             savedBookDate.Value.Date == liveBookDate.Value.Date);
        if (!sameKey && !independentlyConfirmed)
            throw new InvalidOperationException(
                "Tally regenerated the company key without a matching saved company number, GSTIN or BooksFrom. Confirm the existing link; no import sent.");

        RequireLinkedFinancialYearCoverage(accountingFinancialYear ?? link.FinancialYear, live);
        return live;
    }

    /// <summary>
    /// Consolidation is only a wrapper around the already-confirmed parent Tally link.
    /// The ordinary Tally coordinator may rediscover the same live company with a
    /// recomputed key (name/first-books FY drift). Reuse the ORIGINAL saved link only
    /// after unique, independent company-number/GSTIN evidence and no contradictions.
    /// This does not relink, mutate the ComboBox, or permit another company to import.
    /// The live importers repeat independent Tally discovery and master readback.
    /// </summary>
    public static TallyCompanyInfo ResolveLinkedDestination(
        TallyClientLink link,
        TallyCompanyInfo? selected,
        IReadOnlyCollection<TallyCompanyInfo> openCompanies,
        string? accountingFinancialYear = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(openCompanies);
        if (selected is null)
        {
            var existing = openCompanies.Where(company =>
                company.CompanyKey.Trim().Equals(link.CompanyKey.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existing.Length == 1)
            {
                selected = existing[0];
            }
            else if (existing.Length == 0)
            {
                // Ordinary 5-second discovery may replace the WPF ComboBox item
                // with a recomputed key and temporarily clear SelectedItem.
                // Recover only a UNIQUE target independently proved by a saved
                // CompanyNumber/GSTIN, with no contradictory BooksFrom or ID.
                static bool SameRecovery(string a, string b) =>
                    a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
                var recoveryGstin = TallyCompanyInfo.NormalizeGstin(link.Gstin);
                var recoveryBooks = TallyCompanyInfo.ParseTallyDate(link.BooksFrom);
                var candidates = openCompanies.Where(company =>
                {
                    var number = !string.IsNullOrWhiteSpace(link.CompanyNumber) &&
                                 !string.IsNullOrWhiteSpace(company.CompanyNumber) &&
                                 SameRecovery(link.CompanyNumber, company.CompanyNumber);
                    var gstin = recoveryGstin.Length > 0 &&
                                SameRecovery(recoveryGstin, TallyCompanyInfo.NormalizeGstin(company.Gstin));
                    var numberConflict = !string.IsNullOrWhiteSpace(link.CompanyNumber) &&
                                         !string.IsNullOrWhiteSpace(company.CompanyNumber) && !number;
                    var gstinConflict = recoveryGstin.Length > 0 &&
                                        TallyCompanyInfo.NormalizeGstin(company.Gstin).Length > 0 && !gstin;
                    var companyBooks = TallyCompanyInfo.ParseTallyDate(company.BooksFrom);
                    return (number || gstin) && !numberConflict && !gstinConflict &&
                           (!recoveryBooks.HasValue || !companyBooks.HasValue ||
                            recoveryBooks.Value.Date == companyBooks.Value.Date) &&
                           (company.LinkedClientId <= 0 || company.LinkedClientId == link.ClientId);
                }).ToArray();
                if (candidates.Length == 1)
                    selected = candidates[0];
            }
            if (selected is null)
                throw new InvalidOperationException(
                    "The saved consolidated Tally company is not uniquely open. Select the intended company and use the existing Link control; no import was sent.");
        }

        if (selected.LinkedClientId > 0 && selected.LinkedClientId != link.ClientId)
            throw new InvalidOperationException(
                "The selected Tally company is linked to another client. The consolidated destination was not changed.");

        var sameKey = selected.CompanyKey.Trim().Equals(link.CompanyKey.Trim(), StringComparison.OrdinalIgnoreCase);
        if (sameKey)
        {
            RequireConfirmedDestination(link, selected, accountingFinancialYear);
            return selected;
        }

        static bool Has(string value) => !string.IsNullOrWhiteSpace(value);
        static bool Same(string a, string b) =>
            a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
        var savedGstin = TallyCompanyInfo.NormalizeGstin(link.Gstin);
        var selectedGstin = TallyCompanyInfo.NormalizeGstin(selected.Gstin);
        var numberMatch = Has(link.CompanyNumber) && Has(selected.CompanyNumber) &&
                          Same(link.CompanyNumber, selected.CompanyNumber);
        var gstinMatch = Has(savedGstin) && Has(selectedGstin) && Same(savedGstin, selectedGstin);
        var numberConflict = Has(link.CompanyNumber) && Has(selected.CompanyNumber) && !numberMatch;
        var gstinConflict = Has(savedGstin) && Has(selectedGstin) && !gstinMatch;
        var savedBooks = TallyCompanyInfo.ParseTallyDate(link.BooksFrom);
        var selectedBooks = TallyCompanyInfo.ParseTallyDate(selected.BooksFrom);
        var booksConflict = savedBooks.HasValue && selectedBooks.HasValue &&
                            savedBooks.Value.Date != selectedBooks.Value.Date;

        // Never promote company NAME, Tally availability or FY into identity evidence.
        // In particular two distinct companies may share a GSTIN, so also require
        // a unique live selected company and reject every contradictory known field.
        if ((!numberMatch && !gstinMatch) || numberConflict || gstinConflict || booksConflict)
            throw new InvalidOperationException(
                "The selected Tally company is not independently verified as the saved consolidated link. " +
                "Use the existing Link control to confirm the intended company; no XML or Tally import was sent.");

        var selectedLiveMatches = openCompanies.Where(company =>
            Same(company.CompanyKey, selected.CompanyKey) &&
            Same(company.Name, selected.Name)).ToArray();
        // When a key has drifted, independent identity must uniquely identify
        // this ONE open company, not just the currently selected ComboBox entry.
        // Different Tally books may reuse a GSTIN or company number.
        var strongLiveMatches = openCompanies.Where(company =>
        {
            var number = Has(link.CompanyNumber) && Has(company.CompanyNumber) &&
                         Same(link.CompanyNumber, company.CompanyNumber);
            var gstin = Has(savedGstin) && Has(company.Gstin) &&
                        Same(savedGstin, TallyCompanyInfo.NormalizeGstin(company.Gstin));
            var liveBooks = TallyCompanyInfo.ParseTallyDate(company.BooksFrom);
            var dateCompatible = !savedBooks.HasValue || !liveBooks.HasValue ||
                                 savedBooks.Value.Date == liveBooks.Value.Date;
            return (number || gstin) && dateCompatible;
        }).ToArray();
        if (selectedLiveMatches.Length != 1 || strongLiveMatches.Length != 1 ||
            !ReferenceEquals(selectedLiveMatches[0], strongLiveMatches[0]))
            throw new InvalidOperationException(
                "The selected consolidated Tally company is not uniquely open or uniquely identified by the saved link. " +
                "Use the existing Link control; no XML or import was sent.");

        // The original linked CompanyKey is kept as the accounting authority;
        // the actual live company name remains the HTTP transport destination.
        var resolved = new TallyCompanyInfo
        {
            CompanyKey = link.CompanyKey,
            Name = selected.Name,
            CompanyNumber = selected.CompanyNumber,
            Gstin = selected.Gstin,
            StateName = selected.StateName,
            BooksFrom = selected.BooksFrom,
            StartingFrom = selected.StartingFrom,
            FinancialYear = selected.FinancialYear,
            LinkedClientId = link.ClientId,
            LinkedClientCode = selected.LinkedClientCode,
            LinkedClientName = selected.LinkedClientName
        };
        RequireConfirmedDestination(link, resolved, accountingFinancialYear);
        return resolved;
    }

    public async Task<TallyBranchMasterImportResult> ImportMastersAsync(
        TallyClientLink confirmedLink,
        TallyCompanyInfo destination,
        IReadOnlyCollection<TallyBranchMasterIdentity> branches,
        bool separateVoucherTypes = false,
        bool separateCashLedgers = false,
        CancellationToken cancellationToken = default,
        string? accountingFinancialYear = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? usedVoucherTypesByBranch = null,
        bool createOnlyVoucherTypes = false)
    {
        // Creation-only never writes cash ledgers, groups, voucher transactions
        // or any already present voucher type (including numbering settings).
        if (createOnlyVoucherTypes && (!separateVoucherTypes || separateCashLedgers))
            throw new InvalidOperationException(
                "Voucher-type creation-only mode cannot change cash, groups or vouchers.");
        ArgumentNullException.ThrowIfNull(confirmedLink);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(branches);
        var financialYear = RequireConfirmedDestination(confirmedLink, destination, accountingFinancialYear);

        // Check Tally itself, not a remembered dropdown item or branch company.
        var live = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        RequireUniqueLiveDestination(confirmedLink, destination, live, financialYear);

        // Read live masters immediately before planning. Local SQLite name matches
        // are never permission to ALTER/overwrite a foreign Tally master.
        tally.RequireFreshErp9MasterAuthorityForExplicitExport();
        var beforeXml = await tally.ExportMastersAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        var before = ReadLiveMasters(beforeXml);
        var allVoucherTypes = before.Where(x => x.Type == "Voucher Type")
            .Select(x => x.Name)
            .Where(x => !branches.Any(b => x.EndsWith(" - " + b.DisplayName, StringComparison.OrdinalIgnoreCase))) // exclude already-generated branch types only
            .ToArray();
        var plan = TallyBranchConsolidationMasterPlanner.Build(
            destination.Name, destination.CompanyKey, financialYear,
            branches, before, true, separateVoucherTypes, separateCashLedgers,
            separateVoucherTypes ? allVoucherTypes : null, usedVoucherTypesByBranch);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(plan);

        // Fail before ANY writes when a base type or generated destination name
        // is unverified. Mandatory groups are independent of the two options.
        if (separateVoucherTypes && allVoucherTypes.Length == 0)
            throw new InvalidOperationException("Tally did not return a voucher type catalog.");
        // Reused Voucher Types are verified by name + parent + MASTERID in
        // the existing planner. Their native None numbering is never changed.
        if (createOnlyVoucherTypes && plan.Groups.Any(x => x.Create))
            throw new InvalidOperationException(
                "Creation-only mode found missing compulsory branch groups. " +
                "No group or Voucher Type was created. Verify groups separately.");
        var toCreate = createOnlyVoucherTypes ? Array.Empty<MasterRecord>()
            : plan.Groups.Where(x => x.Create)
                .Select(x => new MasterRecord { Type = "Group", Name = x.Name, Parent = x.Parent })
                .ToArray();
        var cash = createOnlyVoucherTypes ? Array.Empty<MasterRecord>()
            : plan.CashLedgers.Where(x => x.Create)
                .Select(x => new MasterRecord { Type = "Ledger", Name = x.Name, Parent = "Cash-in-Hand" })
                .ToArray();

        int createdGroups = 0, createdCash = 0, createdTypes = 0;
        // One-by-one existing Upsert validates Tally's response AND reads the
        // resulting parent from Tally. A failure halts the stage, never advances
        // to vouchers, and a retry re-plans from fresh live Masters.
        foreach (var group in toCreate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOneCreate(await tally.UpsertAccountingMasterAsync(
                destination, group, "Create", cancellationToken).ConfigureAwait(false), group.Name);
            createdGroups++;
        }
        foreach (var ledger in cash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOneCreate(await tally.UpsertAccountingMasterAsync(
                destination, ledger, "Create", cancellationToken).ConfigureAwait(false), ledger.Name);
            createdCash++;
        }

        // Optional: use the ORIGINAL All Masters XML import transport, with
        // exactly one voucher type per request, and confirm each new MASTERID
        // and inherited base type with an independent fresh export.
        foreach (var requirement in plan.VoucherTypes.Where(x => x.Create))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = SingleVoucherTypeXml(plan.VoucherTypeXml, destination.Name, requirement.Name);
            var response = TallyHttpService.ParseImportResponse(
                await tally.ImportXmlAsync(xml, cancellationToken).ConfigureAwait(false));
            RequireOneCreate(response, requirement.Name);
            tally.RequireFreshErp9MasterAuthorityForExplicitExport();
            var readbackXml = await tally.ExportMastersAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            var readback = ReadLiveMasters(readbackXml);
            RequireMaster(readback, "Voucher Type", requirement.Name, requirement.BaseType);
            RequireNoneNumberingForNewVoucherType(readbackXml, requirement.Name);
            createdTypes++;
        }

        // Final read-back confirms compulsory groups, optional masters and
        // durable Tally MASTERIDs even if Tally initially acknowledged a post.
        tally.RequireFreshErp9MasterAuthorityForExplicitExport();
        var finalXml = await tally.ExportMastersAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        var after = ReadLiveMasters(finalXml);
        foreach (var g in plan.Groups) RequireMaster(after, "Group", g.Name, g.Parent);
        foreach (var l in plan.CashLedgers) RequireMaster(after, "Ledger", l.Name, "Cash-in-Hand");
        foreach (var v in plan.VoucherTypes)
        {
            RequireMaster(after, "Voucher Type", v.Name, v.BaseType);
            if (v.Create) RequireNoneNumberingForNewVoucherType(finalXml, v.Name);
        }
        return new TallyBranchMasterImportResult(
            destination.CompanyKey, plan.Pan, plan.FinancialYear, plan.Groups.Count,
            createdGroups, createdCash, createdTypes,
            (createOnlyVoucherTypes
                ? "Creation-only: missing Voucher Types created and read back; " +
                  "all existing masters/numbering and all vouchers were left unchanged. "
                : "Branch master read-back passed. No consolidated vouchers were posted. ") +
            "Each branch GST ledger remains source-owned; no GST journal was regenerated.");
    }

    /// <summary>Read-only live Tally preflight for the prepared XML stage.</summary>
    public async Task<TallyBranchMasterPlan> PreviewPlanAsync(
        TallyClientLink confirmedLink, TallyCompanyInfo destination,
        IReadOnlyCollection<TallyBranchMasterIdentity> branches,
        bool separateVoucherTypes = false, bool separateCashLedgers = false,
        CancellationToken cancellationToken = default,
        string? accountingFinancialYear = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? usedVoucherTypesByBranch = null)
    {
        ArgumentNullException.ThrowIfNull(confirmedLink);
        ArgumentNullException.ThrowIfNull(destination);
        var financialYear = RequireConfirmedDestination(confirmedLink, destination, accountingFinancialYear);
        var live = await tally.DiscoverCompaniesAsync(cancellationToken).ConfigureAwait(false);
        RequireUniqueLiveDestination(confirmedLink, destination, live, financialYear);
        tally.RequireFreshErp9MasterAuthorityForExplicitExport();
        var freshXml = await tally.ExportMastersAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        var fresh = ReadLiveMasters(freshXml);
        var baseTypes = fresh.Where(x => x.Type == "Voucher Type")
            .Select(x => x.Name)
            .Where(x => !branches.Any(b => x.EndsWith(" - " + b.DisplayName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var plan = TallyBranchConsolidationMasterPlanner.Build(
            destination.Name, destination.CompanyKey, financialYear,
            branches, fresh, true, separateVoucherTypes, separateCashLedgers,
            separateVoucherTypes ? baseTypes : null, usedVoucherTypesByBranch);
        TallyBranchConsolidationMasterPlanner.ValidatePlanXml(plan);
        // An existing type's None numbering does not block the read-only
        // source preview. No voucher type is created/altered here.
        // Generate Consolidated XML is read-only: already existing branch
        // Groups are not repeated in the master CREATE file. Explicitly
        // verify EVERY reused compulsory Group against live destination
        // MASTERID and its correct original accounting parent. Missing
        // Groups remain in plan.GroupXml for master-first CREATE, never
        // silently treated as already imported.
        foreach (var group in plan.Groups.Where(g => !g.Create))
            RequireMaster(fresh, "Group", group.Name, group.Parent);
        return plan;
    }

    /// <summary>
    /// Validate None numbering only for a newly created type. Tally can omit
    /// the native field when it means None/default. Existing types are never
    /// modified or required to change their numbering configuration.
    /// </summary>
    internal static void RequireNoneNumberingForNewVoucherType(
        string liveMastersXml, string typeName)
    {
        var doc = TallyConsolidatedXmlTextSafety.ParseDocument(
            liveMastersXml, "created branch Voucher Type None readback");
        var matching = doc.Descendants().Where(x =>
                x.Name.LocalName.Equals("VOUCHERTYPE", StringComparison.OrdinalIgnoreCase))
            .Where(x => (x.Elements().FirstOrDefault(y =>
                        y.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                        ?.Value.Trim() ??
                    x.Attributes().FirstOrDefault(y =>
                        y.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                        ?.Value.Trim() ?? "")
                    .Equals(typeName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length != 1)
            throw new InvalidOperationException(
                "New Voucher Type '" + typeName +
                "' could not be uniquely read back from linked Tally. " +
                "No voucher was posted.");
        var methods = matching.Select(x => x.Elements().FirstOrDefault(y =>
                y.Name.LocalName.Equals("NUMBERINGMETHOD", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (methods.Any(x => !x.Equals("None", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "New Voucher Type '" + typeName +
                "' did not retain None numbering (found: " +
                string.Join(", ", methods) + "). No existing voucher was altered.");
    }

    /// <summary>
    /// The original full Tally Master XML is the only authority for type
    /// numbering. Do not trust a locally generated child VOUCHERTYPE or a
    /// cached name-only snapshot. Manual numbering avoids silently replacing
    /// Tally-owned VOUCHERNUMBER during the original bound-MASTERID ALTER.
    /// This check is read-only and never alters an existing master.
    /// </summary>
    internal static void RequireManualVoucherTypeNumbering(string liveMastersXml, string typeName)
    {
        var doc = TallyConsolidatedXmlTextSafety.ParseDocument(
            liveMastersXml, "branch Voucher Type numbering readback");
        var types = doc.Descendants().Where(x =>
                x.Name.LocalName.Equals("VOUCHERTYPE", StringComparison.OrdinalIgnoreCase))
            .Where(x =>
            {
                var name = x.Elements().FirstOrDefault(c =>
                    c.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                    ?.Value.Trim() ??
                    x.Attributes().FirstOrDefault(a =>
                        a.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                        ?.Value.Trim() ?? "";
                return name.Equals(typeName, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
        if (types.Length == 0)
            throw new InvalidOperationException(
                "New branch Voucher Type '" + typeName +
                "' is not present in fresh linked-Tally Master readback. No voucher ALTER.");
        var methods = types.Select(x => x.Elements().FirstOrDefault(c =>
                c.Name.LocalName.Equals("NUMBERINGMETHOD", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (methods.Length != 1 ||
            !methods[0].Equals("Manual", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Branch Voucher Type '" + typeName +
                "' does not have one verified Manual numbering method in fresh " +
                "linked Tally (found: " +
                (methods.Length == 0 ? "unavailable" : string.Join(", ", methods)) +
                "). Existing voucher numbers must not be renumbered. " +
                "No voucher CREATE/ALTER was sent.");
    }

    private static string SingleVoucherTypeXml(string batchXml, string company, string name)
    {
        var source = XDocument.Parse(batchXml);
        var nodes = source.Descendants("TALLYMESSAGE")
            .Where(x => x.Element("VOUCHERTYPE")?.Attribute("NAME")?.Value == name).ToArray();
        if (nodes.Length != 1)
            throw new InvalidOperationException("Voucher type XML must identify exactly one planned master.");
        var sb = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(sb, company);
        sb.AppendLine(nodes[0].ToString(SaveOptions.DisableFormatting));
        TallyExcelParityXmlWriter.EndAllMastersImport(sb);
        return sb.ToString();
    }

    public static void RequireOneCreate(TallyImportResponse response, string masterName)
    {
        if (response.Created != 1 || response.Errors != 0 || response.Exceptions != 0 ||
            response.Altered != 0 || response.Deleted != 0 || response.Ignored != 0 ||
            !string.IsNullOrWhiteSpace(response.LineError))
            throw new InvalidOperationException(
                "Tally did not confirm exactly one new master '" + masterName + "': " +
                $"Created={response.Created}, Altered={response.Altered}, Ignored={response.Ignored}, " +
                $"Exceptions={response.Exceptions}, Errors={response.Errors}. {response.LineError} " +
                "Reopen master import to re-read Tally before any retry.");
    }

    private static void RequireMaster(
        IReadOnlyCollection<MasterRecord> masters, string type, string name, string parent)
    {
        var matches = masters.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                                         x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 ||
            !matches[0].Parent.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(matches[0].TallyMasterId))
            throw new InvalidOperationException(
                $"Tally read-back did not verify '{name}' under '{parent}' with a MASTERID. " +
                "No voucher import is allowed.");
    }

    /// <summary>Parse the existing Tally master export; never substitute AAH's local master cache.</summary>
    public static IReadOnlyList<MasterRecord> ReadLiveMasters(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("No Tally master XML was returned.");
        // Fresh Tally master exports can contain legacy 0x04 bytes in free-text
        // descriptions. Repair only text, never protected names, IDs or attributes.
        var doc = TallyConsolidatedXmlTextSafety.ParseDocument(xml, "live Tally master readback");
        var masters = new List<MasterRecord>();
        foreach (var element in doc.Descendants().Where(x =>
                     x.Name.LocalName is "GROUP" or "LEDGER" or "VOUCHERTYPE"))
        {
            static string Field(XElement el, string key) =>
                el.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                ?? el.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                ?? "";
            var name = Field(element, "NAME");
            if (name.Length == 0) continue;
            masters.Add(new MasterRecord
            {
                Type = element.Name.LocalName switch { "GROUP" => "Group", "LEDGER" => "Ledger", _ => "Voucher Type" },
                Name = name,
                Parent = Field(element, "PARENT"),
                TallyMasterId = Field(element, "MASTERID")
            });
        }
        if (masters.Count == 0)
            throw new InvalidOperationException("Tally returned no readable master objects.");

        // Multiple Tally collection reports may echo the same object. Do not
        // mistake an echo for another master, but reject conflicting identifiers.
        var distinct = new List<MasterRecord>();
        foreach (var group in masters.GroupBy(x => (x.Type.ToUpperInvariant(), x.Name.ToUpperInvariant())))
        {
            var entries = group.ToArray();
            var parents = entries.Select(x => x.Parent).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ids = entries.Select(x => x.TallyMasterId).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (parents.Length > 1 || ids.Length > 1)
                throw new InvalidOperationException("Tally returned conflicting Master identities for " + entries[0].Name);
            distinct.Add(new MasterRecord
            {
                Type = entries[0].Type, Name = entries[0].Name,
                Parent = parents.FirstOrDefault() ?? "",
                TallyMasterId = ids.FirstOrDefault() ?? ""
            });
        }
        return distinct;
    }
}

public sealed record TallyBranchMasterImportResult(
    string DestinationCompanyKey, string Pan, string FinancialYear,
    int RequiredGroups, int CreatedGroups, int CreatedCashLedgers,
    int CreatedVoucherTypes, string Status);
