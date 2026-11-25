using System;
using System.Collections.Generic;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// InvoiceVision Child has been unlinked from AAH. This stub remains only for
/// binary/API compatibility and refuses launching external child executables.
/// </summary>
public static class InvoiceVisionChildLauncher
{
    public const bool IsLinked = false;

    public static void Open(long clientId, string financialYear, IEnumerable<string>? paths = null, bool watchMobile = false)
    {
        throw new InvalidOperationException("InvoiceVision Child has been unlinked from AAH and is no longer required.");
    }
}
