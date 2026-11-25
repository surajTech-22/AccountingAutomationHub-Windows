using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// InvoiceVision Child has been unlinked from AAH. AAH no longer invokes external
/// InvoiceVisionChild binaries or trained-model handoffs; Phone Link ONNX OCR runs directly.
/// </summary>
internal static class InvoiceOcrChildBridge
{
    public const bool IsLinked = false;
    public const string InstalledExe = @"C:\AccountingAutomationHub\InvoiceVisionChild\InvoiceVisionChild.exe";

    internal sealed class Status
    {
        public bool Stage1Succeeded { get; init; }
        public string Message { get; init; } = "";
        public string Stage1Archive { get; init; } = "";
        public string RequestId { get; init; } = "";
    }

    public static bool OpenReview(IReadOnlyList<string> paths, long clientId, string financialYear, string scanId, string stage1Archive, string evidenceFolder)
    {
        throw new InvalidOperationException("InvoiceVision Child has been unlinked from AAH. Use AAH's built-in ONNX OCR review instead.");
    }

    public static Task<Status> TryStage1Async(IReadOnlyList<string> paths, long clientId, string financialYear,
        string scanId, string evidenceFolder, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Status
        {
            Stage1Succeeded = false,
            Message = "InvoiceVision Child unlinked; using AAH ONNX Phone Link OCR directly."
        });
    }
}
