using System.Windows.Input;

namespace AccountingAutomationHub.Desktop.Services;

public static class ShortcutGestureMatcher
{
    public static bool Matches(KeyEventArgs e, string configured, string fallback)
    {
        return TryParse(configured, out var gesture) && gesture.Matches(e)
               || (!TryParse(configured, out _) && TryParse(fallback, out gesture) && gesture.Matches(e));
    }

    private static bool TryParse(string text, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        ModifierKeys modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Alt;
            else if (!Enum.TryParse(part, true, out key))
            {
                if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                {
                    var name = char.IsDigit(part[0]) ? $"D{part}" : part.ToUpperInvariant();
                    if (!Enum.TryParse(name, true, out key)) return false;
                }
                else return false;
            }
        }
        if (key == Key.None) return false;
        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    private readonly record struct ShortcutGesture(Key Key, ModifierKeys Modifiers)
    {
        public bool Matches(KeyEventArgs e) => e.Key == Key && Keyboard.Modifiers == Modifiers;
    }
}
