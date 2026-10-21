import com.accountingautomationhub.android.InvoiceRecognitionEngine

private fun rowLine(page: Int, y: Int, vararg cells: Pair<Int,String>): InvoiceRecognitionEngine.OcrLine {
    val words = mutableListOf<InvoiceRecognitionEngine.OcrWord>()
    val texts = mutableListOf<String>()
    for ((x, cell) in cells) {
        var xx = x
        cell.split(Regex("\\s+")).filter { it.isNotBlank() }.forEach { token ->
            val w = (token.length * 9).coerceAtLeast(18)
            words += InvoiceRecognitionEngine.OcrWord(page, token, xx, y, xx + w, y + 24)
            xx += w + 6
            texts += token
        }
    }
    return InvoiceRecognitionEngine.OcrLine(page, texts.joinToString(" "), words.minOfOrNull{it.left}?:0, y, words.maxOfOrNull{it.right}?:100, y+24, words)
}

private fun simpleLine(page:Int, y:Int, text:String, x:Int=80) = rowLine(page,y,x to text)

private fun requireEq(actual:String, expected:String, label:String) {
    if (actual != expected) error("$label expected '$expected' but got '$actual'")
}
private fun requireContains(actual:String, expected:String, label:String) {
    if (!actual.contains(expected, ignoreCase=true)) error("$label expected to contain '$expected' but got '$actual'")
}
private fun requireMoneyEq(actual:String, expected:String, label:String) {
    val a = actual.toBigDecimalOrNull() ?: error("$label expected numeric '$expected' but got '$actual'")
    val e = expected.toBigDecimal()
    if (a.compareTo(e) != 0) error("$label expected '$expected' but got '$actual'")
}

