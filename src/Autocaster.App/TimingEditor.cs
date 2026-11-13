using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Autocaster.Core;

namespace Autocaster.App;

internal sealed class TimingEditor : StackPanel
{
    private readonly CheckBox _random;
    private readonly TextBox _duration;
    private readonly TextBox _min;
    private readonly TextBox _max;
    private readonly Timing _original;
    private readonly bool _compact;
    private readonly bool _hours;
    private int MaximumMs => _hours ? IndexOptions.MaxExtractAfterMs : TimingEngine.MaxMilliseconds;
    private bool _modified;

    internal event Action? Changed;

    internal TimingEditor(Timing timing, string? compactLabel = null, double compactFieldWidth = 155, bool hours = false)
    {
        _original = timing;
        _compact = compactLabel is not null;
        _hours = hours;
        string unit = hours ? "h" : "s";
        int fixedValue = _compact && timing.Random && timing.FixedMs == 0 && timing.MinMs > 0
            ? (timing.MinMs + timing.MaxMs) / 2 : timing.FixedMs;
        int from = timing.Random || timing.MaxMs > 0 ? timing.MinMs : fixedValue;
        int to = timing.Random || timing.MaxMs > 0 ? timing.MaxMs : Math.Min(fixedValue + 100, MaximumMs);

        _random = new CheckBox { Content = "Random", IsChecked = timing.Random, Margin = new(0, 0, 0, 8) };
        _random.SetResourceReference(FrameworkElement.StyleProperty, "EditorCheck");
        string durationLabel = $"Duration ({unit})";
        string fromLabel = $"From ({unit})";
        string toLabel = $"To ({unit})";
        _duration = Number(compactLabel is null ? durationLabel : $"{compactLabel} ({unit})", fixedValue);
        _min = Number(compactLabel is null ? fromLabel : $"{compactLabel} minimum ({unit})", from);
        _max = Number(compactLabel is null ? toLabel : $"{compactLabel} maximum ({unit})", to);
        var fixedPanel = new StackPanel();
        var rangePanel = new StackPanel();
        var bounds = new Grid();
        bounds.ColumnDefinitions.Add(new());
        bounds.ColumnDefinitions.Add(new());
        var minimum = new StackPanel { Margin = new(0, 0, 5, 0) };
        var maximum = new StackPanel { Margin = new(5, 0, 0, 0) };
        Grid.SetColumn(maximum, 1);
        bounds.Children.Add(minimum);
        bounds.Children.Add(maximum);
        rangePanel.Children.Add(bounds);
        if (_compact)
        {
            foreach (var field in new[] { _duration, _min, _max })
            {
                field.Height = 28;
                field.Padding = new(6, 2, 5, 2);
            }
            var dash = Ui.Text("–", 12);
            dash.Margin = new(0);
            dash.HorizontalAlignment = HorizontalAlignment.Center;
            dash.VerticalAlignment = VerticalAlignment.Center;
            dash.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
            Grid.SetColumnSpan(dash, 2);
            bounds.Children.Add(dash);
        }
        AddNumber(fixedPanel, _duration, _compact ? null : durationLabel);
        AddNumber(minimum, _min, _compact ? null : fromLabel);
        AddNumber(maximum, _max, _compact ? null : toLabel);

        if (compactLabel is null)
        {
            Children.Add(_random);
            Children.Add(fixedPanel);
            Children.Add(rangePanel);
        }
        else
            AddCompactRow(compactLabel, fixedPanel, rangePanel, compactFieldWidth);

        void Toggle()
        {
            bool random = _random.IsChecked == true;
            fixedPanel.Visibility = random ? Visibility.Collapsed : Visibility.Visible;
            rangePanel.Visibility = random ? Visibility.Visible : Visibility.Collapsed;
        }
        Toggle();
        _random.Checked += (_, _) => { Toggle(); NotifyChanged(); };
        _random.Unchecked += (_, _) => { Toggle(); NotifyChanged(); };

        _duration.TextChanged += (_, _) => NotifyChanged();
        _min.TextChanged += (_, _) => NotifyChanged();
        _max.TextChanged += (_, _) => NotifyChanged();
    }

    private void AddCompactRow(string label, StackPanel fixedPanel, StackPanel rangePanel, double fieldWidth)
    {
        Margin = new(0, 0, 0, 4);
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(_hours ? 0 : fieldWidth) });
        row.ColumnDefinitions.Add(new() { Width = new(85) });

        var caption = Ui.Text($"{label} ({(_hours ? "h" : "s")})");
        caption.SetResourceReference(TextBlock.ForegroundProperty, "EditorText");
        caption.VerticalAlignment = VerticalAlignment.Center;
        caption.Margin = new(0, 0, 9, 0);
        row.Children.Add(caption);
        var values = new Grid { VerticalAlignment = VerticalAlignment.Center };
        values.Children.Add(fixedPanel);
        values.Children.Add(rangePanel);
        if (_hours)
        {
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            values.Margin = new(0, 4, 0, 0);
            Grid.SetRow(values, 1);
            Grid.SetColumnSpan(values, 3);
        }
        else
            Grid.SetColumn(values, 1);
        row.Children.Add(values);
        _random.Margin = new(10, 0, 0, 0);
        _random.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_random, $"{label} random timing");
        Grid.SetColumn(_random, 2);
        row.Children.Add(_random);
        Children.Add(row);

    }

    internal Timing Read()
    {
        if ((_compact || _hours) && !_modified)
            return _original;

        Timing timing;
        if (_random.IsChecked == true)
        {
            int fixedMs = _compact && TryRead(_duration, out int saved) ? saved : _original.FixedMs;
            timing = new(fixedMs, true, Read(_min, "From"), Read(_max, "To"), EditorConfig.RandomStepMs);
        }
        else
        {
            timing = _original with { FixedMs = Read(_duration, "Duration"), Random = false, StepMs = EditorConfig.RandomStepMs };
            if (_compact && TryRead(_min, out int from) && TryRead(_max, out int to) && from <= to)
                timing = timing with { MinMs = from, MaxMs = to };
        }
        if (_hours && timing.Random && timing.MinMs > timing.MaxMs)
            throw new ArgumentException("From must be less than or equal to To.");
        TimingEngine.Validate(timing, MaximumMs);
        return timing;
    }

    private void NotifyChanged()
    {
        _modified = true;
        Changed?.Invoke();
    }

    private TextBox Number(string name, int value)
    {
        var input = new TextBox
        {
            Text = _hours ? TimingUnits.FormatHours(value) : TimingUnits.Format(value),
            MaxLength = 12, MinHeight = 27, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(input, name);
        return input;
    }

    private static void AddNumber(Panel parent, TextBox input, string? label)
    {
        if (label is not null)
            parent.Children.Add(new Label { Content = label, Target = input, Padding = new(0, 0, 0, 3) });
        parent.Children.Add(input);
    }

    private int Read(TextBox input, string name) => TryRead(input, out int value) ? value :
        throw new ArgumentException(_hours
            ? $"{name} must be between 0 and {TimingUnits.FormatHours(MaximumMs)} hours."
            : $"{name} must be between 0 and {TimingUnits.Format(MaximumMs)} seconds, with up to three decimal places.");

    private bool TryRead(TextBox input, out int value) =>
        TimingUnits.TryRead(input.Text, 0, MaximumMs, _hours ? TimingUnits.MillisecondsPerHour : 1000, out value, round: _hours);

}
