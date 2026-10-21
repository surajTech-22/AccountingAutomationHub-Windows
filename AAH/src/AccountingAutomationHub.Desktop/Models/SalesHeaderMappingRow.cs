using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class SalesHeaderMappingRow : INotifyPropertyChanged
{
    private string _sourceColumn = string.Empty;
    private string _sourceHeader = string.Empty;
    private string _sampleValue = string.Empty;
    private string _targetField = string.Empty;
    private string _rule = string.Empty;

    public string SourceColumn { get => _sourceColumn; set => SetField(ref _sourceColumn, value ?? string.Empty); }
    public string SourceHeader { get => _sourceHeader; set => SetField(ref _sourceHeader, value ?? string.Empty); }
    public string SampleValue { get => _sampleValue; set => SetField(ref _sampleValue, value ?? string.Empty); }
    public string TargetField
    {
        get => _targetField;
        set
        {
            if (SetField(ref _targetField, value ?? string.Empty))
                OnPropertyChanged(nameof(Status));
        }
    }
    public string Rule { get => _rule; set => SetField(ref _rule, value ?? string.Empty); }
    public string Status => TargetField.Length > 0 ? "Mapped" : "Unmapped";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
