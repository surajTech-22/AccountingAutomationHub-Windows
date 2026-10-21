import com.accountingautomationhub.android.InvoiceRecognitionEngine

fun main() {
    // R28 regression: real NANDI shape with one damaged item taxable cell and fragmented
    // accounting-summary header. Bottom GST slab arithmetic must remain authoritative.
    val raw = """NANDI MARKETING
Near KN Rajanna Ring Road
PAN NO : AAQFM2308H
GSTN NO : 29AAQEM2308H1Z3
BALAJI TRADING CO. 9731329698 D
PAN NO : ABZPB5530G
GSTN NO : 29ABZPB5530G1ZE
Bill NO
: E53528
Bill Date :31/08/2026 14:28:29
BS No ST Product Name Total Unit PKM Base Rate Sch Disc RS Disc Taxable GST CGST S/UTGST Sch/Pcs Net Amt
1 34011190 LB NEEM & ALOE SOAP 125G 126 40.00 173 0726 35.27 305.11 57.97 5739.14 5 143.48 143.48 1.85 6026.11
2 34011190 LB NEEM & ALOE SOAP 125G 126 40.00 79 0826 35.27 139.33 26.47 2620.76 5 65.52 65.52 1.85 2751.80
3 34011190 LB NEEM & ALOE SOAP 40G L 216 10.00 216 0826 8.74 9.44 18.78 1858.98 5 46.47 46.47 0.05 1951.92
4 34011190 LUX ADV EVEN-TONED GLOW 39G 216 10.00 216 0726 8.74 9.44 18.78 1858.98 5 46.47 46.47 0.05 1951.92
5 34025000 SURFXL EW 70G 6T 144 10.00 720 0826 7.57 27.24 54.21 5366.79 18 483.01 483.01 0.04 6332.81
6 34025000 SURFXL MIC LIQ FL 60G (40% EXTRA) 120 10.00 360 0826 7.85 423.72 24.01 2377.19 18 213.95 213.95 2.39 2805.08
7 34054000 VIM BAR TUB 480G WITH NKED SCRUB PD 24 55.00 24 0826 41.43 4.97 9.89 48 18 88.15 88.15 0.24 1155.79
8 34025000 VIM DRP DISHWASH ACTIVE GEL YELLOW 500ML 24 140.00 6 0826 108.73 0.00 6.52 645.84 18 58.13 58.13 0.00 762.10
9 34025000 VIM DRP DW LMN ACT GEL 115ML CP 36 15.00 36 0826 11.65 0.00 4.19 415.10 18 37.36 37.36 0.00 489.82
10 34025000 VIM RS 10 DW LIQUID 85ML 40 10.00 3 0826 7.85 0.00 0.24 23.31 18 2.10 2.10 0.00 27.50
Total 1833 919.25 221.07 21885.57 1184.64 1184.64 5.47 24254.85
Particulars
Pcs Gross Amt Sch Amt RS Disc
Taxable Amt Tax Amt Debit Adj Amt Credit Adj/Payout Amt TCS Net Payable Amt
CGST 2.5 + SGST 2.5 684 12663.18 463.32 122.00 12077.86 603.89
CGST 9 SG5T 9 1149 10362.70 455.93 99.07 9807.71 1765.39
Total 1833 23025.89 919.25 221.07 21885.57 2369.28 0.00 0.00 24255.00
""".trimIndent()
    val client = InvoiceRecognitionEngine.ClientIdentity("BALAJI TRADING CO", "29ABZPB5530G1ZE")
    val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.ACCOUNTING, raw, emptyList())
    check(r.direction == InvoiceRecognitionEngine.PURCHASE) { "direction ${r.direction}" }
    check(r.seller.name.contains("NANDI", true)) { "seller ${r.seller}" }
    check(r.seller.pan == "AAQFM2308H") { "seller PAN ${r.seller.pan}" }
    check(r.seller.gstin == "29AAQFM2308H1Z3") { "seller GSTIN ${r.seller.gstin}" }
    check(r.buyer.pan == "ABZPB5530G") { "buyer PAN ${r.buyer.pan}" }
    check(r.invoiceNumber == "E53528") { "invoice ${r.invoiceNumber}" }
    check(r.gstSlabRates.toSet() == setOf("5", "18")) { "slabs ${r.gstSlabRates}" }
    check(r.taxableTotal == "21885.57") { "taxable ${r.taxableTotal}" }
    check(r.cgstTotal == "1184.64") { "cgst ${r.cgstTotal}" }
    check(r.sgstTotal == "1184.64") { "sgst ${r.sgstTotal}" }
    check(r.invoiceTotal == "24255.00") { "total ${r.invoiceTotal}" }
    check(r.roundOff == "0.15") { "round ${r.roundOff}" }
    println("ALL R46 R28 LAYOUT/PAN/SLAB-AUTHORITY REGRESSIONS PASSED")
}
