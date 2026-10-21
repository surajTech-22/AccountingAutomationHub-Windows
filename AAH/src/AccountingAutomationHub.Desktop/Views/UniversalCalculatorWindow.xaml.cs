using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AccountingAutomationHub.Desktop.Views;

public partial class UniversalCalculatorWindow : Window
{
    private decimal? _result;
    private IReadOnlyList<decimal> _selectedValues = [];
    private string _selectionExpression = string.Empty;
    private bool _updatingSelectionExpression;

    public UniversalCalculatorWindow(
        string? seed = null,
        IReadOnlyList<decimal>? selectedValues = null)
    {
        InitializeComponent();
        SetSeed(seed ?? string.Empty, selectedValues);
        Loaded += (_, _) =>
        {
            ExpressionBox.Focus();
            ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
        };
    }

    public void SetSeed(
        string seed,
        IReadOnlyList<decimal>? selectedValues = null)
    {
        _selectedValues = selectedValues?.ToArray() ?? [];
        if (_selectedValues.Count >= 2)
        {
            ApplySelectionOperator('+');
        }
        else
        {
            ExitSelectionMode();
            if (!string.IsNullOrWhiteSpace(seed))
            {
                ExpressionBox.Text = seed;
            }
        }

        ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
        ExpressionBox.Focus();
    }

    /// <summary>
    /// Fix12 live-grid mode. While Calculator remains open, changing the selection in
    /// any AAH DataGrid immediately replaces the selection expression with the current
    /// selected numeric values and defaults back to addition. This intentionally does
    /// not focus the calculator window, so Ctrl/Shift multi-selection can continue in
    /// the source grid without the calculator stealing focus after every selected cell.
    /// </summary>
    public void SetLiveSelection(IReadOnlyList<decimal>? selectedValues)
    {
        var values = selectedValues?.ToArray() ?? [];
        if (values.Length >= 2)
        {
            _selectedValues = values;
            ApplySelectionOperator('+', focusExpression: false);
            return;
        }

        ExitSelectionMode();
        _updatingSelectionExpression = true;
        try
        {
            ExpressionBox.Text = values.Length == 1
                ? FormatOperand(values[0])
                : string.Empty;
            ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
        }
        finally
        {
            _updatingSelectionExpression = false;
        }

        RefreshResult(false);
    }

    private bool IsSelectionMode => _selectedValues.Count >= 2;

