package aah.invoice

// R46 R39 design-time corpus: document families/layouts the Paddle spatial pipeline must preserve
// before AAH accounting semantics decide where values go. This is intentionally independent of
// any one supplier template. Native Paddle model execution is performed on the target Windows PC.
object InvoiceRecognitionR39PaddleSpatialCorpusTest {
    data class Fixture(
        val name: String,
        val documentType: String,
        val layout: String,
        val supply: String,
        val posting: String,
        val mustUseSummaryAuthority: Boolean = false,
        val mustFailClosed: Boolean = false
    )

    val fixtures = listOf(
        Fixture("01 ruled B2B goods intra-state", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("02 borderless B2B goods", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("03 service invoice no quantity", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("04 single-line invoice", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("05 multi-page invoice", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("06 bill of supply", "Bill of Supply", "wireless", "Exempt/Nil", "Purchase/Sales"),
        Fixture("07 credit note", "Credit Note", "wired", "Intra-State", "NoteWorkflow", mustFailClosed=true),
        Fixture("08 debit note", "Debit Note", "wired", "Intra-State", "NoteWorkflow", mustFailClosed=true),
        Fixture("09 export with payment", "Tax Invoice", "wireless", "Export", "Purchase/Sales"),
        Fixture("10 SEZ with payment", "Tax Invoice", "wireless", "SEZ", "Purchase/Sales"),
        Fixture("11 reverse charge", "Tax Invoice", "wired", "Reverse Charge", "Purchase/Sales"),
        Fixture("12 inter-state IGST", "Tax Invoice", "wired", "Inter-State", "Purchase/Sales"),
        Fixture("13 local CGST SGST", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("14 zero-rated export", "Tax Invoice", "wireless", "Zero Rated", "Purchase/Sales"),
        Fixture("15 exempt invoice", "Bill of Supply", "wireless", "Exempt", "Purchase/Sales"),
        Fixture("16 nil-rated invoice", "Bill of Supply", "wireless", "Nil Rated", "Purchase/Sales"),
        Fixture("17 cess invoice", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("18 40 percent GST", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("19 28 percent GST", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("20 18 percent GST", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("21 12 percent GST", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("22 5 percent GST", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("23 3 percent GST", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("24 1.5 percent GST", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("25 0.25 percent GST", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("26 0.1 percent GST", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("27 mixed 5 and 18", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("28 mixed 12 and 18", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("29 three GST slabs", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("30 positive round off", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("31 negative round off", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("32 no round off", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("33 scheme plus RS discount", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("34 MRP tax-inclusive columns", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("35 e-invoice QR", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("36 invoice without QR", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("37 OCR damaged invoice number", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("38 split Bill and number blocks", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("39 invalid GSTIN checksum", "Tax Invoice", "mixed", "Unknown", "NeedsReview", mustFailClosed=true),
        Fixture("40 client is buyer", "Tax Invoice", "mixed", "Intra-State", "Purchase"),
        Fixture("41 client is seller", "Tax Invoice", "mixed", "Intra-State", "Sales"),
        Fixture("42 client matches neither party", "Tax Invoice", "mixed", "Unknown", "NeedsReview", mustFailClosed=true),
        Fixture("43 borderless item table", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("44 fully ruled item table", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("45 merged cells and spanning header", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("46 multi-row item headers", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("47 wrapped product descriptions", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("48 service row blank qty UOM", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("49 goods row UOM omitted", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("50 GST summary outranks damaged rows", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("51 one local component OCR damaged", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("52 damaged line net", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("53 gross subtotal not final payable", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales", mustUseSummaryAuthority=true),
        Fixture("54 payment table beside item table", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("55 bank-details table in footer", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("56 amount-in-words footer", "Tax Invoice", "wireless", "Intra-State", "Purchase/Sales"),
        Fixture("57 repeated HSN multiple rows", "Tax Invoice", "wired", "Intra-State", "Purchase/Sales"),
        Fixture("58 repeated header each page", "Tax Invoice", "mixed", "Intra-State", "Purchase/Sales"),
        Fixture("59 proforma invoice", "Proforma Invoice", "wired", "Reference", "NeedsReview", mustFailClosed=true),
        Fixture("60 delivery challan", "Delivery Challan", "wired", "Reference", "NeedsReview", mustFailClosed=true),
        Fixture("61 purchase order", "Purchase Order", "wired", "Reference", "NeedsReview", mustFailClosed=true),
        Fixture("62 receipt voucher", "Receipt Voucher", "wireless", "Reference", "NeedsReview", mustFailClosed=true),
        Fixture("63 refund voucher", "Refund Voucher", "wireless", "Reference", "NeedsReview", mustFailClosed=true),
        Fixture("64 payment voucher", "Payment Voucher", "wireless", "Reference", "NeedsReview", mustFailClosed=true)
    )

    @JvmStatic fun main(args: Array<String>) {
        check(fixtures.size >= 50)
        check(fixtures.map { it.name }.distinct().size == fixtures.size)
        check(fixtures.any { it.layout == "wired" })
        check(fixtures.any { it.layout == "wireless" })
        check(fixtures.any { it.mustUseSummaryAuthority })
        check(fixtures.any { it.mustFailClosed })
        check(fixtures.any { it.posting == "Purchase" })
        check(fixtures.any { it.posting == "Sales" })
        println("ALL R46 R39 PADDLE SPATIAL 64-INVOICE CORPUS REGRESSIONS PASSED")
    }
}
