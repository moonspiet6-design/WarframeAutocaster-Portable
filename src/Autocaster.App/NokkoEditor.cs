using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autocaster.Core;
using static Autocaster.App.Words;

namespace Autocaster.App;

internal enum NokkoBlock { Mushroom2, Mushroom1, Roar, Fourth, Operator }

internal sealed class NokkoRow(NokkoBlock block) : StepRow(Guid.NewGuid(), 0)
{
    private string _number = "";
    private string _summary = "";
    internal NokkoBlock Block { get; } = block;
    public override string Number => _number;
    public override string Kind => NokkoEditor.Title(Block);
    public override string Summary => _summary;
    public override ImageSource Icon => NokkoEditor.Icon(Block);

    internal void Update(NokkoOptions options, int count, int? returnMs)
    {
        var (key, enabled) = NokkoEditor.Control(Block, options);
        string number = Key(key);
        if (_number != number)
        {
            _number = number;
            Changed(nameof(Number));
        }
        string summary = count > 0 ? returnMs is int delay ? $"×{count} · {TimingUnits.Format(delay)} s" : $"×{count}"
            : !enabled || !options.Enabled ? "Off" : "Not in this cycle";
        if (_summary == summary)
            return;
        _summary = summary;
        UpdateDisplay();
    }
}

internal static class NokkoEditor
{
    private static readonly Lazy<ImageSource[]> Icons = new(LoadIcons);

    internal static string Title(NokkoBlock block) => block switch
    {
        NokkoBlock.Mushroom2 => "Brightbonnet",
        NokkoBlock.Mushroom1 => "Stinkbrain",
        NokkoBlock.Roar => "Roar",
        NokkoBlock.Fourth => "Sporespring",
        _ => "Operator & return"
    };

    internal static ImageSource Icon(NokkoBlock block) => Icons.Value[(int)block];