    private void ExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsSelectionMode &&
            !_updatingSelectionExpression &&
            !string.Equals(ExpressionBox.Text, _selectionExpression, StringComparison.Ordinal))
        {
            ExitSelectionMode();
        }

        RefreshResult(false);
    }

    private void ExpressionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RefreshResult(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ExpressionBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsSelectionMode || string.IsNullOrEmpty(e.Text)) return;

        var operation = e.Text switch
        {
            "+" => '+',
            "-" => '-',
            "*" or "×" when _selectedValues.Count == 2 => '*',
            "/" or "÷" when _selectedValues.Count == 2 => '/',
            _ => '\0'
        };

        if (operation != '\0')
        {
            ApplySelectionOperator(operation);
            e.Handled = true;
            return;
        }

        // With three or more selected values, selection-mode arithmetic deliberately
        // supports only + and -. A normal edit (digits, brackets, etc.) leaves selection
        // mode and turns the expression back into an ordinary unrestricted calculator.
        if (_selectedValues.Count > 2 && e.Text is ("*" or "/" or "×" or "÷" or "%"))
        {
            e.Handled = true;
            return;
        }

        ExitSelectionMode();
    }

    private void Input_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string text }) return;

        if (IsSelectionMode)
        {
            var selectionOperation = text switch
            {
                "+" => '+',
                "-" => '-',
                "×" when _selectedValues.Count == 2 => '*',
                "÷" when _selectedValues.Count == 2 => '/',
                _ => '\0'
            };

            if (selectionOperation != '\0')
            {
                ApplySelectionOperator(selectionOperation);
                return;
            }

            ExitSelectionMode();
        }

        var insert = text switch
        {
            "×" => "*",
            "÷" => "/",
            _ => text
        };
        var start = ExpressionBox.SelectionStart;
        ExpressionBox.Text = ExpressionBox.Text
            .Remove(start, ExpressionBox.SelectionLength)
            .Insert(start, insert);
        ExpressionBox.SelectionStart = start + insert.Length;
        ExpressionBox.Focus();
    }

    private void ApplySelectionOperator(char operation, bool focusExpression = true)
    {
        if (_selectedValues.Count < 2) return;
        if (_selectedValues.Count > 2 && operation is not ('+' or '-')) return;

        var separator = operation switch
        {
            '*' => "*",
            '/' => "/",
            '-' => "-",
            _ => "+"
        };

        _selectionExpression = string.Join(
            separator,
            _selectedValues.Select(FormatOperand));

        _updatingSelectionExpression = true;
        try
        {
            ExpressionBox.Text = _selectionExpression;
            ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
        }
        finally
        {
            _updatingSelectionExpression = false;
        }

        UpdateSelectionOperatorAvailability();
        RefreshResult(false);
        if (focusExpression)
            ExpressionBox.Focus();
    }

    private static string FormatOperand(decimal value)
    {
        var formatted = value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
        return value < 0 ? $"({formatted})" : formatted;
    }

    private void ExitSelectionMode()
    {
        _selectedValues = [];
        _selectionExpression = string.Empty;
        UpdateSelectionOperatorAvailability();
    }

    private void UpdateSelectionOperatorAvailability()
    {
        if (MultiplyButton is null || DivideButton is null || PercentButton is null) return;
        var restricted = _selectedValues.Count > 2;
        MultiplyButton.IsEnabled = !restricted;
        DivideButton.IsEnabled = !restricted;
        PercentButton.IsEnabled = !restricted;
        MultiplyButton.ToolTip = restricted ? "For three or more selected values use + or -." : null;
        DivideButton.ToolTip = restricted ? "For three or more selected values use + or -." : null;
        PercentButton.ToolTip = restricted ? "For three or more selected values use + or -." : null;
        Title = restricted
            ? $"Universal Calculator — {_selectedValues.Count} selected (+ / -)"
            : _selectedValues.Count == 2
                ? "Universal Calculator — 2 selected"
                : "Universal Calculator";
    }

    private void Backspace_Click(object sender, RoutedEventArgs e)
    {
        if (IsSelectionMode) ExitSelectionMode();
        var start = ExpressionBox.SelectionStart;
        if (ExpressionBox.SelectionLength > 0)
            ExpressionBox.Text = ExpressionBox.Text.Remove(start, ExpressionBox.SelectionLength);
        else if (start > 0)
        {
            ExpressionBox.Text = ExpressionBox.Text.Remove(start - 1, 1);
            ExpressionBox.SelectionStart = start - 1;
        }
        ExpressionBox.Focus();
    }

    private void Equals_Click(object sender, RoutedEventArgs e)
    {
        ExitSelectionMode();
        RefreshResult(true);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ExitSelectionMode();
        ExpressionBox.Clear();
        ResultText.Text = "0";
        _result = null;
        ExpressionBox.Focus();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result.HasValue)
            Clipboard.SetText(_result.Value.ToString(
                "0.############################",
                CultureInfo.InvariantCulture));
    }

    private void RefreshResult(bool replace)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ExpressionBox.Text))
            {
                ResultText.Text = "0";
                _result = null;
                return;
            }

            var value = new Parser(ExpressionBox.Text).Parse();
            _result = value;
            var formatted = value.ToString(
                "0.############################",
                CultureInfo.InvariantCulture);
            ResultText.Text = formatted;
            if (replace)
            {
                ExpressionBox.Text = formatted;
                ExpressionBox.SelectionStart = ExpressionBox.Text.Length;
            }
        }
        catch
        {
            _result = null;
            ResultText.Text = "…";
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;

        public Parser(string text) => _text = text.Replace('×', '*').Replace('÷', '/');

        public decimal Parse()
        {
            var value = Expression();
            Skip();
            if (_index != _text.Length) throw new FormatException();
            return value;
        }

        private decimal Expression()
        {
            var value = Term();
            while (true)
            {
                Skip();
                if (Take('+')) value += Term();
                else if (Take('-')) value -= Term();
                else return value;
            }
        }

        private decimal Term()
        {
            var value = Factor();
            while (true)
            {
                Skip();
                if (Take('*')) value *= Factor();
                else if (Take('/'))
                {
                    var divisor = Factor();
                    if (divisor == 0) throw new DivideByZeroException();
                    value /= divisor;
                }
                else return value;
            }
        }

        private decimal Factor()
        {
            Skip();
            decimal value;
            if (Take('+')) value = Factor();
            else if (Take('-')) value = -Factor();
            else if (Take('('))
            {
                value = Expression();
                Skip();
                if (!Take(')')) throw new FormatException();
            }
            else value = Number();
            Skip();
            while (Take('%'))
            {
                value /= 100m;
                Skip();
            }
            return value;
        }

        private decimal Number()
        {
            Skip();
            var start = _index;
            var dot = false;
            while (_index < _text.Length)
            {
                var c = _text[_index];
                if (char.IsDigit(c) || c == ',')
                {
                    _index++;
                    continue;
                }
                if (c == '.' && !dot)
                {
                    dot = true;
                    _index++;
                    continue;
                }
                break;
            }
            if (start == _index) throw new FormatException();
            return decimal.Parse(
                _text[start.._index].Replace(",", string.Empty),
                NumberStyles.Number,
                CultureInfo.InvariantCulture);
        }

        private void Skip()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++;
        }

        private bool Take(char c)
        {
            if (_index < _text.Length && _text[_index] == c)
            {
                _index++;
                return true;
            }
            return false;
        }
    }
}
