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
        bool editorTest = e.Args.Contains("--editor-test", StringComparer.Ordinal);
        bool smoke = editorTest || e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        string folder = smoke ? Path.Combine(Path.GetTempPath(), $"AutocasterSmoke-{Guid.NewGuid():N}") :
            AppContext.BaseDirectory;
        if (!smoke)
        {
            _instance = new Mutex(false, @"Local\WarframeAutocaster");
            try { _ownsInstance = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            if (!_ownsInstance)
            {
                MessageBox.Show("Warframe Autocaster is already running. Use the editor hotkey or the tray icon.", "Warframe Autocaster");
                Shutdown(1);
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(folder);
            string settingsPath = Path.Combine(folder, "settings.json");
            Preferences settings;
            string? warning = null;
            try { settings = await Preferences.Load(settingsPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                settings = new();
                warning = error.Message;
            }
            ApplyTheme(settings.Dark);
            WindowCaptureProtection.SetEnabled(settings.CaptureProtection);
            string logs = Path.Combine(folder, "logs");
            var log = new FileRunLog(logs) { Detailed = settings.DetailedLog };
            log.Write("Application started.");
            if (_captureProtection.InstallationError is not 0)
                log.Write($"Could not install the capture exclusion hook ({_captureProtection.InstallationError}).");
            if (editorTest)
                await VerifyBundledProfiles();
            var store = new ProfileService(Path.Combine(folder, "profiles"));
            var loaded = await store.LoadAll();
            var clock = new WindowsDeadlineClock { PrecisionMicroseconds = settings.SpinMicroseconds };
            var sharedInput = new SharedInputBackend(new SendInputBackend());
            var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(sharedInput), log);
            if (smoke)
            {
                int exitCode = 0;
                try
                {
                    if (editorTest)
                    {
                        await EditorSmoke.Run(store, settingsPath);
                        log.Write("Background editor test passed.");
                    }
                    else
                    {
                        await WindowsSmoke.Run(engine, clock, store);
                        log.Write("Local SendInput, safe stop, and shortcut capture tests passed.");
                    }
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(folder, "failure.txt"), error.ToString());
                    exitCode = 3;
                }
                finally
                {
                    await engine.Stop();
                    clock.Dispose();
                    await log.DisposeAsync();
                }
                Shutdown(exitCode);
                return;
            }

            List<ScenarioProfile> configs = [];
            var warnings = loaded.Errors.ToList();
            if (warning is not null)
                warnings.Add(warning);
            foreach (var profile in loaded.Profiles)
            {
                try { configs.Add(EditorConfig.Upgrade(profile)); }
                catch (ScenarioException error) { warnings.Add($"{profile.Name}: {error.Message}"); }
            }
            var main = new MainWindow(engine, sharedInput, clock, log, store, settings, settingsPath, logs, configs,
                string.Join(" | ", warnings));
            MainWindow = main;
            main.ShowOnStartup(e.Args.Contains("--show-no-activate", StringComparer.Ordinal));
        }
        catch (Exception error)
        {
            if (smoke)
                File.WriteAllText(Path.Combine(folder, "failure.txt"), error.ToString());
            else
                MessageBox.Show(error.ToString(), "Warframe Autocaster", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    private static async System.Threading.Tasks.Task VerifyBundledProfiles()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (!Directory.Exists(folder))
            throw new InvalidDataException("The portable profiles folder is missing.");
        var loaded = await new ProfileService(folder).LoadAll();
        if (loaded.Errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, loaded.Errors));

        var compiler = new ScenarioCompiler(new(new Random(0)));
        foreach (string name in new[] { "nidus aol", "nidus ol", "Nokko Index" })
        {
            var stored = loaded.Profiles.FirstOrDefault(profile => profile.Name == name)
                ?? throw new InvalidDataException($"Bundled config missing: {name}.");
            var profile = EditorConfig.Upgrade(stored);
            var plan = profile.Index?.Nokko is not null ? compiler.PreviewNokko(profile) : compiler.Compile(profile);
            if (plan.Events.IsEmpty)
                throw new InvalidDataException($"Bundled config has no planned inputs: {name}.");
        }
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
