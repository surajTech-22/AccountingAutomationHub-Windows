using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Result of resolving a bank statement's issuing bank from printed OCR headers and RBI IFSC codes.
/// </summary>
public sealed class BankIdentityResolution
{
    public string BankName { get; init; } = string.Empty;
    public string BankStatus { get; init; } = "NOT_DETECTED";
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public bool IsSharedApexIfscFallback { get; init; }
}

/// <summary>
/// Shared Indian bank / IFSC / co-operative bank identity resolver used by Bank OCR verification
/// and statement workbook import. Preserves exact canonical names for established banks while
/// supporting State Co-operative Apex banks, DCCBs, RRBs, Urban Co-op banks, SFBs, and arbitrary
/// valid 11-character RBI IFSC codes.
/// </summary>
public static class BankIfscCatalog
{
    public sealed record CatalogEntry(
        string CanonicalUpper,
        string DisplayName,
        string[] IfscPrefixes,
        string[] Aliases,
        bool IsSharedCoopApex = false);

    private sealed record AliasMatch(int Start, int End, string Bank, string Alias);

    private static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        // Existing 37 core banks — keep CanonicalUpper and DisplayName strings unchanged.
        new("STATE BANK OF INDIA", "State Bank of India", ["SBIN"], ["STATE BANK OF INDIA"]),
        new("HDFC BANK", "HDFC Bank", ["HDFC"], ["HDFC BANK"]),
        new("ICICI BANK", "ICICI Bank", ["ICIC"], ["ICICI BANK"]),
        new("AXIS BANK", "Axis Bank", ["UTIB"], ["AXIS BANK"]),
        new("CANARA BANK", "Canara Bank", ["CNRB"], ["CANARA BANK"]),
        new("BANK OF BARODA", "Bank of Baroda", ["BARB"], ["BANK OF BARODA"]),
        new("BANK OF INDIA", "Bank of India", ["BKID"], ["BANK OF INDIA"]),
        new("INDIAN BANK", "Indian Bank", ["IDIB"], ["INDIAN BANK"]),
        new("INDIAN OVERSEAS BANK", "Indian Overseas Bank", ["IOBA"], ["INDIAN OVERSEAS BANK"]),
        new("UNION BANK OF INDIA", "Union Bank of India", ["UBIN"], ["UNION BANK OF INDIA", "UNION BANK"]),
        new("PUNJAB NATIONAL BANK", "Punjab National Bank", ["PUNB"], ["PUNJAB NATIONAL BANK"]),
        new("KOTAK MAHINDRA BANK", "Kotak Mahindra Bank", ["KKBK"], ["KOTAK MAHINDRA BANK", "KOTAK BANK"]),
        new("IDFC FIRST BANK", "IDFC FIRST Bank", ["IDFB"], ["IDFC FIRST BANK"]),
        new("KARNATAKA BANK", "Karnataka Bank", ["KARB"], ["KARNATAKA BANK"]),
        new("FEDERAL BANK", "Federal Bank", ["FDRL"], ["FEDERAL BANK"]),
        new("SOUTH INDIAN BANK", "South Indian Bank", ["SIBL"], ["SOUTH INDIAN BANK"]),
        new("YES BANK", "Yes Bank", ["YESB"], ["YES BANK"]),
        new("INDUSIND BANK", "IndusInd Bank", ["INDB"], ["INDUSIND BANK"]),
        new("RBL BANK", "RBL Bank", ["RATN"], ["RBL BANK"]),
        new("BANDHAN BANK", "Bandhan Bank", ["BDBL"], ["BANDHAN BANK"]),
        new("AU SMALL FINANCE BANK", "AU Small Finance Bank", ["AUBL"], ["AU SMALL FINANCE BANK"]),
        new("UJJIVAN SMALL FINANCE BANK", "Ujjivan Small Finance Bank", ["UJVN"], ["UJJIVAN SMALL FINANCE BANK"]),
        new("EQUITAS SMALL FINANCE BANK", "Equitas Small Finance Bank", ["ESFB"], ["EQUITAS SMALL FINANCE BANK"]),
        new("DBS BANK INDIA", "DBS Bank India", ["DBSS"], ["DBS BANK INDIA", "DBS BANK"]),
        new("HSBC", "HSBC", ["HSBC"], ["HSBC"]),
        new("STANDARD CHARTERED BANK", "Standard Chartered Bank", ["SCBL"], ["STANDARD CHARTERED BANK", "STANDARD CHARTERED"]),
        new("CITI BANK", "Citi Bank", ["CITI"], ["CITIBANK", "CITI BANK"]),
        new("CENTRAL BANK OF INDIA", "Central Bank of India", ["CBIN"], ["CENTRAL BANK OF INDIA"]),
        new("BANK OF MAHARASHTRA", "Bank of Maharashtra", ["MAHB"], ["BANK OF MAHARASHTRA"]),
        new("UCO BANK", "UCO Bank", ["UCBA"], ["UCO BANK"]),
        new("PUNJAB & SIND BANK", "Punjab & Sind Bank", ["PSIB"], ["PUNJAB & SIND BANK", "PUNJAB AND SIND BANK"]),
        new("JAMMU & KASHMIR BANK", "Jammu & Kashmir Bank", ["JAKA"], ["JAMMU & KASHMIR BANK", "JAMMU AND KASHMIR BANK"]),
        new("CSB BANK", "CSB Bank", ["CSBK"], ["CSB BANK", "CATHOLIC SYRIAN BANK"]),
        new("DCB BANK", "DCB Bank", ["DCBL"], ["DCB BANK"]),
        new("TAMILNAD MERCANTILE BANK", "Tamilnad Mercantile Bank", ["TMBL"], ["TAMILNAD MERCANTILE BANK"]),
        new("KARUR VYSYA BANK", "Karur Vysya Bank", ["KVBL"], ["KARUR VYSYA BANK"]),
        new("CITY UNION BANK", "City Union Bank", ["CIUB"], ["CITY UNION BANK"]),

