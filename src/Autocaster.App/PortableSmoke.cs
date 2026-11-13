using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autocaster.Core;
using Autocaster.Infrastructure;
using Autocaster.Windows;

namespace Autocaster.App;

internal static class PortableSmoke
{
    internal static async Task Run(IReadOnlyList<ScenarioProfile> macros, IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0 || macros.Count == 0)
            throw new InvalidOperationException($"Could not load portable macros. {string.Join(" | ", warnings)}");

        string folder = Path.Combine(Path.GetTempPath(), $"AutocasterPortableSmoke-{Guid.NewGuid():N}");
        var store = new ProfileService(Path.Combine(folder, "profiles"));
        var compiler = new ScenarioCompiler(new());
        foreach (var macro in macros)
        {
            var steps = new MacroSteps();
            int count = macro.Macro?.Step == true ? steps.Position(macro).Total : 1;
            for (int index = 0; index < count; index++)
            {
                compiler.Compile(steps.Plan(macro), false);
                steps.Advance(macro);
            }
            await store.Save(macro);
        }
        var roundTrip = await store.LoadAll();
        if (roundTrip.Errors.Count != 0 || roundTrip.Profiles.Count != macros.Count ||
            roundTrip.Profiles.Any(profile => profile.Macro is null || profile.Index is not null))
            throw new InvalidOperationException("Portable macro profiles did not survive reopening.");

        App.ApplyTheme(dark: true);
        var log = new FileRunLog(Path.Combine(folder, "logs"));
        using var clock = new WindowsDeadlineClock();
        try
        {
            var editor = new MainWindow(new SendInputBackend(), clock, log, store, new(),
                Path.Combine(folder, "settings.json"), Path.Combine(folder, "logs"), macros, "");
            var frame = (Border)editor.Content;
            frame.Measure(new Size(640, 400));
            frame.Arrange(new Rect(0, 0, 640, 400));
            frame.UpdateLayout();
            var toolbar = ((DockPanel)frame.Child).Children.OfType<Border>().Select(border => border.Child)
                .OfType<WrapPanel>().Single();
            if (!toolbar.Children.OfType<ProfilePicker>().Any() ||
                !toolbar.Children.OfType<Button>().Select(button => button.Content).SequenceEqual(new object[] { "Macro settings" }) ||
                ((EditorViewModel)editor.DataContext).Profile.Macro is null || ForegroundWindow.IsOwnWindowActive)
                throw new InvalidOperationException("The macro-only editor or its navigation failed the background check.");
        }
        finally { await log.DisposeAsync(); }
    }
}
