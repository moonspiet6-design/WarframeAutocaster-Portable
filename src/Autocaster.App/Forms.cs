using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using Autocaster.Core;
using Autocaster.Windows;
using MouseButton = Autocaster.Core.MouseButton;
using static Autocaster.App.Words;

namespace Autocaster.App;

internal static class Ui
{
    internal static Button ToolbarButton(string text, Action? action = null, string style = "ToolbarAction")
    {
        var button = new Button { Content = text, Margin = new(0, 0, 6, 5), Padding = new(9, 4, 9, 4), MinHeight = 28 };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (action is not null)
            button.Click += (_, _) => action();
        return button;
    }

    internal static TextBlock Text(string text, double size = 13) =>
        new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 5) };

    internal static ScrollViewer Scroll(UIElement content, double bottomPadding = 10) => new()
    {
        Content = new Border { Padding = new(0, 0, 14, bottomPadding), Child = content },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    internal static void UseBorderlessFrame(Window window, bool resizable = false)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(resizable ? 5 : 0),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
    }

    internal static void HideCursorWhileGameFocused(Window window, Func<bool> gameHasFocus)
    {
        window.AddHandler(UIElement.QueryCursorEvent, new QueryCursorEventHandler((_, args) =>
        {
            if (!gameHasFocus())
                return;
            args.Cursor = Cursors.None;
            args.Handled = true;
        }), handledEventsToo: true);
    }

    internal static DockPanel Header(string title, params UIElement[] right)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 5) };
        foreach (var item in right)
        {
            DockPanel.SetDock(item, Dock.Right);
            row.Children.Add(item);
        }

        var label = Text(title, 17);
        label.Background = Brushes.Transparent;
        label.FontWeight = FontWeights.SemiBold;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        return row;
    }

    internal static void EnableSurfaceDrag(Window window, Func<bool>? allowed = null)
    {
        Point? origin = null;
        window.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (allowed?.Invoke() == false || (args.OriginalSource is DependencyObject surface &&
                (Ancestor<ListBox>(surface) is not null || Ancestor<MousePathPreview>(surface) is not null ||
                    Ancestor<MacroHudView>(surface) is not null)))
            {
                origin = null;
                return;
            }
            bool force = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            origin = force || args.OriginalSource is DependencyObject hit && IsDragSurface(hit)
                ? args.GetPosition(window) : null;
            if (force)
                args.Handled = true;
        };
        window.PreviewMouseMove += (_, args) =>
        {
            if (allowed?.Invoke() == false)
            {
                origin = null;
                return;
            }
            if (origin is not Point point || args.LeftButton != MouseButtonState.Pressed)
                return;
            var distance = args.GetPosition(window) - point;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            origin = null;
            args.Handled = true;
            window.DragMove();
        };
        window.PreviewMouseLeftButtonUp += (_, _) => origin = null;
    }

    internal static T? Ancestor<T>(DependencyObject? hit) where T : DependencyObject
    {
        while (hit is not null)
        {
            if (hit is T found)
                return found;
            hit = hit is Visual or Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return null;
    }

    private static bool IsDragSurface(DependencyObject? hit)
    {
        while (hit is not null && hit is not Window)
        {
            if (hit is ButtonBase or TextBoxBase or ComboBox or Slider or Thumb or ScrollBar or ListBoxItem or MenuItem or TabItem or Expander)
                return false;
            hit = hit is Visual or Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return true;
    }

    internal static Window Dialog(Window owner, string title, int width, int height)
    {
        var window = new Window
        {
            Owner = owner, Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Topmost = owner.Topmost
        };
        window.SetResourceReference(Window.BackgroundProperty, "EditorBackground");
        window.SetResourceReference(Window.ForegroundProperty, "EditorText");
        window.UseLayoutRounding = true;
        UseBorderlessFrame(window);
        EnableSurfaceDrag(window);
        return window;
    }

    internal static bool Confirm(Window owner, string question) =>
        MessageBox.Show(owner, question, "Warframe Autocaster", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}

internal class Form : StackPanel
{
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed };
    private Action? _apply;
    internal bool Dirty { get; private set; }
    internal event Action? Edited;

    internal void MarkDirty()
    {
        Dirty = true;
        Edited?.Invoke();
    }

    internal void Heading(string text)
    {
        var heading = Ui.Text(text, 16);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new(0, 9, 0, 7);
        Children.Add(heading);
    }

    internal void Label(string text) => Children.Add(Ui.Text(text));

    internal Func<string> TextField(string name, string value, int maxLength = 150, bool multiline = false)
    {
        var input = new TextBox
        {
            Text = value, MaxLength = maxLength, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 52 : 27,
            Margin = new(0, 0, 0, 7), VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(input, name);
        AddField(name, input);
        input.TextChanged += (_, _) => MarkDirty();
        return () => input.Text;
    }

    internal Func<int> Seconds(string label, int value, int min, int max, int ticksPerSecond = 1000)
    {
        var text = TextField(label, TimingUnits.Format(value, ticksPerSecond), 16);
        return () => TimingUnits.TryRead(text(), min, max, ticksPerSecond, out int ticks) ? ticks :
            throw new ArgumentException($"{label}: enter a value between {TimingUnits.Format(min, ticksPerSecond)} and {TimingUnits.Format(max, ticksPerSecond)}.");
    }

    internal CheckBox Check(string name, bool value, Panel? panel = null)
    {
        var input = new CheckBox { Content = name, IsChecked = value, FocusVisualStyle = null,
            Margin = panel is null ? new(0, 0, 0, 8) : new(0, 0, 10, 4) };
        input.SetResourceReference(FrameworkElement.StyleProperty, "EditorCheck");
        (panel ?? this).Children.Add(input);
        input.Checked += (_, _) => MarkDirty();
        input.Unchecked += (_, _) => MarkDirty();
        return input;
    }

    internal Func<T> Choice<T>(string label, T current, IEnumerable<T> values, Func<T, string>? display = null) where T : notnull
    {
        var combo = new ComboBox { MinHeight = 27, Margin = new(0, 0, 0, 7), MaxDropDownHeight = 320 };
        foreach (var value in values)
            combo.Items.Add(new ComboBoxItem { Content = display?.Invoke(value) ?? value.ToString(), Tag = value });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, current));
        AddField(label, combo);
        combo.SelectionChanged += (_, _) => MarkDirty();
        return () => combo.SelectedItem is ComboBoxItem { Tag: T chosen }
            ? chosen : throw new ArgumentException($"{label} — select a value.");
    }

    internal Func<Timing> Time(string name, Timing value, bool inline = false, bool hours = false)
    {
        var input = new TimingEditor(value, hours: hours);
        var group = inline ? null : new Expander { Header = $"{name} · {TimingUnits.Format(value, hours)}", Content = input, Margin = new(0, 0, 0, 6) };
        if (group is not null)
            input.Margin = new(12, 6, 0, 0);
        Children.Add((UIElement?)group ?? input);
        input.Changed += MarkDirty;
        return () =>
        {
            try
            {
                return input.Read();
            }
            catch (Exception error) when (error is ArgumentException or ScenarioException)
            {
                if (group is not null)
                    group.IsExpanded = true;
                throw new ScenarioException($"{name}: {error.Message}");
            }
        };
    }

    internal Func<Timing> CompactTime(string name, Timing value, double fieldWidth = 155, bool hours = false)
    {
        var input = new TimingEditor(value, compactLabel: name, compactFieldWidth: fieldWidth, hours: hours);
        Children.Add(input);
        input.Changed += MarkDirty;
        return () =>
        {
            try { return input.Read(); }
            catch (Exception error) when (error is ArgumentException or ScenarioException)
            {
                throw new ScenarioException($"{name} — {error.Message}");
            }
        };
    }

    internal void SetApply(Action apply)
    {
        _apply = apply;
        Children.Add(_error);
    }

    internal bool TryApply()
    {
        if (!Dirty)
            return true;
        try
        {
            _apply?.Invoke();
            _error.Text = "";
            _error.Visibility = Visibility.Collapsed;
            Dirty = false;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or ScenarioException or InvalidOperationException)
        {
            _error.Text = error.Message;
            _error.Visibility = Visibility.Visible;
            return false;
        }
    }

    internal void AddField(string label, Control control)
    {
        var caption = new Label { Content = label, Target = control, Padding = new(0, 0, 0, 3) };
        Children.Add(caption);
        Children.Add(control);
    }
}

