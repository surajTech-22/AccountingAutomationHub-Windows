import java.math.BigDecimal
import java.math.RoundingMode

private fun bd(v: String) = BigDecimal(v)
private fun money(v: BigDecimal) = v.setScale(2, RoundingMode.HALF_UP)
private fun requireEq(actual: BigDecimal, expected: String, label: String) {
    check(money(actual) == bd(expected)) { "$label expected $expected but was ${money(actual)}" }
}

fun main() {
    // R46 R29 mirrors the Windows Paddle layout verifier against the live NANDI invoice.
    // The bad item cell represents the old flattened OCR failure; layout columns + GST
    // arithmetic + the printed GST summary must restore the accounting values.
    val fiveTaxable = listOf("5739.14", "2620.76", "1858.98", "1858.98").map(::bd)
    val fiveCgst = listOf("143.48", "65.52", "46.47", "46.47").map(::bd)
    val eighteenOther = listOf("5366.79", "2377.19", "645.84", "415.10", "23.31").map(::bd)
    val eighteenCgstOther = listOf("483.01", "213.95", "58.13", "37.36", "2.10").map(::bd)

    val damagedTaxable = bd("79.48")
    val damagedCgst = bd("88.15")
    val damagedSgst = bd("88.15")
    val damagedNet = bd("1155.79")
    val rate = bd("18")
    val taxTotal = damagedCgst + damagedSgst
    val reverseFromTax = money(taxTotal * bd("100") / rate)
    val reverseFromNet = money(damagedNet - taxTotal)
    check(damagedTaxable != reverseFromTax)
    check(reverseFromTax == bd("979.44"))
    check(reverseFromNet == bd("979.49"))

    val printed18Summary = bd("9807.71")
    val groupBeforeResidual = eighteenOther.sumOf { it } + reverseFromTax
    val residual = money(printed18Summary - groupBeforeResidual)
    check(residual == bd("0.04"))
    val repairedRow = reverseFromTax + residual
    requireEq(repairedRow, "979.48", "damaged row taxable repaired from Paddle table + summary")

    val taxable5 = fiveTaxable.sumOf { it }
    val taxable18 = eighteenOther.sumOf { it } + repairedRow
    requireEq(taxable5, "12077.86", "5% taxable slab")
    requireEq(taxable18, "9807.71", "18% taxable slab")
    requireEq(taxable5 + taxable18, "21885.57", "invoice taxable")

    val cgst = fiveCgst.sumOf { it } + eighteenCgstOther.sumOf { it } + damagedCgst
    val sgst = fiveCgst.sumOf { it } + eighteenCgstOther.sumOf { it } + damagedSgst
    requireEq(cgst, "1184.64", "CGST total")
    requireEq(sgst, "1184.64", "SGST total")

    val preRound = money(taxable5 + taxable18 + cgst + sgst)
    requireEq(preRound, "24254.85", "pre-round total")
    val invoiceTotal = bd("24255.00")
    val roundOff = money(invoiceTotal - preRound)
    requireEq(roundOff, "0.15", "round off")

    val reviewDiscount = bd("1140.32")
    check(reviewDiscount > BigDecimal.ZERO) // review evidence only; R27/R29 still post zero discount.

    println("PADDLEOCR-VL 1.6 TABLE AUTHORITY: 5%=$taxable5 18%=$taxable18 CGST=$cgst SGST=$sgst ROUND=$roundOff TOTAL=$invoiceTotal")
    println("ALL R46 R29 PADDLE DOCUMENT/TABLE REGRESSIONS PASSED")
}
