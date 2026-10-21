using System.ComponentModel;
using System.Runtime.CompilerServices;
using AccountingAutomationHub.Application.Settings;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class EReturnsRootContext :
    INotifyPropertyChanged
{
    public const string PreferenceKey = "EReturnsRoot";

    private readonly IApplicationPreferenceRepository _repository;
    private string _rootPath;

    public EReturnsRootContext(
        IApplicationPreferenceRepository repository)
    {
        _repository = repository;
        _rootPath = _repository.Get(
            PreferenceKey);
    }

    public string RootPath
    {
        get => _rootPath;
        private set
        {
            var normalized = value.Trim();

            if (string.Equals(
                    _rootPath,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _rootPath = normalized;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetRoot(
        string path)
    {
        var normalized = path.Trim();

        _repository.Set(
            PreferenceKey,
            normalized);

        RootPath = normalized;
    }

    public void Refresh()
    {
        RootPath = _repository.Get(
            PreferenceKey);
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}
