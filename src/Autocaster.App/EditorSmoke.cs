using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autocaster.Core;
using Autocaster.Infrastructure;
using Autocaster.Windows;

namespace Autocaster.App;

internal static class EditorSmoke
{
    private static async Task CheckMacroWheel(nint source, string settingsPath)
    {
        var enabled = ScenarioProfile.Create("Fix to stairs") with { Macro = new(Enabled: true) { UseHotkey = false } };
        var disabled = ScenarioProfile.Create("Disabled fix") with { Macro = new() };
        var preferences = new Preferences();
        var assignments = preferences.MacroWheelSlots.SetItem(0, enabled.Id).SetItem(2, disabled.Id);
        string path = Path.Combine(Path.GetDirectoryName(settingsPath)!, "wheel-settings.json");
        await (preferences with { MacroWheelSlots = assignments }).Save(path);
        var saved = await Preferences.Load(path);
        if (!saved.MacroWheelSlots.SequenceEqual(assignments) || preferences.MacroWheelSlots.Any(id => id.HasValue))
            throw new InvalidOperationException("Q menu assignments did not survive reopening settings.");

        var slots = MacroWheelSettings.Resolve(saved.MacroWheelSlots, [enabled, disabled]);
        if (slots[0]?.Id != enabled.Id || slots[2] is not null)
            throw new InvalidOperationException("The Q menu must include wheel-only macros and skip disabled ones.");

        var view = new MacroWheelView();
        view.SetSlots(slots);
        view.Measure(new Size(420, 420));
        view.Arrange(new Rect(0, 0, 420, 420));
        view.UpdateLayout();
        if (view.ChoiceAt(new Point(210, 210)) is not null || view.ChoiceAt(new Point(410, 210)) is not null ||
            view.ChoiceAt(new Point(335, 210)) is not null || view.ChoiceAt(new Point(210, 85)) != enabled.Id)
            throw new InvalidOperationException("The Q menu selection did not respect the center, empty sectors, or assigned sector.");

        for (int sector = 0; sector < MacroWheelView.SlotCount; sector++)
        {
            double angle = sector * Math.PI / 4;
            if (MacroWheelView.SlotAt(new Vector(60 * Math.Sin(angle), -60 * Math.Cos(angle)), .5) != sector)
                throw new InvalidOperationException("Q menu directions are incorrect at reduced scale.");
        }

        view.Highlight(new Point(210, 85));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(420, 420, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(220, 66, 1, 1), pixel, 4, 0);
        if (pixel[3] == 0 || pixel[2] <= pixel[0])
            throw new InvalidOperationException($"The selected Q menu sector did not render. Pixel {string.Join(",", pixel)}.");
        using var shortcut = new HoldKey(source, InputKey.Q, () => false);
        if (shortcut.SetEnabled(true) is { } error || ForegroundWindow.IsOwnWindowActive)
            throw new InvalidOperationException("The Q hold shortcut could not register in the background.");
    }