        // Commercial / Small Finance / Payments Banks
        new("IDBI BANK", "IDBI Bank", ["IBKL"], ["IDBI BANK"]),
        new("ESAF SMALL FINANCE BANK", "ESAF Small Finance Bank", ["ESAF"], ["ESAF SMALL FINANCE BANK"]),
        new("SURYODAY SMALL FINANCE BANK", "Suryoday Small Finance Bank", ["SURY"], ["SURYODAY SMALL FINANCE BANK"]),
        new("JANA SMALL FINANCE BANK", "Jana Small Finance Bank", ["JSFB"], ["JANA SMALL FINANCE BANK"]),
        new("FINCARE SMALL FINANCE BANK", "Fincare Small Finance Bank", ["FSFB"], ["FINCARE SMALL FINANCE BANK"]),
        new("UTKARSH SMALL FINANCE BANK", "Utkarsh Small Finance Bank", ["USFB"], ["UTKARSH SMALL FINANCE BANK"]),
        new("CAPITAL SMALL FINANCE BANK", "Capital Small Finance Bank", ["CLBL"], ["CAPITAL SMALL FINANCE BANK"]),
        new("NORTH EAST SMALL FINANCE BANK", "North East Small Finance Bank", ["NESF"], ["NORTH EAST SMALL FINANCE BANK"]),
        new("SHIVALIK SMALL FINANCE BANK", "Shivalik Small Finance Bank", ["SHIX"], ["SHIVALIK SMALL FINANCE BANK"]),
        new("UNITY SMALL FINANCE BANK", "Unity Small Finance Bank", ["UNBA"], ["UNITY SMALL FINANCE BANK"]),
        new("AIRTEL PAYMENTS BANK", "Airtel Payments Bank", ["AIRP"], ["AIRTEL PAYMENTS BANK"]),
        new("INDIA POST PAYMENTS BANK", "India Post Payments Bank", ["IPOS"], ["INDIA POST PAYMENTS BANK"]),
        new("PAYTM PAYMENTS BANK", "Paytm Payments Bank", ["PYTM"], ["PAYTM PAYMENTS BANK"]),
        new("FINO PAYMENTS BANK", "Fino Payments Bank", ["FINO"], ["FINO PAYMENTS BANK"]),
        new("JIO PAYMENTS BANK", "Jio Payments Bank", ["JIOP"], ["JIO PAYMENTS BANK"]),
        new("NSDL PAYMENTS BANK", "NSDL Payments Bank", ["NSPB"], ["NSDL PAYMENTS BANK"]),

