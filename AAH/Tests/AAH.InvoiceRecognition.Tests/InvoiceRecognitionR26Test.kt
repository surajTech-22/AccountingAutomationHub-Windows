import com.accountingautomationhub.android.InvoiceRecognitionEngine

private fun wl(y:Int,vararg cells:Pair<Int,String>):InvoiceRecognitionEngine.OcrLine{
 val words=mutableListOf<InvoiceRecognitionEngine.OcrWord>(); cells.forEach{(x0,cell)->var x=x0;cell.split(Regex("\\s+")).filter{it.isNotBlank()}.forEach{t->val w=(t.length*9).coerceAtLeast(18);words+=InvoiceRecognitionEngine.OcrWord(0,t,x,y,x+w,y+22);x+=w+5}}
 return InvoiceRecognitionEngine.OcrLine(0,cells.joinToString(" "){it.second},words.minOfOrNull{it.left}?:0,y,words.maxOfOrNull{it.right}?:100,y+22,words)
}
private fun tl(y:Int,t:String)=wl(y,80 to t)
fun main(){
 val client=InvoiceRecognitionEngine.ClientIdentity("BALAJI TRADING CO","29ABZPB5530G1ZE")
 val raw="""NANDI MARKETING
GSTN NO 29AAQFM2308H1Z3
BALAJI TRADING CO
GSTN NO 29ABZPB5530G1ZE
Bili NO : E53528
Bill Date : 31/08/2026
HSN No Product Name UPC MRP CS Pcs
Total Unit PKM Base Rate Sch Disc RS Disc
Taxable GST CGST S/UTGST Net
Amt Amt Amt Amt
34011190 LB NEEM ALOE SOAP 125G 126 40.00 173 0726 35.27 305.11 57.97 5739.14 5 143.48 143.48 6026.21
34025000 SURFXL EW 70G 6T 144 10.00 720 0826 7.57 27.24 54.21 5366.79 18 483.01 483.01 6332.81
Total 21885.57 1184.64 1184.64 24254.85
Net Payable 24255.00
""".trimIndent()
 val lines=listOf(
  tl(20,"NANDI MARKETING"),tl(45,"GSTN NO 29AAQFM2308H1Z3"),tl(75,"BALAJI TRADING CO"),tl(100,"GSTN NO 29ABZPB5530G1ZE"),
  wl(130,700 to "Bili NO",950 to "E53528"),wl(155,700 to "Bill Date",950 to "31/08/2026"),
  wl(210,40 to "HSN No",180 to "Product Name",520 to "UPC",600 to "MRP",680 to "CS",740 to "Pcs"),
  wl(232,800 to "Total Unit",930 to "PKM",1010 to "Base Rate",1130 to "Sch Disc",1240 to "RS Disc"),
  wl(254,1360 to "Taxable",1500 to "GST",1580 to "CGST",1690 to "S/UTGST",1880 to "Net"),
  wl(276,1360 to "Amt",1580 to "Amt",1690 to "Amt",1880 to "Amt"),
  wl(315,40 to "34011190",180 to "LB NEEM ALOE SOAP 125G",520 to "126",600 to "40.00",680 to "1",740 to "73",800 to "173",930 to "0726",1010 to "35.27",1130 to "305.11",1240 to "57.97",1360 to "5739.14",1500 to "5",1580 to "143.48",1690 to "143.48",1880 to "6026.21"),
  wl(350,40 to "34025000",180 to "SURFXL EW 70G 6T",520 to "144",600 to "10.00",680 to "1",740 to "20",800 to "720",930 to "0826",1010 to "7.57",1130 to "27.24",1240 to "54.21",1360 to "5366.79",1500 to "18",1580 to "483.01",1690 to "483.01",1880 to "6332.81"),
  wl(400,1100 to "Total",1360 to "21885.57",1580 to "1184.64",1690 to "1184.64",1880 to "24254.85"),wl(430,1100 to "Net Payable",1880 to "24255.00")
 )
 val r=InvoiceRecognitionEngine.recognize(client,InvoiceRecognitionEngine.AUTO_DIRECTION,InvoiceRecognitionEngine.ACCOUNTING,raw,lines)
 check(r.invoiceNumber=="E53528"){"invoice number ${r.invoiceNumber}"}
 check(r.rows.size>=2){"fragmented header rows ${r.rows.map{it.description}}"}
 check(r.rows.take(2).map{it.gstRate}.toSet()==setOf("5","18")){"rates ${r.rows.map{it.gstRate}}"}
 check(r.invoiceTotal=="24255.00"){"total ${r.invoiceTotal}"}
 println("ALL R46 R26 FRAGMENTED TABLE REGRESSIONS PASSED")
}
