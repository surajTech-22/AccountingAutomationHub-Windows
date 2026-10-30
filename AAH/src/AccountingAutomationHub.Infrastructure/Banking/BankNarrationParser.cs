using System.Text;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Bank narration parser ported from the completed Excel Ledger Mapping workbench.
/// The parser deliberately uses structural narration rules rather than bank-specific
/// formats. BuildRawSummaryName mirrors VBA GetSummaryName; BuildSummaryName mirrors
/// GetNormalizedSummaryName and adds a conservative periodic-prefix cleanup.
/// </summary>
public static partial class BankNarrationParser
{
    private static readonly string[] ContraKeywords =
    [
        "CASH WITHDRAWAL BY SELF", "CASH DEPOSIT BY SELF", "CASH WITHDRAWAL SELF",
        "CASH DEPOSIT SELF", "SELF WITHDRAWAL", "SELF DEPOSIT", "CASH WITHDRAWAL",
        "CASH DEPOSIT", "BY CASH", "ATM CASH WITHDRAWAL", "ATM CASH DEPOSIT",
        "ATM WITHDRAWAL", "ATM DEPOSIT", "ATM WDL", "ATM WD", "CASH WDL",
        "CASH WD", "CASH DEP", "CSH WDL", "CSH WD", "CSH DEP", "CDM CASH",
        "CDM", "SELF"
    ];

    private static readonly string[] ContraExclusions =
    ["ATM CHARGE", "ATM CHARGES", "CHARGE", "CHARGES", "CHRG", "FEE"];

