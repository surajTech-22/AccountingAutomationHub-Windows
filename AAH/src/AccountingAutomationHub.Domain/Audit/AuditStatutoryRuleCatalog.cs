namespace AccountingAutomationHub.Domain.Audit;

/// <summary>
/// Local, versioned statutory rule metadata used by Audit presentation tests.
/// Rules are review authorities, not automatic legal conclusions: applicability,
/// prescribed exceptions and supporting evidence still require auditor review.
/// Fix97 starts the cumulative rule library; later fixes append further rule packs.
/// </summary>
public sealed record AuditStatutoryRule(
    string RuleId,
    string Law,
    string Provision,
    string Title,
    decimal Threshold,
    string ThresholdBasis,
    string ReviewNote,
    string AuthoritySource,
    int EffectiveFinancialYearStart);

public static class AuditStatutoryRuleCatalog
{
    public static readonly AuditStatutoryRule IncomeTax40A3 = new(
        "IT-40A3-CASH-PAYMENT",
        "Income-tax Act, 1961",
        "Section 40A(3)/(3A)",
        "Cash expenditure/payment review",
        10_000m,
        "Aggregate payment to a person in a day",
        "Review Rule 6DD exceptions and the Rs.35,000 goods-carriage threshold before concluding.",
        "Income Tax Department · Section 40A / Threshold Limits",
        2017);

    public static readonly AuditStatutoryRule IncomeTax269ST = new(
        "IT-269ST-CASH-RECEIPT",
        "Income-tax Act, 1961",
        "Section 269ST",
        "High-value cash receipt review",
        200_000m,
        "Aggregate receipt from a person in a day (clause (a) first-pass test)",
        "Also review single-transaction/event tests and statutory/notified exceptions before concluding.",
        "Income Tax Department · Section 269ST",
        2017);

    public static readonly AuditStatutoryRule IncomeTax269SS = new(
        "IT-269SS-CASH-LOAN-ACCEPTANCE",
        "Income-tax Act, 1961",
        "Section 269SS",
        "Cash loan/deposit acceptance review",
        20_000m,
        "Loan/deposit/specified sum and relevant aggregate/outstanding threshold",
        "Review statutory exceptions, outstanding balances and special PACS/PCARD threshold before concluding.",
        "Income Tax Department · Section 269SS / Mode of Receipts and Payments",
        1984);

    public static readonly AuditStatutoryRule IncomeTax269T = new(
        "IT-269T-CASH-LOAN-REPAYMENT",
        "Income-tax Act, 1961",
        "Section 269T",
        "Cash loan/deposit repayment review",
        20_000m,
        "Loan/deposit/specified advance repayment and relevant aggregate threshold",
        "Review statutory exceptions, interest/aggregate balances and special PACS/PCARD threshold before concluding.",
        "Income Tax Department · Section 269T / Mode of Receipts and Payments",
        1981);

    public static IReadOnlyList<AuditStatutoryRule> Fix97IncomeTaxPack { get; } =
        new[] { IncomeTax40A3, IncomeTax269ST, IncomeTax269SS, IncomeTax269T };
}