    private static ImageSource[] LoadIcons()
    {
        var sprite = new BitmapImage(new Uri("pack://application:,,,/WarframeAutocaster;component/NokkoIcons.png"));
        var transfer = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(255, 173, 114)), 1.5),
            Geometry.Parse("M 2,8 H 18 M 6,4 L 2,8 6,12 M 14,4 L 18,8 14,12"));
        ImageSource[] icons = [Crop(1), Crop(0), Crop(2), Crop(3), new DrawingImage(transfer)];
        foreach (var icon in icons)
            icon.Freeze();
        return icons;

        CroppedBitmap Crop(int slot) => new(sprite, new Int32Rect(slot * 70, 0, 70, 70));
    }

    internal static (InputKey Key, bool Enabled) Control(NokkoBlock block, NokkoOptions options) => block switch
    {
        NokkoBlock.Mushroom2 => (options.Mushroom2, options.Mushroom2Enabled),
        NokkoBlock.Mushroom1 => (options.Mushroom1, options.Mushroom1Enabled),
        NokkoBlock.Roar => (options.Roar, options.RoarEnabled),
        NokkoBlock.Fourth => (options.Fourth, options.FourthEnabled),
        _ => (options.Operator, options.OperatorEnabled)
    };

    internal static Form StepForm(NokkoBlock block, Func<ScenarioProfile> current, Action<ScenarioProfile> apply)
    {
        var options = current().Index!.Nokko!;
        var form = new Form { Width = 420, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var (_, enabled) = Control(block, options);
        var toggle = form.Check("Enabled", enabled);
        var fields = new Form();
        form.Children.Add(fields);
        fields.Edited += form.MarkDirty;
        fields.IsEnabled = enabled;
        toggle.Checked += (_, _) => fields.IsEnabled = true;
        toggle.Unchecked += (_, _) => fields.IsEnabled = false;

        Func<NokkoOptions, NokkoOptions> read = block switch
        {
            NokkoBlock.Mushroom2 => Mushrooms(second: true),
            NokkoBlock.Mushroom1 => Mushrooms(second: false),
            NokkoBlock.Roar => Roar(),
            NokkoBlock.Fourth => Fourth(),
            _ => Operator()
        };

        form.SetApply(() =>
        {
            var profile = current();
            var index = profile.Index!;
            bool active = toggle.IsChecked == true;
            var edited = active ? read(index.Nokko!) : index.Nokko!;
            edited = block switch
            {
                NokkoBlock.Mushroom2 => edited with { Mushroom2Enabled = active },
                NokkoBlock.Mushroom1 => edited with { Mushroom1Enabled = active },
                NokkoBlock.Roar => edited with { RoarEnabled = active },
                NokkoBlock.Fourth => edited with { FourthEnabled = active },
                _ => edited with { OperatorEnabled = active }
            };
            apply(profile with { Index = index with { Nokko = edited } });
        });
        return form;

        Func<NokkoOptions, NokkoOptions> Mushrooms(bool second)
        {
            var casts = fields.TextField("Casts per batch",
                (second ? options.Mushroom2Count : options.Mushroom1Count).ToString(CultureInfo.InvariantCulture), maxLength: 2);
            var originalGap = options.MushroomCastGap(second);
            var gap = fields.CompactTime("Delay after each cast", originalGap);
            var lead = fields.CompactTime("Refresh early", options.MushroomLead);
            return latest =>
            {
                if (!int.TryParse(casts(), out int count) || count is < 1 or > 12)
                    throw new ArgumentException("Casts per batch must be between 1 and 12.");
                var editedGap = gap();
                var shared = latest with { MushroomLead = lead() };
                return second
                    ? shared with { Mushroom2Count = count, Mushroom2Gap = editedGap == originalGap ? options.Mushroom2Gap : editedGap }
                    : shared with { Mushroom1Count = count, Mushroom1Gap = editedGap == originalGap ? options.Mushroom1Gap : editedGap };
            };
        }

        Func<NokkoOptions, NokkoOptions> Roar()
        {
            var extra = fields.CompactTime("Recast delay", options.RoarExtra);
            return latest => latest with { RoarExtra = extra() };
        }

        Func<NokkoOptions, NokkoOptions> Fourth()
        {
            var interval = fields.CompactTime("Repeat interval", options.FourthInterval);
            return latest => latest with { FourthInterval = interval() };
        }

        Func<NokkoOptions, NokkoOptions> Operator()
        {
            var interval = fields.CompactTime("Switch every", options.OperatorInterval);
            var stay = fields.CompactTime("Return after", options.OperatorStay);
            return latest => latest with { OperatorInterval = interval(), OperatorStay = stay() };
        }
    }

    internal static Form SettingsForm(Func<ScenarioProfile> current, Action<ScenarioProfile> apply, Form? previous = null)
    {
        var profile = current();
        var index = profile.Index!;
        var options = index.Nokko!;
        var form = new Form();
        form.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "EditorText");
        form.Heading("Index settings");
        var sections = new Grid();
        sections.ColumnDefinitions.Add(new());
        sections.ColumnDefinitions.Add(new());
        form.Children.Add(sections);

        var round = new Form();
        var choice = new ComboBox { MinHeight = 27, Margin = new(0, 0, 0, 7), SelectedIndex = -1 };
        choice.Items.Add("Manual");
        choice.Items.Add("Always stay");
        choice.Items.Add("Stay until timer, then cash in");
        choice.SelectedIndex = (int)index.Vote;
        round.AddField("End of round", choice);
        var extraction = new Form();
        var after = extraction.CompactTime("Cash in after", index.ExtractAfter, hours: true);
        extraction.Edited += round.MarkDirty;
        round.Children.Add(extraction);
        var roundSection = Section("Round decision", round, 0);
        void UpdateVote()
        {
            extraction.Visibility = choice.SelectedIndex == (int)IndexVoteMode.ExtractOnTimer ? Visibility.Visible : Visibility.Collapsed;
            roundSection.Header = extraction.Visibility == Visibility.Visible
                ? "Round decision  ·  Timed cash in" : "Round decision  ·  " + choice.SelectedItem;
        }
        UpdateVote();
        choice.SelectionChanged += (_, _) => { UpdateVote(); round.MarkDirty(); };

        var rotation = new Form();
        var enabled = rotation.Check("Enable rotation", options.Enabled);
        var gap = rotation.CompactTime("Default cast gap", options.CastGap, 95);
        var name = rotation.TextField("Config name", profile.Name);
        Section("Nokko rotation", rotation, 1);
        form.SetApply(() =>
        {
            var latest = current();
            var latestIndex = latest.Index!;
            var vote = (IndexVoteMode)choice.SelectedIndex;
            apply(latest with
            {
                Name = name().Trim(),
                Index = latestIndex with
                {
                    Vote = vote,
                    ExtractAfter = vote == IndexVoteMode.ExtractOnTimer ? after() : latestIndex.ExtractAfter,
                    Nokko = latestIndex.Nokko! with { Enabled = enabled.IsChecked == true, CastGap = gap() }
                }
            });
        });
        return form;

        Expander Section(string title, Form body, int column)
        {
            body.Margin = new(7, 1, 7, 5);
            body.Edited += form.MarkDirty;
            var expander = new Expander
            {
                Header = title, Content = body, VerticalAlignment = VerticalAlignment.Top,
                Margin = column == 0 ? new(0, 0, 6, 0) : new(6, 0, 0, 0),
                IsExpanded = (previous?.Children.OfType<Grid>().FirstOrDefault()?.Children[column] as Expander)?.IsExpanded == true
            };
            expander.SetResourceReference(FrameworkElement.StyleProperty, "MacroSettingsSection");
            Grid.SetColumn(expander, column);
            sections.Children.Add(expander);
            return expander;
        }
    }
}
