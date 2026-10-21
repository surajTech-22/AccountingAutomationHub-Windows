namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalPastePreview
{
    public string SourceName { get; set; } = string.Empty;
    public string Mode { get; set; } = "Full Vouchers";
    public List<JournalVoucher> Vouchers { get; set; } = [];
    public List<JournalLine> Lines { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public decimal DebitTotal => Vouchers.Count > 0
        ? Vouchers.SelectMany(voucher => voucher.Lines)
            .Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            .Sum(line => Math.Abs(line.Amount))
        : Lines.Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            .Sum(line => Math.Abs(line.Amount));
    public decimal CreditTotal => Vouchers.Count > 0
        ? Vouchers.SelectMany(voucher => voucher.Lines)
            .Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
            .Sum(line => Math.Abs(line.Amount))
        : Lines.Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
            .Sum(line => Math.Abs(line.Amount));
    public int VoucherCount => Vouchers.Count;
    public int LineCount => Vouchers.Count > 0 ? Vouchers.Sum(voucher => voucher.Lines.Count) : Lines.Count;
}