        // Regional Rural Banks (RRBs)
        new("KARNATAKA GRAMIN BANK", "Karnataka Gramin Bank", ["PKGB"], ["KARNATAKA GRAMIN BANK", "PRAGATHI KRISHNA GRAMIN BANK"]),
        new("KARNATAKA VIKAS GRAMEENA BANK", "Karnataka Vikas Grameena Bank", ["KVGB"], ["KARNATAKA VIKAS GRAMEENA BANK"]),
        new("KERALA GRAMIN BANK", "Kerala Gramin Bank", ["KLGB"], ["KERALA GRAMIN BANK"]),
        new("ANDHRA PRAGATHI GRAMEENA BANK", "Andhra Pragathi Grameena Bank", ["APGB"], ["ANDHRA PRAGATHI GRAMEENA BANK"]),
        new("ANDHRA PRADESH GRAMEENA VIKAS BANK", "Andhra Pradesh Grameena Vikas Bank", ["APGV"], ["ANDHRA PRADESH GRAMEENA VIKAS BANK"]),
        new("BARODA GUJARAT GRAMIN BANK", "Baroda Gujarat Gramin Bank", ["BGGB"], ["BARODA GUJARAT GRAMIN BANK"]),
        new("BARODA U.P. BANK", "Baroda U.P. Bank", ["BUPB"], ["BARODA U.P. BANK", "BARODA UP BANK"]),
        new("BARODA RAJASTHAN KSHETRIYA GRAMIN BANK", "Baroda Rajasthan Kshetriya Gramin Bank", ["BRKG"], ["BARODA RAJASTHAN KSHETRIYA GRAMIN BANK"]),
        new("MAHARASHTRA GRAMIN BANK", "Maharashtra Gramin Bank", ["MAHG"], ["MAHARASHTRA GRAMIN BANK"]),
        new("TAMIL NADU GRAMA BANK", "Tamil Nadu Grama Bank", ["TNGB"], ["TAMIL NADU GRAMA BANK"]),
        new("CHHATTISGARH RAJYA GRAMIN BANK", "Chhattisgarh Rajya Gramin Bank", ["CGBX"], ["CHHATTISGARH RAJYA GRAMIN BANK"]),
        new("JHARKHAND RAJYA GRAMIN BANK", "Jharkhand Rajya Gramin Bank", ["JRGB"], ["JHARKHAND RAJYA GRAMIN BANK"]),
        new("MADHYA PRADESH GRAMIN BANK", "Madhya Pradesh Gramin Bank", ["MPGB"], ["MADHYA PRADESH GRAMIN BANK"]),
        new("RAJASTHAN MARUDHARA GRAMIN BANK", "Rajasthan Marudhara Gramin Bank", ["RMGB"], ["RAJASTHAN MARUDHARA GRAMIN BANK"]),
        new("SAURASHTRA GRAMIN BANK", "Saurashtra Gramin Bank", ["SRGB"], ["SAURASHTRA GRAMIN BANK"]),
        new("TELANGANA GRAMEENA BANK", "Telangana Grameena Bank", ["TGGB"], ["TELANGANA GRAMEENA BANK"]),

