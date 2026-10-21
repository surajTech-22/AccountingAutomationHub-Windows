namespace AccountingAutomationHub.Desktop.ViewModels;

public abstract class PageViewModel(
    string title,
    string subtitle) : ViewModelBase
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}
