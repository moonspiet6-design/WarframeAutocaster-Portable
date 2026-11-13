using System;
using System.Windows;
using System.Windows.Controls;
using Autocaster.Core;

namespace Autocaster.App;

internal sealed class MacroHudSettings : Form
{
    private readonly CheckBox _step;
    private readonly CheckBox _rathuum;
    private readonly CheckBox _arrow;
    private readonly CheckBox _timer;

    internal MacroHudSettings(MacroOptions options, Action edited, Action<HudPart>? place)
    {
        _step = Row("Step HUD", options.ShowStepHud, HudPart.Step, "Place Step HUD");
        _rathuum = Check("Show Rathuum wave size", options.ShowRathuumWaveSize);
        _rathuum.Margin = new(16, 2, 0, 5);
        _rathuum.IsEnabled = options.ShowStepHud;
        _step.Checked += (_, _) => _rathuum.IsEnabled = true;
        _step.Unchecked += (_, _) => _rathuum.IsEnabled = false;
        _arrow = Row("Next route arrow", options.ShowNextMovement, HudPart.Arrow, "Place next route arrow");
        _timer = Row("Timer", options.ShowTimer, HudPart.Timer, "Place timer");
        Edited += edited;

        CheckBox Row(string label, bool value, HudPart part, string buttonName)
        {
            var row = new DockPanel { Margin = new(0, 1, 0, 1) };
            if (place is not null)
            {
                var button = Ui.ToolbarButton("Place", () => place(part));
                button.Margin = new(6, 0, 0, 0);
                button.MinHeight = 25;
                button.Padding = new(8, 2, 8, 2);
                System.Windows.Automation.AutomationProperties.SetName(button, buttonName);
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
            }
            var check = Check(label, value, row);
            check.Margin = new(0);
            check.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(row);
            return check;
        }
    }

    internal void Refresh(MacroOptions options)
    {
        _step.IsChecked = options.ShowStepHud;
        _rathuum.IsChecked = options.ShowRathuumWaveSize;
        _arrow.IsChecked = options.ShowNextMovement;
        _timer.IsChecked = options.ShowTimer;
    }

    internal MacroOptions Read(MacroOptions options)
    {
        return options with
        {
            ShowStepHud = _step.IsChecked == true,
            ShowRathuumWaveSize = _rathuum.IsChecked == true,
            ShowNextMovement = _arrow.IsChecked == true,
            ShowTimer = _timer.IsChecked == true
        };
    }
}
