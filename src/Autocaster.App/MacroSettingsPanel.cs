using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Autocaster.Core;
using Autocaster.Windows;

namespace Autocaster.App;

internal sealed class MacroSettingsPanel : Form
{
    private readonly TextBox _name;
    private readonly CheckBox _enabled;
    private readonly CheckBox _step;
    private readonly CheckBox _useHotkey;
    private readonly Button _shortcut;
    private readonly Button _reset;
    private readonly MacroHudSettings _hud;
    private readonly Border _mapRow;
    private readonly TextBlock _routeCount;
    private readonly bool _canOpenMap;
    private Hotkey _key;
    private Hotkey? _resetKey;
    private bool _refreshing;

    internal MacroSettingsPanel(Func<ScenarioProfile> current, Action<ScenarioProfile> apply, InputRecorder recorder,
        Hotkey? reset, Func<Hotkey, string?> applyReset, Action<HudPart>? placeHud = null, Action? openMap = null)
    {
        var profile = current();
        var macro = profile.Macro!;
        _key = Shortcut(macro);
        _resetKey = reset;
        _canOpenMap = openMap is not null;

        var header = new DockPanel { Margin = new(10, 6, 10, 6) };
        _enabled = Toggle("Enabled", macro.Enabled);
        DockPanel.SetDock(_enabled, Dock.Right);
        header.Children.Add(_enabled);
        var title = Ui.Text("Macro settings", 15);
        title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new(0, 0, 8, 0);
        title.SetResourceReference(TextBlock.ForegroundProperty, "EditorText");
        header.Children.Add(title);
        Children.Add(header);

        var general = new StackPanel { Margin = new(10, 0, 10, 7) };
        var fields = new Grid { Margin = new(0, 0, 0, 6) };
        fields.ColumnDefinitions.Add(new() { Width = new(1.12, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _name = new TextBox { Text = profile.Name, MaxLength = 150, MinHeight = 29, VerticalContentAlignment = VerticalAlignment.Center };
        _name.SetResourceReference(StyleProperty, "DefaultTextBoxBaseStyle");
        AutomationProperties.SetName(_name, "Macro name");
        _name.TextChanged += (_, _) => EditedField();
        var nameField = Field("Name", _name);
        nameField.Margin = new(0, 0, 8, 0);
        fields.Children.Add(nameField);

        _shortcut = recorder.ShortcutField("Hotkey", () => _key, key => { _key = key; EditedField(); }, "Press shortcut…");
        StyleShortcut(_shortcut);
        var hotkeyField = Field("Hotkey", _shortcut);
        Grid.SetColumn(hotkeyField, 1);
        fields.Children.Add(hotkeyField);
        general.Children.Add(fields);

        var toggles = new WrapPanel { Margin = new(0, 0, 0, 5) };
        _step = Toggle("Step mode", macro.Step);
        _step.Margin = new(0, 0, 12, 0);
        _useHotkey = Toggle("Direct hotkey", macro.UseHotkey);
        _shortcut.IsEnabled = macro.UseHotkey;
        _useHotkey.Checked += (_, _) => _shortcut.IsEnabled = true;
        _useHotkey.Unchecked += (_, _) => _shortcut.IsEnabled = false;
        toggles.Children.Add(_step);
        toggles.Children.Add(_useHotkey);
        general.Children.Add(toggles);

        var resetRow = new DockPanel();
        var error = Ui.Text("", 12);
        error.Foreground = Brushes.IndianRed;
        error.Margin = new(0, 5, 0, 0);
        error.Visibility = Visibility.Collapsed;
        _reset = recorder.ShortcutField("Reset macro steps", () => _resetKey, key =>
        {
            // The recorder restores the existing registrations before the new shortcut is applied.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                var problem = applyReset(key);
                if (problem is null)
                    SyncReset(key);
                error.Text = problem ?? "";
                error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            });
        }, "Press shortcut…");
        StyleShortcut(_reset);
        _reset.Width = 145;
        _reset.Margin = new(8, 0, 0, 0);
        DockPanel.SetDock(_reset, Dock.Right);
        resetRow.Children.Add(_reset);
        var resetLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        resetLabel.Children.Add(Caption("Reset all steps", size: 13));
        resetLabel.Children.Add(Caption("Global shortcut"));
        resetRow.Children.Add(resetLabel);
        general.Children.Add(resetRow);
        general.Children.Add(error);
        Children.Add(Section("General", general));

        _hud = new MacroHudSettings(macro, EditedField, placeHud) { Margin = new(10, 0, 10, 8) };
        Children.Add(Section("In-game HUD", _hud));

        var mapLabel = new DockPanel();
        var chevron = new TextBlock { Text = "›", Margin = new(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(chevron, Dock.Right);
        mapLabel.Children.Add(chevron);
        _routeCount = Caption("");
        _routeCount.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_routeCount, Dock.Right);
        mapLabel.Children.Add(_routeCount);
        mapLabel.Children.Add(new TextBlock { Text = "Movement map", VerticalAlignment = VerticalAlignment.Center });
        var map = Ui.ToolbarButton("Movement map", openMap);
        map.Margin = new(0);
        map.Content = mapLabel;
        map.SetResourceReference(StyleProperty, "MacroSettingsMap");
        AutomationProperties.SetName(map, "Movement map");
        _mapRow = new Border { Child = map, Padding = new(10, 7, 10, 7), BorderThickness = new(0, 1, 0, 0) };
        _mapRow.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        Children.Add(_mapRow);
        UpdateRoutes(profile);

        SetApply(() =>
        {
            var latest = current();
            apply(latest with
            {
                Name = _name.Text.Trim(),
                Macro = _hud.Read(latest.Macro!) with
                {
                    Key = _key.Key, Modifiers = (uint)_key.Modifiers,
                    Enabled = _enabled.IsChecked == true, Step = _step.IsChecked == true,
                    UseHotkey = _useHotkey.IsChecked == true
                }
            });
        });
    }

    internal void Refresh(ScenarioProfile profile, bool updateFields)
    {
        UpdateRoutes(profile);
        if (!updateFields || Dirty || profile.Macro is not { } macro)
            return;
        _refreshing = true;
        try
        {
            if (_name.Text.Trim() != profile.Name)
                _name.Text = profile.Name;
            if (_key != Shortcut(macro))
            {
                _key = Shortcut(macro);
                _shortcut.Content = _key.ToString();
            }
            _enabled.IsChecked = macro.Enabled;
            _step.IsChecked = macro.Step;
            _useHotkey.IsChecked = macro.UseHotkey;
            _hud.Refresh(macro);
        }
        finally { _refreshing = false; }
    }

    internal void SyncReset(Hotkey? shortcut)
    {
        _resetKey = shortcut;
        _reset.Content = shortcut?.ToString() ?? "Unassigned";
    }

    private void UpdateRoutes(ScenarioProfile profile)
    {
        _mapRow.Visibility = _canOpenMap && EditorForms.HasMovementMap(profile) ? Visibility.Visible : Visibility.Collapsed;
        if (_mapRow.Visibility == Visibility.Visible)
            _routeCount.Text = $"{profile.Actions.Count(action => action.Kind == ActionKind.MouseMovement)} routes";
    }

    private void EditedField()
    {
        if (!_refreshing)
            MarkDirty();
    }

    private CheckBox Toggle(string name, bool value)
    {
        var check = new CheckBox { Content = name, IsChecked = value, FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center };
        check.SetResourceReference(StyleProperty, "EditorCheck");
        check.Checked += (_, _) => EditedField();
        check.Unchecked += (_, _) => EditedField();
        return check;
    }

    private static Hotkey Shortcut(MacroOptions macro) => new((HotkeyModifiers)macro.Modifiers, macro.Key);

    private static TextBlock Caption(string text, double size = 12)
    {
        var caption = Ui.Text(text, size);
        caption.Margin = new(0);
        caption.SetResourceReference(TextBlock.ForegroundProperty, size == 12 ? "EditorMuted" : "EditorText");
        return caption;
    }

    private static StackPanel Field(string label, Control control)
    {
        var field = new StackPanel();
        var caption = new Label { Content = label, Target = control, Padding = new(0, 0, 0, 3), FontSize = 12 };
        caption.SetResourceReference(Control.ForegroundProperty, "EditorMuted");
        field.Children.Add(caption);
        field.Children.Add(control);
        return field;
    }

    private static void StyleShortcut(Button button)
    {
        button.MinWidth = 0;
        button.MinHeight = 29;
        button.Padding = new(6, 3, 6, 3);
        button.SetResourceReference(Control.ForegroundProperty, "EditorAccentText");
        button.SetBinding(ToolTipProperty, new Binding(nameof(ContentControl.Content)) { Source = button });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        button.ContentTemplate = new DataTemplate { VisualTree = text };
    }

    private static Expander Section(string title, UIElement body)
    {
        var section = new Expander { Header = title, Content = body, IsExpanded = true };
        section.SetResourceReference(StyleProperty, "MacroSettingsSection");
        return section;
    }
}
