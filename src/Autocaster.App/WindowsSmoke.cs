using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Autocaster.Core;
using Autocaster.Infrastructure;
using Autocaster.Windows;

namespace Autocaster.App;

internal static class WindowsSmoke
{
    internal static async Task Run(ScenarioExecutionEngine engine, WindowsDeadlineClock clock, ProfileService store)
    {
        var profile = ScenarioProfile.Create("SendInput test") with
        {
            Actions =
            [
                new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(200) },
                new() { Kind = ActionKind.KeyPress, Key = InputKey.A },
                new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(100) },
                new() { Kind = ActionKind.KeyPress, Key = InputKey.RightCtrl },
                new() { Kind = ActionKind.MouseWheel }
            ]
        };
        await store.Save(profile);
        var copy = await store.LoadAll();
        if (copy.Profiles.Single().Actions.Length != profile.Actions.Length || !clock.HighResolutionAvailable)
            throw new InvalidOperationException("Profile round trip or Windows high-resolution timer failed.");

        var target = new Window { Title = "Autocaster · local input verification", Width = 460, Height = 220, Topmost = true };
        var text = new TextBox { Text = "Testing keyboard input locally", VerticalContentAlignment = VerticalAlignment.Center };
        target.Content = text;
        List<(Key Key, bool Down)> received = [];
        text.PreviewKeyDown += (_, args) => received.Add((args.Key, true));
        text.PreviewKeyUp += (_, args) => received.Add((args.Key, false));
        target.Show();
        try
        {
            target.Activate();
            text.Focus();
            var guard = new WindowTargetService(new WindowInteropHelper(target).Handle);
            await WaitUntil(guard.TryArm, attempts: 1000);
            text.Focus();
            var keyboardOnly = profile with { Actions = profile.Actions.RemoveAt(profile.Actions.Length - 1) };
            if (!engine.Start(() => keyboardOnly, guard, new()))
                throw new InvalidOperationException("Test run was rejected.");
            await WaitUntil(() => engine.Status.State is RunState.Stopped or RunState.Error);
            (Key Key, bool Down)[] expected = [(Key.A, true), (Key.A, false), (Key.RightCtrl, true), (Key.RightCtrl, false)];
            if (engine.Status.State != RunState.Stopped || engine.Status.Stats.Sent != 4 ||
                !received.SequenceEqual(expected))
                throw new InvalidOperationException($"Unexpected input: {string.Join(", ", received)}; {engine.Status}");

            await engine.Stop();
            received.Clear();
            var repeated = profile with
            {
                Actions =
                [
                    new() { Kind = ActionKind.Repeat, RepeatCount = 3,
                        Children = [new() { Kind = ActionKind.KeyPress, Key = InputKey.C, Hold = Timing.Fixed(0) }] }
                ]
            };
            if (!engine.Start(() => repeated, guard, new()))
                throw new InvalidOperationException("Repeated key test was rejected.");
            await WaitUntil(() => engine.Status.State is RunState.Stopped or RunState.Error);
            await engine.Stop();
            (Key Key, bool Down)[] repetitions = [(Key.C, true), (Key.C, false), (Key.C, true),
                (Key.C, false), (Key.C, true), (Key.C, false)];
            if (engine.Status.State != RunState.Stopped || !received.SequenceEqual(repetitions))
                throw new InvalidOperationException($"Unexpected repeat input: {string.Join(", ", received)}; {engine.Status}");

            received.Clear();
            var sustained = profile with
            {
                Actions =
                [
                    new() { Kind = ActionKind.KeyDown, Key = InputKey.B },
                    new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(3000) }
                ]
            };
            if (!engine.Start(() => sustained, guard, new()))
                throw new InvalidOperationException("Hold test was rejected.");
            await WaitUntil(() => received.Contains((Key.B, true)));
            await engine.Stop();
            await WaitUntil(() => received.Contains((Key.B, false)));

            received.Clear();
            bool inputAllowed = true;
            var editorGuard = new WindowTargetService(new WindowInteropHelper(target).Handle, () => Volatile.Read(ref inputAllowed));
            if (!engine.Start(() => sustained, editorGuard, new()))
                throw new InvalidOperationException("Editor gate test was rejected.");
            await WaitUntil(() => received.Contains((Key.B, true)));
            Volatile.Write(ref inputAllowed, false);
            await WaitUntil(() => engine.Status.State == RunState.Stopped && received.Contains((Key.B, false)));
            if (editorGuard.TryArm())
                throw new InvalidOperationException("The run armed while the editor gate blocked input.");

            await engine.Stop();
            await VerifyShortcutCapture(target, engine);
        }
        finally
        {
            await engine.Stop();
            target.Close();
        }
    }

    private static async Task VerifyShortcutCapture(Window owner, ScenarioExecutionEngine engine)
    {
        var window = Ui.Dialog(owner, "Autocaster shortcut verification", 380, 130);
        var panel = new StackPanel { Margin = new(12) };
        window.Content = panel;
        bool capturing = false;
        var recorder = new InputRecorder(window, active => capturing = active);
        var shortcut = recorder.AddShortcut(panel, "Editor", new Hotkey(0, InputKey.F8));
        var record = (Button)((DockPanel)panel.Children[0]).Children[0];
        window.Show();
        try
        {
            var target = new WindowTargetService(new WindowInteropHelper(window).Handle);
            await WaitUntil(target.TryArm);
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!capturing || !record.IsKeyboardFocused)
                throw new InvalidOperationException("Shortcut capture did not take keyboard focus.");
            var press = ScenarioProfile.Create("Shortcut test") with
            {
                Actions =
                [
                    new() { Kind = ActionKind.Combination, Keys = [InputKey.LeftCtrl, InputKey.F7], Hold = Timing.Fixed(80) }
                ]
            };
            if (!engine.Start(() => press, target, new()))
                throw new InvalidOperationException("Shortcut capture test could not send keys.");
            await WaitUntil(() => engine.Status.State is RunState.Stopped or RunState.Error);
            if (capturing || shortcut() != new Hotkey(HotkeyModifiers.Control, InputKey.F7))
                throw new InvalidOperationException($"Shortcut capture returned {shortcut()}.");
        }
        finally
        {
            await engine.Stop();
            window.Close();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int attempts = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("The expected result was not observed.");
    }
}