        // State Co-operative Apex Banks (shared sponsor prefixes used by affiliated District Central Co-op Banks)
        new("KARNATAKA STATE CO-OPERATIVE APEX BANK", "Karnataka State Co-operative Apex Bank", ["KSCB"], ["KARNATAKA STATE CO-OPERATIVE APEX BANK", "KARNATAKA STATE COOPERATIVE APEX BANK"], IsSharedCoopApex: true),
        new("MAHARASHTRA STATE CO-OPERATIVE BANK", "Maharashtra State Co-operative Bank", ["MSCI"], ["MAHARASHTRA STATE CO-OPERATIVE BANK", "MAHARASHTRA STATE COOPERATIVE BANK"], IsSharedCoopApex: true),
        new("GUJARAT STATE CO-OPERATIVE BANK", "Gujarat State Co-operative Bank", ["GSCB"], ["GUJARAT STATE CO-OPERATIVE BANK", "GUJARAT STATE COOPERATIVE BANK"], IsSharedCoopApex: true),
        new("TAMIL NADU STATE APEX CO-OPERATIVE BANK", "Tamil Nadu State Apex Co-operative Bank", ["TNSC"], ["TAMIL NADU STATE APEX CO-OPERATIVE BANK", "TNSC BANK"], IsSharedCoopApex: true),
        new("ANDHRA PRADESH STATE CO-OPERATIVE BANK", "Andhra Pradesh State Co-operative Bank", ["APBL"], ["ANDHRA PRADESH STATE CO-OPERATIVE BANK", "APCOB"], IsSharedCoopApex: true),
        new("TELANGANA STATE CO-OPERATIVE APEX BANK", "Telangana State Co-operative Apex Bank", ["TSAB"], ["TELANGANA STATE CO-OPERATIVE APEX BANK", "TSCAB"], IsSharedCoopApex: true),
        new("RAJASTHAN STATE CO-OPERATIVE BANK", "Rajasthan State Co-operative Bank", ["RSCB"], ["RAJASTHAN STATE CO-OPERATIVE BANK"], IsSharedCoopApex: true),
        new("WEST BENGAL STATE CO-OPERATIVE BANK", "West Bengal State Co-operative Bank", ["WBSC"], ["WEST BENGAL STATE CO-OPERATIVE BANK"], IsSharedCoopApex: true),
        new("ODISHA STATE CO-OPERATIVE BANK", "Odisha State Co-operative Bank", ["ORCB"], ["ODISHA STATE CO-OPERATIVE BANK"], IsSharedCoopApex: true),
        new("HARCO BANK", "HARCO Bank", ["HARC"], ["HARCO BANK", "HARYANA STATE CO-OPERATIVE APEX BANK"], IsSharedCoopApex: true),
        new("PUNJAB STATE CO-OPERATIVE BANK", "Punjab State Co-operative Bank", ["PSCB"], ["PUNJAB STATE CO-OPERATIVE BANK"], IsSharedCoopApex: true),
        new("HIMACHAL PRADESH STATE CO-OPERATIVE BANK", "Himachal Pradesh State Co-operative Bank", ["HPSC"], ["HIMACHAL PRADESH STATE CO-OPERATIVE BANK"], IsSharedCoopApex: true),
        new("UTTAR PRADESH CO-OPERATIVE BANK", "Uttar Pradesh Co-operative Bank", ["UPCB"], ["UTTAR PRADESH CO-OPERATIVE BANK"], IsSharedCoopApex: true),
        new("MADHYA PRADESH RAJYA SAHAKARI BANK", "Madhya Pradesh Rajya Sahakari Bank", ["MPCB"], ["MADHYA PRADESH RAJYA SAHAKARI BANK"], IsSharedCoopApex: true),

