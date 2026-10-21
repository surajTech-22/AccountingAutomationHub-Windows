import com.accountingautomationhub.android.InvoiceRecognitionEngine

private fun line(y: Int, text: String): InvoiceRecognitionEngine.OcrLine {
    val words = text.split(Regex("\\s+")).filter { it.isNotBlank() }.mapIndexed { index, token ->
        InvoiceRecognitionEngine.OcrWord(0, token, 50 + index * 75, y, 110 + index * 75, y + 24)
    }
    return InvoiceRecognitionEngine.OcrLine(0, text, 50, y, 1500, y + 24, words)
}

fun main() {
    val client = InvoiceRecognitionEngine.ClientIdentity("Balaji Trading Co", "29ABZPB5530G1ZE")
    val raw = """
        Demo Supplier Pvt Ltd
        GSTIN 29ABCDE1234F1Z5
        TAX INVOICE
        Invoice Serial Number INV-46-001
        Date of Issue 11-09-2026
        Bill To Balaji Trading Co
        GSTIN 29ABZPB5530G1ZE
        Taxable Value 1000.00
        CGST 90.00
        SGST 90.00
        Round Off -0.01
        Amount Including GST 1179.99
        Company's Bank Details
        Bank Name Canara Bank OD
        Account 125007189792
        IFSC CNRB0000045
    """.trimIndent()
    val lines = raw.lines().filter { it.isNotBlank() }.mapIndexed { i, text -> line(20 + i * 30, text) }
    val result = InvoiceRecognitionEngine.recognize(
        client,
        InvoiceRecognitionEngine.PURCHASE,
        InvoiceRecognitionEngine.ACCOUNTING,
        raw,
        lines
    )
    check(result.invoiceNumber == "INV-46-001") { "Invoice serial label failed: ${result.invoiceNumber}" }
    check(result.invoiceDate == "11-09-2026") { "Date-of-issue label failed: ${result.invoiceDate}" }
    check(result.roundOff == "-0.01") { "Signed explicit round-off failed: ${result.roundOff}" }
    check(result.bankDetails.isBlank()) { "Bank details must remain raw OCR evidence only: ${result.bankDetails}" }
    check(result.extraRecognized.none { it.contains("Bank / Payment", true) }) { "Bank/payment leaked into mapped invoice fields" }
    check(result.rows.all { it.ledger.isBlank() }) { "Scanner must not allocate accounting ledger" }
    val errors = InvoiceRecognitionEngine.validate(result, emptyList())
    check(errors.none { it.contains("row", true) || it.contains("ledger", true) }) { "Accounting mode must defer row/ledger allocation: $errors" }
    println("PASS R46 invoice review: explicit signed round-off, raw bank evidence only, accounting rows/ledger deferred")
}