internal static class EditorForms
{
    internal static Form ActionForm(ScenarioAction action, InputRecorder recorder, Action<ScenarioAction> apply,
        Action<Guid>? armRecording = null, bool inline = false, bool macro = false, MacroTimerOptions? timer = null,
        bool followsHudGate = false)
    {
        var form = new Form();
        if (action.Kind == ActionKind.RathuumKillFeed)
            return RathuumForm(form, action, apply, inline);
        if (action.Kind == ActionKind.MouseMovement)
            return MouseMovementForm(form, action, apply, armRecording, inline, macro && !followsHudGate);
        if (!inline)
            form.Heading(Action(action.Kind));
        var enabled = action.Kind == ActionKind.Wait ? null : form.Check("Enabled", action.Enabled);

        InputKey key = action.Key;
        MouseButton button = action.Button;
        Func<Timing> hold = () => action.Hold;
        Func<Timing> delay = () => action.Delay;
        TextBox? timerDuration = null;
        if (action.Kind == ActionKind.StartMacroTimer)
        {
            var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 4) };
            timerDuration = new TextBox { Text = TimingUnits.Format(action.MacroTimerDurationMs ??
                (timer ?? new()).DurationMs), Width = 72, MinHeight = 27, MaxLength = 16,
                VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            var caption = new Label { Content = "Duration (s)", Target = timerDuration, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(timerDuration, "Timer duration (s)");
            timerDuration.TextChanged += (_, _) => form.MarkDirty();
            fields.Children.Add(caption);
            fields.Children.Add(timerDuration);
            form.Children.Add(fields);
        }

        if (action.Kind is ActionKind.KeyPress or ActionKind.KeyHold)
            form.AddField("Key", recorder.KeyField("Assign key", () => Key(key), picked =>
            {
                key = picked;
                form.MarkDirty();
            }));
        if (action.Kind == ActionKind.MouseClick)
            form.AddField("Mouse button", recorder.MouseField("Assign mouse button", () => Mouse(button), picked =>
            {
                button = picked;
                form.MarkDirty();
            }));
        if (action.Kind == ActionKind.KeyHold)
        {
            form.Label("Release after");
            hold = form.Time("Release after", action.Hold, inline: true);
        }
        else if (action.Kind == ActionKind.MouseClick)
            hold = form.Time("Hold duration", action.Hold);
        if (action.Kind == ActionKind.Wait)
            delay = inline ? form.CompactTime("Time", action.Delay, fieldWidth: 115) : form.Time("Delay", action.Delay, inline: true);
        var larvaTimeout = action.Kind == ActionKind.LarvaDetect
            ? form.Seconds("Timeout (s)", action.LarvaTimeoutMs ?? HudGate.DefaultTimeoutMs,
                HudGate.MinTimeoutMs, HudGate.MaxTimeoutMs) : null;
        var automatic = macro && !followsHudGate && action.Kind is not (ActionKind.Wait or ActionKind.LarvaDetect)
            ? form.Check("Run after previous", action.RunWithPrevious) : null;
        if (inline)
        {
            form.MaxWidth = 390;
            form.HorizontalAlignment = HorizontalAlignment.Left;
            form.MinWidth = 250;
        }
        form.SetApply(() =>
        {
            int? duration = action.MacroTimerDurationMs;
            if (timerDuration is not null)
            {
                if (!TimingUnits.TryRead(timerDuration.Text.Replace(',', '.'), 1, TimingEngine.MaxMilliseconds, 1000, out int milliseconds))
                    throw new ArgumentException("Enter a timer duration from 0.001 to 3600 seconds.");
                duration = milliseconds;
            }
            var edited = action with
            {
                Enabled = enabled is null || enabled.IsChecked == true,
                RunWithPrevious = automatic?.IsChecked ?? action.RunWithPrevious,
                Key = key, Button = button, Hold = hold(), Delay = delay(), MacroTimerDurationMs = duration,
                LarvaTimeoutMs = larvaTimeout?.Invoke() ?? action.LarvaTimeoutMs
            };
            ScenarioCompiler.ValidateAction(edited);
            apply(edited);
        });
        return form;
    }

