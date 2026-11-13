using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Autocaster.Core;

namespace Autocaster.App;

internal static class MacroWheelSettings
{
    private static readonly string[] Directions =
        ["Up", "Up right", "Right", "Down right", "Down", "Down left", "Left", "Up left"];

    internal static Window Create(Window owner, ImmutableArray<Guid?> assignments, IReadOnlyList<ScenarioProfile> macros,
        bool shortcutConflict, Action<int, Guid?> assign)
    {
        var window = Ui.Dialog(owner, "Q menu", 635, 380);
        var root = new DockPanel { Margin = new(12) };
        var header = Ui.Header("Q menu", Ui.ToolbarButton("Done", window.Close));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        string description = shortcutConflict
            ? "Q is assigned to another shortcut. Change that shortcut to enable the menu."
            : macros.Count == 0 ? "Create a macro to assign it to the menu."
            : "Hold Q in Warframe, move to a sector, then release Q to run its macro.";
        var help = Ui.Text(description, 12);
        help.TextWrapping = TextWrapping.Wrap;
        help.SetResourceReference(TextBlock.ForegroundProperty, shortcutConflict ? "EditorAccentText" : "EditorMuted");
        help.Margin = new(0, 0, 0, 9);
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);

        var wheel = new MacroWheelView { MinWidth = 280, Margin = new(0, 0, 15, 0) };
        AutomationProperties.SetName(wheel, "Q menu preview");
        DockPanel.SetDock(wheel, Dock.Left);
        root.Children.Add(wheel);

        var fields = new Grid();
        fields.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new());
        for (int i = 0; i < Directions.Length; i++)
        {
            int slot = i;
            fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var label = new Label { Content = Directions[slot], Padding = new(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(Control.ForegroundProperty, "EditorText");
            var choice = new ComboBox { MinHeight = 27, Margin = new(0, 2, 0, 2), MaxDropDownHeight = 260 };
            AutomationProperties.SetName(choice, Directions[slot]);
            label.Target = choice;
            choice.Items.Add(new ComboBoxItem { Content = "Unassigned" });
            foreach (var macro in macros)
                choice.Items.Add(new ComboBoxItem
                {
                    Tag = macro.Id,
                    Content = macro.Name + (macro.Macro?.Enabled == true ? "" : " (disabled)")
                });
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, assignments[slot]))
                ?? choice.Items[0];
            choice.SelectionChanged += (_, _) =>
            {
                if (choice.SelectedItem is not ComboBoxItem item)
                    return;
                Guid? selected = item.Tag is Guid id ? id : null;
                assignments = assignments.SetItem(slot, selected);
                assign(slot, selected);
                UpdatePreview();
            };
            Grid.SetRow(label, slot);
            Grid.SetRow(choice, slot);
            Grid.SetColumn(choice, 1);
            fields.Children.Add(label);
            fields.Children.Add(choice);
        }
        root.Children.Add(Ui.Scroll(fields, bottomPadding: 0));
        window.Content = root;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && !fields.Children.OfType<ComboBox>().Any(combo => combo.IsDropDownOpen))
            {
                e.Handled = true;
                window.Close();
            }
        };
        UpdatePreview();
        return window;

        void UpdatePreview() => wheel.SetSlots(Resolve(assignments, macros));
    }

    internal static MacroWheelSlot?[] Resolve(ImmutableArray<Guid?> assignments, IEnumerable<ScenarioProfile> macros)
    {
        var enabled = macros.Where(macro => macro.Macro?.Enabled == true).ToDictionary(macro => macro.Id);
        return assignments.Select(id => id.HasValue && enabled.TryGetValue(id.Value, out var macro)
            ? new MacroWheelSlot(macro.Id, macro.Name) : null).ToArray();
    }
}