fun main() {
    val client = InvoiceRecognitionEngine.ClientIdentity("Balaji Trading Co", "29ABZPB5530G1ZE")

    run {
        val raw = """
S.S. PANDIAN & SONS
#148, 3rd Main Road, Industrial Town, Bengaluru 560010
GSTIN: 29AATFS8172J1Z7
To.
Balaji Trading Co Tumkur
APMC Yard 1st Main, Tumakuru
GSTIN: 29ABZPB5530G1ZE
TAX INVOICE
Date 08-Sep-2026
Invoice No BM26-6421
Item Name Quantity Rate Amount
Compounded Asafoetida 2 Jar 285.72 571.44
Maazter 200g 1 Box 3565.71 3565.71
Subtotal 4137.15
CGST @ 2.50% 103.43
SGST @ 2.50% 103.43
Round Off -0.01
Grand Total 4344.00
""".trimIndent()
        val lines = listOf(
            simpleLine(0,20,"S.S. PANDIAN & SONS"), simpleLine(0,50,"#148, 3rd Main Road, Industrial Town, Bengaluru 560010"), simpleLine(0,80,"GSTIN: 29AATFS8172J1Z7"),
            simpleLine(0,120,"To."), simpleLine(0,150,"Balaji Trading Co Tumkur"), simpleLine(0,180,"APMC Yard 1st Main, Tumakuru"), simpleLine(0,210,"GSTIN: 29ABZPB5530G1ZE"),
            simpleLine(0,250,"TAX INVOICE"), rowLine(0,280,500 to "Date",700 to "08-Sep-2026"), rowLine(0,310,500 to "Invoice No",700 to "BM26-6421"),
            rowLine(0,360,100 to "Item Name",650 to "Quantity",900 to "Rate",1120 to "Amount"),
            rowLine(0,400,100 to "Compounded Asafoetida",650 to "2 Jar",900 to "285.72",1120 to "571.44"),
            rowLine(0,435,100 to "Maazter 200g",650 to "1 Box",900 to "3565.71",1120 to "3565.71"),
            rowLine(0,500,850 to "Subtotal",1120 to "4137.15"), rowLine(0,530,850 to "CGST @ 2.50%",1120 to "103.43"),
            rowLine(0,560,850 to "SGST @ 2.50%",1120 to "103.43"), rowLine(0,590,850 to "Round Off",1120 to "-0.01"), rowLine(0,620,850 to "Grand Total",1120 to "4344.00")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.AUTO_MODE, raw, lines)
        requireEq(r.invoiceNumber,"BM26-6421","SSP invoice no")
        requireEq(r.invoiceDate,"08-09-2026","SSP date")
        requireContains(r.seller.name,"PANDIAN","SSP seller")
        requireContains(r.buyer.name,"Balaji Trading","SSP buyer")
        requireEq(r.direction,InvoiceRecognitionEngine.PURCHASE,"SSP direction")
        requireEq(r.taxableTotal,"4137.15","SSP taxable")
        requireEq(r.cgstTotal,"103.43","SSP CGST")
        requireEq(r.sgstTotal,"103.43","SSP SGST")
        requireEq(r.invoiceTotal,"4344.00","SSP total")
        if (r.rows.size < 2) error("SSP rows expected >=2 got ${r.rows.size}")
        requireEq(r.rows[0].gstRate,"5","SSP combined GST rate")
        println("PASS SSP: ${r.seller.name} -> ${r.buyer.name}, ${r.invoiceDate}, ${r.invoiceTotal}, rows=${r.rows.size}")
    }

    run {
        val raw = """
S A ENTERPRISES
3RD CROSS, APMC YARD, BATWADI, TUMKUR - 572103
GSTIN #:29AWKPN8131H1Z5
TAX INVOICE
Invoice No. S264757 Date 02/09/2026
Details of Receiver / Billed To.
BALAJI TRADING BT
APMC YARD TUMKUR
GSTIN #:29ABZPB5530G1ZE
Products HSNCode MRP Case Pcs Rate Scheme Taxable CGST SGST Net Amt.
LEVISTA 168 PCS 2RS 21011120 336.00 1 0 248.57 5965.71 2.50% 149.14 2.50% 149.14 6263.99
SW CITRAN 1KG 20019000 110.00 0 3 85.71 257.14 2.50% 6.43 2.50% 6.43 270.00
PRAN POTATO BISCATE 10/- 19059200 10.00 0 48 8.57 411.43 2.50% 10.29 2.50% 10.29 432.01
Total 6634.28 165.86 165.86 6966.00
Net Amount after TAX 6966.00
""".trimIndent()
        val lines = listOf(
            simpleLine(0,20,"S A ENTERPRISES"), simpleLine(0,50,"3RD CROSS, APMC YARD, BATWADI, TUMKUR - 572103"), simpleLine(0,80,"GSTIN #:29AWKPN8131H1Z5"),
            simpleLine(0,120,"TAX INVOICE"), rowLine(0,155,100 to "Invoice No.",350 to "S264757",650 to "Date",790 to "02/09/2026"),
            simpleLine(0,195,"Details of Receiver / Billed To."), simpleLine(0,225,"BALAJI TRADING BT"), simpleLine(0,255,"APMC YARD TUMKUR"), simpleLine(0,285,"GSTIN #:29ABZPB5530G1ZE"),
            rowLine(0,340,60 to "S.No",140 to "Products",480 to "HSNCode",610 to "MRP",700 to "Case",780 to "Pcs",860 to "Rate",950 to "Taxable",1080 to "CGST",1210 to "SGST",1360 to "Net Amt"),
            rowLine(0,380,60 to "1",140 to "LEVISTA 168 PCS 2RS",480 to "21011120",610 to "336.00",700 to "1",780 to "0",860 to "248.57",950 to "5965.71",1080 to "149.14",1210 to "149.14",1360 to "6263.99"),
            rowLine(0,415,60 to "2",140 to "SW CITRAN 1KG",480 to "20019000",610 to "110.00",700 to "0",780 to "3",860 to "85.71",950 to "257.14",1080 to "6.43",1210 to "6.43",1360 to "270.00"),
            rowLine(0,450,60 to "3",140 to "PRAN POTATO BISCATE 10/-",480 to "19059200",610 to "10.00",700 to "0",780 to "48",860 to "8.57",950 to "411.43",1080 to "10.29",1210 to "10.29",1360 to "432.01"),
            rowLine(0,500,950 to "Total",1040 to "6634.28",1160 to "165.86",1280 to "165.86",1420 to "6966.00"),
            rowLine(0,540,900 to "Net Amount after TAX",1360 to "6966.00")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.AUTO_MODE, raw, lines)
        requireEq(r.invoiceNumber,"S264757","SA invoice no")
        requireEq(r.invoiceDate,"02-09-2026","SA date")
        requireContains(r.seller.name,"S A ENTERPRISES","SA seller")
        requireContains(r.buyer.name,"BALAJI TRADING","SA buyer")
        requireEq(r.taxableTotal,"6634.28","SA taxable")
        requireEq(r.cgstTotal,"165.86","SA CGST")
        requireEq(r.sgstTotal,"165.86","SA SGST")
        requireEq(r.invoiceTotal,"6966.00","SA total")
        if (r.rows.size != 3) error("SA rows expected 3 got ${r.rows.size}: ${r.rows}")
        requireEq(r.rows[1].quantity,"3","SA pcs quantity")
        requireEq(r.rows[1].unit,"PCS","SA pcs unit")
        requireEq(r.rows[0].hsnSac,"21011120","SA HSN")
        requireEq(r.rows[0].lineTotal,"6263.99","SA row total")
        println("PASS SA: ${r.seller.name} -> ${r.buyer.name}, totals ${r.taxableTotal}/${r.cgstTotal}/${r.sgstTotal}/${r.invoiceTotal}, rows=${r.rows.size}")
    }

    run {
        val raw = """
NANDI MARKETING
Near KN Bajrang Road, Badalahalli Tumkur 572103
GSTIN NO : 29AAQFM2308H1Z3
BALAJI TRADING CO. 9731329698 D
R M C YARD TUMKUR
GST NO : 29ABZPB5530G1ZE
** TAX INVOICE **
E-Inv Dt:31/08/2026
Ack Date:31/08/2026
Bill No : E53528
Bill Date : 31/08/2026 14:28:29
HSN No Product Name UPC MRP CS Pcs Total Unit PKM Base Rate Sch Disc RS Disc Taxable Amt GST % CGST Amt S/UTGST Amt Sch/Pcs Net Amt
A 34011190 PRODUCT A 126 40.00 1 0 684 0726 20.00 0.00 0.00 12077.86 5 301.95 301.94 0 12681.75
B 34011900 PRODUCT B 126 40.00 0 0 1149 0826 30.00 0.00 0.00 9807.71 18 882.69 882.70 0 11573.10
CGST 2.5 + SGST 2.5 603.89
CGST 9 + SGST 9 1765.39
Total Taxable Amt 21885.57
Net Payable Amt 24255.00
""".trimIndent()
        val lines = listOf(
            simpleLine(0,20,"NANDI MARKETING"), simpleLine(0,50,"Near KN Bajrang Road, Badalahalli Tumkur 572103"), simpleLine(0,80,"GSTIN NO : 29AAQFM2308H1Z3"),
            simpleLine(0,120,"BALAJI TRADING CO. 9731329698 D"), simpleLine(0,150,"R M C YARD TUMKUR"), simpleLine(0,180,"GST NO : 29ABZPB5530G1ZE"), simpleLine(0,215,"** TAX INVOICE **"),
            rowLine(0,245,850 to "E-Inv Dt",1040 to "31/08/2026"), rowLine(0,275,850 to "Ack Date",1040 to "31/08/2026"),
            rowLine(0,305,700 to "Bill No",880 to "E53528"), rowLine(0,335,700 to "Bill Date",880 to "31/08/2026 14:28:29"),
            rowLine(0,390,50 to "SL",110 to "HSN No",250 to "Product Name",560 to "UPC",640 to "MRP",710 to "CS",770 to "Pcs",840 to "Total Unit",960 to "PKM",1030 to "Base Rate",1150 to "Sch Disc",1260 to "RS Disc",1370 to "Taxable Amt",1510 to "GST %",1590 to "CGST Amt",1700 to "S/UTGST Amt",1840 to "Sch/Pcs",1940 to "Net Amt"),
            rowLine(0,430,50 to "1",110 to "34011190",250 to "PRODUCT A",560 to "126",640 to "40.00",710 to "1",770 to "0",840 to "684",960 to "0726",1030 to "20.00",1150 to "0.00",1260 to "0.00",1370 to "12077.86",1510 to "5%",1590 to "301.95",1700 to "301.94",1840 to "0",1940 to "12681.75"),
            rowLine(0,465,50 to "2",110 to "34011900",250 to "PRODUCT B",560 to "126",640 to "40.00",710 to "0",770 to "0",840 to "1149",960 to "0826",1030 to "30.00",1150 to "0.00",1260 to "0.00",1370 to "9807.71",1510 to "18%",1590 to "882.69",1700 to "882.70",1840 to "0",1940 to "11573.10"),
            rowLine(0,520,1000 to "CGST 2.5 + SGST 2.5",1450 to "603.89"), rowLine(0,550,1000 to "CGST 9 + SGST 9",1450 to "1765.39"),
            rowLine(0,585,1100 to "Total Taxable Amt",1500 to "21885.57"), rowLine(0,620,1100 to "Net Payable Amt",1500 to "24255.00")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.INVENTORY, raw, lines)
        requireEq(r.invoiceNumber,"E53528","Nandi bill no")
        requireEq(r.invoiceDate,"31-08-2026","Nandi bill date vs ack date")
        requireContains(r.seller.name,"NANDI MARKETING","Nandi seller")
        requireContains(r.buyer.name,"BALAJI TRADING","Nandi buyer")
        requireEq(r.taxableTotal,"21885.57","Nandi taxable")
        requireEq(r.cgstTotal,"1184.64","Nandi CGST aggregate")
        requireEq(r.sgstTotal,"1184.64","Nandi SGST aggregate")
        requireEq(r.invoiceTotal,"24255.00","Nandi net payable")
        if (r.rows.size != 2) error("Nandi rows expected 2 got ${r.rows.size}: ${r.rows}")
        requireEq(r.rows[0].quantity,"684","Nandi total unit quantity")
        requireEq(r.rows[0].unit,"PCS","Nandi total unit inferred PCS")
        requireEq(r.rows[1].gstRate,"18","Nandi GST rate")
        requireMoneyEq(r.rows[1].sgst,"882.70","Nandi S/UTGST")
        requireMoneyEq(r.rows[1].lineTotal,"11573.10","Nandi Net Amt")
        val validationErrors = InvoiceRecognitionEngine.validate(r)
        if (validationErrors.isNotEmpty()) error("Nandi validation errors: $validationErrors")
        println("PASS NANDI: ${r.seller.name} -> ${r.buyer.name}, date=${r.invoiceDate}, totals ${r.taxableTotal}/${r.cgstTotal}/${r.sgstTotal}/${r.invoiceTotal}")
    }

    run {
        // Adarsh-style e-invoice regression: numeric/slash serial, Dated vs Ack Date,
        // Buyer (Bill to), IRN/QR metadata and a compact one-row inventory table.
        val raw = """
Adarsh Papad Private Limited
GSTIN/UIN: 29ABACA5056C1ZY
IRN: 1b68ff...
Ack No: 112633111999999
Ack Date: 08-Sep-2026
Invoice No. 2126/26-27
Dated 7-Sep-26
Buyer (Bill to)
Balaji Trading Co.
GSTIN/UIN: 29ABZPB5530G1ZE
Description of Goods HSN/SAC Quantity Rate per Amount
Adarsh Unfried Extruded 3D Bat 1kg 19059030 20 Packet 66.66 Packet 1333.20
CGST 2.5% 33.33
SGST 2.5% 33.33
Round Off 0.14
Total 1400.00
""".trimIndent()
        val lines = listOf(
            simpleLine(0,20,"Adarsh Papad Private Limited"), simpleLine(0,50,"GSTIN/UIN: 29ABACA5056C1ZY"),
            simpleLine(0,80,"IRN: 1b68ff00112233445566778899aabbcc"), simpleLine(0,110,"Ack No: 112633111999999"),
            rowLine(0,140,650 to "Ack Date",850 to "08-Sep-2026"), rowLine(0,170,650 to "Invoice No.",850 to "2126/26-27"),
            rowLine(0,200,650 to "Dated",850 to "7-Sep-26"), simpleLine(0,230,"Buyer (Bill to)"),
            simpleLine(0,260,"Balaji Trading Co."), simpleLine(0,290,"GSTIN/UIN: 29ABZPB5530G1ZE"),
            rowLine(0,340,80 to "Description of Goods",700 to "HSN/SAC",900 to "Quantity",1080 to "Rate",1260 to "per",1370 to "Amount"),
            rowLine(0,380,80 to "Adarsh Unfried Extruded 3D Bat 1kg",700 to "19059030",900 to "20 Packet",1080 to "66.66",1260 to "Packet",1370 to "1333.20"),
            rowLine(0,440,1000 to "CGST 2.5%",1370 to "33.33"), rowLine(0,470,1000 to "SGST 2.5%",1370 to "33.33"),
            rowLine(0,500,1000 to "Round Off",1370 to "0.14"), rowLine(0,530,1000 to "Total",1370 to "1400.00")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.INVENTORY, raw, lines)
        requireEq(r.invoiceNumber,"2126/26-27","Adarsh numeric/slash invoice no")
        requireEq(r.invoiceDate,"07-09-2026","Adarsh Dated must outrank Ack Date")
        requireContains(r.seller.name,"Adarsh Papad","Adarsh seller")
        requireContains(r.buyer.name,"Balaji Trading","Adarsh buyer")
        requireEq(r.taxableTotal,"1333.20","Adarsh taxable")
        requireEq(r.cgstTotal,"33.33","Adarsh CGST")
        requireEq(r.sgstTotal,"33.33","Adarsh SGST")
        requireEq(r.invoiceTotal,"1400.00","Adarsh total")
        if (r.rows.size != 1) error("Adarsh rows expected 1 got ${r.rows.size}: ${r.rows}")
        println("PASS ADARSH E-INVOICE: ${r.invoiceNumber}, ${r.invoiceDate}, ${r.seller.name} -> ${r.buyer.name}")
    }


    run {
        // Public/official-layout regression based on the CBIC GST INV-1 field structure:
        // Billed-to receiver, HSN/Qty/Unit/Rate/Total/Discount/Taxable and component taxes.
        // A bare Total column here is pre-discount goods value, not the tax-inclusive row total.
        val raw = """
ABC Hardware & Co.
GSTIN: 29ABCDE1234F1Z5
TAX INVOICE
Invoice No INV/2026-27/001
Date 11-09-2026
Details of Receiver (Billed to)
Balaji Trading Co
GSTIN: 29ABZPB5530G1ZE
Description of Goods HSN Qty Unit Rate Total Discount Taxable value CGST SGST IGST
INDUSTRIAL PART 84839000 2 Nos 1000.00 2000.00 100.00 1900.00 171.00 171.00 0.00
Total Invoice Value 2242.00
""".trimIndent()
        val lines = listOf(
            simpleLine(0,20,"ABC Hardware & Co."), simpleLine(0,50,"GSTIN: 29ABCDE1234F1Z5"), simpleLine(0,90,"TAX INVOICE"),
            rowLine(0,120,100 to "Invoice No",330 to "INV/2026-27/001"), rowLine(0,150,100 to "Date",330 to "11-09-2026"),
            simpleLine(0,190,"Details of Receiver (Billed to)"), simpleLine(0,220,"Balaji Trading Co"), simpleLine(0,250,"GSTIN: 29ABZPB5530G1ZE"),
            rowLine(0,300,50 to "Description of Goods",470 to "HSN",600 to "Qty",700 to "Unit",800 to "Rate",900 to "Total",1000 to "Discount",1120 to "Taxable value",1270 to "CGST",1390 to "SGST",1510 to "IGST"),
            rowLine(0,340,50 to "INDUSTRIAL PART",470 to "84839000",600 to "2",700 to "Nos",800 to "1000.00",900 to "2000.00",1000 to "100.00",1120 to "1900.00",1270 to "171.00",1390 to "171.00",1510 to "0.00"),
            rowLine(0,400,1100 to "Total Invoice Value",1510 to "2242.00")
        )
        val r = InvoiceRecognitionEngine.recognize(client, InvoiceRecognitionEngine.AUTO_DIRECTION, InvoiceRecognitionEngine.INVENTORY, raw, lines)
        requireEq(r.invoiceNumber,"INV/2026-27/001","CBIC invoice no")
        requireEq(r.invoiceDate,"11-09-2026","CBIC date")
        requireContains(r.seller.name,"ABC Hardware","CBIC seller")
        requireContains(r.buyer.name,"Balaji Trading","CBIC buyer")
        requireEq(r.buyer.address,"","CBIC table must not bleed into buyer address")
        requireEq(r.taxableTotal,"1900.00","CBIC taxable")
        requireEq(r.cgstTotal,"171.00","CBIC CGST")
        requireEq(r.sgstTotal,"171.00","CBIC SGST")
        requireEq(r.invoiceTotal,"2242.00","CBIC invoice total")
        if (r.rows.size != 1) error("CBIC rows expected 1 got ${r.rows.size}: ${r.rows}")
        requireEq(r.rows[0].quantity,"2","CBIC quantity")
        requireEq(r.rows[0].unit,"NOS","CBIC unit")
        requireEq(r.rows[0].rate,"1000","CBIC rate")
        requireEq(r.rows[0].discount,"100","CBIC discount")
        requireEq(r.rows[0].taxable,"1900","CBIC row taxable")
        requireEq(r.rows[0].lineTotal,"2242.00","CBIC derived tax-inclusive row total")
        val errors = InvoiceRecognitionEngine.validate(r)
        if (errors.isNotEmpty()) error("CBIC validation errors: $errors")
        println("PASS CBIC GST INV-1 STYLE: ${r.invoiceNumber}, ${r.taxableTotal} + tax = ${r.invoiceTotal}")
    }

    println("ALL R45 INVOICE RECOGNITION REGRESSIONS PASSED")
}
