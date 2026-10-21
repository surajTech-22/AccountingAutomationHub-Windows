namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class PlaceholderPageViewModel(
    string title,
    string subtitle,
    string icon,
    string plannedScope)
    : PageViewModel(title, subtitle)
{
    public string Icon { get; } = icon;
    public string PlannedScope { get; } = plannedScope;
    public string StateText { get; } = "App shell only — implementation has not started.";
}
