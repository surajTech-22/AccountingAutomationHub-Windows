using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class TallyExportSelectionContext
{
    private readonly object _gate = new();
    private long _clientId;
    private string _financialYear = string.Empty;
    private IReadOnlyList<TallyVoucherExportRange> _bankRanges = [];
    private string _bankMode = "Month";
    private string _description = "No Tally export period selected";

    public event EventHandler? Changed;

    public void SetBankRanges(
        long clientId,
        string financialYear,
        IEnumerable<TallyVoucherExportRange> ranges,
        string mode,
        string description)
    {
        var normalized = ranges
            .Select(range => range.Normalize())
            .OrderBy(range => range.FromDate)
            .ThenBy(range => range.ToDate)
            .ToArray();
        var newDescription = normalized.Length == 0
            ? "No Tally export period selected"
            : string.IsNullOrWhiteSpace(description)
                ? BuildDescription(normalized, mode)
                : description.Trim();

        var changed = false;
        lock (_gate)
        {
            changed = _clientId != clientId ||
                      !_financialYear.Equals(financialYear ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                      !_bankMode.Equals(mode ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                      !_description.Equals(newDescription, StringComparison.Ordinal) ||
                      !RangesEqual(_bankRanges, normalized);
            _clientId = clientId;
            _financialYear = financialYear ?? string.Empty;
            _bankRanges = normalized;
            _bankMode = mode ?? string.Empty;
            _description = newDescription;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetBankRange(
        long clientId,
        string financialYear,
        DateTime fromDate,
        DateTime toDate,
        string mode,
        string periodKey) =>
        SetBankRanges(
            clientId,
            financialYear,
            [new TallyVoucherExportRange { FromDate = fromDate, ToDate = toDate }],
            mode,
            mode.Equals("Month", StringComparison.OrdinalIgnoreCase) && periodKey.Length == 6
                ? fromDate.ToString("MMMM yyyy")
                : $"{fromDate:dd.MM.yyyy} to {toDate:dd.MM.yyyy}");

    public bool TryGetBankRanges(
        long clientId,
        string financialYear,
        out IReadOnlyList<TallyVoucherExportRange> ranges,
        out string description)
    {
        lock (_gate)
        {
            if (_clientId == clientId &&
                _financialYear.Equals(financialYear ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                _bankRanges.Count > 0)
            {
                ranges = _bankRanges.ToArray();
                description = _description;
                return true;
            }
        }

        ranges = [];
        description = "No Tally export period selected";
        return false;
    }

    public bool TryGetBankRange(
        long clientId,
        string financialYear,
        out DateTime fromDate,
        out DateTime toDate,
        out string description)
    {
        if (TryGetBankRanges(clientId, financialYear, out var ranges, out description))
        {
            fromDate = ranges.Min(range => range.FromDate);
            toDate = ranges.Max(range => range.ToDate);
            return true;
        }

        fromDate = default;
        toDate = default;
        return false;
    }

    private static bool RangesEqual(
        IReadOnlyList<TallyVoucherExportRange> left,
        IReadOnlyList<TallyVoucherExportRange> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.FromDate.Date == pair.Second.FromDate.Date &&
            pair.First.ToDate.Date == pair.Second.ToDate.Date);

    private static string BuildDescription(
        IReadOnlyList<TallyVoucherExportRange> ranges,
        string mode)
    {
        if (!mode.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            return $"{ranges[0].FromDate:dd.MM.yyyy} to {ranges[^1].ToDate:dd.MM.yyyy}";
        }
        return string.Join(", ", ranges.Select(range => range.FromDate.ToString("MMM yyyy")));
    }
}
