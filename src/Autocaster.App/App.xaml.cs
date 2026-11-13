using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Autocaster.Core;
using Autocaster.Infrastructure;
using Autocaster.Windows;

namespace Autocaster.App;

public partial class App : Application
{
    private Mutex? _instance;
    private WindowCaptureProtection? _captureProtection;
    private bool _ownsInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        _captureProtection = new();
        base.OnStartup(e);
        bool smoke = e.Args.Contains("--editor-test", StringComparer.Ordinal);
        string folder = AppContext.BaseDirectory;
        if (!smoke)
        {
            _instance = new Mutex(false, @"Local\WarframeAutocaster.Portable");
            try { _ownsInstance = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            if (!_ownsInstance)
            {
                MessageBox.Show("Warframe Autocaster Portable is already open.", "Warframe Autocaster Portable");
                Shutdown(1);
                return;
            }
        }

        try
        {
            var store = new ProfileService(Path.Combine(folder, "profiles"));
            var loaded = await store.LoadAll();
            List<ScenarioProfile> macros = [];
            var warnings = loaded.Errors.ToList();
            foreach (var profile in loaded.Profiles.Where(profile => profile.Macro is not null))
            {
                try { macros.Add(EditorConfig.Upgrade(profile with { Index = null })); }
                catch (ScenarioException error) { warnings.Add($"{profile.Name}: {error.Message}"); }
            }

            if (smoke)
            {
                await PortableSmoke.Run(macros, warnings);
                Shutdown(0);
                return;
            }

            string settingsPath = Path.Combine(folder, "settings.json");
            Preferences settings;
            try { settings = await Preferences.Load(settingsPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                settings = new();
                warnings.Add(error.Message);
            }
            ApplyTheme(settings.Dark);
            WindowCaptureProtection.SetEnabled(settings.CaptureProtection);
            string logs = Path.Combine(folder, "logs");
            var log = new FileRunLog(logs) { Detailed = settings.DetailedLog };
            log.Write("Portable application started.");
            var clock = new WindowsDeadlineClock { PrecisionMicroseconds = settings.SpinMicroseconds };
            var sharedInput = new SharedInputBackend(new SendInputBackend());
            var main = new MainWindow(sharedInput, clock, log, store, settings, settingsPath, logs, macros,
                string.Join(" | ", warnings));
            MainWindow = main;
            main.ShowOnStartup(e.Args.Contains("--show-no-activate", StringComparer.Ordinal));
        }
        catch (Exception error)
        {
            if (smoke)
                File.WriteAllText(Path.Combine(folder, "editor-test-failure.txt"), error.ToString());
            else
                MessageBox.Show(error.Message, "Warframe Autocaster Portable", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    internal static bool MainBuildIsOpen()
    {
        if (!Mutex.TryOpenExisting(@"Local\WarframeAutocaster", out var main))
            return false;
        main.Dispose();
        return true;
    }

    internal static void ApplyTheme(bool dark)
    {
#pragma warning disable WPF0001 // Fluent theme switching is marked experimental by WPF.
        Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        void Brush(string name, uint rgb)
        {
            var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Current.Resources[name] = brush;
        }

        Brush("EditorBackground", dark ? 0x151517u : 0xF7F6F4u);
        Brush("EditorChrome", dark ? 0x1A1A1Du : 0xFFFFFFu);
        Brush("EditorSurface", dark ? 0x242427u : 0xECEAE7u);
        Brush("EditorHover", dark ? 0x303033u : 0xE2E0DCu);
        Brush("EditorLine", dark ? 0x38383Cu : 0xCCC8C3u);
        Brush("EditorText", dark ? 0xF1F0EEu : 0x211D19u);
        Brush("EditorMuted", dark ? 0xB8B7BBu : 0x56514Du);
        Brush("EditorSelected", dark ? 0x37271Fu : 0xFCE5D5u);
        Brush("EditorAccent", dark ? 0xFF9C54u : 0xB84A0Cu);
        Brush("EditorAccentText", dark ? 0xFFAD72u : 0x963907u);
        Brush("EditorAccentLine", dark ? 0xA4623Bu : 0xA84B1Au);
        Brush("EditorAccentInk", dark ? 0x241308u : 0xFFFFFFu);
        Brush("EditorFocus", dark ? 0xF4DDCCu : 0x672B0Bu);
        foreach (string key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver" })
            Current.Resources[key] = Current.Resources["EditorAccent"];
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _captureProtection?.Dispose();
        if (_ownsInstance)
            _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
