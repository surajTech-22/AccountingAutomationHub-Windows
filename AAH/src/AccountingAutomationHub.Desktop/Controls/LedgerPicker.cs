namespace AccountingAutomationHub.Desktop.Controls;

/// <summary>
/// Dedicated accounting-Ledger selector.  It deliberately does not allow free text:
/// Ledger creation/mapping remains an explicit Master workflow, while entry screens get
/// native click/search/keyboard selection over the complete Master list.
/// </summary>
public sealed class LedgerPicker : SearchableComboBox
{
    public LedgerPicker()
    {
        AllowCustomText = false;
        SelectFirstVisibleOnNoMatch = false;
        UpdateSourceWhileTyping = false;
        OpenOnFocus = true;
        HandleEnterKey = false;
        EnableMouseWheelSelection = false;
        MaxDropDownHeight = 430;
        StaysOpenOnEdit = true;

        // Keep the editable text/value strictly on the Ledger Master name. Rich
        // Group/balance metadata belongs only to the popup ItemTemplate/tooltip.
        System.Windows.Controls.TextSearch.SetTextPath(this, "Name");
    }
}