    internal static async Task Run(ProfileService store, string settingsPath)
    {
        // The HWND exists only as a source for routed event arguments. It is never shown or activated.
        const int noActivate = 0x08000000;
        using var source = new HwndSource(new HwndSourceParameters("Autocaster editor smoke")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = noActivate
        });
        OverlayWindow.ExcludeFromTaskSwitcher(source.Handle);
        if (!OverlayWindow.IsExcludedFromTaskSwitcher(source.Handle))
            throw new InvalidOperationException("The overlay must be excluded from the task switcher.");
        if (ForegroundWindow.IsOwnWindowActive)
            throw new InvalidOperationException("The background editor test must not activate its window.");
        using (var capture = new RawMouseCapture(source.Handle))
        {
            capture.Start();
            if (!capture.Recording || !capture.Stop().IsEmpty)
                throw new InvalidOperationException("The native raw mouse capture could not register and unregister.");
        }
        await CheckMacroWheel(source.Handle, settingsPath);
        using (var shortcuts = new HotkeyService(source.Handle))
        {
            var errors = shortcuts.Configure([new(0, InputKey.Mouse4), new(0, InputKey.Mouse5)]);
            if (errors.Count != 0 || !shortcuts.IsRegistered(HotkeyAction.ToggleRun) || !shortcuts.IsRegistered(HotkeyAction.ToggleEditor))
                throw new InvalidOperationException($"Side mouse buttons could not register: {string.Join(" | ", errors)}");
            var reset = new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, InputKey.Mouse5);
            if (shortcuts.ConfigureMacroReset(reset) is not null ||
                !shortcuts.IsMessageFor(HotkeyAction.ResetMacroSteps, ((int)reset.Key << 16) | (int)reset.Modifiers))
                throw new InvalidOperationException("The reset shortcut did not register or decode.");
            if (shortcuts.ConfigureMacroReset(new Hotkey(0, InputKey.Mouse4)) is null ||
                !shortcuts.IsRegistered(HotkeyAction.ResetMacroSteps))
                throw new InvalidOperationException("The reset shortcut did not preserve its previous binding on conflict.");
            var editorKey = new Hotkey(0, InputKey.Mouse5);
            if (shortcuts.ConfigureGlobals([new(0, InputKey.Mouse4), reset], editorKey).Count != 0 ||
                !shortcuts.IsMessageFor(HotkeyAction.ResetMacroSteps, Encode(editorKey)) ||
                !shortcuts.IsMessageFor(HotkeyAction.ToggleEditor, Encode(reset)))
                throw new InvalidOperationException("The editor and reset shortcuts could not swap bindings.");
            var macroKey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, InputKey.Mouse4);
            if (shortcuts.ConfigureMacros(new Dictionary<Guid, Hotkey> { [Guid.NewGuid()] = macroKey }).Count != 0 ||
                shortcuts.ConfigureGlobals([reset, new(0, InputKey.Mouse4)], macroKey).Count == 0 ||
                !shortcuts.IsMessageFor(HotkeyAction.ResetMacroSteps, Encode(editorKey)) ||
                !shortcuts.IsMessageFor(HotkeyAction.ToggleEditor, Encode(reset)))
                throw new InvalidOperationException("A conflicting macro reset binding displaced working shortcuts.");

            var rathuumKey = new Hotkey(HotkeyModifiers.Alt, InputKey.Mouse4);
            if (shortcuts.ConfigureGlobals([new(0, InputKey.Mouse4), reset], editorKey, rathuumKey).Count != 0 ||
                !shortcuts.IsMessageFor(HotkeyAction.ToggleRathuumWaveSize, Encode(rathuumKey)) ||
                shortcuts.ConfigureRathuumWaveSize(macroKey) is null ||
                !shortcuts.IsMessageFor(HotkeyAction.ToggleRathuumWaveSize, Encode(rathuumKey)))
                throw new InvalidOperationException("The Rathuum shortcut did not register or survive a conflict.");

            var playbackReset = new Hotkey(HotkeyModifiers.Control, InputKey.Mouse5);
            if (shortcuts.ConfigureGlobals([new(0, InputKey.Mouse4), reset], editorKey, rathuumKey, playbackReset).Count != 0 ||
                !shortcuts.IsMessageFor(HotkeyAction.ResetPlayback, Encode(playbackReset)) ||
                shortcuts.ConfigurePlaybackReset(rathuumKey) is null ||
                !shortcuts.IsMessageFor(HotkeyAction.ResetPlayback, Encode(playbackReset)))
                throw new InvalidOperationException("The playback reset shortcut did not register or survive a conflict.");

            static int Encode(Hotkey shortcut) => ((int)shortcut.Key << 16) | (int)shortcut.Modifiers;
        }
        await VerifyExtendedHotkeyDispatch(source);
        // An off-screen, non-activating top-level popup follows the same Win32 path
        // as dropdowns and context menus. The application hook must cover it.
        using (var popup = new HwndSource(new HwndSourceParameters("Capture exclusion popup")
        {
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = noActivate,
            PositionX = -32000, PositionY = -32000, Width = 8, Height = 8
        }))
        {
            if (!WindowCaptureProtection.IsExcluded(popup.Handle))
                throw new InvalidOperationException("A top-level popup was not excluded from capture.");
            WindowCaptureProtection.SetEnabled(false);
            if (WindowCaptureProtection.IsExcluded(popup.Handle))
                throw new InvalidOperationException("Capture protection did not clear an open window.");
            WindowCaptureProtection.SetEnabled(true);
            if (!WindowCaptureProtection.IsExcluded(popup.Handle))
                throw new InvalidOperationException("Capture protection did not restore an open window.");
        }

        var badge = new RecordingIndicator();
        nint badgeHandle = new WindowInteropHelper(badge).EnsureHandle();
        if (badge.IsVisible || badge.ShowActivated || !OverlayWindow.IsExcludedFromTaskSwitcher(badgeHandle) || !WindowCaptureProtection.IsExcluded(badgeHandle))
            throw new InvalidOperationException("The recording badge must be a passive overlay.");
        badge.Close();
        VerifyHudWindow();
        VerifyIndexHud();
        VerifyHudPlacement();
        VerifyMovementMap();
        var window = new Window();
        bool capturing = false;
        ModifierKeys modifiers = ModifierKeys.None;
        var recorder = new InputRecorder(window, active => capturing = active, () => modifiers);
        VerifyRowFrame(recorder, Path.GetDirectoryName(settingsPath)!);
        var editor = new EditorViewModel();
        await File.WriteAllTextAsync(settingsPath, """
            { "Hotkeys": [{ "Modifiers": 6, "Key": 119 }, { "Modifiers": 6, "Key": 120 }, { "Modifiers": 6, "Key": 121 }] }
            """);
        var preferences = await Preferences.Load(settingsPath);
        await VerifyListInteractions(store, settingsPath, preferences);
        await VerifyMacroRuntime(preferences);
        await VerifyResetSetting(source, settingsPath, preferences);
        if (preferences.Hotkeys.Length != 1 || preferences.Hotkeys[0] != new Hotkey(0, InputKey.F9))
            throw new InvalidOperationException("Legacy shortcuts did not migrate to F9.");
        try
        {
            bool gameFocused = true;
            Ui.HideCursorWhileGameFocused(window, () => gameFocused);
            var cursorTarget = new Border();
            window.Content = cursorTarget;
            cursorTarget.QueryCursor += (_, args) =>
            {
                args.Cursor = Cursors.Arrow;
                args.Handled = true;
            };
            VerifyCursor(Cursors.None);
            gameFocused = false;
            VerifyCursor(Cursors.Arrow);

            void VerifyCursor(Cursor expected)
            {
                var args = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = Mouse.QueryCursorEvent
                };
                cursorTarget.RaiseEvent(args);
                if (args.Cursor != expected)
                    throw new InvalidOperationException("The overlay did not apply the cursor policy for the current focus.");
            }

            var key = editor.Add(ActionKind.KeyPress, null, preferences);
            var mouse = editor.Add(ActionKind.MouseClick, null, preferences);
            var delay = editor.Add(ActionKind.Wait, key, preferences);
            var hold = editor.Add(ActionKind.KeyHold, mouse, preferences);
            var movement = editor.Add(ActionKind.MouseMovement, hold, preferences);

            var keyForm = Open(key);
            if (Descendants<TimingEditor>(keyForm).Any() || editor.Profile.Actions[0].Hold != EditorConfig.KeyPressHold)
                throw new InvalidOperationException("An ordinary key press must use the built-in hold duration.");
            Click(keyForm, "Assign key");
            if (!capturing)
                throw new InvalidOperationException("Key capture did not start.");
            RaiseKey(Keyboard.PreviewKeyDownEvent, Key.F7);
            RaiseKey(Keyboard.PreviewKeyUpEvent, Key.F7);
            if (capturing || !keyForm.TryApply() || editor.Profile.Actions[0].Key != InputKey.F7)
                throw new InvalidOperationException("The key was not applied to the action.");

            var mouseForm = Open(mouse);
            Click(mouseForm, "Assign mouse button");
            RaiseMouse(Mouse.PreviewMouseDownEvent);
            RaiseMouse(Mouse.PreviewMouseUpEvent);
            if (capturing || !mouseForm.TryApply() || editor.Profile.Actions[2].Button != Autocaster.Core.MouseButton.Right)
                throw new InvalidOperationException("The mouse button was not applied to the action.");

            var delayForm = Open(delay);
            int editEvents = 0;
            delayForm.Edited += () => editEvents++;
            var field = FindNumber(delayForm, "Duration (s)");
            field.Text = "0.581";
            if (editEvents < 1 || !delayForm.TryApply())
                throw new InvalidOperationException("The standalone delay could not be applied.");

            var holdForm = Open(hold);
            Descendants<CheckBox>(holdForm).Single(check => Equals(check.Content, "Random")).IsChecked = true;
            FindNumber(holdForm, "From (s)").Text = "0.6";
            FindNumber(holdForm, "To (s)").Text = "1.25";
            if (FindNumber(holdForm, "From (s)").Text != "0.6" || FindNumber(holdForm, "To (s)").Text != "1.25" || !holdForm.TryApply())
                throw new InvalidOperationException("The random range did not synchronize with its numeric fields.");
            var configForm = EditorForms.ProfileForm(editor.Profile, editor.EditProfile);
            Descendants<CheckBox>(configForm).Single(check => Equals(check.Content, "Loop")).IsChecked = true;
            if (!configForm.TryApply() || editor.Profile.Mode != CycleMode.Infinite)
                throw new InvalidOperationException("The config Loop checkbox did not enable looping.");
            editor.Undo();
            if (editor.Profile.Mode != CycleMode.Once)
                throw new InvalidOperationException("Undo did not disable Loop.");
            editor.Redo();
            if (editor.Profile.Mode != CycleMode.Infinite)
                throw new InvalidOperationException("Redo did not restore Loop.");

            var oldMovement = editor.Rows.Single(item => item.Id == movement).Action with
            {
                MousePath = [new(8, 15, 2), new(40, 20, 6)], MouseSpeed = 2
            };
            var movementAction = EditorConfig.UpgradeMouseTiming(oldMovement);
            if (movementAction is not { MouseDurationMs: 20, MouseSpeed: 1 })
                throw new InvalidOperationException("The legacy speed did not migrate to a duration.");
            editor.Update(movementAction);
            Guid? armed = null;
            var movementForm = EditorForms.ActionForm(movementAction, recorder, editor.Update, id => armed = id);
            Descendants<Button>(movementForm).Single(button => Equals(button.Content, "Record · Insert"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Descendants<CheckBox>(movementForm).Single(check => Equals(check.Content, "Reverse")).IsChecked = true;
            var movementTime = FindNumber(movementForm, "Movement time (s)");
            if (movementTime.Text != "0.02")
                throw new InvalidOperationException("The converted duration is missing from the editor.");
            movementTime.Clear();
            if (!movementForm.TryApply() || editor.Profile.Actions.Single(action => action.Id == movement).MouseDurationMs is not null)
                throw new InvalidOperationException("Clearing the duration must use the recorded timing.");
            movementTime.Text = "1,375";
            window.Content = movementForm;
            movementForm.Measure(new Size(236, double.PositiveInfinity));
            if (movementForm.DesiredSize.Height > 250)
                throw new InvalidOperationException($"The movement settings are {movementForm.DesiredSize.Height} px tall at the minimum editor width.");
            movementTime.ApplyTemplate();
            if (movementTime.Template.FindName("DeleteButton", movementTime) is not null)
                throw new InvalidOperationException("The clear button takes space from the movement duration.");
            var points = new Point[] { new(0, 0), new(15, 2), new(35, 8) };
            var moved = (Point[])points.Clone();
            MousePathPreview.MovePoint(points, moved, 1, 2, new(5, -3));
            var rebuilt = MousePathPreview.Rebuild(movementAction.MousePath, moved);
            if (!rebuilt.SequenceEqual([new(8, 20, -1), new(40, 15, 9)]))
                throw new InvalidOperationException("Dragging a route point changed the endpoint or recorded timing.");
            if (armed != movement || !Descendants<MousePathPreview>(movementForm).Any() || !movementForm.TryApply())
                throw new InvalidOperationException("The movement form did not expose recording, preview, and Reverse.");
            var row = editor.Highlight(delay);
            if (row is not { Running: true } || !row.Text.StartsWith("▶", StringComparison.Ordinal))
                throw new InvalidOperationException("The current row did not highlight.");
            editor.Highlight(null);
            if (row.Running)
                throw new InvalidOperationException("The current row did not clear its highlight.");

            var preset = ScenarioProfile.Create("Nokko Index") with
            {
                Index = new() { Vote = IndexVoteMode.Stay, Nokko = new() { Operator = InputKey.T } }
            };
            var mushroomForm = NokkoEditor.StepForm(NokkoBlock.Mushroom2, () => preset, updated => preset = updated);
            window.Content = mushroomForm;
            var casts = FindNumber(mushroomForm, "Casts per batch");
            casts.Text = "13";
            if (mushroomForm.TryApply() || preset.Index?.Nokko?.Mushroom2Count != 2)
                throw new InvalidOperationException("An invalid mushroom cast count was applied.");
            casts.Text = "3";
            var gapRandom = Descendants<CheckBox>(mushroomForm)
                .Single(check => AutomationProperties.GetName(check) == "Delay after each cast random timing");
            gapRandom.IsChecked = false;
            FindNumber(mushroomForm, "Delay after each cast (s)").Text = "0.42";
            var lead = Descendants<CheckBox>(mushroomForm).Single(check => AutomationProperties.GetName(check) == "Refresh early random timing");
            lead.IsChecked = false;
            if (FindNumber(mushroomForm, "Refresh early (s)").Text != "3")
                throw new InvalidOperationException("Changing a random timer to fixed should start at the midpoint.");
            lead.IsChecked = true;
            FindNumber(mushroomForm, "Refresh early minimum (s)").Text = "2.7";
            if (!mushroomForm.TryApply())
                throw new InvalidOperationException("The inline mushroom settings did not apply.");
            Descendants<CheckBox>(mushroomForm).Single(check => Equals(check.Content, "Enabled")).IsChecked = false;
            if (!mushroomForm.TryApply() || preset.Index?.Nokko is not
                { Mushroom2Count: 3, Mushroom2Enabled: false, Mushroom2Gap.FixedMs: 420, Mushroom1Gap: null })
                throw new InvalidOperationException("Disabling a block should preserve its settings.");
            var stinkbrainForm = NokkoEditor.StepForm(NokkoBlock.Mushroom1, () => preset, updated => preset = updated);
            FindNumber(stinkbrainForm, "Delay after each cast minimum (s)").Text = "0.95";
            FindNumber(stinkbrainForm, "Delay after each cast maximum (s)").Text = "1.15";
            if (!stinkbrainForm.TryApply() || preset.Index?.Nokko is not
                { Mushroom1Gap: { Random: true, MinMs: 950, MaxMs: 1150 }, Mushroom2Gap.FixedMs: 420 })
                throw new InvalidOperationException("The two mushroom cast delays did not save independently.");

            var operatorForm = NokkoEditor.StepForm(NokkoBlock.Operator, () => preset, updated => preset = updated);
            window.Content = operatorForm;
            FindNumber(operatorForm, "Return after (s)").Text = "0.45";
            var indexForm = NokkoEditor.SettingsForm(() => preset, updated => preset = updated);
            window.Content = indexForm;
            Descendants<ComboBox>(indexForm).Single().SelectedIndex = (int)IndexVoteMode.ExtractOnTimer;
            var cashAfter = FindNumber(indexForm, "Cash in after (h)");
            if (cashAfter.Text != "0.1666667")
                throw new InvalidOperationException("The existing ten-minute timer was not displayed in hours.");
            cashAfter.Text = "1.25";
            if (!indexForm.TryApply() || !operatorForm.TryApply() ||
                preset.Index is not { Vote: IndexVoteMode.ExtractOnTimer, ExtractAfter.FixedMs: 4_500_000,
                Nokko: { Mushroom2Count: 3, Mushroom2Enabled: false, Operator: InputKey.T, OperatorStay.FixedMs: 450,
                    MushroomLead: { Random: true, MinMs: 2700, MaxMs: 3500 } } })
                throw new InvalidOperationException("The Index choice did not apply alongside the saved step settings.");
            var ordinary = ScenarioProfile.Create("Timed scenario") with
            {
                Index = new() { Vote = IndexVoteMode.ExtractOnTimer, ExtractAfter = Timing.Range(600_001, 600_015, 10) }
            };
            var ordinaryForm = EditorForms.ProfileForm(ordinary, updated => ordinary = updated);
            var fromHours = FindNumber(ordinaryForm, "From (h)");
            if (fromHours.Text != "0.1666669")
                throw new InvalidOperationException("The regular scenario did not display the range in hours.");
            FindNumber(ordinaryForm, "To (h)").Text = "2.5";
            if (!ordinaryForm.TryApply() || ordinary.Index?.ExtractAfter is not { Random: true, MinMs: 600_001, MaxMs: 9_000_000 })
                throw new InvalidOperationException("The edited hour range did not preserve its unchanged millisecond boundary.");
            var queue = new NokkoQueue();
            var plan = new ScenarioCompiler(new(new Random(0))).PreviewNokko(preset);
            var transfer = plan.Events.First(step => step.Action == "Enter Operator" && step.Type == InputKind.KeyDown);
            var back = plan.Events.First(step => step.Action == "Return to Nokko" && step.Type == InputKind.KeyDown);
            queue.Refresh(preset, plan, transfer.Number, null);
            var transferRow = queue.Rows.Single(row => row.Block == NokkoBlock.Operator);
            queue.Refresh(preset, plan, back.Number, null);
            if (!transferRow.Running || transferRow.Number != "T" || transferRow.Summary != "×2 · 0.45 s" || transferRow.Icon.Width <= 0)
                throw new InvalidOperationException("The two Operator casts did not display and highlight in one row.");
            await store.Save(preset);


            var macro = ScenarioProfile.Create("Macro 1") with
            {
                Macro = new() { Timer = new() { Position = new(.75m, .18m), FontSize = 38 }, StepHudPosition = new(.5m, .3m) }
            };
            HudPart? placement = null;
            var shortcutPanel = new Form();
            var shortcut = recorder.AddShortcut(shortcutPanel, "Test shortcut", new Hotkey(0, InputKey.F8));
            window.Content = shortcutPanel;
            Click(shortcutPanel, "Test shortcut");
            modifiers = ModifierKeys.Alt;
            RaiseKey(Keyboard.PreviewKeyDownEvent, Key.LeftAlt);
            if (!capturing)
                throw new InvalidOperationException("Alt committed before it could be combined.");
            RaiseKey(Keyboard.PreviewKeyUpEvent, Key.LeftAlt);
            if (capturing || shortcut() != new Hotkey(0, InputKey.LeftAlt))
                throw new InvalidOperationException("Standalone Left Alt was not recorded.");
            Click(shortcutPanel, "Test shortcut");
            RaiseKey(Keyboard.PreviewKeyDownEvent, Key.LeftAlt);
            window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            if (capturing || shortcut() != new Hotkey(HotkeyModifiers.Alt, InputKey.WheelDown))
                throw new InvalidOperationException("Alt + wheel down was not recorded.");
            modifiers = ModifierKeys.None;
            foreach (var (button, mouseKey) in new[]
                {
                    (System.Windows.Input.MouseButton.Left, InputKey.MouseLeft),
                    (System.Windows.Input.MouseButton.Right, InputKey.MouseRight),
                    (System.Windows.Input.MouseButton.Middle, InputKey.MouseMiddle)
                })
            {
                Click(shortcutPanel, "Test shortcut");
                RaiseMouse(Mouse.PreviewMouseDownEvent, button);
                RaiseMouse(Mouse.PreviewMouseUpEvent, button);
                if (capturing || shortcut() != new Hotkey(0, mouseKey))
                    throw new InvalidOperationException($"Mouse shortcut {button} was not recorded.");
            }
            Click(shortcutPanel, "Test shortcut");
            window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            var wheel = new Hotkey(0, InputKey.WheelUp);
            var wheelPreferences = preferences with { EditorHotkey = shortcut() };
            await wheelPreferences.Save(settingsPath);
            var restoredWheel = await Preferences.Load(settingsPath);
            if (capturing || restoredWheel.EditorHotkey != wheel)
                throw new InvalidOperationException("Wheel up shortcut did not persist.");

            var macroForm = new MacroSettingsPanel(() => macro, updated => macro = updated, recorder,
                preferences.MacroResetHotkey, _ => null, placeHud: part => placement = part);
            window.Content = macroForm;
            Click(macroForm, "Hotkey");
            RaiseMouse(Mouse.PreviewMouseDownEvent, System.Windows.Input.MouseButton.XButton2);
            RaiseMouse(Mouse.PreviewMouseUpEvent, System.Windows.Input.MouseButton.XButton2);
            Descendants<CheckBox>(macroForm).Single(check => Equals(check.Content, "Enabled")).IsChecked = true;
            var step = Descendants<CheckBox>(macroForm).Single(check => Equals(check.Content, "Step mode"));
            if (step.FocusVisualStyle is not null)
                throw new InvalidOperationException("Step mode still draws a focus outline.");
            step.IsChecked = true;
            var showWaveSize = Descendants<CheckBox>(macroForm).Single(check => Equals(check.Content, "Show Rathuum wave size"));
            if (showWaveSize.IsEnabled)
                throw new InvalidOperationException("The Rathuum label should be disabled without Step HUD.");
            Descendants<CheckBox>(macroForm).Single(check => Equals(check.Content, "Step HUD")).IsChecked = true;
            if (!showWaveSize.IsEnabled)
                throw new InvalidOperationException("Step HUD did not enable the Rathuum label option.");
            showWaveSize.IsChecked = true;
            Descendants<CheckBox>(macroForm).Single(check => Equals(check.Content, "Next route arrow")).IsChecked = true;
            Click(macroForm, "Place timer");
            if (placement != HudPart.Timer)
                throw new InvalidOperationException("The timer placement button did not open the timer placement mode.");
            Click(macroForm, "Place Step HUD");
            if (placement != HudPart.Step)
                throw new InvalidOperationException("The Step HUD placement button did not open the Step placement mode.");
            macroForm.Measure(new Size(270, double.PositiveInfinity));
            macroForm.Arrange(new Rect(0, 0, 270, macroForm.DesiredSize.Height));
            if (macroForm.DesiredSize.Width > 270)
                throw new InvalidOperationException("The HUD settings exceed the minimum sidebar width.");
            if (capturing || !macroForm.TryApply())
                throw new InvalidOperationException("The macro form could not apply its shortcut and mode.");
            var macroEditor = new EditorViewModel();
            macroEditor.Load(macro, saved: false);
            var route = MouseRoute.InsertPoint(movementAction.MousePath, 1, .5);
            var routeId = macroEditor.Add(ActionKind.MouseMovement, null, preferences);
            macroEditor.Update(macroEditor.Profile.Actions[0] with { MousePath = route });
            var timerId = macroEditor.Add(ActionKind.StartMacroTimer, routeId, preferences);
            var timerAction = macroEditor.Profile.Actions.Single(action => action.Id == timerId);
            var timerForm = EditorForms.ActionForm(timerAction, recorder, macroEditor.Update,
                inline: true, macro: true, timer: macro.Macro?.Timer);
            var duration = FindNumber(timerForm, "Timer duration (s)");
            if (duration.Text != "30" || duration.VerticalContentAlignment != VerticalAlignment.Center ||
                duration.TextAlignment != TextAlignment.Center)
                throw new InvalidOperationException("The timer duration did not inherit its default or align correctly.");
            duration.Text = "42,5";
            if (!timerForm.TryApply())
                throw new InvalidOperationException("The Start timer action did not accept a decimal duration.");
            var groupId = macroEditor.AddGroup(null, new HashSet<Guid> { routeId, timerId });
            macroEditor.RenameGroup(groupId, "Approach");
            var emptyGroup = macroEditor.AddGroup(null, null);
            var layout = new ActionLayout(macroEditor.Profile);
            layout.Move(new HashSet<Guid> { timerId }, groupId, after: true, outside: true);
            var reordered = layout.ToProfile();
            macroEditor.PreviewMove(reordered, new HashSet<Guid> { timerId });
            if (macroEditor.Profile.ActionGroups[0].Count != 2 || macroEditor.DisplayRows.Single(row => row.Id == timerId).GroupId is not null)
                throw new InvalidOperationException("Dragging out did not change only the preview before release.");
            macroEditor.CommitMove(reordered);
            if (macroEditor.Profile.ActionGroups[0].Count != 1 || macroEditor.Profile.ActionGroups[1].Id != emptyGroup)
                throw new InvalidOperationException("Dragging out removed the wrong group.");
            macroEditor.Undo();
            macroEditor.ToggleGroup(groupId);
            if (macroEditor.DisplayRows.Any(row => row.Id == timerId) || macroEditor.Profile.Actions.Length != 2)
                throw new InvalidOperationException("Collapsing the group changed its actions.");
            macroEditor.ToggleGroup(groupId);
            macro = macroEditor.Profile;
            await store.Save(macro);
            await store.Save(editor.Profile);
            var loaded = await store.LoadAll();
            var savedMacro = loaded.Profiles.Single(item => item.Id == macro.Id);
            if (savedMacro.Macro != new MacroOptions(InputKey.Mouse5, 0, true, true)
                { ShowStepHud = true, ShowRathuumWaveSize = true, ShowNextMovement = true, StepHudPosition = new(.5m, .3m),
                    Timer = new() { Position = new(.75m, .18m), FontSize = 38 } } ||
                !route.Any(point => point.ControlPoint) || !savedMacro.Actions[0].MousePath.SequenceEqual(route) ||
                savedMacro.Actions[1] is not { Kind: ActionKind.StartMacroTimer, RunWithPrevious: true, MacroTimerDurationMs: 42_500 } ||
                !savedMacro.ActionGroups.SequenceEqual(macro.ActionGroups))
                throw new InvalidOperationException("The macro shortcut, route points or timer step did not persist.");
            var simulated = new TestMacroBackend();
            var reloadedRunner = new MacroRunner(simulated, simulated, message => throw new InvalidOperationException(message));
            reloadedRunner.Trigger(savedMacro, preferences, false, simulated);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (reloadedRunner.Active && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            if (reloadedRunner.Active || reloadedRunner.ReadHud(savedMacro)?.Countdown?.DurationMs != 42_500)
                throw new InvalidOperationException("The reloaded grouped macro did not run with its saved duration.");
            var saved = loaded.Profiles.Single(item => item.Id == editor.Profile.Id);
            if (loaded.Profiles.Single(item => item.Id == preset.Id).Index != preset.Index)
                throw new InvalidOperationException("The edited Nokko config did not persist.");
            var actions = saved.Actions;
            if (!actions.Select(item => item.Kind).SequenceEqual([ActionKind.KeyPress, ActionKind.Wait, ActionKind.MouseClick, ActionKind.KeyHold, ActionKind.MouseMovement]) ||
                actions[0].Key != InputKey.F7 || actions[1].Delay.FixedMs != 581 || actions[2].Button != Autocaster.Core.MouseButton.Right ||
                actions[3].Hold is not { Random: true, MinMs: 600, MaxMs: 1250, StepMs: 10 } ||
                actions[4].MouseDurationMs != 1375 || !actions[4].ReverseMousePath || !actions[4].VaryMousePath || !actions[4].MousePath.SequenceEqual(movementAction.MousePath) ||
                saved.Mode != CycleMode.Infinite)
                throw new InvalidOperationException("The saved configuration differs from the edited rows.");
            if (ForegroundWindow.IsOwnWindowActive)
                throw new InvalidOperationException("The background editor test activated a window.");
        }
        finally
        {
            window.Close();
        }

        Form Open(Guid id)
        {
            var form = EditorForms.ActionForm(editor.Rows.Single(row => row.Id == id).Action, recorder, editor.Update);
            window.Content = form;
            return form;
        }

        void RaiseKey(RoutedEvent kind, Key key) =>
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = kind });

        void RaiseMouse(RoutedEvent kind, System.Windows.Input.MouseButton button = System.Windows.Input.MouseButton.Right) =>
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
            { RoutedEvent = kind });
    }

    private static async Task VerifyExtendedHotkeyDispatch(HwndSource source)
    {
        using var shortcuts = new HotkeyService(source.Handle);
        var alt = new Hotkey(0, InputKey.LeftAlt);
        var wheel = new Hotkey(0, InputKey.WheelDown);
        var middle = new Hotkey(0, InputKey.MouseMiddle);
        var errors = shortcuts.ConfigureGlobals([alt, wheel], middle);
        if (errors.Count != 0)
            throw new InvalidOperationException($"Extended hotkeys could not register: {string.Join(" | ", errors)}");

        var received = new List<HotkeyAction>();
        nint Listen(nint window, int message, nint id, nint parameters, ref bool handled)
        {
            var action = (HotkeyAction)(int)id;
            if (message == HotkeyService.Message && shortcuts.IsMessageFor(action, parameters))
                received.Add(action);
            return 0;
        }

        // Invoke the hook callbacks with native message data and let the window receive the
        // posted messages. No keyboard or mouse input is sent to the Windows desktop.
        source.AddHook(Listen);
        nint data = Marshal.AllocHGlobal(40);
        try
        {
            for (int offset = 0; offset < 40; offset += 4)
                Marshal.WriteInt32(data, offset, 0);

            KeyboardMessage(InputKey.LeftAlt, 0x0104);
            KeyboardMessage(InputKey.F7, 0x0104);
            KeyboardMessage(InputKey.LeftAlt, 0x0105);
            KeyboardMessage(InputKey.LeftAlt, 0x0104);
            KeyboardMessage(InputKey.LeftAlt, 0x0104);
            KeyboardMessage(InputKey.LeftAlt, 0x0105);

            Marshal.WriteInt32(data, 0, int.MinValue);
            Marshal.WriteInt32(data, 4, int.MinValue);
            Marshal.WriteInt32(data, 8, -120 << 16);
            Invoke("ObserveMouse", 0x020A);
            Marshal.WriteInt32(data, 12, 1);
            Invoke("ObserveMouse", 0x0207);
            Marshal.WriteInt32(data, 12, 0);
            Invoke("ObserveMouse", 0x0207);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (received.Count < 3 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            if (!received.SequenceEqual([HotkeyAction.ToggleRun, HotkeyAction.ToggleEditor, HotkeyAction.ResetMacroSteps]))
                throw new InvalidOperationException($"Unexpected hotkey messages: {string.Join(", ", received)}.");
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            source.RemoveHook(Listen);
        }

        void KeyboardMessage(InputKey key, int message)
        {
            Marshal.WriteInt32(data, (int)key);
            Invoke("ObserveKeyboard", message);
        }

        void Invoke(string name, int message) =>
            typeof(HotkeyService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(shortcuts, [0, (nint)message, data]);
    }

    private static void VerifyRowFrame(InputRecorder recorder, string folder)
    {
        const double width = 580;
        var delay = new ScenarioAction { Kind = ActionKind.Wait, Delay = Timing.Fixed(550) };
        var timer = new ScenarioAction { Kind = ActionKind.StartMacroTimer, MacroTimerDurationMs = 30_000, RunWithPrevious = true };
        var group = new ActionGroup(Guid.NewGuid(), "Approach", 0, 1);
        var foldedGroup = new ActionGroup(Guid.NewGuid(), "Return", 0, 1, Color: 1);
        var folded = new GroupRow(foldedGroup, true);
        var header = new GroupRow(group, false);
        var inside = new ActionRow(timer, 0);
        var outside = new ActionRow(delay, 1);
        header.SetLayout(0, group, end: false, moving: false);
        folded.SetLayout(0, foldedGroup, end: true, moving: false);
        inside.SetLayout(0, group, end: true, moving: false);
        inside.SetMacroDisplay("Auto", "30 s");
        outside.SetMacroDisplay("Auto", null);
        outside.SetEditor(EditorForms.ActionForm(delay, recorder, _ => { }, inline: true, macro: true));
        var panel = new StackPanel { Width = width, Margin = new(8), UseLayoutRounding = true };
        panel.SetResourceReference(Panel.BackgroundProperty, "EditorBackground");
        var style = (Style)Application.Current.FindResource("StepRow");
        foreach (var row in new StepRow[] { folded, header, inside, outside })
            panel.Children.Add(new ListBoxItem { Style = style, DataContext = row, IsSelected = row is ActionRow });
        VisualTreeHelper.SetRootDpi(panel, new DpiScale(1.5, 1.5));
        panel.Measure(new Size(width + 16, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        panel.UpdateLayout();
        var item = (ListBoxItem)panel.Children[3];
        var frame = (Border)item.Template.FindName("RowFrame", item);
        var form = (Border)item.Template.FindName("InlinePanel", item);
        var grouped = (ListBoxItem)panel.Children[2];
        var groupFrame = (Border)grouped.Template.FindName("RowFrame", grouped);
        if (groupFrame.BorderBrush != inside.GroupColors!.Line || groupFrame.Background != inside.GroupColors.Selected)
            throw new InvalidOperationException("A selected action must use the color of its group.");
        if (frame.CornerRadius != new CornerRadius(5) || frame.BorderThickness != new Thickness(1) ||
            form.Visibility != Visibility.Visible || form.Background is not null || form.BorderThickness.Bottom != 0)
            throw new InvalidOperationException("The expanded editor must share one rounded outer frame.");
        var closedHeader = (ListBoxItem)panel.Children[0];
        var openHeader = (ListBoxItem)panel.Children[1];
        var closedBand = (Border)closedHeader.Template.FindName("GroupBand", closedHeader);
        var openBand = (Border)openHeader.Template.FindName("GroupBand", openHeader);
        var closedArrow = (System.Windows.Shapes.Path)closedHeader.Template.FindName("Chevron", closedHeader);
        var openArrow = (System.Windows.Shapes.Path)openHeader.Template.FindName("Chevron", openHeader);
        if (!closedBand.SnapsToDevicePixels || !openBand.SnapsToDevicePixels ||
            closedArrow.RenderSize != openArrow.RenderSize || closedArrow.Data.Bounds.Height <= closedArrow.Data.Bounds.Width ||
            openArrow.Data.Bounds.Width <= openArrow.Data.Bounds.Height ||
            closedArrow.Data.Bounds.Right + closedArrow.StrokeThickness / 2 > closedArrow.ActualWidth ||
            closedArrow.Data.Bounds.Bottom + closedArrow.StrokeThickness / 2 > closedArrow.ActualHeight)
            throw new InvalidOperationException("Folded and open group chevrons must fit the same aligned slot.");
        var image = new RenderTargetBitmap((int)Math.Ceiling(panel.DesiredSize.Width * 1.5),
            (int)Math.Ceiling(panel.DesiredSize.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
        image.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(folder, "editor-rows.png"));
        encoder.Save(file);
    }

    private static async Task VerifyListInteractions(ProfileService store, string settingsPath, Preferences preferences)
    {
        var backend = new TestMacroBackend();
        using var clock = new WindowsDeadlineClock();
        var folder = Path.GetDirectoryName(settingsPath)!;
        await using var log = new FileRunLog(Path.Combine(folder, "interaction-test"));
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(backend), log);
        var profile = ScenarioProfile.Create("Interaction test") with
        {
            Actions = Enumerable.Range(0, 24).Select(i => new ScenarioAction
            { Kind = i is 7 or 8 ? ActionKind.MouseMovement : ActionKind.KeyPress }).ToImmutableArray()
        };
        var window = new MainWindow(engine, backend, clock, log, store, preferences, settingsPath, folder, [profile], "");
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "EditorText");
        using var host = new HwndSource(new HwndSourceParameters("Editor gesture test")
        {
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000,
            PositionX = -32000, PositionY = -32000, Width = 760, Height = 500
        });
        host.RootVisual = root;
        root.UpdateLayout();
        try
        {
            var list = Field<ListBox>("_actions");
            var editor = Field<EditorViewModel>("_editor");
            var rows = editor.Rows;
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[2]);
            if (item is null || list.ActualHeight >= list.Items.Count * item.ActualHeight)
                throw new InvalidOperationException("The gesture test list must fill its scroll viewport.");
            var scrollbar = VisualDescendants<ScrollBar>(list).Single(bar => bar.Orientation == Orientation.Vertical);
            var rowFrame = (Border)item.Template.FindName("RowFrame", item);
            if (rowFrame.TranslatePoint(new Point(rowFrame.ActualWidth, 0), list).X >
                scrollbar.TranslatePoint(new Point(), list).X)
                throw new InvalidOperationException("The vertical scrollbar overlaps the steps.");
            var right = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
            item.RaiseEvent(right);
            Call("SetContextTarget", rows[2]);
            if (!right.Handled || list.SelectedItems.Count != 0 || rows.Any(row => row.HasInlineEditor))
                throw new InvalidOperationException("Right-clicking a row opened or selected it.");

            Call("Select", rows[0].Id);
            root.UpdateLayout();
            if (!rows[0].HasInlineEditor)
                throw new InvalidOperationException("Selecting the action did not open its inline editor.");
            var start = ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[0])).TranslatePoint(new Point(60, 8), list);
            SetField("_dragOrigin", start);
            Call("BeginRowDrag", rows[0].Id);
            root.UpdateLayout();
            var destination = ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[2])).TranslatePoint(new Point(60, 26), list);
            Call("UpdateDrag", destination, rows[0].Id);
            if (editor.Profile.Actions[0].Id != rows[0].Id || editor.DisplayRows[2].Id != rows[0].Id ||
                rows.Any(row => row.HasInlineEditor))
                throw new InvalidOperationException("Dragging must preview the order while keeping the inline editor closed.");
            Call("FinishDrag", true);
            if (editor.Profile.Actions[2].Id != rows[0].Id || rows.Any(row => row.HasInlineEditor))
                throw new InvalidOperationException("Finishing a drag opened an action or failed to commit.");

            root.UpdateLayout();
            var marquee = Field<MarqueeSelection>("_marquee");
            var indexAt = typeof(MarqueeSelection).GetMethod("IndexAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int GutterIndex(int index)
            {
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                var point = new Point(5, row.TranslatePoint(new Point(0, 12), list).Y);
                return (int)indexAt.Invoke(marquee, [point])!;
            }
            int first = GutterIndex(1), last = GutterIndex(3);
            if (first != 1 || last != 3)
                throw new InvalidOperationException("The left gutter did not resolve the adjacent rows.");
            Call("SelectRange", first, last, false);
            var members = list.SelectedItems.OfType<ActionRow>().Select(row => row.Id).ToArray();
            Call("SetContextTarget", editor.Rows[0]);
            list.ContextMenu.Items.OfType<MenuItem>().Single(menu => Equals(menu.Header, "Add group"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var group = editor.Profile.ActionGroups.Single();
            if (group is not { Name: "Group 1", Start: 1, Count: 3 } ||
                !editor.Profile.Actions.Skip(group.Start).Take(group.Count).Select(action => action.Id).SequenceEqual(members) ||
                editor.DisplayRows.Any(row => row.HasInlineEditor))
                throw new InvalidOperationException("Add group did not take the selected rows without opening Rename.");
            root.UpdateLayout();
            VerifyGroupedColors();
            Call("Select", members[0]);
            root.UpdateLayout();
            VerifyGroupedColors();
            list.UnselectAll();
            editor.RenameGroup(group.Id, "Renamed");
            editor.AddGroup(null, null);
            if (editor.Profile.ActionGroups[^1].Name != "Group 2")
                throw new InvalidOperationException("Renaming an earlier group reset the default numbering.");

            Call("SetContextTarget", editor.Rows[0]);
            Call("Add", ActionKind.MouseMovement);
            var newRoute = (Guid)Field<Guid?>("_newMouseAction")!;
            list.UnselectAll();
            int actionCount = editor.Profile.Actions.Length;
            if ((Guid?)Call("RecordingTarget") != newRoute ||
                editor.DisplayRows.Single(row => row.Id == newRoute).HasInlineEditor)
                throw new InvalidOperationException("The new mouse action cannot be recorded while collapsed.");
            Call("SaveMouseRecording", newRoute, ImmutableArray.Create(new MouseSample(60, 50, 0)));
            if ((Guid?)Call("RecordingTarget") != newRoute)
                throw new InvalidOperationException("Insert should target the last recorded movement again.");
            Call("SaveMouseRecording", newRoute, ImmutableArray.Create(new MouseSample(90, 70, -15)));
            var replacement = editor.Profile.Actions.Single(action => action.Id == newRoute);
            if (editor.Profile.Actions.Length != actionCount || replacement.MousePath.Length != 1 ||
                replacement.MousePath[0].DeltaX != 70 || replacement.MousePath[0].DeltaY != -15)
                throw new InvalidOperationException("A new take did not replace the last movement.");
            await Field<Task>("_save");

            foreach (bool macro in new[] { false, true })
                for (int count = 0; count <= 2; count++)
                {
                    var value = profile with
                    {
                        Macro = macro ? new() : null,
                        Actions = profile.Actions.Where(action => action.Kind == ActionKind.MouseMovement).Take(count).ToImmutableArray()
                    };
                    bool opened = false;
                    Form form = macro
                        ? new MacroSettingsPanel(() => value, _ => { }, new InputRecorder(window, _ => { }),
                            preferences.MacroResetHotkey, _ => null, openMap: () => opened = true)
                        : EditorForms.ProfileForm(value, _ => { }, () => opened = true);
                    var button = Descendants<Button>(form).SingleOrDefault(button =>
                        Equals(button.Content, "Movement map") || AutomationProperties.GetName(button) == "Movement map");
                    if (macro && count < 2)
                    {
                        if (Ui.Ancestor<Border>(button) is not { Visibility: Visibility.Collapsed })
                            throw new InvalidOperationException("The sidebar displayed the movement map without two routes.");
                        continue;
                    }
                    button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if ((button is not null) != (count == 2) || opened != (count == 2))
                        throw new InvalidOperationException("Movement map was not gated by the action count in settings.");
                }

            var sample = ScenarioProfile.Create("Camera route") with
            {
                Macro = new(InputKey.Mouse5, Step: true) { ShowStepHud = true, ShowNextMovement = true },
                Actions =
                [
                    new() { Kind = ActionKind.MouseMovement, MousePath = [new(1200, 40, -10)] },
                    new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(300) },
                    new() { Kind = ActionKind.StartMacroTimer, RunWithPrevious = true },
                    new() { Kind = ActionKind.MouseMovement, MousePath = [new(850, -18, 30)] },
                    new() { Kind = ActionKind.KeyPress, Key = InputKey.R }
                ]
            };
            Call("Open", sample, true, false);
            Call("ToggleProfileDetails");
            root.UpdateLayout();
            var sidebar = Field<MacroSettingsPanel>("_macroSettings");
            var inspector = Field<Border>("_macroInspector");
            if (inspector.Visibility != Visibility.Visible || !list.IsVisible ||
                list.TranslatePoint(new Point(list.ActualWidth, 0), root).X > inspector.TranslatePoint(new Point(), root).X)
                throw new InvalidOperationException("The macro sidebar must sit beside the visible action list.");
            var settingsSection = Descendants<Expander>(sidebar).First();
            var expanderToggle = VisualDescendants<ToggleButton>(settingsSection).First();
            expanderToggle.IsChecked = false;
            if (settingsSection.IsExpanded)
                throw new InvalidOperationException("The sidebar section did not collapse.");
            expanderToggle.IsChecked = true;
            Call("Select", sample.Actions[1].Id);
            root.UpdateLayout();
            if (Field<MacroSettingsPanel>("_macroSettings") != sidebar || !editor.Rows[1].HasInlineEditor)
                throw new InvalidOperationException("Editing an action closed the macro sidebar.");

            var name = FindNumber(sidebar, "Macro name");
            name.Text = "Edited route";
            editor.Add(ActionKind.KeyPress, null, preferences);
            if (!(bool)Call("ApplyDraft")! || editor.Profile.Name != "Edited route" || editor.Profile.Actions.Length != 6)
                throw new InvalidOperationException("Applying macro settings discarded an action edit.");
            editor.Undo();
            if (name.Text != sample.Name)
                throw new InvalidOperationException("The sidebar did not reflect an undone macro settings change.");

            using (var shortcuts = new HotkeyService(host.Handle))
            {
                SetField("_hotkeys", shortcuts);
                Click(sidebar, "Reset macro steps");
                RaiseResetKey(Key.F18);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                await Field<Task>("_settingsSave");
                var replacementShortcut = new Hotkey(0, InputKey.F18);
                if (!shortcuts.IsMessageFor(HotkeyAction.ResetMacroSteps, (int)InputKey.F18 << 16) ||
                    (await Preferences.Load(settingsPath)).MacroResetHotkey != replacementShortcut)
                    throw new InvalidOperationException("The shortcut entered in the sidebar was not registered and saved.");
                Click(sidebar, "Reset macro steps");
                RaiseResetKey(Key.F9);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (!shortcuts.IsMessageFor(HotkeyAction.ResetMacroSteps, (int)InputKey.F18 << 16) ||
                    !Equals(Descendants<Button>(sidebar).Single(button =>
                        AutomationProperties.GetName(button) == "Reset macro steps").Content, "F18"))
                    throw new InvalidOperationException("A conflicting shortcut displaced the previous reset shortcut.");
                SetField("_hotkeys", null);
                sidebar.SyncReset(preferences.MacroResetHotkey);
            }
            void RaiseResetKey(Key key)
            {
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, host, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, host, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            }

            Click(sidebar, "Movement map");
            if (inspector.IsVisible || list.IsVisible)
                throw new InvalidOperationException("The map must use the full editor workspace.");
            Call("CloseMovementMap");
            if (!inspector.IsVisible || !list.IsVisible || Field<MacroSettingsPanel>("_macroSettings") != sidebar)
                throw new InvalidOperationException("Closing the movement map did not restore the sidebar.");

            SetField("_settings", preferences);
            SetField("_hotkeyWarning", null);
            SetField("_macroWarning", null);
            Call("Open", sample with { Macro = sample.Macro! with { Enabled = true } }, true, false);
            Call("ToggleProfileDetails");
            Call("Report", "");
            root.UpdateLayout();
            var preview = new RenderTargetBitmap(760, 500, 96, 96, PixelFormats.Pbgra32);
            preview.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var file = File.Create(Path.Combine(folder, "macro-settings-sidebar.png")))
                encoder.Save(file);

            Call("Select", sample.Actions[0].Id);
            root.Measure(new Size(640, 400));
            root.Arrange(new Rect(0, 0, 640, 400));
            var routeItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var inline = (Border)routeItem.Template.FindName("InlinePanel", routeItem);
            var recordButton = Descendants<Button>((Form)editor.Rows[0].InlineEditor!).Single(button =>
                Equals(button.Content, "Record · Insert"));
            double recordEdge = recordButton.TranslatePoint(new Point(recordButton.ActualWidth, 0), inline).X;
            if (recordEdge > inline.ActualWidth || Field<MacroSettingsPanel>("_macroSettings").ActualWidth < 250)
                throw new InvalidOperationException("The sidebar and the inline movement controls must fit at the minimum width.");

            Call("SetContextTarget", editor.Rows[0]);
            var add = list.ContextMenu.Items.OfType<MenuItem>().Single(menu => Equals(menu.Header, "Add action"));
            add.Items.OfType<MenuItem>().Single(menu => Equals(menu.Header, "Larva detect"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var gate = editor.Rows.Single(row => row.Action.Kind == ActionKind.LarvaDetect);
            var timeout = FindNumber((Form)gate.InlineEditor!, "Timeout (s)");
            if (timeout.Text != "4")
                throw new InvalidOperationException("Larva detect must start with a four-second timeout.");
            timeout.Text = "2.5";
            if (!(bool)Call("ApplyDraft")!)
                throw new InvalidOperationException("The Larva timeout did not apply.");
            await store.Save(editor.Profile);
            var loadedGate = (await store.LoadAll()).Profiles.Single(profile => profile.Id == editor.Profile.Id)
                .Actions.Single(action => action.Id == gate.Id);
            if (loadedGate is not { Kind: ActionKind.LarvaDetect, LarvaTimeoutMs: 2500 })
                throw new InvalidOperationException("Larva detect did not survive saving and loading.");

            Call("SetContextTarget", gate);
            add.Items.OfType<MenuItem>().Single(menu => Equals(menu.Header, "Rathuum kill feed"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var rathuum = editor.Rows.Single(row => row.Action.Kind == ActionKind.RathuumKillFeed);
            var rathuumForm = (Form)rathuum.InlineEditor!;
            var lead = FindNumber(rathuumForm, "Read delay correction (ms)");
            if (lead.Text != "150")
                throw new InvalidOperationException("Rathuum kill feed must start at 150 ms reading correction.");
            lead.Text = "175";
            var currentScore = Descendants<CheckBox>(rathuumForm).Single(check => Equals(check.Content, "Use current team score"));
            currentScore.IsChecked = true;
            var capture = rathuumForm.Children.OfType<Expander>().Single();
            capture.IsExpanded = true;
            FindNumber((Form)capture.Content, "Scan limit (Hz)").Text = "90";
            if (!(bool)Call("ApplyDraft")!)
                throw new InvalidOperationException("Rathuum settings did not apply.");
            if (!(await (Task<bool>)Call("FlushAutosave")!))
                throw new InvalidOperationException("Rathuum settings did not autosave.");
            var loadedProfile = (await store.LoadAll()).Profiles.Single(profile => profile.Id == editor.Profile.Id);
            var loadedRathuum = loadedProfile.Actions.Single(action => action.Id == rathuum.Id);
            if (loadedRathuum.Rathuum is not { ReadCorrectionMs: 175, ScanHz: 90, WaveSize: 5, RespawnMs: 8000, StartAtCurrentScore: true })
                throw new InvalidOperationException("Rathuum settings did not survive saving and loading.");
            Call("Open", loadedProfile, true, false);
            Call("Select", rathuum.Id);
            var reopened = (Form)editor.Rows.Single(row => row.Id == rathuum.Id).InlineEditor!;
            if (Descendants<CheckBox>(reopened).Single(check => Equals(check.Content, "Use current team score")).IsChecked != true)
                throw new InvalidOperationException("Reopening Rathuum did not restore current score tracking.");
            Descendants<CheckBox>(reopened).Single(check => Equals(check.Content, "Trigger on first executioner kill")).IsChecked = true;
            FindNumber(reopened, "Launch lead (ms)").Text = "360";
            if (!(await (Task<bool>)Call("FlushAutosave")!) ||
                (await store.LoadAll()).Profiles.Single(profile => profile.Id == loadedProfile.Id)
                    .Actions.Single(action => action.Id == rathuum.Id).Rathuum is not { FirstKillOnly: true, FirstKillLeadMs: 360 })
                throw new InvalidOperationException("First-kill timing did not autosave.");

            var firstRoute = new ScenarioAction { Kind = ActionKind.MouseMovement, MouseDurationMs = 350, MousePath = [new(16, 12, 3)] };
            var secondRoute = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = [new(32, 15, -8)] };
            var restartProfile = ScenarioProfile.Create("Route restart test") with
            {
                Macro = new(Step: true),
                Actions = [firstRoute, secondRoute]
            };
            await store.Save(restartProfile);
            Call("Open", restartProfile, true, false);
            if (Call("RecordingTarget") is not null)
                throw new InvalidOperationException("An old macro with several routes picked an arbitrary replacement target.");
            Call("Select", firstRoute.Id);
            if ((Guid?)Call("RecordingTarget") != firstRoute.Id)
                throw new InvalidOperationException("Insert did not target the explicitly selected existing route.");
            var take = ImmutableArray.Create(new MouseSample(40, 27, 4));
            Call("SaveMouseRecording", firstRoute.Id, take);
            await Field<Task>("_save");
            var reloaded = (await store.LoadAll()).Profiles.Single(profile => profile.Id == restartProfile.Id);
            Call("Open", reloaded, true, false);
            if ((Guid?)Call("RecordingTarget") != firstRoute.Id ||
                !reloaded.Actions[0].MousePath.SequenceEqual(take) || reloaded.Actions[0].MouseDurationMs != 350 ||
                !reloaded.Actions[1].MousePath.SequenceEqual(secondRoute.MousePath))
                throw new InvalidOperationException("Reopening the macro lost the recorded route, settings, or Insert target.");

            var pending = new TaskCompletionSource();
            SetField("_save", pending.Task);
            Call("SaveMouseRecording", firstRoute.Id, ImmutableArray.Create(new MouseSample(56, -55, 40)));
            editor.Undo();
            if (editor.Dirty)
                throw new InvalidOperationException("Undo did not return to the last saved route.");
            var flush = (Task<bool>)Call("FlushAutosave")!;
            pending.SetResult();
            if (!await flush)
                throw new InvalidOperationException("The restored route did not save.");
            await Field<Task>("_save");
            var restored = (await store.LoadAll()).Profiles.Single(profile => profile.Id == restartProfile.Id);
            if (!restored.Actions[0].MousePath.SequenceEqual(take))
                throw new InvalidOperationException("An in-flight save overwrote the restored route.");

            Call("Select", firstRoute.Id);
            var time = FindNumber((Form)editor.Rows[0].InlineEditor!, "Movement time (s)");
            time.Text = "invalid";
            var closing = new System.ComponentModel.CancelEventArgs();
            Call("OnClosing", closing);
            if (!closing.Cancel || Field<bool>("_closeReady") || !((Form)editor.Rows[0].InlineEditor!).Dirty)
                throw new InvalidOperationException("Exiting discarded an invalid movement draft.");
            time.Text = "0.35";
            if (!(bool)Call("ApplyDraft")!)
                throw new InvalidOperationException("The movement editor could not recover after a rejected exit.");

            if (ForegroundWindow.IsOwnWindowActive)
                throw new InvalidOperationException("The gesture test activated a window.");

            void VerifyGroupedColors()
            {
                foreach (var action in list.SelectedItems.OfType<ActionRow>())
                {
                    var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(action);
                    var selected = (Border)container.Template.FindName("RowFrame", container);
                    if (selected.BorderBrush != action.GroupColors!.Line || selected.Background != action.GroupColors.Selected)
                        throw new InvalidOperationException("The editor did not use the group color for a selected member.");
                }
            }
        }
        finally
        {
            Field<System.Windows.Threading.DispatcherTimer>("_autosave").Stop();
            host.RootVisual = null;
            SetField("_closeReady", true);
            window.Close();
        }

        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void SetField(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        object? Call(string name, params object[] values) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values);
    }

    private static void VerifyIndexHud()
    {
        var window = new IndexHudWindow();
        try
        {
            var sample = new IndexRunHud("Nokko Index", 0, 21_600_000, 19, 35, "Fourth ability", 6_509_000,
                false, 6_492_000, IndexHudPhase.StayInvested);
            var screen = new System.Drawing.Rectangle(-32000, -32000, 640, 360);
            if (!window.Present(screen, sample, 6_501_000, 0))
                throw new InvalidOperationException("The Index HUD could not open offscreen.");
            window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            var strip = Descendants<IndexHudView>(window).Single();
            var labels = Descendants<TextBlock>(strip).Select(text => text.Text).ToArray();
            if (window.ShowActivated || ForegroundWindow.IsOwnWindowActive || !WindowCaptureProtection.IsExcluded(handle) ||
                !OverlayWindow.IsExcludedFromTaskSwitcher(handle) ||
                !new[] { "01:48:21", "04:11:39", "19 / 35", "00:17", "Vote · Stay Invested", "Paused · Sporespring" }.All(labels.Contains))
                throw new InvalidOperationException("The Index HUD did not present its protected, frozen vote state.");
            var start = strip.PointToScreen(new Point());
            var end = strip.PointToScreen(new Point(strip.ActualWidth, strip.ActualHeight));
            if (Math.Abs((start.X + end.X) / 2 - (screen.X + screen.Width / 2)) > 2 ||
                end.Y > screen.Bottom - 32 || start.Y < screen.Top + 220)
                throw new InvalidOperationException("The Index HUD did not fit below the vote controls.");

            var due = sample with { RotationFrozenAtMs = null, OperatorDueMs = 21_601_300,
                InOperator = true, Phase = IndexHudPhase.Running };
            strip.Update(due, 21_601_000);
            labels = Descendants<TextBlock>(strip).Select(text => text.Text).ToArray();
            if (!new[] { "Next vote", "In Operator", "Return in", "0.30 s" }.All(labels.Contains))
                throw new InvalidOperationException("The Index HUD did not show the Operator return or pending cash in.");
        }
        finally { window.Close(); }
    }

    private static void VerifyHudWindow()
    {
        var window = new MacroHudWindow();
        try
        {
            var id = Guid.NewGuid();
            var arrow = new MouseMovementHint(id, 20, -10);
            var snapshot = new MacroHudSnapshot(id, "HUD test", new(2, 5), null, false, arrow,
                new MacroCountdown(id, 25000, 30000, new(.75m, .2m)));
            var screen = new System.Drawing.Rectangle(-32000, -32000, 640, 360);
            if (!window.Present(screen, [snapshot], arrow))
                throw new InvalidOperationException("The macro HUD did not open in its offscreen test area.");
            window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            var view = (MacroHudView)window.Content;
            var origin = view.PointToScreen(new Point());
            var end = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
            if (!window.IsVisible || window.ShowActivated || ForegroundWindow.IsOwnWindowActive ||
                !WindowCaptureProtection.IsExcluded(handle) || !OverlayWindow.IsExcludedFromTaskSwitcher(handle) ||
                Math.Abs(origin.X - screen.X) > 1 || Math.Abs(origin.Y - screen.Y) > 1 ||
                Math.Abs(end.X - origin.X - screen.Width) > 2 || Math.Abs(end.Y - origin.Y - screen.Height) > 2)
                throw new InvalidOperationException($"The HUD window is not protected, passive, or sized to its target. {origin}–{end}");
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(640, 360, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(view);
            var pixel = new byte[4];
            image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            if (pixel[3] != 0)
                throw new InvalidOperationException("The HUD has a visible background.");
            int centerX = (int)(view.ActualWidth / 2), centerY = (int)(view.ActualHeight / 2);
            image.CopyPixels(new Int32Rect(centerX, centerY, 1, 1), pixel, 4, 0);
            if (pixel[3] != 0)
                throw new InvalidOperationException("The chevron has a visible shaft or background.");
            image.CopyPixels(new Int32Rect(centerX + 4, centerY - 3, 1, 1), pixel, 4, 0);
            if (pixel[3] is 0 or > 140)
                throw new InvalidOperationException("The next-route arrow is missing, opaque, or off center.");
        }
        finally { window.Close(); }
    }

    private static void VerifyMovementMap()
    {
        var original = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = [new(40, 100, 0)] };
        var second = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = [new(40, 0, 50)] };
        var profile = ScenarioProfile.Create("Map test") with { Actions = [original, second] };
        var editor = new EditorViewModel();
        editor.Load(profile, saved: true);
        var map = new MovementMap(editor.Update, () => { });
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorViewModel.Profile))
                map.ShowProfile(editor.Profile);
        };
        map.ShowProfile(profile);
        map.Measure(new Size(600, 260));
        map.Arrange(new Rect(0, 0, 600, 260));
        var canvas = Descendants<MovementMapCanvas>(map).Single();
        var routes = Descendants<ListBox>(map).Single();
        if (routes.Items.Count != 2 || canvas.SelectedId != original.Id)
            throw new InvalidOperationException("The map did not list both routes.");
        routes.SelectedIndex = 1;
        if (canvas.SelectedId != second.Id)
            throw new InvalidOperationException("Selecting a route in the list did not select its path.");
        routes.SelectedIndex = 0;
        var zoom = Descendants<Button>(map).Single(button => AutomationProperties.GetName(button) == "Zoom in");
        var fit = Descendants<Button>(map).Single(button => AutomationProperties.GetName(button) == "Fit all routes");
        zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Descendants<TextBlock>(map).Any(label => label.Text == "125%"))
            throw new InvalidOperationException("Zoom did not update the map.");
        fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var scale = Math.Min((canvas.ActualWidth - 30) / 100, (canvas.ActualHeight - 30) / 50);
        var midpoint = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2 - 25 * scale);
        canvas.EditPointAt(midpoint);
        if (editor.Profile.Actions[0].MousePath.Length != 2)
            throw new InvalidOperationException("Editing the route on the map did not add a point to the config.");
        canvas.EditPointAt(midpoint);
        if (editor.Profile.Actions[0].MousePath.Length != 1 || editor.Profile.Actions[1] != second)
            throw new InvalidOperationException("Removing a point affected the wrong route.");
        editor.Undo();
        if (editor.Profile.Actions[0].MousePath.Length != 2)
            throw new InvalidOperationException("A movement map edit was not undoable.");

        var firstListItem = routes.Items[0];
        editor.Update(editor.Profile.Actions[0] with { MousePath = [new(40, 240, 0)], MouseMapOrigin = new(-20, 0) });
        routes.SelectedIndex = 1;
        var secondMidpoint = new Point(canvas.ActualWidth / 2 + 50 * scale, canvas.ActualHeight / 2);
        canvas.EditPointAt(secondMidpoint);
        if (!ReferenceEquals(firstListItem, routes.Items[0]) || editor.Profile.Actions[1].MousePath.Length != 2)
            throw new InvalidOperationException("Editing an endpoint moved the viewport, another route, or its list entry.");

        Point[] points = [new(0, 0), new(40, 5), new(100, 0)];
        var path = new[] { new MouseSample(20, 40, 5), new MouseSample(40, 60, -5) }.ToImmutableArray();
        var moved = (Point[])points.Clone();
        MousePathPreview.MovePoint(points, moved, 0, 1, new(-10, 20));
        var start = MousePathPreview.Rebuild(path, moved);
        if (moved[0] != new Point(-10, 20) || moved[1] != points[1] || moved[^1] != points[^1] ||
            start.Sum(sample => sample.DeltaX) != 110 || start.Sum(sample => sample.DeltaY) != -20)
            throw new InvalidOperationException("Moving the start did not preserve the end on the map.");
        MousePathPreview.MovePoint(points, moved, 2, 1, new(20, -10));
        var end = MousePathPreview.Rebuild(path, moved);
        if (end.Sum(sample => sample.DeltaX) != 120 || end.Sum(sample => sample.DeltaY) != -10)
            throw new InvalidOperationException("Moving the endpoint did not update the route destination.");

        var mixedPoints = original with { MousePath = [
            new(8, 100, 0) { ControlPoint = true }, new(16, 0, 100), new(24, 100, 0)
        ] };
        editor.Load(ScenarioProfile.Create("Recorded bends") with { Actions = [mixedPoints] }, saved: true);
        scale = Math.Min((canvas.ActualWidth - 30) / 200, (canvas.ActualHeight - 30) / 100);
        var recordedCorner = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2 + 50 * scale);
        canvas.EditPointAt(recordedCorner);
        if (!editor.Profile.Actions[0].MousePath.SequenceEqual([mixedPoints.MousePath[0], new(24, 100, 100)]))
            throw new InvalidOperationException("An unmarked recorded corner was not editable as a route point.");
    }

    private static void VerifyHudPlacement()
    {
        if (MacroHudView.SnapPosition(new(278, 163), new(640, 360), new(100, 28)) != new HudAnchor(.5m, .5m) ||
            MacroHudView.SnapPosition(new(-12, 390), new(640, 360), new(100, 28)) != new HudAnchor(0, 1) ||
            MacroHudView.SnapPosition(new(270, 34), new(640, 360), new(100, 20)) != new HudAnchor(.5m, .1m))
            throw new InvalidOperationException("HUD placement does not snap or clamp its axes independently.");
        var profile = ScenarioProfile.Create("Place HUD") with { Macro = new() };
        foreach (var part in Enum.GetValues<HudPart>())
        {
            (HudAnchor Anchor, int? Size)? saved = null;
            var window = new MacroHudPlacementWindow(profile, new(1, 2), part, (anchor, size) => saved = (anchor, size))
                { ShowActivated = false };
            try
            {
                if (!window.Present(new(-32000, -32000, 640, 360)))
                    throw new InvalidOperationException("The placement overlay could not open.");
                var handle = new WindowInteropHelper(window).Handle;
                if (ForegroundWindow.IsOwnWindowActive || !WindowCaptureProtection.IsExcluded(handle) ||
                    !OverlayWindow.IsExcludedFromTaskSwitcher(handle))
                    throw new InvalidOperationException("The placement overlay is not capture-protected or stole focus during the test.");
                var size = Descendants<TextBox>(window).SingleOrDefault();
                if (part == HudPart.Timer)
                {
                    if (size is null || size.Text != MacroTimerOptions.DefaultFontSize.ToString())
                        throw new InvalidOperationException("Timer placement does not show its current size.");
                    size.Text = "48";
                }
                else if (size is not null)
                    throw new InvalidOperationException("Step placement showed timer controls.");
            }
            finally { window.Close(); }
            if (part == HudPart.Timer && saved != (new MacroTimerOptions().Position, 48))
                throw new InvalidOperationException("Closing timer placement did not save its new size and original position.");
        }
        var sample = new MacroCountdown(profile.Id, 3042, 30_000, default);
        if (MacroHudView.FormatTimer(sample) != "3.042" ||
            MacroHudView.FormatTimer(sample with { RemainingMs = 5 }) != "0.005" ||
            MacroHudView.FormatTimer(sample with { DurationMs = 60_000 }) != "00:04")
            throw new InvalidOperationException("The HUD timer does not format seconds and milliseconds correctly.");
    }

    private static async Task VerifyResetSetting(HwndSource source, string settingsPath, Preferences preferences)
    {
        var owner = new Window();
        _ = new WindowInteropHelper(owner).EnsureHandle();
        Preferences? edited = null;
        var settings = SettingsWindow.Create(owner, preferences, Path.GetDirectoryName(settingsPath)!, _ => { },
            updated => { edited = updated; return null; }, message => throw new InvalidOperationException(message));
        try
        {
            Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Reset macro steps")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F12)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F12)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Toggle Rathuum wave size (5 / 8)")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F18)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F18)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Reset playback and timers")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F19)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F19)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            settings.Close();
            if (edited?.MacroResetHotkey is not { Key: InputKey.F12 })
                throw new InvalidOperationException("Settings did not apply the recorded reset shortcut.");
            await edited.Save(settingsPath);
            var reloaded = await Preferences.Load(settingsPath);
            if (reloaded.MacroResetHotkey != edited.MacroResetHotkey ||
                edited.RathuumWaveHotkey is not { Key: InputKey.F18 } || reloaded.RathuumWaveHotkey != edited.RathuumWaveHotkey ||
                edited.PlaybackResetHotkey is not { Key: InputKey.F19 } || reloaded.PlaybackResetHotkey != edited.PlaybackResetHotkey)
                throw new InvalidOperationException("A shortcut did not survive reloading settings.");
        }
        finally { owner.Close(); }
    }

    private static async Task VerifyMacroRuntime(Preferences preferences)
    {
        var backend = new TestMacroBackend();
        int waveSize = 5;
        var runner = new MacroRunner(backend, backend, message => throw new InvalidOperationException(message), () => waveSize);
        var mouse = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = [new(8, 40, -20)], MouseDurationMs = 3500 };
        var profile = ScenarioProfile.Create("Macro runtime") with
        {
            Macro = new MacroOptions(Enabled: true, Step: true)
            {
                ShowStepHud = true, ShowRathuumWaveSize = true, ShowNextMovement = true, Timer = new() { DurationMs = 15_000 }
            },
            Actions =
            [
                new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(20) },
                new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(30) },
                new() { Kind = ActionKind.StartMacroTimer, RunWithPrevious = true, MacroTimerDurationMs = 42_500 },
                mouse
            ]
        };
        var other = profile with { Id = Guid.NewGuid(), Actions = profile.Actions.SetItem(2,
            profile.Actions[2] with { MacroTimerDurationMs = null }) };
        if (runner.ReadHud(profile) is not { RathuumWaveSize: 5 } initial ||
            MacroHudView.StepLabel(initial) != "1 / 2  ·  Rathuum 5")
            throw new InvalidOperationException("The Step HUD did not start at five kills.");
        if (MacroHudView.StepLabel(initial with { RathuumWaveSize = 1, PreLarvaMs = 2341 }) != "1 / 2  ·  Pre-Larva 2.4 s" ||
            MacroHudView.StepLabel(initial with { RathuumWaveSize = 1 }) != "1 / 2  ·  First kill")
            throw new InvalidOperationException("The Step HUD did not render the first-kill countdown and idle state.");
        waveSize = 8;
        if (runner.ReadHud(profile) is not { RathuumWaveSize: 8 } toggled ||
            MacroHudView.StepLabel(toggled) != "1 / 2  ·  Rathuum 8" ||
            runner.ReadHud(profile with { Macro = profile.Macro! with { ShowRathuumWaveSize = false } })?.RathuumWaveSize is not null)
            throw new InvalidOperationException("The Step HUD did not update or honor the Rathuum display option.");
        waveSize = 5;
        if (runner.ReadHud(profile)?.RathuumWaveSize != 5)
            throw new InvalidOperationException("The Step HUD did not return to five kills.");
        runner.Trigger(profile, preferences, false, backend);
        runner.Trigger(other, preferences, false, backend);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (runner.Active && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        if (runner.Active ||
            runner.ReadHud(profile) is not { Step: { Step: 2, Total: 2 }, Countdown.DurationMs: 42_500, NextMovement.DeltaY: -20 } ||
            runner.ReadHud(other) is not { Step.Step: 2, Countdown.DurationMs: 15_000 })
            throw new InvalidOperationException("The chained macro, timer or HUD state did not complete.");

        runner.Trigger(profile, preferences, false, backend);
        await runner.ResetSteps();
        if (runner.Active || runner.ReadHud(profile) is not { Step.Step: 1, Countdown: not null } ||
            runner.ReadHud(other) is not { Step.Step: 1 })
            throw new InvalidOperationException("Reset did not cancel the running Step macro and rewind both macros.");

        var larva = WaitingMacro(new() { Kind = ActionKind.LarvaDetect, LarvaTimeoutMs = 10_000 }, step: false);
        var rathuum = WaitingMacro(new() { Kind = ActionKind.RathuumKillFeed }, step: true);
        rathuum = rathuum with { Actions = rathuum.Actions.Insert(1, new()
        {
            Kind = ActionKind.KeyHold, Key = InputKey.F24, Hold = Timing.Fixed(10_000), RunWithPrevious = true
        }) };
        runner.Trigger(larva, preferences, false, backend);
        runner.Trigger(rathuum, preferences, false, backend);
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && (!Waiting(larva) || !Waiting(rathuum)))
            await Task.Delay(10);
        if (!Waiting(larva) || !Waiting(rathuum))
            throw new InvalidOperationException("The detector test macros did not start waiting with their timers active.");
        await runner.ResetPlayback().WaitAsync(TimeSpan.FromSeconds(3));
        if (runner.Active || runner.LastTriggered is not null || !backend.MonitorDisposed ||
            runner.ReadHud(profile) is not { Step.Step: 1, Countdown: null } ||
            runner.ReadHud(other)?.Countdown is not null || runner.ReadHud(larva)?.Countdown is not null ||
            runner.ReadHud(rathuum) is not { Step.Step: 1, Countdown: null, WaitingForHud: null } ||
            !backend.Sent.Contains(new(InputKind.KeyUp, (int)InputKey.F24)) ||
            backend.Sent.Any(item => item is { Kind: InputKind.KeyDown, Value: (int)InputKey.D2 }))
            throw new InvalidOperationException("Playback reset did not discard the countdowns, detector waits, and step state.");

        ScenarioProfile WaitingMacro(ScenarioAction gate, bool step) => profile with
        {
            Id = Guid.NewGuid(), Macro = profile.Macro! with { Step = step },
            Actions = [new() { Kind = ActionKind.StartMacroTimer }, gate, new() { Kind = ActionKind.KeyPress, Key = InputKey.D2 }]
        };
        bool Waiting(ScenarioProfile macro) => runner.ReadHud(macro) is { WaitingForHud: not null, Countdown: not null };
    }

    private sealed class TestMacroBackend : IInputBackend, IWindowTarget, IRunLog, ILarvaTarget, IRathuumTarget
    {
        private sealed class PendingMonitor(TestMacroBackend backend) : IRathuumMonitor
        {
            public RathuumGateReading Read() => new(new(1, 0, 0, 0, RathuumWaveStatus.Gathering, null));
            public bool TryConsume(long expectedLaunch) => false;
            public void Dispose() => backend.MonitorDisposed = true;
        }

        public System.Collections.Concurrent.ConcurrentQueue<InputEvent> Sent { get; } = new();
        public bool MonitorDisposed { get; private set; }
        public IRathuumMonitor StartRathuumMonitor(RathuumGateOptions options) => new PendingMonitor(this);
        public LarvaHudState ReadLarvaHud() => LarvaHudState.Idle;
        public bool IsPressed(InputEvent input) => false;
        public void Send(InputEvent input) => Sent.Enqueue(input);
        public bool TryArm() => true;
        public bool HasFocus() => true;
        public void VerifyPointer(InputEvent input) { }
        public void Write(string message, bool detailed = false) { }
    }



    private static TextBox FindNumber(Form form, string name) =>
        Descendants<TextBox>(form).Single(text => AutomationProperties.GetName(text) == name);

    private static void Click(Form form, string name) =>
        Descendants<Button>(form).Single(button => AutomationProperties.GetName(button) == name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
                yield return match;
            foreach (var matchChild in Descendants<T>(child))
                yield return matchChild;
        }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }
}
