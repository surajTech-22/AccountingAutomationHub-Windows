using System.ComponentModel;
using System.Runtime.CompilerServices;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class NavigationItem : INotifyPropertyChanged
{
    private bool _toggleValue;
    private readonly Action<bool>? _toggleChanged;

    public NavigationItem(
        string title,
        string icon,
        string description,
        Func<PageViewModel> pageFactory,
        bool showToggle = false,
        Action<bool>? toggleChanged = null)
    {
        Title = title;
        Icon = icon;
        Description = description;
        PageFactory = pageFactory;
        ShowToggle = showToggle;
        _toggleChanged = toggleChanged;
    }

    public string Title { get; }
    public string Icon { get; }
    public string Description { get; }
    public Func<PageViewModel> PageFactory { get; }
    public bool ShowToggle { get; }

    public string ToggleText => ToggleValue ? "ON" : "OFF";

    public bool ToggleValue
    {
        get => _toggleValue;
        set => SetToggleValue(value, notifyAction: true);
    }

    public void SetToggleValue(bool value, bool notifyAction)
    {
        if (_toggleValue == value) return;
        _toggleValue = value;
        OnPropertyChanged(nameof(ToggleValue));
        OnPropertyChanged(nameof(ToggleText));
        if (notifyAction) _toggleChanged?.Invoke(value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // Fix126: overflow tooltip fallback must show the user-facing navigation label,
    // never AccountingAutomationHub.Desktop.Models.NavigationItem.
    public override string ToString() => Title;
}