    private static Form RathuumForm(Form form, ScenarioAction action, Action<ScenarioAction> apply, bool inline)
    {
        var settings = action.Rathuum ?? new();
        if (!inline)
            form.Heading("Rathuum kill feed");
        form.MaxWidth = 390;
        form.MinWidth = 250;
        form.HorizontalAlignment = HorizontalAlignment.Left;
        var enabled = form.Check("Enabled", action.Enabled);
        var firstOnly = form.Check("Trigger on first executioner kill", settings.FirstKillOnly);
        var correctionFields = new Form();
        var leadFields = new Form();
        correctionFields.Edited += form.MarkDirty;
        leadFields.Edited += form.MarkDirty;
        form.Children.Add(correctionFields);
        form.Children.Add(leadFields);
        var correction = correctionFields.Seconds("Read delay correction (ms)", settings.ReadCorrectionMs, 0, 5000, ticksPerSecond: 1);
        var lead = leadFields.Seconds("Launch lead (ms)", settings.FirstKillLeadMs, 1, 5000, ticksPerSecond: 1);
        var uncertainty = leadFields.Seconds("Score timing preference (ms)", settings.MaxLaunchUncertaintyMs, 1, 1000, ticksPerSecond: 1);
        firstOnly.Checked += (_, _) => RefreshTimingFields();
        firstOnly.Unchecked += (_, _) => RefreshTimingFields();
        RefreshTimingFields();

        void RefreshTimingFields()
        {
            correctionFields.Visibility = firstOnly.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            leadFields.Visibility = firstOnly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        var respawn = form.Seconds("Respawn (s)", settings.RespawnMs, 1000, 60_000);
        var timeout = form.Seconds("Timeout (s)", settings.TimeoutMs, 100, 600_000);
        var baseline = form.Check("Use current team score", settings.StartAtCurrentScore);

        var advanced = new Form { Margin = new(8, 6, 8, 4) };
        advanced.Edited += form.MarkDirty;
        form.Children.Add(new Expander { Header = "Capture and calibration", Content = advanced, Margin = new(0, 3, 0, 6) });
        var compact = advanced.Check("Compact HUD", settings.CompactHud);
        var guard = advanced.Check("Reduce scanning below 240 FPS", settings.FpsGuard);
        var scan = advanced.Seconds("Scan limit (Hz)", settings.ScanHz, 30, 144, ticksPerSecond: 1);
        var spread = advanced.Seconds("Respawn uncertainty (ms)", settings.RespawnUncertaintyMs, 0, 1000, ticksPerSecond: 1);
        var score = advanced.Seconds("Score display delay (ms)", settings.ScoreDelayMs, 0, 1000, ticksPerSecond: 1);
        var feedMin = advanced.Seconds("Earliest feed appearance (ms)", settings.FeedDelayMinMs, 0, 10_000, ticksPerSecond: 1);
        var feedMax = advanced.Seconds("Latest feed appearance (ms)", settings.FeedDelayMaxMs, 0, 10_000, ticksPerSecond: 1);
        var late = advanced.Seconds("Late feed cutoff (ms)", settings.LateFeedDelayMs, 0, 10_000, ticksPerSecond: 1);
        var fadeMin = advanced.Seconds("Earliest feed disappearance (ms)", settings.FadeDelayMinMs, 0, 30_000, ticksPerSecond: 1);
        var fadeMax = advanced.Seconds("Latest feed disappearance (ms)", settings.FadeDelayMaxMs, 0, 30_000, ticksPerSecond: 1);

        form.SetApply(() =>
        {
            var updated = settings with
            {
                FirstKillOnly = firstOnly.IsChecked == true,
                ReadCorrectionMs = firstOnly.IsChecked == true ? settings.ReadCorrectionMs : correction(),
                FirstKillLeadMs = firstOnly.IsChecked == true ? lead() : settings.FirstKillLeadMs,
                MaxLaunchUncertaintyMs = firstOnly.IsChecked == true ? uncertainty() : settings.MaxLaunchUncertaintyMs,
                RespawnMs = respawn(), TimeoutMs = timeout(),
                StartAtCurrentScore = baseline.IsChecked == true, CompactHud = compact.IsChecked == true,
                FpsGuard = guard.IsChecked == true, ScanHz = scan(), RespawnUncertaintyMs = spread(),
                ScoreDelayMs = score(), FeedDelayMinMs = feedMin(),
                FeedDelayMaxMs = feedMax(), LateFeedDelayMs = late(), FadeDelayMinMs = fadeMin(), FadeDelayMaxMs = fadeMax()
            };
            updated.Validate();
            apply(action with { Enabled = enabled.IsChecked == true, Rathuum = updated });
        });
        return form;
    }

    private static Form MouseMovementForm(Form form, ScenarioAction action, Action<ScenarioAction> apply,
        Action<Guid>? armRecording, bool inline, bool macro)
    {
        Panel options = inline ? new StackPanel { Margin = new(0, 0, 10, 0) } : new WrapPanel();
        var header = new DockPanel { Margin = new(0, 0, 0, 4) };
        var enabled = form.Check("Enabled", action.Enabled, inline ? header : options);
        if (inline)
        {
            DockPanel.SetDock(enabled, Dock.Right);
            var heading = Ui.Text("Step settings");
            heading.FontWeight = FontWeights.SemiBold;
            heading.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(heading);
            form.Children.Add(header);
        }
        else
        {
            form.Heading(Action(action.Kind));
            form.Children.Add(options);
        }
        var reverse = form.Check("Reverse", action.ReverseMousePath, options);
        var vary = form.Check("Vary route", action.VaryMousePath, options);
        var automatic = macro ? form.Check("Run after previous", action.RunWithPrevious, options) : null;
        foreach (var option in new[] { enabled, reverse, vary })
        {
            option.MinWidth = 0;
            option.MinHeight = inline ? 23 : 26;
            option.Margin = new(0, 0, 8, 2);
        }

        var controls = new DockPanel { Margin = new(0, 3, 0, 2) };
        var record = Ui.ToolbarButton("Record · Insert", () => armRecording?.Invoke(action.Id), "ActiveToolbarAction");
        record.Padding = new(5, 4, 5, 4);
        if (!inline)
        {
            DockPanel.SetDock(record, Dock.Left);
            controls.Children.Add(record);
        }
        var duration = new TextBox { MinWidth = 65, Height = 27, Padding = new(5, 0, 5, 0),
            Text = action.MouseDurationMs is int milliseconds ? TimingUnits.Format(milliseconds) : "",
            Margin = new(0, 0, 0, 5), VerticalContentAlignment = VerticalAlignment.Center };
        duration.SetResourceReference(FrameworkElement.StyleProperty, "DefaultTextBoxBaseStyle");
        var label = new Label { Content = "Time (s)", Target = duration, VerticalAlignment = VerticalAlignment.Center,
            Padding = new(2, 0, 5, 5) };
        DockPanel.SetDock(label, Dock.Left);
        controls.Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetName(duration, "Movement time (s)");
        duration.TextChanged += (_, _) => form.MarkDirty();
        controls.Children.Add(duration);
        var path = action.MousePath;
        var route = new StackPanel();
        var caption = Ui.Text(path.IsEmpty ? "No recording" : $"Recorded route  ·  {TimingUnits.Format(path[^1].OffsetMs)} s", 12);
        caption.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
        if (inline)
        {
            var routeHeader = new WrapPanel();
            record.Margin = new(0, 0, 0, 5);
            caption.Margin = new(0, 0, 7, 5);
            caption.VerticalAlignment = VerticalAlignment.Center;
            routeHeader.Children.Add(caption);
            routeHeader.Children.Add(record);
            route.Children.Add(routeHeader);
            controls.MaxWidth = 160;
            controls.HorizontalAlignment = HorizontalAlignment.Left;
            options.Children.Insert(0, controls);
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(137) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(options);
            Grid.SetColumn(route, 1);
            columns.Children.Add(route);
            form.Children.Add(columns);
        }
        else
        {
            form.Children.Add(controls);
            form.Children.Add(route);
        }
        if (!path.IsEmpty)
        {
            var preview = new MousePathPreview(path, edited =>
            {
                path = edited;
                form.MarkDirty();
            }) { Height = inline ? 71 : 85 };
            var routeFrame = new Border { Child = preview, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
            routeFrame.SetResourceReference(Border.BackgroundProperty, "EditorBackground");
            routeFrame.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
            route.Children.Add(routeFrame);
            if (!inline)
                route.Children.Add(caption);
        }
        form.SetApply(() =>
        {
            int? milliseconds = null;
            if (!string.IsNullOrWhiteSpace(duration.Text))
            {
                if (!TimingUnits.TryRead(duration.Text.Replace(',', '.'), 1, TimingEngine.MaxMilliseconds, 1000, out int parsed))
                    throw new ArgumentException("Enter 0.001–3600 seconds, with up to three decimal places.");
                milliseconds = parsed;
            }
            apply(action with
            {
                Enabled = enabled.IsChecked == true,
                RunWithPrevious = automatic?.IsChecked ?? action.RunWithPrevious,
                ReverseMousePath = reverse.IsChecked == true,
                VaryMousePath = vary.IsChecked == true,
                MouseDurationMs = milliseconds,
                MouseSpeed = milliseconds is null && path.IsEmpty ? action.MouseSpeed : 1,
                MousePath = path
            });
        });
        return form;
    }

    internal static Form ProfileForm(ScenarioProfile profile, Action<ScenarioProfile> apply, Action? openMovementMap = null)
    {
        var form = new Form();
        form.Heading("Config settings");
        var name = form.TextField("Name", profile.Name);
        var index = profile.Index ?? new();
        var gap = index.Nokko is { } rotation ? form.CompactTime("Cast gap", rotation.CastGap) : null;
        var choice = new ComboBox { MinHeight = 27, Margin = new(0, 0, 0, 7) };
        choice.Items.Add("Manual");
        choice.Items.Add("Always stay");
        choice.Items.Add("Stay until timer, then cash in");
        choice.SelectedIndex = (int)index.Vote;
        form.AddField("Index round choice", choice);

        var extraction = new Form();
        var after = extraction.Time("Cash in at the next vote after", index.ExtractAfter, hours: true);
        extraction.Edited += form.MarkDirty;
        extraction.Visibility = index.Vote == IndexVoteMode.ExtractOnTimer ? Visibility.Visible : Visibility.Collapsed;
        form.Children.Add(extraction);
        choice.SelectionChanged += (_, _) =>
        {
            extraction.Visibility = choice.SelectedIndex == (int)IndexVoteMode.ExtractOnTimer
                ? Visibility.Visible : Visibility.Collapsed;
            form.MarkDirty();
        };

        var loop = index.Nokko is null ? form.Check("Loop", profile.Mode == CycleMode.Infinite) : null;
        AddMovementMap(form, profile, openMovementMap);
        form.SetApply(() =>
        {
            var vote = (IndexVoteMode)choice.SelectedIndex;
            var options = index with
            {
                Vote = vote,
                ExtractAfter = vote == IndexVoteMode.ExtractOnTimer ? after() : index.ExtractAfter,
                Nokko = index.Nokko is { } nokko && gap is not null ? nokko with { CastGap = gap() } : null
            };
            apply(profile with
            {
                Name = name().Trim(),
                Mode = loop is null ? profile.Mode : loop.IsChecked == true ? CycleMode.Infinite :
                    profile.Mode == CycleMode.Count ? CycleMode.Count : CycleMode.Once,
                Index = options.WatchesRound || profile.Index is not null ? options : null
            });
            index = options;
        });
        return form;
    }

    internal static bool HasMovementMap(ScenarioProfile profile) => profile.Index?.Nokko is null &&
        profile.Actions.Where(action => action.Kind == ActionKind.MouseMovement).Take(2).Count() == 2;

    private static void AddMovementMap(Form form, ScenarioProfile profile, Action? open)
    {
        if (open is not null && HasMovementMap(profile))
        {
            var button = Ui.ToolbarButton("Movement map", open);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            form.Children.Add(button);
        }
    }
}
