import com.accountingautomationhub.android.InvoiceRecognitionEngine

private fun wordLine(y: Int, vararg cells: Pair<Int, String>): InvoiceRecognitionEngine.OcrLine {
    val words = mutableListOf<InvoiceRecognitionEngine.OcrWord>()
    cells.forEach { (x0, cell) ->
        var x = x0
        cell.split(Regex("\\s+")).filter { it.isNotBlank() }.forEach { token ->
            val w = (token.length * 9).coerceAtLeast(18)
            words += InvoiceRecognitionEngine.OcrWord(0, token, x, y, x + w, y + 24)
            x += w + 6
        }
    }
    return InvoiceRecognitionEngine.OcrLine(0, cells.joinToString(" ") { it.second }, words.minOfOrNull { it.left } ?: 0, y, words.maxOfOrNull { it.right } ?: 100, y + 24, words)
}
private fun textLine(y: Int, text: String, x: Int = 80) = wordLine(y, x to text)
private fun money(value: String) = value.toBigDecimalOrNull() ?: error("not numeric: $value")

fun main() {
    val client = InvoiceRecognitionEngine.ClientIdentity("Balaji Trading Co", "29ABZPB5530G1ZE")

    // Real Pandian pattern: a stale saved Sales override must lose to exact Client GSTIN on Buyer.
    // Round Off label/value are separate OCR boxes and must preserve the signed value.
    run {
        val raw = """
S.S. PANDIAN & SONS
GSTIN 29AATFS8172J1Z7
Balaji Trading Co Tumkur
GSTIN 29ABZPB5530G1ZE
TAX INVOICE
Invoice No BM26-6421
Date 08-Sep-2026
Item Name Quantity Rate Amount
Maazter 200g 2 Jar 285.72 571.44
Crystal 10g 20 1 box 3565.71 3565.71
Subtotal 4137.15
CGST @ 2.50% 103.43
SGST @ 2.50% 103.43
Round Off
-0.01
Grand Total 4344.00
""".trimIndent()
        val lines = listOf(
            textLine(20, "S.S. PANDIAN & SONS"), textLine(50, "GSTIN 29AATFS8172J1Z7"),
            textLine(90, "Balaji Trading Co Tumkur"), textLine(120, "GSTIN 29ABZPB5530G1ZE"),
            textLine(160, "TAX INVOICE"), wordLine(190, 700 to "Invoice No", 980 to "BM26-6421"),
            wordLine(220, 700 to "Date", 980 to "08-Sep-2026"),
            wordLine(270, 100 to "Item Name", 650 to "Quantity", 900 to "Rate", 1120 to "Amount"),
            wordLine(310, 100 to "Maazter 200g", 650 to "2 Jar", 900 to "285.72", 1120 to "571.44"),
            wordLine(345, 100 to "Crystal 10g 20", 650 to "1 box", 900 to "3565.71", 1120 to "3565.71"),
            wordLine(400, 850 to "Subtotal", 1120 to "4137.15"), wordLine(430, 850 to "CGST @ 2.50%", 1120 to "103.43"),
            wordLine(460, 850 to "SGST @ 2.50%", 1120 to "103.43"),
            wordLine(490, 850 to "Round Off"), wordLine(490, 1120 to "-0.01"),
            wordLine(520, 850 to "Grand Total", 1120 to "4344.00")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.SALES, InvoiceRecognitionEngine.ACCOUNTING, raw, lines)
        check(r.direction == InvoiceRecognitionEngine.PURCHASE) { "exact GSTIN must override stale Sales: ${r.directionEvidence}" }
        check(r.counterparty.name.contains("PANDIAN", true)) { "counterparty: ${r.counterparty}" }
        check(r.roundOff == "-0.01") { "round off split-box failed: ${r.roundOff}" }
        check(r.rows.all { it.taxable.isBlank() || it.gstRate.isNotBlank() }) { "GST % missing on taxable row: ${r.rows}" }
    }

    // Real Nandi pattern: table bounds must prevent page/phone/Ack/legal/footer text becoming rows.
    // Mixed GST 5/18 and calculated +0.15 Round Off must survive.
    run {
        val raw = """
Page 1 of 1
NANDI MARKETING
GSTIN NO 29AAQFM2308H1Z3
BALAJI TRADING CO. 9731329698 D
GST NO 29ABZPB5530G1ZE
Bill No E53528
Bill Date 31/08/2026
HSN No Product Name UPC MRP CS Pcs Total Unit PKM Base Rate Sch Disc RS Disc Taxable Amt GST % CGST Amt S/UTGST Amt Net Amt
34011190 PRODUCT A 126 40.00 1 0 684 0726 20.00 463.32 122.00 12077.86 5 301.95 301.94 12681.75
34011900 PRODUCT B 126 40.00 0 0 1149 0826 30.00 455.93 99.07 9807.71 18 882.69 882.70 11573.10
Total Taxable Amt 21885.57
CGST 2.5 + SGST 2.5 603.89
CGST 9 + SGST 9 1765.39
Net Payable Amt 24255.00
Ack No 112632209014711
Secondary Levercare No 18001232105 MON-SAT 10:00 AM to 7:00 PM
This is a computer generated or electronic invoice as per IT Act 2000
Notification No. 74/2018-Central tax dated 31st December 2018
""".trimIndent()
        val lines = listOf(
            textLine(10, "Page 1 of 1"), textLine(35, "NANDI MARKETING"), textLine(60, "GSTIN NO 29AAQFM2308H1Z3"),
            textLine(90, "BALAJI TRADING CO. 9731329698 D"), textLine(115, "GST NO 29ABZPB5530G1ZE"),
            wordLine(145, 750 to "Bill No", 950 to "E53528"), wordLine(170, 750 to "Bill Date", 950 to "31/08/2026"),
            wordLine(220, 40 to "HSN No", 180 to "Product Name", 520 to "UPC", 600 to "MRP", 680 to "CS", 740 to "Pcs", 800 to "Total Unit", 930 to "PKM", 1010 to "Base Rate", 1130 to "Sch Disc", 1240 to "RS Disc", 1360 to "Taxable Amt", 1500 to "GST %", 1580 to "CGST Amt", 1690 to "S/UTGST Amt", 1880 to "Net Amt"),
            wordLine(260, 40 to "34011190", 180 to "PRODUCT A", 520 to "126", 600 to "40.00", 680 to "1", 740 to "0", 800 to "684", 930 to "0726", 1010 to "20.00", 1130 to "463.32", 1240 to "122.00", 1360 to "12077.86", 1500 to "5%", 1580 to "301.95", 1690 to "301.94", 1880 to "12681.75"),
            wordLine(295, 40 to "34011900", 180 to "PRODUCT B", 520 to "126", 600 to "40.00", 680 to "0", 740 to "0", 800 to "1149", 930 to "0826", 1010 to "30.00", 1130 to "455.93", 1240 to "99.07", 1360 to "9807.71", 1500 to "18%", 1580 to "882.69", 1690 to "882.70", 1880 to "11573.10"),
            wordLine(350, 1100 to "Total Taxable Amt", 1500 to "21885.57"),
            wordLine(380, 1100 to "CGST 2.5 + SGST 2.5", 1500 to "603.89"), wordLine(410, 1100 to "CGST 9 + SGST 9", 1500 to "1765.39"),
            wordLine(440, 1100 to "Net Payable Amt", 1500 to "24255.00"),
            textLine(500, "Ack No 112632209014711"), textLine(530, "Secondary Levercare No 18001232105 MON-SAT 10:00 AM to 7:00 PM"),
            textLine(560, "This is a computer generated or electronic invoice as per IT Act 2000"), textLine(590, "Notification No. 74/2018-Central tax dated 31st December 2018")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.SALES, InvoiceRecognitionEngine.INVENTORY, raw, lines)
        check(r.direction == InvoiceRecognitionEngine.PURCHASE) { "Nandi direction: ${r.directionEvidence}" }
        check(r.counterparty.name.contains("NANDI", true)) { "Nandi counterparty ${r.counterparty}" }
        check(r.rows.size == 2) { "false rows leaked: ${r.rows.map { it.description }}" }
        check(r.rows.map { it.gstRate }.toSet() == setOf("5", "18")) { "mixed GST lost: ${r.rows.map { it.gstRate }}" }
        check(money(r.discountTotal).compareTo("1140.32".toBigDecimal()) == 0) { "discount ${r.discountTotal}" }
        check(money(r.taxableTotal).compareTo("21885.57".toBigDecimal()) == 0) { "taxable ${r.taxableTotal}" }
        check(money(r.cgstTotal).compareTo("1184.64".toBigDecimal()) == 0) { "CGST ${r.cgstTotal}" }
        check(money(r.sgstTotal).compareTo("1184.64".toBigDecimal()) == 0) { "SGST ${r.sgstTotal}" }
        check(money(r.invoiceTotal).compareTo("24255.00".toBigDecimal()) == 0) { "total ${r.invoiceTotal}" }
        check(r.roundOff == "0.15") { "calculated round off ${r.roundOff}" }
        val errors = InvoiceRecognitionEngine.validate(r)
        check(errors.isEmpty()) { "R23 validation: $errors" }
    }

    // Whole-document fallback must not manufacture item rows from legal/header noise.
    run {
        val raw = """Page 1 of 1\nBALAJI TRADING CO 9731329698\nAck No 112632209014711\nNotification No 74/2018-Central tax dated 31st December 2018"""
        val lines = raw.lines().mapIndexed { i, t -> textLine(20 + i * 30, t) }
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.ACCOUNTING, raw, lines)
        check(r.rows.isEmpty()) { "noise became rows: ${r.rows}" }
    }

    println("ALL R46 R23 INVOICE REGRESSIONS PASSED")
}
