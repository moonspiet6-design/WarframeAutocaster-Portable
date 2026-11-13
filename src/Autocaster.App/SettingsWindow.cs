using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Autocaster.Core;

namespace Autocaster.App;

internal static class SettingsWindow
{
    internal static Window Create(Window owner, Preferences settings, string logs, Action<bool> capture,
        Func<Preferences, string?> apply, Action<string> report)
    {
        var window = Ui.Dialog(owner, "Settings", 450, 350);
        var tabs = new TabControl();
        var root = new DockPanel { Margin = new(12) };
        var header = Ui.Header("Settings", Ui.ToolbarButton("Back", window.Close));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(tabs);
        window.Content = root;

        Form Tab(string title)
        {
            var form = new Form { Margin = new(10) };
            var item = new TabItem { Header = title, Content = Ui.Scroll(form) };
            item.SetResourceReference(FrameworkElement.StyleProperty, "EditorSettingsTab");
            tabs.Items.Add(item);
            return form;
        }

        var hotkeys = Tab("Hotkeys");
        hotkeys.Label("Click a field, then press a key, click a mouse button, or scroll.");
        var recorder = new InputRecorder(window, capture);
        var editorShortcut = recorder.AddShortcut(hotkeys, "Show / hide editor", settings.GetEditorHotkey());
        var toggleRun = recorder.AddShortcut(hotkeys, "Start / stop", settings.Hotkeys[0]);
        var resetSteps = recorder.AddShortcut(hotkeys, "Reset macro steps", settings.MacroResetHotkey);

        var waveSize = recorder.AddShortcut(hotkeys, "Toggle Rathuum wave size (5 / 8)", settings.RathuumWaveHotkey);

        var playbackReset = recorder.AddShortcut(hotkeys, "Reset playback and timers", settings.PlaybackResetHotkey);

        var general = Tab("General");
        var dark = general.Check("Dark theme", settings.Dark);
        var protection = general.Check("Capture protection", settings.CaptureProtection);
        var startInTray = general.Check("Start in tray", settings.StartInTray);
        var restore = general.Check("Restore the last config", settings.RestoreProfile);
        var activation = general.Check("Activate Warframe when starting", settings.ActivateGame);

        var timing = Tab("Timing");
        var hold = timing.Seconds("Default timed hold (s)", settings.DefaultHoldMs, 0, TimingEngine.MaxMilliseconds);
        var delay = timing.Seconds("Default delay (s)", settings.DefaultWaitMs, 0, TimingEngine.MaxMilliseconds);
        var threshold = timing.Seconds("Late event threshold (s)", settings.Timing.ThresholdMs, 1, 60000);
        var late = timing.Choice("Late events", settings.Timing.Policy == LatePolicy.SkipWheel ? LatePolicy.Dispatch : settings.Timing.Policy,
            new[] { LatePolicy.Dispatch, LatePolicy.Stop }, value => value == LatePolicy.Dispatch
                ? "Send without shifting the schedule" : "Stop the scenario");
        var spin = timing.Seconds("Precise wait before an event (s)", settings.SpinMicroseconds, 0, 500, ticksPerSecond: 1_000_000);

        var logging = Tab("Logs");
        var detailed = logging.Check("Log each event's timing", settings.DetailedLog);
        logging.Children.Add(Ui.ToolbarButton("Open log folder", () =>
        {
            try
            {
                Directory.CreateDirectory(logs);
                Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true });
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                report(problem.Message);
            }
        }));
        bool confirming = false;
        logging.Children.Add(Ui.ToolbarButton("Delete previous log", () =>
        {
            confirming = true;
            try
            {
                if (Ui.Confirm(window, "Delete the previous log file?"))
                    File.Delete(Path.Combine(logs, "autocaster.previous.log"));
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { report(problem.Message); }
            finally { confirming = false; }
        }));

        window.Deactivated += (_, _) =>
        {
            if (!confirming)
                _ = window.Dispatcher.BeginInvoke(() =>
                {
                    if (window.IsVisible && !window.IsActive)
                        window.Close();
                });
        };
        window.Closing += (_, _) =>
        {
            try
            {
                var edited = settings with
                {
                    Dark = dark.IsChecked == true, CaptureProtection = protection.IsChecked == true, StartInTray = startInTray.IsChecked == true,
                    RestoreProfile = restore.IsChecked == true, ActivateGame = activation.IsChecked == true,
                    DetailedLog = detailed.IsChecked == true, DefaultHoldMs = hold(), DefaultWaitMs = delay(),
                    SpinMicroseconds = spin(), Timing = new(late(), threshold()),
                    Hotkeys = [toggleRun()], EditorHotkey = editorShortcut(), MacroResetHotkey = resetSteps(), RathuumWaveHotkey = waveSize(),
                    PlaybackResetHotkey = playbackReset()
                };
                edited.Validate();
                if (apply(edited) is { } problem)
                    report(problem);
            }
            catch (ArgumentException problem) { report(problem.Message); }
        };
        return window;
    }
}
