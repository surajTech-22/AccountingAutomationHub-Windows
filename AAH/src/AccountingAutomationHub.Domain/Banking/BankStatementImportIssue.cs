namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementImportIssue
{
    public string SourceFile { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public BankImportErrorCode ErrorCode { get; set; } = BankImportErrorCode.E015_UnknownFailure;
    public string ErrorCodeText => BankImportErrorCatalog.Code(ErrorCode);
    public string FriendlyMessage { get; set; } = string.Empty;
    public string TechnicalMessage { get; set; } = string.Empty;
    public bool IsRetryable { get; set; }
    public int Attempt { get; set; } = 1;

    // Kept for existing UI/report bindings.
    public string Message
    {
        get => string.IsNullOrWhiteSpace(FriendlyMessage)
            ? TechnicalMessage
            : string.IsNullOrWhiteSpace(TechnicalMessage) || TechnicalMessage.Equals(FriendlyMessage, StringComparison.OrdinalIgnoreCase)
                ? FriendlyMessage
                : $"{FriendlyMessage}: {TechnicalMessage}";
        set
        {
            TechnicalMessage = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(FriendlyMessage))
                FriendlyMessage = BankImportErrorCatalog.Friendly(ErrorCode);
        }
    }

    public static BankStatementImportIssue Create(
        string sourceFile,
        string stage,
        BankImportErrorCode code,
        string technicalMessage,
        int attempt = 1,
        bool? retryable = null) => new()
        {
            SourceFile = sourceFile,
            Stage = stage,
            ErrorCode = code,
            FriendlyMessage = BankImportErrorCatalog.Friendly(code),
            TechnicalMessage = technicalMessage,
            Attempt = attempt,
            IsRetryable = retryable ?? BankImportErrorCatalog.IsRetryable(code)
        };
}