    private static readonly HashSet<string> GeneralEdgeNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "BY", "TO", "FROM", "IN", "OUT", "INWARD", "OUTWARD", "TRANSFER", "TFR", "TRFR", "DEP", "WDL",
        "PAYMENT", "RECEIPT", "PAID", "RECEIVED", "REMT", "THRU",
        "NEFT", "RTGS", "IMPS", "UPI", "UTR", "REF", "REFERENCE",
        "NO", "NUMBER", "DR", "CR", "DEBIT", "CREDIT", "INB", "CLG",
        "SENDER", "BENEFICIARY", "SETTL", "SETTLEMENT", "PAYOUT", "COLLECTION", "CIN", "MBK",
        // Real-bank channel wrappers seen in IMPS/online narrations. The current Excel workbook
        // does not reject these early enough; keeping them here is the evidence-based desktop
        // correction required by the live OPM/MOB cases while preserving the workbook parser order.
        "OPM", "MOB", "INET", "P2A", "P2P", "PAYM",
        "EBANK", "WIB", "ACHCR", "ACHDR", "NACHMU", "DIGITA", "DIGITB", "CRA", "INST"
    };

    private static readonly HashSet<string> ShortAcronymStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ATM", "UPI", "TDS", "GST", "TIN", "UTR", "RRN", "REF", "TXN", "NEF",
        "CHQ", "MIC", "DRS", "CRS", "DEP", "WDL", "INB", "IFT", "IMT", "NFS",
        "POS", "ACH", "ECS", "MCA", "ROC", "CIN", "MBK", "OPM", "MOB", "INET", "P2A", "P2P",
        "WIB", "ACHCR", "ACHDR", "NACHMU", "CRA", "OUT"
    };

    private static readonly HashSet<string> KnownBankPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SBIN", "HDFC", "ICIC", "ICIN", "UTIB", "YESB", "KKBK", "CNRB", "BARB",
        "IDIB", "IOBA", "UBIN", "KARB", "FDRL", "IDFB", "IBKL", "PUNB", "BKID",
        "MAHB", "CSBK", "PKGB"
    };

    private static readonly HashSet<string> MonthTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC",
        "JANUARY", "FEBRUARY", "MARCH", "APRIL", "JUNE", "JULY", "AUGUST", "SEPTEMBER",
        "OCTOBER", "NOVEMBER", "DECEMBER"
    };

    private static readonly HashSet<string> BankEdgeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "HDFC", "ICICI", "IDFC", "SBI", "AXIS", "CANARA", "CNRB", "KOTAK", "KKBK",
        "YES", "YESB", "UNION", "UBI", "BOB", "BARODA", "PNB", "INDUSIND", "FEDERAL"
    };

    // Short clearing/presentation routing codes proven by the uploaded SBI review.
    // These are removed only after the highly-specific CAS PRES cheque wrapper, so
    // a genuine three-letter party prefix elsewhere is never stripped globally.
    private static readonly HashSet<string> CashPresentationRoutingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TGM", "UBI", "HDF", "CAB", "KOT", "KBL", "KVB", "AXB", "AXS"
    };

    private static readonly string[] ReferenceTailLabels =
    [
        "SENDER NO", "SENDER NUMBER", "SENDER REF", "RECEIVER NO", "RECEIVER NUMBER",
        "UTR NO", "UTRNO", "UTR NUMBER", "REFERENCE NO", "REFERENCE NUMBER", "REF NO", "REFNO",
        "TRANSACTION ID", "TRAN ID", "TXN ID", "TXNID", "INSTRUMENT ID", "INSTR ID", "INSTR NO",
        "BANK REF", "BANK REFERENCE", "RRN NO", "RRN", "CHEQUE NO", "CHQ NO", "CHEQUE NUMBER",
        "CHQ NUMBER", "ACCOUNT NO", "A/C NO", "AC NO", "PAYER VPA", "PAYEE VPA", "CIN NO", "CIN"
    ];

    public static string Normalize(string narration)
    {
        var upper = NormalizeWhitespace(narration);
        var builder = new StringBuilder(upper.Length);
        var previousSpace = false;
        foreach (var character in upper)
        {
            var output = char.IsLetterOrDigit(character) ? character : ' ';
            if (output == ' ')
            {
                if (!previousSpace) builder.Append(output);
                previousSpace = true;
            }
            else
            {
                builder.Append(output);
                previousSpace = false;
            }
        }
        return CollapseSpaces(builder.ToString());
    }

    public static bool IsCashContra(string narration)
    {
        var normalized = Normalize(narration);
        if (ContraExclusions.Any(normalized.Contains)) return false;
        return ContraKeywords.Any(normalized.Contains);
    }

    public static string ExtractReference(string narration)
    {
        var text = narration.ToUpperInvariant();
        foreach (Match match in ReferenceRegex().Matches(text))
        {
            var candidate = match.Value.Trim('/', '-', ' ', ':');
            if (candidate.Length >= 6 && candidate.Any(char.IsDigit)) return candidate;
        }
        return string.Empty;
    }

    public static string DetectMethod(string narration)
    {
        var explicitMethod = DetectExplicitMethod(narration);
        return explicitMethod.Length > 0 ? explicitMethod : "TRANSFER";
    }

    public static string DetectExplicitMethod(string narration)
    {
        var normalized = Normalize(narration);
        foreach (var method in new[] { "UPI", "NEFT", "RTGS", "IMPS", "IFT", "CHEQUE", "CHQ", "ATM", "CASH", "MB" })
        {
            if (normalized.Contains(method, StringComparison.OrdinalIgnoreCase))
                return method is "CHQ" ? "CHEQUE" : method;
        }
        return string.Empty;
    }

    public static IReadOnlySet<string> ExtractReferenceGroups(
        string text,
        string paymentMethod = "",
        decimal amount = 0m,
        DateTime? transactionDate = null)
    {
        var normalized = Normalize(text);
        var method = paymentMethod.Trim().ToUpperInvariant();
        var minimumLength = method switch
        {
            "UPI" or "IMPS" => 8,
            "NEFT" or "RTGS" => 6,
            "CHEQUE" => 4,
            _ => 4
        };
        var maximumLength = method switch
        {
            "UPI" or "IMPS" => 30,
            "NEFT" or "RTGS" => 35,
            "CHEQUE" => 18,
            _ => 35
        };
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ReferenceGroupRegex().Matches(normalized))
        {
            var value = new string(match.Value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            if (value.Length < minimumLength || value.Length > maximumLength || !value.Any(char.IsDigit)) continue;
            if (method.Length == 0 && value.Any(char.IsLetter) && value.Length < 6) continue;
            if (IsIgnoredReference(value, amount, transactionDate)) continue;
            result.Add(value);
        }
        return result;
    }

    private static bool IsIgnoredReference(string value, decimal amount, DateTime? transactionDate)
    {
        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UPI", "IMPS", "NEFT", "RTGS", "CHEQUE", "CHEQ", "CHQ", "TRANSFER", "PAYMENT",
                "RECEIPT", "BANK", "ACCOUNT", "ACNO", "REF", "REFERENCE", "TRANSACTION", "TXN",
                "DEBIT", "CREDIT", "FROM", "TO"
            }.Contains(value)) return true;

        if (value.Length > 1 && value.All(character => character == 'X')) return true;
        if (value.StartsWith('X') && value.SkipWhile(character => character == 'X').All(char.IsDigit)) return true;

        var whole = Math.Round(amount, 0).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        var paise = Math.Round(amount, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace(".", string.Empty);
        if (value.Equals(whole, StringComparison.Ordinal) || value.Equals(paise, StringComparison.Ordinal)) return true;

        if (transactionDate.HasValue)
        {
            var date = transactionDate.Value.Date;
            if (value.Equals(date.ToString("ddMMyyyy"), StringComparison.Ordinal) ||
                value.Equals(date.ToString("yyyyMMdd"), StringComparison.Ordinal) ||
                value.Equals(date.ToString("ddMMyy"), StringComparison.Ordinal) ||
                value.Equals(date.ToString("yyMMdd"), StringComparison.Ordinal) ||
                value.Equals(date.ToString("yyyy"), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// VBA GetSummaryName routing, before frequency-based canonical selection.
    /// Keep this order aligned with the Excel Ledger Mapping workbench: high-confidence
    /// categories first, then structured payment parsers, then generic/reference fallback.
    /// </summary>
    public static string BuildRawSummaryName(string narration) => BuildRawSummaryNameCore(narration);

    /// <summary>
    /// Ledger Mapping parser ported from the CURRENT Import_1_6 workbook supplied by
    /// the user.  This intentionally preserves the workbook's routing order: the
    /// general reference-aware extractor runs before the legacy method-specific
    /// fallbacks.  That order is essential for narrations such as
    /// NEFT/HDFCH.../ESHWARI AGRO TECH/... where the party is structurally present and
    /// NEFT RECEIPT must be used only as a true fallback.
    /// </summary>
    public static string BuildExcelWorkbookRawSummaryName(string narration, string txnMode = "")
    {
        // Short UPI narrations from some bank exports contain only:
        // UPI / reference / time / UPI / tiny channel code.
        // The time and 1-3 character tail are technical metadata, not a party name.
        // Detect this before generic extraction so values such as "05 02 21 IC"
        // can never become Ledger Mapping summary names.
        var normalizedInput = NormalizeWhitespace(narration);
        if (IsTechnicalUpiReferenceTimeNarration(normalizedInput))
            return GetLedgerMappingFallbackCategory(normalizedInput, txnMode);

        var text = SanitizeMappingNarration(narration);
        if (text.Length == 0) return string.Empty;

        // CURRENT workbook high-confidence categories plus real-bank structural
        // corrections proven by the user's exported Ledger Mapping review workbook.
        if (IsBankCharge(text)) return "BANK CHARGES";
        if (IsBankInterest(text)) return "BANK INTEREST";
        if (IsUpiReturn(text)) return "UPI RETURN";
        if (IsChequeReturn(text)) return "CHEQUE RETURN";
        if (ContainsAny(text, "PMSBY", "PMJJBY", "APY PREMIUM", "INSURANCE PREMIUM", "POLICY PREMIUM", "STOCK INSURANCE"))
            return "INSURANCE PREMIUM";
        if (IsGstPaymentNarration(text)) return "GST PAYMENT";
        if (IsTinOtcPaymentNarration(text)) return "TIN / OTC PAYMENT";
        if (ContainsAny(text, "CASH DEPOSIT", "BY CASH", "CASH DEP")) return "CASH DEPOSIT";
        if (ContainsAny(text, "CASH WD", "CASH WITHDRAWAL", "ATM WDL", "ATM WITHDRAWAL")) return "CASH WITHDRAWAL";

        // Clearing rows must be parsed before the broad electronic/general extractor.
        // Blank BY INST ... MICR CLG rows contain no party and therefore stay out of
        // Ledger Mapping; rows that do contain a party return only that party.
        if (CtsClearingRegex().IsMatch(text))
        {
            var clearingParty = ExtractCtsClearingParty(text);
            if (clearingParty.Length > 0) return clearingParty;
            return txnMode.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? "PAID IN CLEARING" :
                   txnMode.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? "RECEIVED IN CLEARING" : "CLEARING";
        }

        var achParty = ExtractAchParty(text);
        if (achParty.Length > 0) return achParty;
        if (IsAchWrapperWithoutParty(text)) return GetLedgerMappingFallbackCategory(text, txnMode);

        var eBankParty = ExtractEBankWibParty(text);
        if (eBankParty.Length > 0) return eBankParty;
        if (StartsWithAny(text, "EBANK:WIB", "EBANK WIB")) return GetLedgerMappingFallbackCategory(text, txnMode);

        // DIGITA/DIGITB are bank routing/processing wrappers. If the narration ends
        // at a NEFT/IMPS/CRA reference there is no recoverable party name. Keep the
        // transaction in Ledger Mapping under a method/direction fallback instead of
        // silently dropping it from the workbench.
        if (StartsWithAny(text, "DIGITA-", "DIGITA ", "DIGITB-", "DIGITB ") && !HasPartyAfterDigitalReference(text))
            return GetLedgerMappingFallbackCategory(text, txnMode);

        // Some statements put a genuine counterparty before a trailing instrument
        // marker, for example SERMAS-CHENNAI/BY INST 251092 -. The instrument
        // number is clearing metadata; keep only the party portion. Rows beginning
        // with BY INST are handled by the clearing branch above and are not guessed.
        var trailingInstrumentParty = ExtractTrailingInstrumentParty(text);
        if (trailingInstrumentParty.Length > 0) return trailingInstrumentParty;

        // SBI cash/cheque statement wrappers are presentation metadata, not Ledger
        // identity. Extract the party before the broad generic parser so CAS/PRES/
        // CHO/CHQ, cheque number, SESS and routing code noise can never become the
        // Summary Name. The original Bank Statement narration remains untouched.
        var cashPresentationParty = ExtractCashPresentationParty(text);
        if (cashPresentationParty.Length > 0) return cashPresentationParty;

        var branchTransferParty = ExtractBranchTransferParty(text);
        if (branchTransferParty.Length > 0) return branchTransferParty;

        // CL019 / Canara Bank regression: these structured exports provide the
        // counterparty BEFORE bank codes, masked accounts, payment notes and
        // own-company labels. Reading the entire tail generates false suggestions
        // such as "VEENA SB PAYMENT FROM PHONE" and "KREATIVE NETBANK MUM".
        // Only the fully recognized delimiter shapes are handled here; unknown
        // layouts continue through the established parser without guessing.
        var structuredParty = ExtractStructuredElectronicCounterparty(narration);
        if (structuredParty.Length > 0) return structuredParty;

        if (InternalTransferOnlyRegex().IsMatch(text)) return "INTERNAL BANK TRANSFER";

        // Retain the approved Bank-of-Baroda/mobile CA-to-OD correction as a narrow
        // desktop extension.  It sits after the workbook categories so an interest or
        // charge narration can never be stolen by the transfer wrapper.
        if (InternalAccountTransferRegex().IsMatch(text) ||
            (IsMobileBankingNarration(text) && MobileBankingTransferFragmentRegex().IsMatch(text)))
        {
            var internalTransfer = ExtractInternalAccountTransferSummary(text);
            if (internalTransfer.Length > 0) return internalTransfer;
        }

        // Plain statement/check sequence numbers are not part of the party. This also
        // lets 183/185/... ASSISTANT EXECUTIVE ENGI collapse to the same identity.
        text = LeadingSequenceNumberRegex().Replace(text, string.Empty).Trim();

        var directionalParty = ExtractSimpleDirectionalParty(text);
        if (directionalParty.Length > 0) return directionalParty;

        // This is the critical order from the CURRENT workbook.
        if (!IsGovernmentOrTax(text))
        {
            var generalParty = GeneralExtractSummary(text);
            if (generalParty.Length > 0)
                return IsLedgerMappingNoiseSummary(generalParty)
                    ? GetLedgerMappingFallbackCategory(text, txnMode)
                    : generalParty;
        }

        var priorityParty = ExtractPriorityPartyName(text);
        if (priorityParty.Length > 0)
            return IsLedgerMappingNoiseSummary(priorityParty)
                ? GetLedgerMappingFallbackCategory(text, txnMode)
                : priorityParty;

        if (text.Contains('@') || text.Contains("UPI", StringComparison.OrdinalIgnoreCase))
        {
            var upi = ParseUpi(text);
            return KeepPartyOrMethodFallback(upi, "UPI", text, txnMode);
        }
        if (text.Contains(" IFT ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("IB IFT", StringComparison.OrdinalIgnoreCase))
            return KeepPartyOrMethodFallback(GetIftName(text), "IFT", text, txnMode);

        // The workbook checks MB/.  MBK support is the approved desktop extension and
        // goes through the same delimiter parser rather than a separate generic rule.
        if (text.Contains("MB/", StringComparison.OrdinalIgnoreCase) || IsMobileBankingNarration(text))
            return KeepPartyOrMethodFallback(GetMbName(text), "MOBILE BANKING", text, txnMode);
        if (text.Contains("NEFT", StringComparison.OrdinalIgnoreCase))
            return KeepPartyOrMethodFallback(GetNeftName(text), "NEFT", text, txnMode);
        if (text.Contains("IMPS", StringComparison.OrdinalIgnoreCase))
            return KeepPartyOrMethodFallback(GetImpsName(text), "IMPS", text, txnMode);
        if (text.Contains("RTGS", StringComparison.OrdinalIgnoreCase))
            return KeepPartyOrMethodFallback(GetRtgsName(text), "RTGS", text, txnMode);
        if (ContainsAny(text, "CHQ", "CHEQUE", "MICR", "CLEARING"))
            return KeepPartyOrMethodFallback(ParseCheque(text), "CLEARING", text, txnMode);
        if (ContainsAny(text, "TRANSFER", "TRANSFER-INB", "TRANSFER INB", "FUNDS TRANSFER"))
            return KeepPartyOrMethodFallback(ParseTransfer(text), "TRANSFER", text, txnMode);

        // Workbook GetGenericName: normalize delimiters but otherwise preserve the
        // narration. If the cleaned value is only technical wrapper/reference noise,
        // keep the transaction visible under a safe method/direction category.
        var generic = CollapseSpaces(text.Replace('/', ' ').Replace('-', ' ').Replace(';', ' '));
        return generic.Length == 0 ||
               IsTimeChannelResidue(generic) ||
               MaskedAccountOnlyRegex().IsMatch(generic) ||
               IsNarrationNoise(generic) ||
               generic.Replace(" ", string.Empty, StringComparison.Ordinal).All(char.IsDigit) ||
               (HasManyNumbers(generic) && CountLetters(generic) < 3)
            ? GetLedgerMappingFallbackCategory(text, txnMode)
            : generic;
    }

    public static string BuildExcelWorkbookNormalizedSummaryName(string narration, string txnMode = "")
    {
        var summary = BuildExcelWorkbookRawSummaryName(narration, txnMode);
        summary = StripPaymentReferenceWrapper(summary, txnMode);
        // SBI/INB exports frequently inject a technical CT/BSBI/CNA/CRH reference
        // between the human purpose text and the real party. Those tokens are not
        // Ledger identity and must be removed before the frequency/canonical pass,
        // otherwise one party is split into a different Summary Name for every payment.
        summary = StripEmbeddedBankReferenceTokens(summary);
        if (summary.Length == 0)
            return GetLedgerMappingFallbackCategory(SanitizeMappingNarration(narration), txnMode);
        return IsExcelWorkbookValidPartyName(summary) ? NormalizeSummaryName(summary) : summary.Trim();
    }

    /// <summary>
    /// Extract a counterparty only when a supported bank export has the
    /// fully identified UPI/IMPS/NEFT/RTGS dash-separated layout.
    /// Do not conflate the merchant with a remittance note, IFSC, bank
    /// account, app name or the current client's own-company name.
    /// </summary>
    private static string ExtractStructuredElectronicCounterparty(string narration)
    {
        var text = NormalizeWhitespace(narration);
        var match = Regex.Match(text,
            @"^(?<method>UPI|IMPS|NEFT|RTGS)(?:\s+(?:DR|CR))?-(?<details>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return string.Empty;

        var parts = match.Groups["details"].Value
            .Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return string.Empty;
        var method = match.Groups["method"].Value;
        var party = string.Empty;
        if (method.Equals("UPI", StringComparison.OrdinalIgnoreCase))
        {
            // Some banks put a payment app between the party and its VPA.
            // The VPA is evidence that the first segment is the payer/payee.
            if (!parts.Skip(1).Take(3).Any(value => value.Contains('@')))
                return string.Empty;
            party = parts[0];
        }
        else if (method.Equals("IMPS", StringComparison.OrdinalIgnoreCase))
        {
            // IMPS-<12-digit reference>-<party>-<bank>-<masked account>-<remark>
            if (parts.Length < 4 ||
                !Regex.IsMatch(parts[0], @"^\d{9,18}$", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(parts[2], @"^[A-Z]{4}(?:[A-Z0-9]{0,7})$", RegexOptions.CultureInvariant))
                return string.Empty;
            party = parts[1];
        }
        else
        {
            // NEFT/RTGS CR/DR-<11-character IFSC>-<counterparty>-<bank/remark>...
            if (!Regex.IsMatch(parts[0], @"^[A-Z]{4}[A-Z0-9]{7}$", RegexOptions.CultureInvariant))
                return string.Empty;
            party = parts[1];
            // The intermediate account is not the sender. The immediately
            // following party in this validated format is the merchant.
            if (Regex.IsMatch(party,
                    @"^INTERNAL\s+(?:A/?C|ACCOUNT|AC)\s+FOR\s+INTERMEDI(?:ARY|ERY)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (parts.Length < 4) return string.Empty;
                party = parts[2];
            }
        }

        // A channel remark on its own is never a person or Ledger name.
        if (Regex.IsMatch(party,
                @"^(?:PAYMENT\s+FROM\s+PHONE|YOU\s+ARE\s+RECHARGING|BANK\s*ACCOUNT\s*VALIDATION|NETBANK(?:\s+MUM)?|UPI|PAYTM|IMPS|NEFT|RTGS)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return string.Empty;

        var candidate = GeneralCleanCandidate(party);
        return GeneralIsGoodCandidate(candidate) &&
               !IsLedgerMappingNoiseSummary(candidate)
            ? candidate
            : string.Empty;
    }

    private static string StripEmbeddedBankReferenceTokens(string summary)
    {
        var words = NormalizeWhitespace(summary).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Empty;

        var kept = words.Where(word => !IsEmbeddedBankReferenceToken(word)).ToArray();
        return kept.Length == 0 ? string.Empty : CollapseSpaces(string.Join(' ', kept));
    }

    /// <summary>
    /// Comparison-only normalizer used when a parser repair rebuilds persisted mappings
    /// or reconnects an older review workbook. It removes only the proven bank reference
    /// tokens; it never changes the saved Ledger name itself.
    /// </summary>
    public static string NormalizeLedgerMappingComparisonName(string value)
    {
        var original = NormalizeWhitespace(value ?? string.Empty);
        var cleaned = StripEmbeddedBankReferenceTokens(original);
        cleaned = NormalizeLegacyBankWrapperSummary(cleaned.Length > 0 ? cleaned : original);
        return cleaned.Length > 0 ? NormalizeWhitespace(cleaned) : original;
    }

    private static string NormalizeLegacyBankWrapperSummary(string value)
    {
        var text = NormalizeWhitespace(value);
        if (text.Length == 0) return string.Empty;
        if (IsBankCharge(text)) return "BANK CHARGES";
        if (IsChequeReturn(text)) return "CHEQUE RETURN";

        if (StartsWithAny(text, "CAS PRES "))
        {
            text = Regex.Replace(text, @"^CAS\s+PRES(?:\s+(?:CHO|CHQ))?\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"^(?:SESS|SESSION)(?:\s+\d{1,2})?\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 1 && CashPresentationRoutingCodes.Contains(words[0])) words.RemoveAt(0);
            text = string.Join(' ', words);
            return FinalizeBankWrapperParty(text);
        }

        if (text.StartsWith("CAS XFER WD ", StringComparison.OrdinalIgnoreCase))
        {
            var ofPosition = text.LastIndexOf(" OF ", StringComparison.OrdinalIgnoreCase);
            if (ofPosition >= 0) text = text[(ofPosition + 4)..];
            else text = text[12..];
            var atPosition = text.IndexOf(" AT RAILWAY STATION ROAD GUBBI", StringComparison.OrdinalIgnoreCase);
            if (atPosition > 0) text = text[..atPosition];
            return FinalizeBankWrapperParty(text);
        }

        if (text.StartsWith("TRFR OF ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[8..];
            var atPosition = text.IndexOf(" AT RAILWAY STATION ROAD GUBBI", StringComparison.OrdinalIgnoreCase);
            if (atPosition > 0) text = text[..atPosition];
            return FinalizeBankWrapperParty(text);
        }

        return text;
    }

    private static bool IsEmbeddedBankReferenceToken(string token)
    {
        var compact = GeneralCompactToken(token);
        if (compact.Length < 7 || !compact.All(char.IsLetterOrDigit)) return false;

        return Regex.IsMatch(compact, @"^CT[0-9A-Z]{7,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(compact, @"^BSBI[0-9A-Z]{8,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(compact, @"^CNA[0-9A-Z]{6,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(compact, @"^CRH[0-9A-Z]{5,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(compact, @"^\d{6,}(?:CHT|CHU|CHV|CK)[0-9A-Z]{5,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string StripPaymentReferenceWrapper(string summary, string txnMode)
    {
        var mode = NormalizeWhitespace(txnMode);
        if (!mode.Equals("PAYMENT", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("DEBIT", StringComparison.OrdinalIgnoreCase))
            return summary;

        var text = NormalizeWhitespace(summary);
        if (text.StartsWith("INB PAYMENT ", StringComparison.OrdinalIgnoreCase))
            text = text[12..].Trim();
        else if (text.StartsWith("PAYMENT ", StringComparison.OrdinalIgnoreCase))
            text = text[8..].Trim();
        else
            return summary;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && IsPaymentWrapperWord(words[0])) words.RemoveAt(0);
        if (words.Count > 1 && IsInbPaymentReferenceToken(words[0])) words.RemoveAt(0);
        while (words.Count > 0 && IsPaymentWrapperWord(words[0])) words.RemoveAt(0);

        var candidate = CollapseSpaces(string.Join(' ', words));
        return candidate.Length >= 3 && CountLetters(candidate) >= 2 ? candidate : summary;
    }


    private static bool IsPaymentWrapperWord(string value) =>
        value.Equals("TO", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("BY", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("TOWARDS", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("FOR", StringComparison.OrdinalIgnoreCase);

    private static bool IsInbPaymentReferenceToken(string token)
    {
        var compact = GeneralCompactToken(token);
        if (compact.Length is < 7 or > 28) return false;
        if (!compact.All(char.IsLetterOrDigit)) return false;
        var letters = CountLetters(compact);
        var digits = CountDigits(compact);
        return letters >= 3 && digits >= 1;
    }

    /// <summary>Exact CURRENT-workbook IsValidPartyName rules.</summary>
    public static bool IsExcelWorkbookValidPartyName(string value)
    {
        var name = NormalizeWhitespace(value);
        if (name.Length == 0 || name.Length > 80) return false;
        if (name.Length < 4)
            return name.Length == 3 &&
                   name.All(character => character is >= 'A' and <= 'Z') &&
                   !ShortAcronymStopWords.Contains(name);

        if (name is "BANK CHARGES" or "BANK INTEREST" or "CASH DEPOSIT" or "CASH WITHDRAWAL" or
            "PAID IN CLEARING" or "RECEIVED IN CLEARING" or "GST PAYMENT" or "TIN / OTC PAYMENT" or
            "UPI" or "IN" or "OUT" or "INWARD" or "OUTWARD")
            return false;
        if (Regex.IsMatch(
                name,
                @"^(?:UPI|IMPS|NEFT|RTGS|IFT|ACH / NACH|CLEARING|TRANSFER|MOBILE BANKING|OTHER)\s+(?:PAYMENT|RECEIPT|REMITTANCE|CONTRA|TRANSACTION)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        if (IsTimeChannelResidue(name) || MaskedAccountOnlyRegex().IsMatch(name)) return false;
        if (StartsWithAny(name, "NACHMU ", "ACHDR ", "ACHCR ", "EBANK ", "DIGITA ", "DIGITB ", "INST ")) return false;
        if (name.Contains("TRANSFER", StringComparison.OrdinalIgnoreCase) || IsGovernmentOrTax(name)) return false;
        if (new[] { "CRN", "UTR", "RRN" }.Any(token => ContainsWholeToken(name, token))) return false;
        return CountLetters(name) >= 2;
    }

    /// <summary>Exact pass-2 canonical chooser used by the CURRENT workbook.</summary>
    public static string GetExcelWorkbookMostFrequentSimilarName(
        string summaryName,
        IReadOnlyDictionary<string, int> summaryCounts)
    {
        var current = NormalizeWhitespace(summaryName);
        if (!IsExcelWorkbookValidPartyName(current)) return current;

        var bestName = current;
        var bestCount = summaryCounts.TryGetValue(current, out var currentCount) ? currentCount : 0;
        var bestQuality = CanonicalNameQuality(bestName);

        foreach (var pair in summaryCounts)
        {
            var candidate = NormalizeWhitespace(pair.Key);
            if (!IsExcelWorkbookValidPartyName(candidate)) continue;
            if (!AreSameParty(current, candidate) && !IsConservativeTruncatedVariant(current, candidate, summaryCounts)) continue;
            var candidateQuality = CanonicalNameQuality(candidate);
            if (!ShouldPreferCanonicalName(
                    candidate, pair.Value, candidateQuality,
                    bestName, bestCount, bestQuality)) continue;

            bestName = candidate;
            bestCount = pair.Value;
            bestQuality = candidateQuality;
        }

        return bestName;
    }

    private static string BuildRawSummaryNameCore(string narration)
    {
        var text = SanitizeMappingNarration(narration);
        if (text.Length == 0) return string.Empty;

        // Excel high-confidence categories. These must run before any generic party
        // extraction so values such as CHARGE never become a fake party ledger.
        if (IsBankCharge(text)) return "BANK CHARGES";
        if (IsBankInterest(text)) return "BANK INTEREST";
        if (IsUpiReturn(text)) return "UPI RETURN";
        if (IsChequeReturn(text)) return "CHEQUE RETURN";
        if (ContainsAny(text, "PMSBY", "PMJJBY", "INSURANCE PREMIUM", "POLICY PREMIUM")) return "INSURANCE PREMIUM";
        if (ContainsAny(text, "FD FOR ", "FIXED DEPOSIT", "TERM DEPOSIT")) return "FIXED DEPOSIT";
        if (ContainsAny(text, "DD/CC ISSUED", "DD CC ISSUED", "DEMAND DRAFT")) return "DEMAND DRAFT";
        if (IsGstPaymentNarration(text)) return "GST PAYMENT";
        if (IsTinOtcPaymentNarration(text)) return "TIN / OTC PAYMENT";
        if (ContainsAny(text, "CASH DEPOSIT", "BY CASH", "CASH DEP", "CASH RECEIPT")) return "CASH DEPOSIT";

        var cashPresentationParty = ExtractCashPresentationParty(text);
        if (cashPresentationParty.Length > 0) return cashPresentationParty;
        var branchTransferParty = ExtractBranchTransferParty(text);
        if (branchTransferParty.Length > 0) return branchTransferParty;

        if (ContainsAny(text, "CASH WD", "CASH WITHDRAWAL", "ATM WDL", "ATM WITHDRAWAL", "CAS XFER WD")) return "CASH WITHDRAWAL";

        // Excel structured parser order. Do not run a broad generic parser ahead of
        // these methods; that was the source of reference codes becoming Summary Name.
        if (text.Contains("UPI", StringComparison.Ordinal)) return ParseUpi(text);
        if (text.Contains("IMPS", StringComparison.Ordinal)) return GetImpsName(text);
        if (text.Contains("NEFT", StringComparison.Ordinal)) return GetNeftName(text);
        if (text.Contains("RTGS", StringComparison.Ordinal)) return GetRtgsName(text);
        if (text.Contains(" IFT ", StringComparison.Ordinal) || text.Contains("IB IFT", StringComparison.Ordinal)) return GetIftName(text);
        if (IsMobileBankingNarration(text)) return GetMbName(text);
        if (ContainsAny(text, "CHQ", "CHEQUE", "MICR", "CLEARING")) return ParseCheque(text);
        if (ContainsAny(text, "TRANSFER", "TFR", "TRFR", "FUNDS TRANSFER")) return ParseTransfer(text);

        // Keep the BOB CA/OD wrapper correction, but only after the same structured
        // parser families Excel gives priority to. This also handles cleaned narrations
        // where the MBK prefix has already been removed by the source statement.
        var internalTransfer = ExtractInternalAccountTransferSummary(text);
        if (internalTransfer.Length > 0) return internalTransfer;

        var referenceParty = ExtractReferencePrefixedParty(text);
        if (referenceParty.Length > 0) return referenceParty;

        return CollapseSpaces(text.Replace('/', ' ').Replace('-', ' ').Replace(';', ' '));
    }

    /// <summary>
    /// Exact cleaned summary before the second-pass frequency selector. This is the
    /// workbook GetNormalizedSummaryName path, plus conservative month/period cleanup.
    /// </summary>
    public static string GetNormalizedSummaryName(string narration)
    {
        var summary = BuildRawSummaryName(narration);
        if (IsValidPartyName(summary)) summary = NormalizeSummaryName(summary);
        return NormalizePeriodicPartyVariant(summary);
    }

    public static string BuildSummaryName(string narration)
    {
        var summary = GetNormalizedSummaryName(narration);
        if (IsSystemSummary(summary) || IsValidPartyName(summary)) return summary;
        return string.Empty;
    }

    public static string BuildSummaryName(string narration, string voucherType)
    {
        var text = SanitizeMappingNarration(narration);
        var mode = NormalizeWhitespace(voucherType);

        if (ContainsAny(text, "CLG CTS", "CLEARING CTS"))
        {
            var clearingParty = ExtractCtsClearingParty(text);
            if (IsValidPartyName(clearingParty)) return NormalizeSummaryName(clearingParty);
            return mode.Equals("PAYMENT", StringComparison.OrdinalIgnoreCase) ? "PAID IN CLEARING" :
                   mode.Equals("RECEIPT", StringComparison.OrdinalIgnoreCase) ? "RECEIVED IN CLEARING" : "CLEARING";
        }

        // Let the Excel high-confidence/structured parser run first. The CA/OD
        // layer is already integrated into mobile parsing and the raw fallback, so
        // it must not steal BANK INTEREST/charge classifications such as CA TO OD INTREST.
        var summary = BuildSummaryName(text);
        if (summary.Length > 0) return summary;

        var internalTransfer = ExtractInternalAccountTransferSummary(text);
        if (internalTransfer.Length > 0) return internalTransfer;

        if (ContainsWholeToken(text, "UPI")) return GetMethodCategory("UPI", mode);
        if (ContainsWholeToken(text, "IMPS")) return GetMethodCategory("IMPS", mode);
        if (ContainsWholeToken(text, "NEFT")) return GetMethodCategory("NEFT", mode);
        if (ContainsWholeToken(text, "RTGS")) return GetMethodCategory("RTGS", mode);
        if (ContainsAny(text, "CHQ", "CHEQUE", "MICR", "CLEARING")) return GetMethodCategory("CHEQUE", mode);
        if (ContainsAny(text, "TRANSFER", "TFR", "TRFR", "FUNDS TRANSFER")) return GetMethodCategory("TRANSFER", mode);

        if (IsMobileBankingNarration(text))
            return mode.Equals("PAYMENT", StringComparison.OrdinalIgnoreCase) ? "MOBILE BANKING PAYMENT" :
                   mode.Equals("RECEIPT", StringComparison.OrdinalIgnoreCase) ? "MOBILE BANKING RECEIPT" : "MOBILE BANKING";

        return string.Empty;
    }

    public static string BuildPattern(string narration) => BuildSummaryName(narration);

    private static bool IsUpiReturn(string text) =>
        ContainsAny(text, "RET-UPI", "RET UPI", "UPI RETURN", "UPI REVERSAL", "UPI REVERSE", "REV-UPI", "REV UPI");

    private static bool IsChequeReturn(string text)
    {
        var value = NormalizeWhitespace(text);
        if (IsPureChequeDishonourCharge(value)) return false;
        return ContainsAny(value, "IW REJ INST", "CHQ RETURN", "CHEQUE RETURN", "CHQ RET", "CHEQUE RET",
                   "CHEQUE BOUNCE", "CHQ BOUNCE", "RETURNED CHEQUE", "REJECTED INSTRUMENT",
                   "CHQ REVERSAL", "CHEQUE REVERSAL", "REVERSAL OF WITHDRAWAL BY CHEQUE") ||
               (value.Contains("REVERSAL OF WITHDRAWAL", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(value, "DISHONOUR", "DISHONOR", "RETURN", "REJECT"));
    }

    private static bool IsPureChequeDishonourCharge(string text)
    {
        var value = NormalizeWhitespace(text);
        return value is "DISHONOUR CHEQ" or "DISHONOUR CHQ" or "DISHONOR CHEQ" or "DISHONOR CHQ";
    }

    private static string KeepPartyOrMethodFallback(string candidate, string methodName, string original, string mode)
    {
        var value = NormalizeWhitespace(candidate);
        if (value.Length == 0 ||
            IsLedgerMappingNoiseSummary(value) ||
            IsTimeChannelResidue(value) ||
            MaskedAccountOnlyRegex().IsMatch(value) ||
            IsNarrationNoise(value))
        {
            return GetMethodCategory(methodName, mode);
        }

        // A method parser sometimes returns the untouched routing/reference narration
        // when no counterparty can be recovered. Do not let that technical text become
        // a fake party; retain the transaction under the method/direction summary.
        if ((value.Equals(NormalizeWhitespace(original), StringComparison.OrdinalIgnoreCase) || value.Contains('*')) &&
            (HasManyNumbers(value) || value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2))
        {
            return GetMethodCategory(methodName, mode);
        }
        return value;
    }

    public static string BuildLedgerMappingFallbackSummary(string narration, string mode) =>
        GetLedgerMappingFallbackCategory(SanitizeMappingNarration(narration), mode);

    private static string GetLedgerMappingFallbackCategory(string text, string mode)
    {
        var normalized = NormalizeWhitespace(text);
        if (ContainsWholeToken(normalized, "UPI") || normalized.Contains('@')) return GetMethodCategory("UPI", mode);
        if (ContainsWholeToken(normalized, "IMPS")) return GetMethodCategory("IMPS", mode);
        if (ContainsWholeToken(normalized, "NEFT")) return GetMethodCategory("NEFT", mode);
        if (ContainsWholeToken(normalized, "RTGS")) return GetMethodCategory("RTGS", mode);
        if (ContainsWholeToken(normalized, "IFT")) return GetMethodCategory("IFT", mode);
        if (ContainsAny(normalized, "ACHCR", "ACHDR", "NACH", "ECS")) return GetMethodCategory("ACH / NACH", mode);
        if (StartsWithAny(normalized, "EBANK", "DIGITA", "DIGITB", "WIB") || IsMobileBankingNarration(normalized))
            return GetMethodCategory("MOBILE BANKING", mode);
        if (ContainsAny(normalized, "CHQ", "CHEQUE", "MICR", "CLEARING", "BY INST")) return GetMethodCategory("CLEARING", mode);
        if (ContainsAny(normalized, "TRANSFER", "TFR", "TRFR", "FUNDS TRANSFER")) return GetMethodCategory("TRANSFER", mode);

        var modeText = NormalizeWhitespace(mode);
        if (modeText.Equals("PAYMENT", StringComparison.OrdinalIgnoreCase) || modeText.Equals("DEBIT", StringComparison.OrdinalIgnoreCase))
            return "OTHER PAYMENT";
        if (modeText.Equals("RECEIPT", StringComparison.OrdinalIgnoreCase) || modeText.Equals("CREDIT", StringComparison.OrdinalIgnoreCase))
            return "OTHER RECEIPT";
        if (modeText.Equals("CONTRA", StringComparison.OrdinalIgnoreCase))
            return "OTHER CONTRA";
        return "OTHER TRANSACTION";
    }

    private static string GetMethodCategory(string methodName, string mode)
    {
        var modeText = NormalizeWhitespace(mode);
        if (modeText.Contains("PAY", StringComparison.OrdinalIgnoreCase) ||
            modeText.Contains("DEBIT", StringComparison.OrdinalIgnoreCase)) return $"{methodName} PAYMENT";
        if (modeText.Contains("RECEIPT", StringComparison.OrdinalIgnoreCase) ||
            modeText.Contains("CREDIT", StringComparison.OrdinalIgnoreCase)) return $"{methodName} RECEIPT";
        if (modeText.Contains("CONTRA", StringComparison.OrdinalIgnoreCase)) return $"{methodName} CONTRA";
        return $"{methodName} TRANSACTION";
    }

    private static string ExtractReferencePrefixedParty(string text)
    {
        var words = NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Empty;

        var start = 0;
        while (start < words.Length)
        {
            var token = words[start];
            if (GeneralIsReferenceToken(token) || token is "NEFT" or "RTGS" or "IMPS" or "UTR" or "REF" or "NO")
            {
                start++;
                continue;
            }
            break;
        }

        if (start == 0 || start >= words.Length) return string.Empty;
        var candidate = GeneralCleanCandidate(string.Join(' ', words.Skip(start)));
        return GeneralIsGoodCandidate(candidate) ? candidate : string.Empty;
    }

    private static string GeneralExtractSummary(string text)
    {
        text = NormalizeWhitespace(text);
        if (text.Length == 0) return string.Empty;
        if (text.Contains("PAID IN CLEARING", StringComparison.Ordinal) ||
            text.Contains("PAID INWARD CLEARING", StringComparison.Ordinal)) return "PAID IN CLEARING";
        if (text.Contains("RECEIVED IN CLEARING", StringComparison.Ordinal)) return "RECEIVED IN CLEARING";

        var candidate = string.Empty;
        foreach (var method in new[] { "UPI", "IMPS", "NEFT", "RTGS" })
        {
            if (!text.Contains(method, StringComparison.Ordinal)) continue;
            candidate = GeneralExtractElectronicParty(text, method);
            break;
        }
        if (GeneralIsGoodCandidate(candidate)) return candidate;
        if (!GeneralNarrationHasReferenceShape(text)) return string.Empty;

        candidate = GeneralCleanCandidate(text);
        return GeneralIsGoodCandidate(candidate) ? candidate : string.Empty;
    }

    private static string GeneralExtractElectronicParty(string text, string methodName)
    {
        var position = text.IndexOf(methodName, StringComparison.OrdinalIgnoreCase);
        if (position < 0) return string.Empty;

        var tail = GeneralStripElectronicPrefix(text[(position + methodName.Length)..]);
        tail = GeneralCutReferenceTail(tail);
        var splitText = Regex.Replace(tail.Replace('\\', '/').Replace('|', '/'), "/+", "/");
        foreach (var token in splitText.Split('/'))
        {
            var candidate = GeneralCleanCandidate(token);
            if (GeneralIsGoodCandidate(candidate)) return candidate;
        }

        var finalCandidate = GeneralCleanCandidate(tail);
        return GeneralIsGoodCandidate(finalCandidate) ? finalCandidate : string.Empty;
    }

    private static string GeneralStripElectronicPrefix(string tail)
    {
        // Bank exports use several direction wrappers immediately after the payment
        // method: IMPS-OUT/..., IMPS-IN/..., NEFT_OUT:..., NEFT_IN:..., RTGS-OUT/...
        // and similar variants.  OUT/IN are routing direction markers, never party
        // names.  The old 1-2 letter prefix rule removed IN but could not remove OUT,
        // which is why a large Ledger Mapping row named "OUT" was produced instead
        // of the actual NEFT party or the safe IMPS PAYMENT fallback.
        tail = NormalizeWhitespace(tail).TrimStart(' ', '/', '\\', '|', ':', '-', '*', '_');
        tail = Regex.Replace(
            tail,
            @"^(?:IN|OUT|INWARD|OUTWARD)(?:[ _/\\|:\-*]+|$)",
            string.Empty,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Retain the workbook's compact one/two-letter channel-wrapper cleanup
        // after the explicit direction marker has been removed.
        var match = Regex.Match(tail, @"^([A-Z]{1,2})([ /\\|:\-*])", RegexOptions.CultureInvariant);
        if (match.Success) tail = tail[match.Length..];
        return NormalizeWhitespace(tail.TrimStart(' ', '/', '\\', '|', ':', '-', '*', '_'));
    }

    private static string GeneralCutReferenceTail(string text)
    {
        text = NormalizeWhitespace(text);
        var bestPosition = int.MaxValue;
        foreach (var label in ReferenceTailLabels)
        {
            var position = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (position >= 0 && position < bestPosition) bestPosition = position;
        }
        if (bestPosition is > 0 and < int.MaxValue) text = text[..bestPosition];
        return NormalizeWhitespace(text);
    }

    private static bool GeneralNarrationHasReferenceShape(string text)
    {
        text = NormalizeWhitespace(text);
        var firstToken = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return GeneralIsReferenceToken(firstToken) || text.Contains("'-", StringComparison.Ordinal) ||
               ContainsAny(text, "SENDER NO", "UTR NO", "REF NO", "CIN:", "CIN ");
    }

    private static string GeneralCleanCandidate(string candidate)
    {
        candidate = GeneralCutReferenceTail(candidate);
        candidate = NormalizeWhitespace(candidate)
            .Replace("'-", " ", StringComparison.Ordinal)
            .Replace("-'", " ", StringComparison.Ordinal)
            .Replace("'", " ", StringComparison.Ordinal);

        // Account-type suffixes written after a bank delimiter are routing metadata,
        // not part of the counterparty. Keep this delimiter-sensitive so a genuine
        // party name that simply contains the letters CA/SB/OD/CC is not damaged.
        candidate = Regex.Replace(
            candidate,
            @"(?:[-/\\|:])\s*(?:CA|SB|OD|CC)\s*$",
            " ",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Truncated corporate endings from narrow bank export fields can look like
        // "PVT L-KO". Once PVT/PRIVATE has started, the trailing L/LT plus the
        // tiny location/routing tail cannot be the party identity.
        candidate = Regex.Replace(
            candidate,
            @"\s+(?:PVT|PRIVATE|PRIVAT|PRIVA)\s+(?:L|LT|LTD)(?:[-/\\|\s]+[A-Z]{1,4})?\s*$",
            " ",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        candidate = Regex.Replace(candidate, @"[/\\|:;,.*\-()\[\]{}]+", " ", RegexOptions.CultureInvariant);

        var result = new List<string>();
        foreach (var token in CollapseSpaces(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (GeneralIsReferenceToken(token) || GeneralIsMethodWrapper(token)) continue;
            if (result.Count == 0 && GeneralIsEdgeNoise(token)) continue;
            result.Add(token);
        }

        var output = GeneralTrimEdgeNoise(string.Join(' ', result));
        output = GeneralRemoveTruncatedSuffix(output);
        output = CleanPartyName(output);
        output = RemoveBankingSuffix(output);
        output = GeneralTrimEdgeNoise(output);
        output = CollapseRepeatedPartyName(output);
        return NormalizeWhitespace(output);
    }

    private static string GeneralTrimEdgeNoise(string text)
    {
        var words = NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && GeneralIsEdgeNoise(words[0])) words.RemoveAt(0);
        while (words.Count > 0 && (GeneralIsEdgeNoise(words[^1]) || GeneralIsReferenceToken(words[^1]))) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    private static bool GeneralIsEdgeNoise(string token) => GeneralEdgeNoise.Contains(NormalizeWhitespace(token));

    private static bool GeneralIsMethodWrapper(string token)
    {
        var compact = GeneralCompactToken(token);
        foreach (var method in new[] { "UPI", "IMPS", "NEFT", "RTGS" })
        {
            if (!compact.StartsWith(method, StringComparison.Ordinal)) continue;
            var remaining = compact[method.Length..];
            return remaining.Length == 0 || (remaining.Length <= 2 && remaining.All(character => character is >= 'A' and <= 'Z'));
        }
        return false;
    }

    private static bool GeneralIsReferenceToken(string token)
    {
        var compact = GeneralCompactToken(token);
        if (compact.Length == 0) return false;
        var digitCount = CountDigits(compact);
        var letterCount = CountLetters(compact);
        if (compact.All(char.IsDigit)) return compact.Length >= 4;
        if (StartsWithAny(compact, "UTR", "REF", "CRN", "RRN", "TXN")) return digitCount >= 3;
        if (StartsWithAny(compact, "TRAN", "INSTR")) return digitCount >= 3;
        if (digitCount >= 5) return true;
        return letterCount > 0 && digitCount >= 3 && compact.Length >= 6;
    }

    private static bool GeneralIsGoodCandidate(string candidate)
    {
        candidate = NormalizeWhitespace(candidate);
        var compact = candidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (candidate.Length < 3 || candidate.Length > 80 || compact.Length == 0 || compact.All(char.IsDigit)) return false;
        if (IsTimeChannelResidue(candidate)) return false;
        if (CountLetters(candidate) < 2) return false;
        if (CountDigits(candidate) >= 5 && WordCount(candidate) <= 2) return false;
        if (candidate.Contains('@') || ContainsAny(candidate, "SENDER NO", "UTR NO", "REF NO")) return false;
        if (GeneralEdgeNoise.Contains(candidate) || ShortAcronymStopWords.Contains(candidate)) return false;
        return candidate is not ("UPI" or "IMPS" or "NEFT" or "RTGS" or "TRANSFER" or "PAYMENT" or "RECEIPT" or
            "DR" or "CR" or "BANK" or "ACCOUNT" or "CHQ" or "CHEQUE" or "CLEARING" or "MICR" or
            "BY TRANSFER" or "TO TRANSFER" or "REMT THRU CHQ");
    }

    private static string GeneralCompactToken(string token) =>
        Regex.Replace(NormalizeWhitespace(token), @"[\s\-\\/:.,*_\'()\[\]]", string.Empty, RegexOptions.CultureInvariant);

    private static string GeneralRemoveTruncatedSuffix(string text)
    {
        text = NormalizeWhitespace(text);
        foreach (var suffix in new[] { " PRIVAT", " PRIVA", " PRI", " LIMIT", " LIMI" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal)) return NormalizeWhitespace(text[..^suffix.Length]);
        }
        return text;
    }

    private static string ExtractPriorityPartyName(string text)
    {
        text = NormalizeWhitespace(text);
        var candidate = string.Empty;
        if (text.Contains("NEFT UTR NO", StringComparison.Ordinal) && text.Contains("--", StringComparison.Ordinal))
            candidate = TextBeforeDelimiter(text[(text.IndexOf("--", StringComparison.Ordinal) + 2)..], "/");
        else if (text.Contains("CLEARING", StringComparison.Ordinal) && text.Contains("SESS", StringComparison.Ordinal))
            candidate = TextBeforeDelimiter(text[(text.IndexOf("SESS", StringComparison.Ordinal) + "SESS".Length)..], "--");
        else if (text.Contains("CHEQUE WDL", StringComparison.Ordinal) && text.Contains("TRANSFER FROM", StringComparison.Ordinal))
            candidate = TextBeforeDelimiter(text[(text.LastIndexOf("TRANSFER FROM", StringComparison.Ordinal) + "TRANSFER FROM".Length)..], "/");
        else if (text.Contains("TRANSFER-INB", StringComparison.Ordinal) && text.Contains("PAYMENT TOWARDS", StringComparison.Ordinal) &&
                 text.Contains("TRANSFER FROM", StringComparison.Ordinal))
            candidate = TextBeforeDelimiter(text[(text.LastIndexOf("TRANSFER FROM", StringComparison.Ordinal) + "TRANSFER FROM".Length)..], "/");

        return candidate.Length == 0 ? string.Empty : FinalizePriorityParty(candidate);
    }

    private static string TextBeforeDelimiter(string text, string delimiter)
    {
        var position = text.IndexOf(delimiter, StringComparison.OrdinalIgnoreCase);
        return (position >= 0 ? text[..position] : text).Trim();
    }

    private static string FinalizePriorityParty(string candidate)
    {
        candidate = Regex.Replace(NormalizeWhitespace(candidate), @"--+|[-\\|:,.()]", " ", RegexOptions.CultureInvariant);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CHQ", "CHEQUE", "CLEARING", "SESS", "SESSION", "NEFT", "UTR", "NO", "TRANSFER", "FROM", "TO",
            "BY", "INB", "PAYMENT", "TOWARDS", "INVOICE", "BILL", "WDL"
        };
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !token.All(char.IsDigit))
            .Where(token => CountDigits(token) < 4)
            .Where(token => !StartsWithKnownBankCode(token))
            .Where(token => !skip.Contains(token));
        var result = CleanPartyName(string.Join(' ', words));
        if (result.EndsWith(" LT", StringComparison.Ordinal)) result = result[..^3].Trim();
        result = CollapseRepeatedPartyName(result);
        return result.Length >= 3 ? result : string.Empty;
    }

    private static string ExtractCashPresentationParty(string text)
    {
        var normalized = NormalizeWhitespace(text);
        var match = Regex.Match(
            normalized,
            @"^CAS\s+PRES\s+(?:CHO|CHQ)\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return string.Empty;

        var candidate = normalized[match.Length..];
        candidate = Regex.Replace(
            candidate,
            @"^(?:CHQ|CHEQUE)(?:\s+NO\.?)?\s+\d{3,12}\s+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        candidate = Regex.Replace(
            candidate,
            @"^(?:SESS|SESSION)\s+\d{1,2}\s+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var words = NormalizeWhitespace(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && CashPresentationRoutingCodes.Contains(words[0])) words.RemoveAt(0);
        candidate = string.Join(' ', words);
        candidate = Regex.Replace(
            candidate,
            @"\s+\d{5,}\s*$",
            string.Empty,
            RegexOptions.CultureInvariant);

        return FinalizeBankWrapperParty(candidate);
    }

    private static string ExtractBranchTransferParty(string text)
    {
        var normalized = NormalizeWhitespace(text);
        var match = Regex.Match(
            normalized,
            @"^(?:CAS\s+CHQ\s+XFER\s+WD|CHQ\s+TRFR\s+FROM)\b.*?\bOF\s+(?<party>.+?)\s+AT\s+\d{3,8}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return string.Empty;
        return FinalizeBankWrapperParty(match.Groups["party"].Value);
    }

    private static string FinalizeBankWrapperParty(string candidate)
    {
        var value = NormalizeWhitespace(candidate);
        if (value.Length == 0) return string.Empty;

        // PROP / PROPRIETOR tails describe legal capacity, not the Ledger identity.
        // A trailing single initial after PROP is also presentation text in the
        // supplied statement (for example SLV ENTERPRISES PROP L).
        value = Regex.Replace(
            value,
            @"\s+PROP(?:RIETOR)?(?:\s+[A-Z])?\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = GeneralCleanCandidate(value);
        return value.Length >= 3 && !IsNarrationNoise(value) ? value : string.Empty;
    }

    private static string CollapseRepeatedPartyName(string partyName)
    {
        var words = NormalizeWhitespace(partyName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 4 && words.Length % 2 == 0)
        {
            var half = words.Length / 2;
            if (words.Take(half).SequenceEqual(words.Skip(half), StringComparer.OrdinalIgnoreCase))
                return string.Join(' ', words.Take(half));
        }
        return string.Join(' ', words);
    }

    private static bool IsNarrationNoise(string text)
    {
        text = NormalizeWhitespace(text);
        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PAYMENT", "RECEIPT", "TRANSFER", "INVOICE", "BILL", "PURCHASE ORDER", "PAYMENT TOWARDS",
                "PAYMENT AGAINST", "PAYMENT TOWARDS INVOICE", "PAYMENT TOWARDS BILL", "AGAINST INVOICE", "AGAINST BILL",
                // Technical channel labels are not party names. Excel's structured IMPS
                // parser already skips MOB/INET; keep the final generic fallback from
                // surfacing these (or OPM) as fake Ledger Mapping parties.
                "MOB", "INET", "OPM", "P2A", "P2P", "PAYM", "BY", "TO TRANSFER", "FROM TRANSFER", "BY TRANSFER",
                "EBANK", "WIB", "ACHCR", "ACHDR", "NACHMU", "DIGITA", "DIGITB", "CRA", "MB RECEIPT",
                "BILLAMOUNT", "BILLAMOUNTPA", "BILLNO", "BILLNO10", "XXXXXXXXXX"
            }.Contains(text)) return true;
        if (MaskedAccountOnlyRegex().IsMatch(text)) return true;
        return StartsWithAny(text, "PAYMENT TOWARDS", "PAYMENT AGAINST", "AGAINST BILL", "AGAINST INVOICE", "BILLAMOUNT", "BILLNO");
    }

    private static bool IsBankInterest(string text) =>
        ContainsAny(NormalizeWhitespace(text), "INTEREST CREDIT", "INT CREDIT", "SAVINGS INTEREST", "INTEREST PAID", "INTEREST RECEIVED",
            "INT.COLL", "INT COLL", "INTEREST COLLECTION", "INTEREST DEBIT", "INT DEBIT", "INT.PD", "INT PD", "INTREST", "INT REST");

    private static string ParseUpi(string text)
    {
        text = NormalizeWhitespace(text);
        var firstBracket = text.IndexOf('(');
        if (firstBracket >= 0)
        {
            var secondBracket = text.IndexOf(')', firstBracket + 1);
            var candidate = secondBracket > firstBracket
                ? text[(firstBracket + 1)..secondBracket]
                : Regex.Split(text[(firstBracket + 1)..], "[:/]")[0];
            candidate = NormalizeWhitespace(candidate);
            if (UpiIsGoodCandidate(candidate)) return CleanPartyName(candidate);
        }

        var normalized = Regex.Replace(Regex.Replace(text, @"[/\-\\|]", ":", RegexOptions.CultureInvariant), ":+", ":");
        var bestCandidate = string.Empty;
        var bestScore = -1;
        foreach (var token in normalized.Split(':'))
        {
            var candidate = NormalizeWhitespace(token);
            if (!UpiIsGoodCandidate(candidate)) continue;
            var score = candidate.Length + (WordCount(candidate) >= 2 ? 200 : 0) + (candidate.Contains(' ') ? 100 : 0);
            if (score <= bestScore) continue;
            bestCandidate = candidate;
            bestScore = score;
        }
        if (bestCandidate.Length > 0) return CleanPartyName(bestCandidate);
        if (ContainsAny(text, "/CR/", "UPI CR", ":CR:")) return "UPI RECEIPT";
        if (ContainsAny(text, "/DR/", "UPI DR", ":DR:")) return "UPI PAYMENT";
        return "UPI";
    }

    private static bool UpiIsGoodCandidate(string text)
    {
        text = NormalizeWhitespace(text);
        if (IsTimeChannelResidue(text)) return false;
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UPI", "CR", "DR", "PAYMENT", "PAYME", "PAYMENT FRO", "PAYMENT FROM", "PAYMENT TO", "COLLECT",
            "AUTOPAY", "MANDATE", "REQUEST", "PHONEPE", "PAYTM", "GOOGLEPAY", "GPAY", "BHIM", "YBL", "AXL",
            "OKICICI", "OKHDFCBANK", "OKSBI", "IBL"
        };
        return text.Length >= 3 && !text.Contains('@') && !text.Contains(".COM", StringComparison.Ordinal) &&
               !text.Contains(".IN", StringComparison.Ordinal) && !text.Replace(" ", string.Empty).All(char.IsDigit) &&
               !HasManyNumbers(text) && !rejected.Contains(text) && !StartsWithKnownBankCode(text) && !IsNarrationNoise(text);
    }

    private static string GetNeftName(string text)
    {
        var referenceParty = ExtractReferencePrefixedParty(text);
        if (referenceParty.Length >= 3) return referenceParty;

        var normalized = Regex.Replace(NormalizeWhitespace(text), @"[-/\\|:]+", "*", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\*+", "*", RegexOptions.CultureInvariant);
        var bestName = string.Empty;
        var bestScore = -1;
        foreach (var token in normalized.Split('*'))
        {
            var candidate = NormalizeWhitespace(token);
            if (candidate.Length < 3 || candidate.Replace(" ", string.Empty).All(char.IsDigit)) continue;
            if (HasManyNumbers(candidate) && candidate.Length > 10) continue;
            if (candidate is "BY" or "TO" or "FROM" or "TRANSFER" or "BY TRANSFER" or "TRANSFER FROM" or
                "TRANSFER TO" or "NEFT" or "RTGS" or "IMPS" or "PAYMENT" or "RECEIPT" or "ACCOUNT" or "ACC") continue;
            if (StartsWithKnownBankCode(candidate) || StartsWithAny(candidate, "UTR", "REF", "CRN") ||
                ContainsAny(candidate, "IFSC", "PAYMENT TOWARDS", "INVOICE", "BILL") || IsBankName(candidate) || IsNarrationNoise(candidate)) continue;

            var wordCount = WordCount(candidate);
            var score = candidate.Length + (wordCount >= 4 ? 450 : wordCount == 3 ? 350 : wordCount == 2 ? 250 : 50) +
                        (!HasManyNumbers(candidate) ? 150 : 0);
            if (ContainsAny(candidate, "LIMITED", "LTD", "PRIVATE", "PVT")) score += 60;
            if (score <= bestScore) continue;
            bestName = RemoveBankingSuffix(CleanPartyName(candidate));
            bestScore = score;
        }
        return bestName.Length > 0 ? bestName : CleanPartyName(normalized);
    }

    private static string GetImpsName(string text)
    {
        text = NormalizeWhitespace(text);
        var direction = Regex.Match(text, @"(?:CR|DR)/", RegexOptions.CultureInvariant);
        if (direction.Success)
        {
            var candidate = text[(direction.Index + direction.Length)..].Split('/')[0].Trim();
            if (candidate.Length >= 3 && !HasManyNumbers(candidate)) return CleanPartyName(candidate);
        }

        var normalized = Regex.Replace(text, @"--+", "*", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"[-/\\|~:]+", "*", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\*+", "*", RegexOptions.CultureInvariant);
        var junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "IMPS", "MOB", "INET", "TRANSFER", "PAYMENT", "RECEIPT", "FROM", "TO", "CR", "DR" };
        var bestName = string.Empty;
        var bestScore = -1;
        foreach (var token in normalized.Split('*'))
        {
            var candidate = NormalizeWhitespace(token);
            if (candidate.Length == 0 || (candidate.Length > 12 && HasManyNumbers(candidate)) || candidate.Length < 4 ||
                candidate.Replace(" ", string.Empty).All(char.IsDigit) || HasManyNumbers(candidate)) continue;
            if (junk.Contains(candidate) || StartsWithAny(candidate, "UTR", "CRN", "REF") || StartsWithKnownBankCode(candidate)) continue;
            if (ContainsAny(candidate, "TRANSFER", "PAYMENT", "RECEIPT", "SENDING", "RECEIVING", "MONEY", "COLLECTION")) continue;

            var wordCount = WordCount(candidate);
            var score = candidate.Length + (wordCount >= 3 ? 300 : wordCount == 2 ? 200 : 25) + 100;
            if (score <= bestScore) continue;
            bestName = CleanPartyName(candidate);
            bestScore = score;
        }
        if (bestName.Length > 0) return CleanPartyName(bestName);
        if (normalized.Contains("CR", StringComparison.Ordinal)) return "IMPS RECEIPT";
        if (normalized.Contains("DR", StringComparison.Ordinal)) return "IMPS PAYMENT";
        return "IMPS";
    }

    private static string GetRtgsName(string text)
    {
        var referenceParty = ExtractReferencePrefixedParty(text);
        if (referenceParty.Length >= 3) return referenceParty;

        var normalized = Regex.Replace(NormalizeWhitespace(text), @"[-/\\|:;~]+", "*", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\*+", "*", RegexOptions.CultureInvariant);
        var bestName = string.Empty;
        var bestScore = -1;
        foreach (var token in normalized.Split('*'))
        {
            var candidate = CleanRtgsCandidate(token);
            if (IsRtgsRejectedCandidate(candidate)) continue;
            var wordCount = WordCount(candidate);
            var score = candidate.Length + (wordCount >= 4 ? 450 : wordCount == 3 ? 350 : wordCount == 2 ? 250 : 50);
            foreach (var (word, bonus) in new[]
                     {
                         ("LIMITED", 80), ("LTD", 80), ("PRIVATE", 80), ("PVT", 80), ("TRADERS", 60),
                         ("ENTERPRISES", 60), ("COMMUNICATIONS", 60), ("CRUSHER", 60), ("STONES", 60)
                     })
            {
                if (candidate.Contains(word, StringComparison.Ordinal)) score += bonus;
            }
            if (score <= bestScore) continue;
            bestName = CleanPartyName(RemoveBankingSuffix(candidate));
            bestScore = score;
        }
        return bestName.Length > 0 ? CleanPartyName(bestName) : "RTGS RECEIPT";
    }

    private static string CleanRtgsCandidate(string text)
    {
        text = NormalizeWhitespace(text);
        foreach (var phrase in new[]
                 {
                     "BY TRANSFER", "RTGS UTR NO", "RTGS UTRNO", "UTR NO", "UTRNO", "RTGS", "UTR", "REF NO",
                     "REFNO", "CRN NO", "CRNNO", "SETTLEMENT", "SETTL"
                 }) text = text.Replace(phrase, " ", StringComparison.Ordinal);

        var position = text.IndexOf("TRANSFER FROM", StringComparison.Ordinal);
        if (position > 0) text = text[..position];
        else if (position == 0) text = text["TRANSFER FROM".Length..];
        foreach (var phrase in new[] { "TRANSFER FROM", "TRANSFER", "FROM", "BY", "TO" })
            text = text.Replace(phrase, " ", StringComparison.Ordinal);

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "BY", "TO", "FROM", "TRANSFER", "RTGS", "UTR", "NO", "REF", "CRN", "IFSC", "SETTL", "SETTLEMENT" };
        return string.Join(' ', CollapseSpaces(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !skip.Contains(token) && !GeneralIsReferenceToken(token)));
    }

    private static bool IsRtgsRejectedCandidate(string text)
    {
        text = NormalizeWhitespace(text);
        return text.Length < 3 || text.Replace(" ", string.Empty).All(char.IsDigit) || StartsWithKnownBankCode(text) ||
               StartsWithAny(text, "UTR", "REF", "CRN") || text.Contains("IFSC", StringComparison.Ordinal) ||
               IsBankName(text) || IsNarrationNoise(text) || (CountDigits(text) >= 8 && WordCount(text) <= 2);
    }

    private static string ParseCheque(string text)
    {
        var original = NormalizeWhitespace(text);
        var priority = ExtractPriorityPartyName(original);
        if (priority.Length >= 3 && !IsNarrationNoise(priority)) return priority;

        var cleaned = original;
        foreach (var phrase in new[]
                 {
                     "CHQ TRANSFER", "CHEQUE TRANSFER", "CHQ PAID", "CHEQUE PAID", "MICR INWARD CLEARING",
                     "MICR OUTWARD CLEARING", "INWARD CLEARING", "OUTWARD CLEARING", "TO CLEARING", "UTR NO.",
                     "UTR NO", "UTRNO", "REF NO.", "REF NO", "REFNO", "CHQ NO.", "CHQ NO", "CHEQUE NO.", "CHEQUE NO"
                 }) cleaned = cleaned.Replace(phrase, " ", StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"[-/\\|:;~*.,()]+", " ", RegexOptions.CultureInvariant);

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CHQ", "CHEQUE", "MICR", "CLEARING", "INWARD", "OUTWARD", "PAID", "NEFT", "RTGS", "IMPS", "UPI",
            "TRANSFER", "TRF", "TO", "FROM", "BY", "UTR", "REF", "CRN", "NO", "NUMBER", "ACCOUNT", "ACC", "AC",
            "IFSC", "BRANCH", "BILL", "BILLS", "INVOICE", "INVOICES", "INV", "VOUCHER", "DOC", "DOCUMENT",
            "TOWARDS", "AGAINST"
        };
        var result = string.Join(' ', CollapseSpaces(cleaned).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !skip.Contains(token) && !GeneralIsReferenceToken(token) && !StartsWithKnownBankCode(token)));
        result = CleanPartyName(RemoveLeadingReferenceNumbers(result));
        if (result.Length >= 3 && !IsBankName(result) && !IsNarrationNoise(result)) return result;
        return CleanPartyName(original);
    }

    private static string ParseTransfer(string text)
    {
        var original = NormalizeWhitespace(text);
        if (IsGovernmentOrTax(original)) return original;

        var referenceParty = ExtractReferencePrefixedParty(original);
        if (referenceParty.Length >= 3 && !IsNarrationNoise(referenceParty)) return referenceParty;

        var cleaned = original;
        foreach (var phrase in new[]
                 {
                     "TO TRANSFER-INB BY TRANSFER", "FROM TRANSFER-INB BY TRANSFER", "TRANSFER-INB BY TRANSFER",
                     "INB BY TRANSFER", "FUNDS TRANSFER DEBIT", "FUNDS TRANSFER CREDIT", "FUNDS TRANSFER DR",
                     "FUNDS TRANSFER CR", "FUNDS TRANSFER", "A/C", "A\\C"
                 }) cleaned = cleaned.Replace(phrase, " ", StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"[-/\\|:;~*.,()]+", " ", RegexOptions.CultureInvariant);

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TO", "FROM", "BY", "TRANSFER", "TRF", "INB", "IB", "FUNDS", "FUND", "DEBIT", "CREDIT", "DR", "CR",
            "ACCOUNT", "ACC", "AC", "IFSC", "BRANCH", "NEFT", "RTGS", "IMPS", "UPI", "BILL", "BILLS", "INVOICE",
            "INVOICES", "INV", "VOUCHER", "VCH", "DOC", "DOCUMENT", "TOWARDS", "AGAINST"
        };
        var result = string.Join(' ', CollapseSpaces(cleaned).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !skip.Contains(token) && !GeneralIsReferenceToken(token)));
        result = CleanPartyName(RemoveLeadingReferenceNumbers(result));
        return result.Length >= 3 && !IsNarrationNoise(result) ? result : original;
    }

    private static string RemoveLeadingReferenceNumbers(string text) =>
        string.Join(' ', NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !HasManyNumbers(token) && !token.All(char.IsDigit)));

    private static string GetIftName(string text)
    {
        text = NormalizeWhitespace(text);
        var position = text.IndexOf("OTH-", StringComparison.Ordinal);
        if (position > 0)
        {
            var candidate = RemoveLeadingReferenceNumbers(text[..position].Replace("IB IFT", " ", StringComparison.Ordinal));
            if (candidate.Length >= 4) return CleanPartyName(candidate);
        }
        return "IFT RECEIPT";
    }

    private static string GetMbName(string text)
    {
        text = SanitizeMappingNarration(text);

        var internalTransfer = ExtractInternalAccountTransferSummary(text);
        if (internalTransfer.Length > 0)
        {
            return internalTransfer;
        }

        // Preserve the workbook's delimiter-based MB behaviour for ordinary mobile
        // banking narrations that are not account-to-account transfer wrappers.
        foreach (var token in NormalizeWhitespace(text).Replace('\\', '/').Split('/'))
        {
            var candidate = NormalizeWhitespace(token);
            if (candidate.Length < 4) continue;
            if (candidate.Replace(" ", string.Empty).All(char.IsDigit)) continue;
            if (HasManyNumbers(candidate)) continue;
            if (candidate is "MB" or "MBK") continue;
            if (candidate.Contains("CANARA", StringComparison.Ordinal)) continue;

            candidate = GeneralCleanCandidate(candidate);
            if (GeneralIsGoodCandidate(candidate) && !IsNarrationNoise(candidate))
            {
                return candidate;
            }
        }

        return "MB RECEIPT";
    }

    private static bool IsMobileBankingNarration(string text)
    {
        var normalized = NormalizeWhitespace(text);
        return normalized.StartsWith("MBK ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("MBK/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("MB ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("MB/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" MOBILE BANKING ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractInternalAccountTransferSummary(string text)
    {
        var normalized = SanitizeMappingNarration(text);
        if (normalized.Length == 0) return string.Empty;

        var hasFullWrapper = InternalAccountTransferRegex().IsMatch(normalized);
        var hasMobileFragment = IsMobileBankingNarration(normalized) &&
                                MobileBankingTransferFragmentRegex().IsMatch(normalized);
        if (!hasFullWrapper && !hasMobileFragment) return string.Empty;

        var candidate = normalized;

        // For MBK/REF/TIME/NARRATION shapes, discard structural segments before
        // cleaning the account-transfer wrapper. This mirrors what users read in
        // the statement instead of allowing the mobile reference/time to become a
        // Summary Name.
        if (IsMobileBankingNarration(candidate))
        {
            var slashParts = candidate.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (slashParts.Length >= 2)
            {
                var usefulParts = slashParts
                    .Skip(1)
                    .Where(part => !IsMobileBankingStructuralSegment(part))
                    .ToArray();
                if (usefulParts.Length > 0)
                {
                    candidate = string.Join(' ', usefulParts);
                }
            }

            candidate = MobileBankingPrefixRegex().Replace(candidate, string.Empty);
            candidate = Regex.Replace(
                candidate,
                @"^(?:[A-Z]{0,4}\d{6,24}|\d{6,24})\s+",
                string.Empty,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            candidate = TimeNoiseRegex().Replace(candidate, " ");
            candidate = SpacedTimePrefixRegex().Replace(candidate, string.Empty);
        }

        candidate = InternalAccountTransferRegex().Replace(candidate, " ");
        if (IsMobileBankingNarration(normalized))
            candidate = MobileBankingTransferFragmentRegex().Replace(candidate, " ");
        candidate = Regex.Replace(candidate, @"\b(?:ACCOUNT|A/C|A\\C)\b", " ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        candidate = RemoveLeadingReferenceNumbers(candidate);
        candidate = GeneralCleanCandidate(candidate);
        candidate = CleanPartyName(candidate);
        candidate = NormalizeWhitespace(candidate);

        if (GeneralIsGoodCandidate(candidate) &&
            !IsNarrationNoise(candidate) &&
            !candidate.Equals("CA", StringComparison.OrdinalIgnoreCase) &&
            !candidate.Equals("OD", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        // A transfer wrapper with no usable party should never create a ledger named
        // CA TO OD. Keep it as a system summary that can be reviewed/mapped once.
        return "INTERNAL BANK TRANSFER";
    }

    private static bool IsMobileBankingStructuralSegment(string segment)
    {
        var value = NormalizeWhitespace(segment);
        if (value.Length == 0) return true;
        if (value is "MB" or "MBK") return true;
        if (TimeNoiseRegex().IsMatch(value) && TimeNoiseRegex().Match(value).Value.Length == value.Length) return true;
        if (value.All(character => char.IsDigit(character) || char.IsWhiteSpace(character))) return true;
        if (CountDigits(value) >= 6 && CountLetters(value) <= 4) return true;
        return false;
    }

    private static bool IsBankName(string text)
    {
        text = NormalizeWhitespace(text);
        return text.EndsWith(" BANK", StringComparison.Ordinal) || text.StartsWith("BANK ", StringComparison.Ordinal) ||
               text.Contains("BANK OF", StringComparison.Ordinal);
    }

    private static bool HasManyNumbers(string text) => CountDigits(text) >= 4;

    private static bool IsGstPaymentNarration(string text)
    {
        var tokenText = TokenText(text);
        if (ContainsAny(tokenText, " NEFTO GST ", " NEFT GST ", " GST PAYMENT ", " GST CHALLAN ", " GST PMT ", " GSTN PAYMENT "))
            return true;
        if (ContainsWholeToken(text, "GST") &&
            new[] { "NEFT", "NEFTO", "RTGS" }.Any(token => ContainsWholeToken(text, token)))
            return true;
        return ContainsWholeToken(text, "GST") && TextHasLongNumericToken(text, 10) &&
               new[] { "NEFT", "NEFTO", "RTGS", "CHALLAN", "PAYMENT" }.Any(token => ContainsWholeToken(text, token));
    }

    private static bool IsTinOtcPaymentNarration(string text) =>
        ContainsWholeToken(text, "TIN") && ContainsWholeToken(text, "OTC");

    private static bool IsGovernmentOrTax(string text)
    {
        if (IsGstPaymentNarration(text) || IsTinOtcPaymentNarration(text)) return true;
        if (new[] { "CGST", "SGST", "IGST", "TDS", "CBDT", "EPFO", "ESIC", "CUSTOMS", "EXCISE" }
            .Any(token => ContainsWholeToken(text, token))) return true;
        var tokenText = TokenText(text);
        return ContainsAny(tokenText, " INCOME TAX ", " PROFESSIONAL TAX ", " PROF TAX ", " MCA PAYMENT ",
            " ROC PAYMENT ", " MINISTRY OF CORPORATE AFFAIRS ");
    }

    private static bool ContainsWholeToken(string text, string token) =>
        TokenText(text).Contains($" {Normalize(token)} ", StringComparison.Ordinal);

    private static string TokenText(string text) => $" {Normalize(text)} ";

    private static bool TextHasLongNumericToken(string text, int minimumDigits) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token => token.Length >= minimumDigits && token.All(char.IsDigit));

    private static string CleanPartyName(string text)
    {
        text = NormalizeWhitespace(text);
        text = Regex.Replace(text, @"[/\\\-_.: ,]+", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\b(LIMITED|LTD|PRIVATE|PVT|LLP)\b", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\b(NEFT|RTGS|IMPS)\b", " ", RegexOptions.CultureInvariant);
        return CollapseSpaces(text);
    }

    private static string RemoveBankingSuffix(string text)
    {
        text = NormalizeWhitespace(text);
        foreach (var marker in new[] { " INB ", " TRANSFER TO ", " TRANSFER FROM " })
        {
            var position = text.IndexOf(marker, StringComparison.Ordinal);
            if (position >= 0) text = text[..position];
        }

        // Counterparty bank names appended after the beneficiary are routing metadata,
        // not part of the Ledger Mapping identity (KLENE PAKS-HDFC BANK LTD,
        // SANTHOSHPAPERPRODU-I.C.I.C.I, V SRINIVAS MURTHY-STATE BANK).
        text = Regex.Replace(text,
            @"\s+(?:HDFC(?:\s+BANK)?|ICICI(?:\s+BANK)?|I\s+C\s+I\s+C\s+I|STATE\s+BANK|SBI|AXIS(?:\s+BANK)?|CANARA(?:\s+BANK)?|KOTAK(?:\s+BANK)?|YES(?:\s+BANK)?|BANK\s+OF\s+BARODA)\s*$",
            string.Empty, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return NormalizeWhitespace(text);
    }

    public static string NormalizeSummaryName(string summaryName)
    {
        var text = RemoveTrailingNumbers(NormalizeWhitespace(summaryName));
        text = Regex.Replace(text, @"\b(LIMITED|LTD|PRIVATE|PVT|LLP)\b", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\b(NEFT(?:\s+INB)?|RTGS|IMPS)\b", " ", RegexOptions.CultureInvariant);
        var colon = text.IndexOf(':');
        if (colon >= 0) text = text[..colon];
        return CollapseSpaces(text);
    }

    private static string RemoveTrailingNumbers(string text)
    {
        text = NormalizeWhitespace(text);
        return Regex.Replace(text, @"(?:\s+\d+)+\s*$", string.Empty, RegexOptions.CultureInvariant).Trim();
    }

    /// <summary>
    /// Conservative improvement for monthly bank narration variants. It removes a
    /// leading month only when it behaves as a period prefix, strips compact month/year
    /// suffixes, and removes obvious bank/branch display tails. Natural descriptions
    /// such as "AUG MONTH STATIONERY EXP" are retained.
    /// </summary>
    public static string NormalizePeriodicPartyVariant(string summaryName)
    {
        var words = NormalizeWhitespace(summaryName).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count >= 3 && MonthTokens.Contains(words[0]) && !string.Equals(words[1], "MONTH", StringComparison.OrdinalIgnoreCase))
            words.RemoveAt(0);

        var cleaned = new List<string>(words.Count);
        foreach (var word in words)
        {
            var compactPeriod = CompactMonthSuffixRegex().Match(word);
            var value = compactPeriod.Success && CountLetters(compactPeriod.Groups[1].Value) >= 2
                ? compactPeriod.Groups[1].Value
                : word;
            if (StandaloneMonthPeriodRegex().IsMatch(value)) continue;
            cleaned.Add(value);
        }
        words = cleaned;

        if (words.Count >= 3 && BankEdgeTokens.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count >= 3 && OrdinalSuffixRegex().IsMatch(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count >= 4 && (string.Equals(words[^1], "ROAD", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(words[^1], "BRANCH", StringComparison.OrdinalIgnoreCase)))
        {
            words.RemoveRange(words.Count - 2, 2);
        }
        return NormalizeWhitespace(string.Join(' ', words));
    }

    public static string GetMostFrequentSimilarName(string summaryName, IReadOnlyDictionary<string, int> summaryCounts)
    {
        var current = NormalizeWhitespace(summaryName);
        if (!IsValidPartyName(current)) return current;

        var bestName = current;
        var bestCount = summaryCounts.TryGetValue(current, out var count) ? count : 0;
        var bestQuality = CanonicalNameQuality(bestName);
        foreach (var pair in summaryCounts)
        {
            var candidate = NormalizeWhitespace(pair.Key);
            if (!IsValidPartyName(candidate) || !AreSameParty(current, candidate)) continue;
            var candidateQuality = CanonicalNameQuality(candidate);
            if (!ShouldPreferCanonicalName(candidate, pair.Value, candidateQuality, bestName, bestCount, bestQuality)) continue;
            bestName = candidate;
            bestCount = pair.Value;
            bestQuality = candidateQuality;
        }
        return bestName;
    }

    /// <summary>
    /// Final second-pass canonical name. The Excel parser remains the first authority,
    /// then a very narrow source-row cleanup collapses short sequence suffixes only
    /// when the current statement proves that the same alphabetic party occurs with
    /// two or more different one/two-digit suffixes (for example PARTY 03 / PARTY 06).
    /// A single numeric suffix is never guessed away.
    /// </summary>
    public static string GetCanonicalSummaryName(string summaryName, IReadOnlyDictionary<string, int> summaryCounts)
    {
        var canonical = GetMostFrequentSimilarName(summaryName, summaryCounts);
        if (!IsValidPartyName(canonical)) return canonical;
        if (!TryGetShortSequenceBase(canonical, out var baseName, out _)) return canonical;

        var distinctSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasUnsuffixedBase = summaryCounts.Keys.Any(key =>
            NormalizeWhitespace(key).Equals(baseName, StringComparison.OrdinalIgnoreCase));
        foreach (var key in summaryCounts.Keys)
        {
            if (!TryGetShortSequenceBase(key, out var candidateBase, out var suffix)) continue;
            if (!candidateBase.Equals(baseName, StringComparison.OrdinalIgnoreCase)) continue;
            distinctSuffixes.Add(suffix);
            if (distinctSuffixes.Count >= 2) break;
        }

        return hasUnsuffixedBase || distinctSuffixes.Count >= 2
            ? baseName
            : canonical;
    }

    private static bool TryGetShortSequenceBase(string summaryName, out string baseName, out string suffix)
    {
        baseName = string.Empty;
        suffix = string.Empty;
        var match = ShortSequenceSuffixRegex().Match(NormalizeWhitespace(summaryName));
        if (!match.Success) return false;

        var candidateBase = NormalizeWhitespace(match.Groups["base"].Value);
        if (candidateBase.Length < 5 || WordCount(candidateBase) < 2 || CountLetters(candidateBase) < 5) return false;
        if (!IsValidPartyName(candidateBase)) return false;

        baseName = candidateBase;
        suffix = match.Groups["suffix"].Value;
        return true;
    }

    public static bool IsValidPartyName(string value)
    {
        var name = NormalizeWhitespace(value);
        if (name.Length == 0 || name.Length > 80) return false;
        if (name.Length < 4)
            return name.Length == 3 && name.All(character => character is >= 'A' and <= 'Z') && !ShortAcronymStopWords.Contains(name);
        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BANK CHARGES", "BANK INTEREST", "UPI RETURN", "CHEQUE RETURN", "INSURANCE PREMIUM",
                "FIXED DEPOSIT", "DEMAND DRAFT", "CASH DEPOSIT", "CASH WITHDRAWAL", "PAID IN CLEARING",
                "RECEIVED IN CLEARING", "CLEARING", "GST PAYMENT", "TIN / OTC PAYMENT",
                "UPI PAYMENT", "UPI RECEIPT", "IMPS PAYMENT", "IMPS RECEIPT", "NEFT PAYMENT", "NEFT RECEIPT",
                "RTGS PAYMENT", "RTGS RECEIPT", "NEFT REMITTANCE", "RTGS REMITTANCE", "CHEQUE PAYMENT",
                "CHEQUE RECEIPT", "TRANSFER PAYMENT", "TRANSFER RECEIPT", "MOBILE BANKING PAYMENT",
                "MOBILE BANKING RECEIPT", "INTERNAL BANK TRANSFER"
            }.Contains(name)) return false;
        if (name.Contains("TRANSFER", StringComparison.OrdinalIgnoreCase) || IsGovernmentOrTax(name)) return false;
        if (new[] { "CRN", "UTR", "RRN", "CIN" }.Any(token => ContainsWholeToken(name, token))) return false;
        return CountLetters(name) >= 2;
    }

    private static bool IsSystemSummary(string value) =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BANK CHARGES", "BANK INTEREST", "UPI RETURN", "CHEQUE RETURN", "INSURANCE PREMIUM",
            "FIXED DEPOSIT", "DEMAND DRAFT", "CASH DEPOSIT", "CASH WITHDRAWAL",
            "PAID IN CLEARING", "RECEIVED IN CLEARING", "CLEARING",
            "GST PAYMENT", "TIN / OTC PAYMENT", "UPI PAYMENT", "UPI RECEIPT",
            "IMPS PAYMENT", "IMPS RECEIPT", "NEFT PAYMENT", "NEFT RECEIPT", "RTGS PAYMENT", "RTGS RECEIPT",
            "NEFT REMITTANCE", "RTGS REMITTANCE", "CHEQUE PAYMENT", "CHEQUE RECEIPT",
            "TRANSFER PAYMENT", "TRANSFER RECEIPT", "MOBILE BANKING PAYMENT", "MOBILE BANKING RECEIPT",
            "INTERNAL BANK TRANSFER"
        }.Contains(NormalizeWhitespace(value));

    public static bool AreSameParty(string first, string second)
    {
        var firstKey = BuildPartyMatchKey(first);
        var secondKey = BuildPartyMatchKey(second);
        if (firstKey.Length == 0 || secondKey.Length == 0) return false;
        if (firstKey.Equals(secondKey, StringComparison.OrdinalIgnoreCase)) return true;
        if (firstKey.Length <= 4 || secondKey.Length <= 4) return false;

        var minimumLength = Math.Min(firstKey.Length, secondKey.Length);
        var maximumLength = Math.Max(firstKey.Length, secondKey.Length);
        var allowedDifference = Math.Max(4, (int)(minimumLength * 0.45m));
        if (minimumLength >= 8 && maximumLength - minimumLength <= allowedDifference &&
            (firstKey.Contains(secondKey, StringComparison.OrdinalIgnoreCase) || secondKey.Contains(firstKey, StringComparison.OrdinalIgnoreCase)))
            return true;

        return FuzzyPartyNamesCompatible(first, second);
    }

    private static bool FuzzyPartyNamesCompatible(string first, string second)
    {
        var firstWords = NormalizeWhitespace(first).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var secondWords = NormalizeWhitespace(second).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (firstWords.Length == 0 || firstWords.Length != secondWords.Length) return false;
        for (var index = 0; index < firstWords.Length - 1; index++)
        {
            if (firstWords[index].Equals(secondWords[index], StringComparison.OrdinalIgnoreCase)) continue;
            if (!ConservativeWordVariant(firstWords[index], secondWords[index])) return false;
        }

        var firstLast = firstWords[^1];
        var secondLast = secondWords[^1];
        if (firstLast.Equals(secondLast, StringComparison.OrdinalIgnoreCase)) return true;

        // A final bank-field truncation such as PACK -> PA is safe only after every
        // preceding word has already matched conservatively. This lets the uploaded
        // CHINDULUR POLY PACK / CHINDALOOR POLY PA evidence collapse without making
        // general short tokens globally fuzzy.
        var shortLast = firstLast.Length <= secondLast.Length ? firstLast : secondLast;
        var longLast = firstLast.Length <= secondLast.Length ? secondLast : firstLast;
        if (shortLast.Length >= 2 && longLast.Length >= 4 &&
            longLast.Length - shortLast.Length >= 2 &&
            longLast.StartsWith(shortLast, StringComparison.OrdinalIgnoreCase)) return true;

        if (firstLast.Length <= 3 || secondLast.Length <= 3) return false;
        if (firstLast.Contains(secondLast, StringComparison.OrdinalIgnoreCase) || secondLast.Contains(firstLast, StringComparison.OrdinalIgnoreCase)) return true;
        return firstLast.Length >= 6 && secondLast.Length >= 6 &&
               firstLast[..3].Equals(secondLast[..3], StringComparison.OrdinalIgnoreCase) && SimilarEnough(firstLast, secondLast);
    }

    private static bool ConservativeWordVariant(string first, string second)
    {
        var left = NormalizeWhitespace(first);
        var right = NormalizeWhitespace(second);
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        if (left.Length < 6 || right.Length < 6 || Math.Abs(left.Length - right.Length) > 1) return false;
        if (!left[..3].Equals(right[..3], StringComparison.OrdinalIgnoreCase)) return false;

        static string ConsonantSkeleton(string value) =>
            new string(value.Where(character => "AEIOU".IndexOf(character) < 0).ToArray());

        var leftSkeleton = ConsonantSkeleton(left);
        var rightSkeleton = ConsonantSkeleton(right);
        return leftSkeleton.Length >= 4 && leftSkeleton.Equals(rightSkeleton, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SimilarEnough(string first, string second)
    {
        var minimumLength = Math.Min(first.Length, second.Length);
        if (minimumLength == 0) return false;
        var matches = 0;
        for (var index = 0; index < minimumLength; index++)
            if (char.ToUpperInvariant(first[index]) == char.ToUpperInvariant(second[index])) matches++;
        return matches >= minimumLength * 0.8m;
    }

    private static string BuildPartyMatchKey(string value)
    {
        var normalized = NormalizeWhitespace(value);
        normalized = Regex.Replace(normalized, @"\b(LIMITED|LTD|PRIVATE|PVT|LLP)\b", " ", RegexOptions.CultureInvariant);
        return new string(normalized.Where(char.IsLetterOrDigit).ToArray());
    }

    private static int CanonicalNameQuality(string value)
    {
        var name = NormalizeWhitespace(value);
        if (name.Length == 0) return 0;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var compactLength = name.Count(char.IsLetterOrDigit);
        var score = name.Length + words.Length * 30;
        if (name.Contains(' ')) score += 35;
        if (!name.Any(char.IsDigit)) score += 25;
        if (words.Length >= 2) score += 20;
        if (words.Length == 1 && compactLength >= 12) score -= 20;
        if (HasLikelyTruncatedEnding(name)) score -= 70;
        return score;
    }

    private static bool HasLikelyTruncatedEnding(string value)
    {
        var words = NormalizeWhitespace(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var last = words[^1];
        if (last.Length <= 2 && !last.All(char.IsDigit)) return true;
        return last is "PRIVA" or "PRIVAT" or "LIMI" or "LIMIT" or "COMMUNICATIO" or "ENTERPRIS" or "AGEN" or "ESCRO";
    }

    private static bool ShouldPreferCanonicalName(
        string candidateName, int candidateCount, int candidateQuality,
        string bestName, int bestCount, int bestQuality)
    {
        var candidateKey = BuildPartyMatchKey(candidateName);
        var bestKey = BuildPartyMatchKey(bestName);
        if (candidateKey.Length > bestKey.Length && candidateKey.Contains(bestKey, StringComparison.OrdinalIgnoreCase) &&
            candidateQuality >= bestQuality - 10 && !HasLikelyTruncatedEnding(candidateName)) return true;
        if (bestKey.Length > candidateKey.Length && bestKey.Contains(candidateKey, StringComparison.OrdinalIgnoreCase) &&
            bestQuality >= candidateQuality - 10) return false;
        if (candidateQuality >= bestQuality + 15) return true;
        if (bestQuality >= candidateQuality + 15) return false;
        if (candidateCount != bestCount) return candidateCount > bestCount;
        if (candidateQuality != bestQuality) return candidateQuality > bestQuality;
        return candidateName.Length > bestName.Length;
    }

    public static string NormalizeLedgerExactMatchKey(string text)
    {
        var value = text.Trim().ToUpperInvariant().Replace("&", "AND", StringComparison.Ordinal);
        return new string(value.Where(char.IsLetterOrDigit).ToArray());
    }

    public static bool IsBankCharge(string narration)
    {
        var text = NormalizeWhitespace(narration);
        if (text is "CHARGE" or "CHARGES" or "CHRGS" or "CHGS") return true;
        return ContainsAny(text,
            "BANK CHARGE", "BANK CHGS", "CHGS", "CHRGE", "CHARGE REC FOR MICR", "SERVICE CHG", "SERVICE CHARGE",
            "SERVICE TAX", "SMS CHARGE", "SMS ALERT", "NEFT SC", "RTGS SC", "NEFT CHG", "RTGS CHG", "IMPS CHARGE",
            "IMPS CHG", "UPI CHARGE", "UPI CHG", "CASH HANDLING", "COLLECTION CHARGE", "PROCESSING CHARGE",
            "PROCESSING FEE", "ANNUAL FEE", "CARD FEE", "DEBIT CARD FEE", "ATM CHARGE", "ATM FEE", "PENAL CHARGE",
            "INCIDENTAL CHARGE", "LEDGER FOLIO", "BANK COMMISSION", "COMMISSION",
            "AC KEEPING FEE", "ACCOUNT KEEPING FEE", "CASH HND CHR", "CASH HANDLING CHR",
            "DISHONOUR CHEQ", "DISHONOUR CHQ", "DISHONOR CHEQ", "DISHONOR CHQ",
            // GST components raised by the bank on card/service charges are part of the same bank-charge family.
            "GST ON CHARGE", "CGST ON CHARGE", "SGST ON CHARGE", "IGST ON CHARGE",
            "CHARGES FOR PORD CUSTOMER PAYMENT", "CHARGES FOR PORD CUSTOMER PAYMEN",
            "CHARGES FOR :IMPS", "CHARGES FOR IMPS", "DCARDFEE", "DCARD FEE", "REVIEW CHARGES",
            "CHEQUE BOOK CHARGE", "CHEQUE BOOK CHARGES", "CHQ BOOK CHARGE", "CHQ BOOK CHARGES") ||
               text.StartsWith("CHARGES FOR ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCtsClearingParty(string text)
    {
        var normalized = SanitizeMappingNarration(text);
        var match = CtsClearingRegex().Match(normalized);
        if (!match.Success || match.Index <= 0) return string.Empty;

        var candidate = normalized[..match.Index];
        candidate = LeadingSequenceNumberRegex().Replace(candidate, string.Empty);
        candidate = Regex.Replace(candidate,
            @"^(?:BY\s+)?(?:INST|INSTR|INSTRUMENT)\s+\d{1,8}\s*[:\-]?\s*",
            " ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        candidate = Regex.Replace(candidate, @"\bMICR(?:\s+INWARD|\s+OUTWARD)?\s*$", " ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        candidate = Regex.Replace(candidate, @"^(?:BY\s+)?(?:INST|INSTR|INSTRUMENT)\s+", " ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        candidate = GeneralCleanCandidate(candidate);
        return GeneralIsGoodCandidate(candidate) ? candidate : string.Empty;
    }

    private static string ExtractAchParty(string text)
    {
        var normalized = NormalizeWhitespace(text).Replace('\\', '/');
        if (normalized.StartsWith("NACHMU", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/ACHCR/", StringComparison.OrdinalIgnoreCase))
        {
            var marker = normalized.IndexOf("ACHCR", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var tail = normalized[(marker + "ACHCR".Length)..].Trim(' ', '/', '-', ':');
                var token = tail.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                var candidate = GeneralCleanCandidate(token);
                if (GeneralIsGoodCandidate(candidate)) return candidate;
            }
        }

        if (normalized.StartsWith("ACHDR", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var candidate = Regex.Replace(parts[1], @"^TP\s+ACH\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                candidate = GeneralCleanCandidate(candidate);
                if (GeneralIsGoodCandidate(candidate)) return candidate;
            }
        }
        return string.Empty;
    }

    private static bool IsAchWrapperWithoutParty(string text) =>
        StartsWithAny(NormalizeWhitespace(text), "NACHMU", "ACHDR", "ACHCR");

    private static string ExtractEBankWibParty(string text)
    {
        var normalized = NormalizeWhitespace(text).Replace('\\', '/');
        if (!StartsWithAny(normalized, "EBANK:WIB", "EBANK WIB")) return string.Empty;
        var parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Length < 3) return string.Empty;
        var candidate = GeneralCleanCandidate(parts[^1]);
        return GeneralIsGoodCandidate(candidate) ? candidate : string.Empty;
    }

    private static bool HasPartyAfterDigitalReference(string text)
    {
        var normalized = NormalizeWhitespace(text).Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return false;
        for (var i = 1; i < parts.Length; i++)
        {
            var candidate = GeneralCleanCandidate(parts[i]);
            if (candidate.Length == 0 || GeneralIsReferenceToken(candidate) ||
                StartsWithAny(candidate, "NEFT", "IMPS", "RTGS", "P2A", "P2P", "CRA")) continue;
            if (GeneralIsGoodCandidate(candidate)) return true;
        }
        return false;
    }

    private static string ExtractTrailingInstrumentParty(string text)
    {
        var normalized = NormalizeWhitespace(text);
        var match = Regex.Match(normalized, @"(?:[/\-]|\s)BY\s+INST(?:R(?:UMENT)?)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || match.Index <= 0) return string.Empty;
        var candidate = GeneralCleanCandidate(normalized[..match.Index]);
        return GeneralIsGoodCandidate(candidate) ? candidate : string.Empty;
    }

    private static string ExtractSimpleDirectionalParty(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (!normalized.StartsWith("BY ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("BY INST", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("BY TRANSFER", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var candidate = GeneralCleanCandidate(normalized[3..]);
        return GeneralIsGoodCandidate(candidate) ? candidate : string.Empty;
    }

    private static bool IsConservativeTruncatedVariant(
        string first,
        string second,
        IReadOnlyDictionary<string, int> summaryCounts)
    {
        var firstName = NormalizeWhitespace(first);
        var secondName = NormalizeWhitespace(second);
        var firstKey = BuildPartyMatchKey(firstName);
        var secondKey = BuildPartyMatchKey(secondName);
        if (firstKey.Length == 0 || secondKey.Length == 0) return false;

        var shortName = firstKey.Length <= secondKey.Length ? firstName : secondName;
        var shortKey = firstKey.Length <= secondKey.Length ? firstKey : secondKey;
        var longName = firstKey.Length <= secondKey.Length ? secondName : firstName;
        var longKey = firstKey.Length <= secondKey.Length ? secondKey : firstKey;
        if (shortKey.Length < 4 || longKey.Length - shortKey.Length < 2 || !longKey.StartsWith(shortKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (CountLetters(shortName) < 4) return false;

        var shortCount = summaryCounts.TryGetValue(shortName, out var count) ? count : 0;
        var longCount = summaryCounts.TryGetValue(longName, out var candidateCount) ? candidateCount : 0;

        // Normal multi-word truncated variants follow the workbook-frequency guard.
        if (WordCount(longName) >= 2) return shortCount <= 3 || longCount >= shortCount;

        // Bank export fields also truncate long unspaced business names (for example
        // SANTHOSHPA vs SANTHOSHPAPERPRODU). Only accept a strong alphabetic prefix
        // with at least four additional letters; this is deliberately narrower than
        // general fuzzy matching and lets the higher-quality full form win.
        return WordCount(shortName) == 1 && shortKey.Length >= 8 &&
               longKey.Length - shortKey.Length >= 4 &&
               shortKey.All(char.IsLetter) && longKey.All(char.IsLetter);
    }

    private static string SanitizeMappingNarration(string narration)
    {
        var text = NormalizeWhitespace(narration);
        if (text.Length == 0) return string.Empty;
        var serializedShape = text.Contains('"') || text.Contains('[') || text.Contains('{');

        var bracketPosition = text.IndexOfAny(new[] { '[', '{' });
        if (bracketPosition > 0)
        {
            var tail = text[bracketPosition..];
            if (tail.Contains('"') || tail.Contains(',') || DateNoiseRegex().IsMatch(tail))
                text = text[..bracketPosition];
        }

        // Semicolons are bank narration separators, never party-name characters.
        // Remove them before every UPI/IMPS/NEFT/RTGS/transfer/fallback parser so
        // the same residue cannot survive through a method-specific branch.
        text = text.Replace(';', ' ').Replace('"', ' ').Replace('[', ' ').Replace(']', ' ').Replace('{', ' ').Replace('}', ' ');
        // Clock times are bank-channel metadata for Ledger Mapping and are never
        // useful as a party identity. Strip them for every narration shape, not only
        // serialized Python/JSON-like rows.
        text = TimeNoiseRegex().Replace(text, " ");
        if (serializedShape)
        {
            text = DateNoiseRegex().Replace(text, " ");
            text = Regex.Replace(text, @"\b(?:NIL|NULL|NONE)\b", " ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }
        text = NormalizeWhitespace(text);
        if (serializedShape)
            text = Regex.Replace(text, @"^\d{1,3}\s+(?=[A-Z])", string.Empty, RegexOptions.CultureInvariant);
        return NormalizeWhitespace(text);
    }

    private static string UpiFallbackForMode(string txnMode)
    {
        var mode = NormalizeWhitespace(txnMode);
        if (mode.Equals("PAYMENT", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("DEBIT", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("DR", StringComparison.OrdinalIgnoreCase))
            return "UPI PAYMENT";
        if (mode.Equals("RECEIPT", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("CREDIT", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("CR", StringComparison.OrdinalIgnoreCase))
            return "UPI RECEIPT";
        return "UPI";
    }

    private static bool IsTechnicalUpiReferenceTimeNarration(string text) =>
        TechnicalUpiReferenceTimeRegex().IsMatch(NormalizeWhitespace(text));

    /// <summary>
    /// True when a candidate is just a clock time (possibly already converted from
    /// ':' to spaces) plus a tiny UPI/channel residue. Public so persisted legacy
    /// Ledger Mapping rows can be detected and rebuilt once after this parser fix.
    /// </summary>
    public static bool IsTechnicalTimeResidue(string value) => IsTimeChannelResidue(value);

    /// <summary>
    /// True only when a parsed Ledger Mapping summary is unusable technical residue.
    /// Safe method/direction fallback categories remain valid workbench summaries so
    /// every mapping-eligible transaction stays accounted for even without a party.
    /// </summary>
    public static bool IsLedgerMappingNoiseSummary(string value)
    {
        var name = NormalizeWhitespace(value);
        if (name.Length == 0 || IsTimeChannelResidue(name)) return true;

        // Method/direction and system fallback categories are intentional Ledger
        // Mapping summaries. They must remain visible so Pending/All counts account
        // for every transaction even when the bank narration contains no party name.
        if (Regex.IsMatch(
                name,
                @"^(?:UPI|IMPS|NEFT|RTGS|IFT|ACH / NACH|CLEARING|TRANSFER|MOBILE BANKING|OTHER)\s+(?:PAYMENT|RECEIPT|REMITTANCE|CONTRA|TRANSACTION)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PAID IN CLEARING", "RECEIVED IN CLEARING", "CLEARING", "INTERNAL BANK TRANSFER",
                "BANK CHARGES", "BANK INTEREST", "GST PAYMENT", "TIN / OTC PAYMENT",
                "CASH DEPOSIT", "CASH WITHDRAWAL", "INSURANCE PREMIUM", "FIXED DEPOSIT",
                "DEMAND DRAFT", "UPI RETURN", "CHEQUE RETURN"
            }.Contains(name)) return false;

        // Bare routing/wrapper labels and reference metadata are still noise.
        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PAYMENT", "RECEIPT", "TRANSFER", "UPI", "IMPS", "NEFT", "RTGS",
                "BY", "TO TRANSFER", "FROM TRANSFER", "BY TRANSFER", "P2A", "P2P", "PAYM", "CIN",
                "EBANK", "WIB", "ACHCR", "ACHDR", "NACHMU", "DIGITA", "DIGITB", "CRA", "MB RECEIPT"
            }.Contains(name)) return true;

        if (MaskedAccountOnlyRegex().IsMatch(name) ||
            StartsWithAny(name, "BILLNO", "BILLAMOUNT", "XXXXXXXX", "DIGITA ", "DIGITB ") ||
            name.Equals("EBANK WIB", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("INST CLG CTS", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("INST MICR INWARD CLG CTS", StringComparison.OrdinalIgnoreCase)) return true;

        return Regex.IsMatch(
            name,
            @"^\d{1,6}\s+(?:CIN|UTR|RRN|REF)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool RequiresLedgerMappingRebuild(string value)
    {
        var name = NormalizeWhitespace(value);
        if (name.Length == 0) return false;
        if (IsLedgerMappingNoiseSummary(name) || MaskedAccountOnlyRegex().IsMatch(name)) return true;
        if (LeadingSequenceNumberRegex().IsMatch(name)) return true;
        if (StartsWithAny(name, "NACHMU ", "ACHDR ", "ACHCR ", "EBANK ", "DIGITA ", "DIGITB ", "INST ", "BY ",
                "BILLNO", "BILLAMOUNT", "XXXXXXXX", "CAS PRES ", "CAS XFER WD ", "TRFR OF ")) return true;
        if (name.StartsWith("PAYMENT ", StringComparison.OrdinalIgnoreCase) && name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2) return true;
        if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsEmbeddedBankReferenceToken)) return true;
        if (ContainsAny(name, "MICR INWARD CLG CTS", "MICR CLG CTS", "GST RESERVE BANK",
                "PMSBY", "PMJJBY", "APY PREMIUM", "STOCK INSURANCE")) return true;
        if (InternalTransferOnlyRegex().IsMatch(name) || name.EndsWith(" BY INST", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("GST", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsBankCharge(name) && !name.Equals("BANK CHARGES", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsChequeReturn(name) && !name.Equals("CHEQUE RETURN", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("CAS CORR ", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(" HDFC BANK", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" I C I C I", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" STATE BANK", StringComparison.OrdinalIgnoreCase)) return true;
        if (WordCount(name) >= 3 && Regex.IsMatch(name, @"\s+(?:CA|SB|OD|CC)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        if (WordCount(name) >= 3 && Regex.IsMatch(name, @"\s+(?:L|LT)\s+[A-Z]{1,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        return false;
    }

    private static bool IsTimeChannelResidue(string value)
    {
        var normalized = NormalizeWhitespace(value).Replace(':', ' ');
        return TimeChannelResidueRegex().IsMatch(CollapseSpaces(normalized));
    }

    private static bool StartsWithKnownBankCode(string text)
    {
        var compact = GeneralCompactToken(text);
        return compact.Length >= 4 && KnownBankPrefixes.Contains(compact[..4]);
    }

    private static int CountDigits(string text) => text.Count(char.IsDigit);
    private static int CountLetters(string text) => text.Count(character => (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z'));
    private static int WordCount(string text) => NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string NormalizeWhitespace(string text)
    {
        var value = (text ?? string.Empty).Replace('\u00A0', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
            .Trim().ToUpperInvariant();
        return CollapseSpaces(value);
    }

    private static string CollapseSpaces(string text) => Regex.Replace(text ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool StartsWithAny(string text, params string[] values) =>
        values.Any(value => text.StartsWith(value, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<!\d)(?:0?[1-9]|[12]\d|3[01])[ ./-](?:0?[1-9]|1[0-2])[ ./-](?:19|20)\d{2}(?!\d)|(?<!\d)(?:19|20)\d{2}[ ./-](?:0?[1-9]|1[0-2])[ ./-](?:0?[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DateNoiseRegex();

    [GeneratedRegex(@"(?<!\d)(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeNoiseRegex();

    [GeneratedRegex(@"(?:^|[/\\|\-])UPI[/\\|\-]+[A-Z0-9]{6,30}[/\\|\-]+(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?[/\\|\-]+UPI[/\\|\-]+[A-Z0-9]{1,3}(?:$|[/\\|\-])", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalUpiReferenceTimeRegex();

    [GeneratedRegex(@"^(?:[01]?\d|2[0-3])\s+[0-5]\d(?:\s+[0-5]\d)?(?:\s+(?:UPI|[A-Z0-9]{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeChannelResidueRegex();

    [GeneratedRegex(@"^MBK\s+(?:[A-Z0-9]{8,}\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex MobileBankingPrefixRegex();

    [GeneratedRegex(@"\s+(?:CA|SB|OD|CC)\s+TO\s+(?:CA|SB|OD|CC)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex MobileBankingAccountTailRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])(?:TO\s+)?(?:(?:CURRENT\s+)?(?:ACCOUNT|A/?C)|CA|SB|SAVINGS|CC|CASH\s+CREDIT|OD|OVERDRAFT)\s+TO\s+(?:(?:CURRENT\s+)?(?:ACCOUNT|A/?C)|CA|SB|SAVINGS|CC|CASH\s+CREDIT|OD|OVERDRAFT)(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex InternalAccountTransferRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])(?:(?:CURRENT\s+)?(?:ACCOUNT|A/?C)|CA|SB|SAVINGS|CC|CASH\s+CREDIT|OD|OVERDRAFT)\s+TO(?![A-Z0-9])|(?<![A-Z0-9])TO\s+(?:(?:CURRENT\s+)?(?:ACCOUNT|A/?C)|CA|SB|SAVINGS|CC|CASH\s+CREDIT|OD|OVERDRAFT)(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex MobileBankingTransferFragmentRegex();

    [GeneratedRegex(@"^(?:[01]?\d|2[0-3])\s+[0-5]\d\s+[0-5]\d\s+", RegexOptions.IgnoreCase)]
    private static partial Regex SpacedTimePrefixRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])(?:[A-Z]{2,8}[/-]?)?\d{6,22}(?![A-Z0-9])|(?<![A-Z0-9])[A-Z0-9]{10,24}(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z0-9]{4,35}(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceGroupRegex();

    [GeneratedRegex(@"^(.+?)(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\d{2,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex CompactMonthSuffixRegex();

    [GeneratedRegex(@"^(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\d{2,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneMonthPeriodRegex();

    [GeneratedRegex(@"^\d+(ST|ND|RD|TH)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSuffixRegex();

    [GeneratedRegex(@"(?:MICR\s+(?:INWARD|OUTWARD)\s+)?CLG\s*\(?CTS\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CtsClearingRegex();

    [GeneratedRegex(@"^\d{3,6}\s+(?=[A-Z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSequenceNumberRegex();

    [GeneratedRegex(@"^X{4,}(?:\d{1,8})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaskedAccountOnlyRegex();

    [GeneratedRegex(@"^TO\s+(?:CA\s+)?\d{1,4}(?:[/\s]+)\d{1,8}(?:\s*[-/]\s*[A-Z0-9]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternalTransferOnlyRegex();

    [GeneratedRegex(@"^(?<base>.+\D)\s+(?<suffix>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ShortSequenceSuffixRegex();
}