        // Urban / District / Multi-State Co-operative & Sahakari Banks
        new("TUMKUR GRAIN MERCHANTS CO-OPERATIVE BANK", "Tumkur Grain Merchants Co-operative Bank", ["TGMB", "TGMC"], ["TUMKUR GRAIN MERCHANTS CO-OPERATIVE BANK", "TUMKUR GRAIN MERCHANTS CO-OP BANK", "TGMC BANK", "TGM CO-OP BANK"]),
        new("KALUPUR COMMERCIAL CO-OPERATIVE BANK", "Kalupur Commercial Co-operative Bank", ["KCCB"], ["KALUPUR COMMERCIAL CO-OPERATIVE BANK"]),
        new("SVC CO-OPERATIVE BANK", "SVC Co-operative Bank", ["SVCB"], ["SVC CO-OPERATIVE BANK", "SHAMRAO VITHAL CO-OPERATIVE BANK"]),
        new("SARASWAT CO-OPERATIVE BANK", "Saraswat Co-operative Bank", ["SRCB"], ["SARASWAT CO-OPERATIVE BANK", "SARASWAT BANK"]),
        new("COSMOS CO-OPERATIVE BANK", "Cosmos Co-operative Bank", ["COSB"], ["COSMOS CO-OPERATIVE BANK", "COSMOS BANK"]),
        new("ABHYUDAYA CO-OPERATIVE BANK", "Abhyudaya Co-operative Bank", ["ABHY"], ["ABHYUDAYA CO-OPERATIVE BANK"]),
        new("TJSB SAHAKARI BANK", "TJSB Sahakari Bank", ["TJSB"], ["TJSB SAHAKARI BANK"]),
        new("NKGSB CO-OPERATIVE BANK", "NKGSB Co-operative Bank", ["NKGS"], ["NKGSB CO-OPERATIVE BANK"]),
        new("DOMBIVLI NAGARI SAHAKARI BANK", "Dombivli Nagari Sahakari Bank", ["DNSB"], ["DOMBIVLI NAGARI SAHAKARI BANK", "DNS BANK"]),
        new("JANATA SAHAKARI BANK", "Janata Sahakari Bank", ["JSBP"], ["JANATA SAHAKARI BANK"]),
        new("BHARAT CO-OPERATIVE BANK", "Bharat Co-operative Bank", ["BCBM"], ["BHARAT CO-OPERATIVE BANK"]),
        new("SURAT DISTRICT CO-OPERATIVE BANK", "Surat District Co-operative Bank", ["SDCB"], ["SURAT DISTRICT CO-OPERATIVE BANK"]),
        new("SUTEX CO-OPERATIVE BANK", "Sutex Co-operative Bank", ["SUTB"], ["SUTEX CO-OPERATIVE BANK"]),
        new("VASAI VIKAS SAHAKARI BANK", "Vasai Vikas Sahakari Bank", ["VVSB"], ["VASAI VIKAS SAHAKARI BANK"]),
        new("NUTAN NAGARIK SAHAKARI BANK", "Nutan Nagarik Sahakari Bank", ["NNSB"], ["NUTAN NAGARIK SAHAKARI BANK"]),
        new("PRIME CO-OPERATIVE BANK", "Prime Co-operative Bank", ["PMEC"], ["PRIME CO-OPERATIVE BANK"]),
        new("KALYAN JANATA SAHAKARI BANK", "Kalyan Janata Sahakari Bank", ["KJSB"], ["KALYAN JANATA SAHAKARI BANK"]),
        new("AKOLA JANATA COMMERCIAL CO-OPERATIVE BANK", "Akola Janata Commercial Co-operative Bank", ["AKJB"], ["AKOLA JANATA COMMERCIAL CO-OPERATIVE BANK"]),
        new("ZILA SAHAKARI BANK", "Zila Sahakari Bank", ["ZSBL"], ["ZILA SAHAKARI BANK"])
    ];

    private static readonly IReadOnlyDictionary<string, string> PrefixToUpper = BuildPrefixToUpper();
    private static readonly IReadOnlyDictionary<string, string> PrefixToDisplay = BuildPrefixToDisplay();
    private static readonly IReadOnlyDictionary<string, string[]> BankAliases = BuildBankAliases();
    private static readonly HashSet<string> SharedCoopApexPrefixes = Entries
        .Where(entry => entry.IsSharedCoopApex)
        .SelectMany(entry => entry.IfscPrefixes)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ValidIfscRegex = new(@"^[A-Z]{4}0[A-Z0-9]{6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BankNameLabelRegex = new(@"^\s*BANK\s+NAME\s*[:\-]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndsWithBankRegex = new(@"\bBANK(?:\s+(?:LTD\.?|LIMITED|NIYAMIT))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoopBankMarkerRegex = new(
        @"(?:\bCO[\s\-]?OP(?:ERATIVE)?\b|\bSAHAKARI\b|\bDCC\b|\bCDCC\b|\bGRAMIN[A]?\b|\bSOUHARDA\b|\bDISTRICT\s+CENTRAL\b|\bNAGARIK\b|\bURBAN\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingHonorificRegex = new(
        @"^(?:M\s*/\s*S\.?|MS\.?|MR\.?|MRS\.?|MISS|SMT\.?)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RejectedHeaderWordRegex = new(
        @"(?:\bSTATEMENT\b|\bACCOUNT\b|\bCUSTOMER\b|\bBRANCH\b|\bADDRESS\b|\bNEAR\b|\bOPP\.?\b|\bOPPOSITE\b|\bBESIDE\b|\bROAD\b|\bSTREET\b|\bPOST\s+OFFICE\b|\bBUS\s+STAND\b|\bIFSC\b|\bMICR\b|\bBALANCE\b|\bDEPOSIT\b|\bWITHDRAWAL\b|\bCHEQUE\b|\bNOMINEE\b|\bCURRENCY\b|\bEMAIL\b|\bPHONE\b|\bMOBILE\b|\bHELPLINE\b|\bTOLL[\s\-]?FREE\b|\bGST(?:IN)?\b|\bPAN\b|\bLIMIT\b|\bDRAWING\b|\bPRODUCT\b|\bBANKING\b|\bCHARGES?\b|\bHOLDER\b|\bJOINT\b|\bPAGE\b|\bDATE\b|\bTIME\b|\bPERIOD\b|\bSUMMARY\b|\bINTEREST\b|\bCLEARED\b|\bUNCLEARED\b|\bBROUGHT\b|\bCARRIED\b|\bTRANSFERS?\b|\bTRANSACTIONS?\b|\bDETAILS\b|\bPARTICULARS\b|\bNARRATION\b|\bDEBITS?\b|\bCREDITS?\b|\bSAVINGS?\b|\bCURRENT\b|\bOVERDRAFT\b|WWW\.|HTTP|@)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> UpperAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "HDFC", "ICICI", "IDFC", "IDBI", "SBI", "HSBC", "RBL", "DBS", "CSB", "DCB",
        "UCO", "AU", "ESAF", "SVCB", "SVC", "TJSB", "NKGSB", "HARCO", "APCOB", "TSCAB",
        "TNSC", "DCC", "CDCC", "TGMC", "TGM", "NSDL", "U.P.", "UP"
    };

    private static IReadOnlyDictionary<string, string> BuildPrefixToUpper()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            foreach (var prefix in entry.IfscPrefixes)
                map[prefix] = entry.CanonicalUpper;
        return map;
    }

    private static IReadOnlyDictionary<string, string> BuildPrefixToDisplay()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            foreach (var prefix in entry.IfscPrefixes)
                map[prefix] = entry.DisplayName;
        return map;
    }

    private static IReadOnlyDictionary<string, string[]> BuildBankAliases()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            map[entry.CanonicalUpper] = entry.Aliases;
        return map;
    }

    public static bool IsSharedCoopApexPrefix(string? prefixOrIfsc)
    {
        var compact = NormalizeAlphaPrefix(prefixOrIfsc);
        return compact.Length == 4 && SharedCoopApexPrefixes.Contains(compact);
    }

    public static string ResolveFromIfscPrefix(string? prefixOrIfsc, bool allowGenericFallback = false)
    {
        var prefix = NormalizeAlphaPrefix(prefixOrIfsc);
        if (prefix.Length != 4) return "Unknown Bank";
        if (PrefixToDisplay.TryGetValue(prefix, out var display)) return display;
        return allowGenericFallback ? $"{prefix} Bank" : "Unknown Bank";
    }

    public static BankIdentityResolution ResolveBankIdentity(string? headerText, IReadOnlyList<string>? ifscs)
    {
        var safeHeader = headerText ?? string.Empty;
        var validIfscs = (ifscs ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim().ToUpperInvariant())
            .Where(value => ValidIfscRegex.IsMatch(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ifscMatchedBanks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliasMatchedBanks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<string>();

        foreach (var ifsc in validIfscs)
        {
            var prefix = ifsc[..4];
            if (!PrefixToUpper.TryGetValue(prefix, out var bank)) continue;
            scores[bank] = scores.TryGetValue(bank, out var score) ? score + 10 : 10;
            ifscMatchedBanks[bank] = prefix;
            evidence.Add($"IFSC {ifsc} -> {bank}");
        }

        foreach (var match in FindAliasMatches(safeHeader))
        {
            scores[match.Bank] = scores.TryGetValue(match.Bank, out var score) ? score + 5 : 5;
            aliasMatchedBanks.Add(match.Bank);
            evidence.Add($"Header text: {match.Alias}");
        }

        var printedBank = ExtractSourcePrintedBankName(safeHeader);
        if (scores.Count > 0)
        {
            var ranked = scores
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var topBank = ranked[0].Key;
            var tied = ranked.Count > 1 && ranked[0].Value == ranked[1].Value;

            if (!tied &&
                ifscMatchedBanks.TryGetValue(topBank, out var matchedPrefix) &&
                SharedCoopApexPrefixes.Contains(matchedPrefix) &&
                !aliasMatchedBanks.Contains(topBank))
            {
                if (printedBank.Length > 0)
                {
                    evidence.Add($"Header text: {printedBank}");
                    return new BankIdentityResolution
                    {
                        BankName = printedBank,
                        BankStatus = "VERIFIED",
                        Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        IsSharedApexIfscFallback = false
                    };
                }

                return new BankIdentityResolution
                {
                    BankName = topBank,
                    BankStatus = "VERIFIED",
                    Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    IsSharedApexIfscFallback = true
                };
            }

            var status = tied
                ? "REVIEW_REQUIRED"
                : ranked[0].Value >= 10 ? "VERIFIED" : "DETECTED";
            return new BankIdentityResolution
            {
                BankName = topBank,
                BankStatus = status,
                Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IsSharedApexIfscFallback = false
            };
        }

        if (validIfscs.Count > 0)
        {
            var primaryIfsc = validIfscs[0];
            var prefix = primaryIfsc[..4];
            if (printedBank.Length > 0)
            {
                evidence.Add($"IFSC {primaryIfsc}");
                evidence.Add($"Header text: {printedBank}");
                return new BankIdentityResolution
                {
                    BankName = printedBank,
                    BankStatus = "VERIFIED",
                    Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    IsSharedApexIfscFallback = false
                };
            }

            var genericBank = $"{prefix} BANK";
            evidence.Add($"IFSC {primaryIfsc} -> {genericBank}");
            return new BankIdentityResolution
            {
                BankName = genericBank,
                BankStatus = "VERIFIED",
                Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IsSharedApexIfscFallback = false
            };
        }

        if (printedBank.Length > 0 && CoopBankMarkerRegex.IsMatch(printedBank))
        {
            evidence.Add($"Header text: {printedBank}");
            return new BankIdentityResolution
            {
                BankName = printedBank,
                BankStatus = "DETECTED",
                Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IsSharedApexIfscFallback = false
            };
        }

        return new BankIdentityResolution
        {
            BankName = string.Empty,
            BankStatus = "NOT_DETECTED",
            Evidence = Array.Empty<string>(),
            IsSharedApexIfscFallback = false
        };
    }

    /// <summary>
    /// Extracts an explicit printed bank name from non-table statement header lines while rejecting
    /// addresses, branch/account labels, and generic statement headings.
    /// </summary>
    public static string ExtractSourcePrintedBankName(string? headerText)
    {
        if (string.IsNullOrWhiteSpace(headerText)) return string.Empty;

        var lines = headerText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var cleaned = CleanCandidateLine(rawLine);
            if (cleaned.Length == 0) continue;

            var labeled = BankNameLabelRegex.Match(cleaned);
            if (labeled.Success)
            {
                var candidate = NormalizeCandidateValue(labeled.Groups[1].Value);
                if (IsAcceptablePrintedBankLine(candidate, requireTwoWords: false))
                    return candidate.ToUpperInvariant();
            }
        }

        foreach (var rawLine in lines)
        {
            var cleaned = CleanCandidateLine(rawLine);
            if (cleaned.Length == 0 || cleaned.Contains(':', StringComparison.Ordinal)) continue;
            var candidate = NormalizeCandidateValue(cleaned);
            if (!IsAcceptablePrintedBankLine(candidate, requireTwoWords: true)) continue;

            var hasBankSuffix = EndsWithBankRegex.IsMatch(candidate);
            var hasCoopBank = CoopBankMarkerRegex.IsMatch(candidate) &&
                Regex.IsMatch(candidate, @"\bBANK\b", RegexOptions.IgnoreCase);
            if (hasBankSuffix || hasCoopBank)
                return candidate.ToUpperInvariant();
        }

        return string.Empty;
    }

    public static string ToReadableBankName(string? bankName)
    {
        var raw = NormalizeSpaces(bankName);
        if (raw.Length == 0) return string.Empty;
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (UpperAcronyms.Contains(token))
            {
                parts[i] = token.ToUpperInvariant();
                continue;
            }
            if (token.Equals("OF", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = i == 0 ? Capitalize(token) : token.ToLowerInvariant();
                continue;
            }
            if (token.Contains('-', StringComparison.Ordinal))
            {
                parts[i] = string.Join("-", token.Split('-').Select(Capitalize));
                continue;
            }
            parts[i] = Capitalize(token);
        }
        return string.Join(" ", parts);
    }

    private static string CleanCandidateLine(string line)
    {
        var trimmed = Regex.Replace((line ?? string.Empty).Trim(), @"^#+\s*", string.Empty);
        return NormalizeSpaces(trimmed);
    }

    private static string NormalizeCandidateValue(string value)
    {
        var trimmed = NormalizeSpaces(value).Trim('.', ',', '-', '*', ' ');
        return trimmed;
    }

    private static bool IsAcceptablePrintedBankLine(string candidate, bool requireTwoWords)
    {
        if (candidate.Length is < 5 or > 85) return false;
        if (candidate.Any(char.IsDigit)) return false;
        if (LeadingHonorificRegex.IsMatch(candidate)) return false;
        if (RejectedHeaderWordRegex.IsMatch(candidate)) return false;
        var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requireTwoWords && tokens.Length < 2) return false;
        return Regex.IsMatch(candidate, @"\bBANK\b", RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<AliasMatch> FindAliasMatches(string text)
    {
        var upper = NormalizeSpaces(text).ToUpperInvariant();
        var candidates = new List<AliasMatch>();
        foreach (var pair in BankAliases)
        {
            foreach (var alias in pair.Value)
            {
                var escaped = Regex.Escape(NormalizeSpaces(alias).ToUpperInvariant()).Replace("\\ ", @"\s+");
                var regex = new Regex($@"(?<![A-Z0-9]){escaped}(?![A-Z0-9])", RegexOptions.IgnoreCase);
                foreach (Match match in regex.Matches(upper))
                    candidates.Add(new AliasMatch(match.Index, match.Index + match.Length, pair.Key, alias));
            }
        }

        var selected = new List<AliasMatch>();
        foreach (var candidate in candidates
            .OrderByDescending(item => item.End - item.Start)
            .ThenBy(item => item.Start)
            .ThenBy(item => item.Bank, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Any(item => !(candidate.End <= item.Start || candidate.Start >= item.End)))
                continue;
            selected.Add(candidate);
        }
        return selected.OrderBy(item => item.Start).ToList();
    }

    private static string NormalizeAlphaPrefix(string? value)
    {
        var compact = new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (compact.Length < 4) return string.Empty;
        var prefix = compact[..4];
        return prefix.All(char.IsLetter) ? prefix : string.Empty;
    }

    private static string NormalizeSpaces(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private static string Capitalize(string token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        if (token.Length == 1) return token.ToUpperInvariant();
        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }
}
