using AccountingAutomationHub.Infrastructure.Banking;

internal static class LedgerMappingNoiseTests
{
    public static void Run()
    {
        // Exact historical narration shapes from CL019's Canara Bank review.
        // A source-derived party is a suggestion, NEVER an automatically
        // approved Master or accounting posting.
        var cases = new (string Narration, string Type, string Expected)[]
        {
            ("UPI-VEENA SB-9900256154@IBL-SBIN0020207-400472477359-PAYMENT FROM PHONE",
                "Receipt", "VEENA SB"),
            ("UPI-SIDDAPPA LAKSHATTI-7259987923@YBL-SBIN0016281-822542652426-PAYMENT FROM PHONE",
                "Receipt", "SIDDAPPA LAKSHATTI"),
            ("UPI-SOLANKI SURAJ-7984719172.ETB@ICICI-ICIC0002814-518597961166-MITANSH TEC-UPI PA",
                "Receipt", "SOLANKI SURAJ"),
            ("UPI-DELHIVERY LIMITED-PAYTM-DELHIVERY123@PTYBL-YESB0PTMUPI-113842858667-UPI",
                "Payment", "DELHIVERY"),
            ("UPI-AMAZON ADS-AMAZONADS@YAPL-YESB0APLUPI-602607018008-YOU ARE RECHARGING",
                "Payment", "AMAZON ADS"),
            ("UPI-M VARALAXMI-9986160027-4@YBL-SBIN0020205-942377452824-PAYMENT FROM PHONE",
                "Receipt", "M VARALAXMI"),
            ("IMPS-515112396135-KREATIVE AND CREATIVE-DCBL-XXXXXXXXXX3919-KREATIVE",
                "Payment", "KREATIVE AND CREATIVE"),
            ("IMPS-525716396537-TAASA INDUSTRIES-SBIN-XXXXXXXXXXXXX5944-TAASA",
                "Payment", "TAASA INDUSTRIES"),
            ("IMPS-602114977749-RIYA BIKES-SBIN-XXXXXXX0671-RIYA BIKES",
                "Payment", "RIYA BIKES"),
            ("IMPS-532923426825-PERFIOS SOFTWARE SOL-IDFB-XXXXXXX9385-BANKACCOUNTVALIDATIONAMAZONSELLERSERVICES",
                "Receipt", "PERFIOS SOFTWARE SOL"),
            ("NEFT DR-SBIN0020205-SANTHOSH KUMAR S-NETBANK, MUM-HDFCN52025081617215293-CA SANTOSHKUMAR",
                "Payment", "SANTHOSH KUMAR S"),
            ("NEFT DR-PUNB0190220-GREENOVA PLAST PRIVATE LIMITED-NETBANK, MUM-HDFCN52025082226603811-GREENOVA",
                "Payment", "GREENOVA PLAST"),
            ("NEFT CR-DEUT0797BGL-INTERNAL AC FOR INTERMEDIERY- AMAZON SELLER SERVI-MITANSH TECHNOLOGY INDIA PRIVATE LI-DEUTH026021A10",
                "Receipt", "AMAZON SELLER SERVI"),
            ("NEFT CR-UTIB0001506-RAZORPAY SOFTWARE PRIVATE LIMITED - NODAL ACCOUNT-MITANSH TECHNOLOGY INDIA PRIVATE LIMITED-AXISCN118",
                "Receipt", "RAZORPAY SOFTWARE"),
            ("RTGS DR-UTIB0001658-KRISHNA LAM ENTERPRISES-KARATAGI KOP-HDFCR52025052875396929",
                "Payment", "KRISHNA LAM ENTERPRISES"),
            ("RTGS CR-UBIN0537535-BHIMAPPA MAVEER GONGADI-MITANSH TECHNOLOGY INDIA PVT LTD-UBINR22025071501243536",
                "Receipt", "BHIMAPPA MAVEER GONGADI"),
            ("UPI-SUPRGENIUS-SUPRGENIUS165750.RZP@AXISBANK-UTIB0000100-457352012765-1 YEAR PLAN",
                "Payment", "SUPRGENIUS"),
            ("UPI-M PAVAN KUMAR-8884164410@SUPERYES-SBIN0003661-603399399765-PAID VIA SUPERMONE",
                "Receipt", "M PAVAN KUMAR"),
            ("UPI-MAHIPALSINGH-7878313237@UPI-INDB0000868-165151940888-NO REMARKS",
                "Payment", "MAHIPALSINGH"),
            ("CHQ DEP - CTS CLG1 - GANGAVATHI: PENAJA :CANARA BANK",
                "Receipt", "PENAJA"),
            ("CHQ DEP - CTS CLG1 - GANGAVATHI: SHAILAJA :CANARA BANK",
                "Receipt", "SHAILAJA"),
            ("99920258882025-TPT-HDFC8F9DD35050C3-GROSS E BIZ PRIVATE LIMITED",
                "Receipt", "GROSS E BIZ"),
            ("HGAJP0F5360587114619-TORRENTPOWE-BILLPAY-50200026202917",
                "Payment", "TORRENTPOWE"),
        };

        foreach (var (narration, type, expected) in cases)
        {
            var actual = ExcelLedgerMappingParser.BuildNormalizedSummary(narration, type);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"CL019 Ledger Mapping noise: expected [{expected}], found [{actual}] for [{narration}].");
        }

        // Protected system categories and original non-structured descriptions
        // continue through the existing parser; never guess parties out of them.
        foreach (var (narration, category) in new[]
        {
            ("CASH DEPOSIT BY - NATWAR - KARATAGI KOPPAL DISTRICT", "CASH DEPOSIT"),
            ("UPI RETURN", "UPI RETURN")
        })
        {
            var parsed = ExcelLedgerMappingParser.BuildNormalizedSummary(narration, "Receipt");
            if (!parsed.Equals(category, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Protected bank category changed: expected {category}, found {parsed}.");
        }

        Console.WriteLine($"PASS CL019: {cases.Length} structured electronic party parsing regressions + protected categories.");
    }
}
