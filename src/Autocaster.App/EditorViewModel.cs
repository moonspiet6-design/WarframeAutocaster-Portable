using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Autocaster.Core;

namespace Autocaster.App;

public abstract class StepRow(Guid id, int index) : INotifyPropertyChanged
{
    private int _index = index;
    private bool _running;
    private Form? _inlineEditor;
    private Guid? _groupId;
    private GroupColors? _groupColors;
    private bool _groupEnd;
    private bool _moving;
    public Guid Id { get; } = id;
    public virtual string Number => $"{_index + 1:00}";
    public virtual ImageSource? Icon => null;
    public bool HasIcon => Icon is not null;
    public abstract string Kind { get; }
    public abstract string Summary { get; }
    public string Text => $"{(_running ? "▶  " : "")}{Kind}   {Summary}";
    public bool Running => _running;
    public object? InlineEditor => _inlineEditor;
    public bool HasInlineEditor => _inlineEditor is not null;
    public bool IsGroupHeader => this is GroupRow;
    public virtual bool Folded => false;
    public Guid? GroupId => _groupId;
    public bool InGroup => _groupId.HasValue;
    public bool GroupEnd => _groupEnd;
    public bool Moving => _moving;
    public GroupColors? GroupColors => _groupColors;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void UpdateDisplay()
    {
        Changed(nameof(Kind));
        Changed(nameof(Summary));
        Changed(nameof(Text));
    }

    internal void SetEditor(Form? editor)
    {
        if (_inlineEditor == editor)
            return;
        _inlineEditor = editor;
        Changed(nameof(InlineEditor));
        Changed(nameof(HasInlineEditor));
    }

    internal void SetRunning(bool running)
    {
        if (_running == running)
            return;
        _running = running;
        Changed(nameof(Text));
        Changed(nameof(Running));
    }

    internal void SetLayout(int index, ActionGroup? group, bool end, bool moving)
    {
        if (_index != index)
        {
            _index = index;
            Changed(nameof(Number));
        }
        var colors = group is null ? null : GroupColors.For(group.Color);
        if (_groupId != group?.Id || _groupColors != colors)
        {
            _groupId = group?.Id;
            _groupColors = colors;
            Changed(nameof(GroupId));
            Changed(nameof(InGroup));
            Changed(nameof(GroupColors));
        }
        if (_groupEnd != end)
        {
            _groupEnd = end;
            Changed(nameof(GroupEnd));
        }
        if (_moving != moving)
        {
            _moving = moving;
            Changed(nameof(Moving));
        }
    }

    protected void Changed(string property) => PropertyChanged?.Invoke(this, new(property));

    public override string ToString() => Text;
}

public sealed class GroupColors
{
    private static readonly uint[] DarkHues = [0x78C8CC, 0xC0A2EA, 0xA7BD87, 0xDBA2AA];
    private static readonly uint[] LightHues = [0x23777C, 0x754EA6, 0x50713C, 0xA25564];
    private static readonly GroupColors[] Dark = DarkHues.Select(rgb => new GroupColors(rgb, 0x151517, 0x242427, 0x39383D)).ToArray();
    private static readonly GroupColors[] Light = LightHues.Select(rgb => new GroupColors(rgb, 0xF7F6F4, 0xECEAE7, 0xCCC8C3)).ToArray();

    private GroupColors(uint hue, uint background, uint row, uint line)
    {
        Text = Blend(hue, hue, 0);
        Block = Blend(background, hue, .07);
        Row = Blend(row, hue, .05);
        Hover = Blend(row, hue, .10);
        Selected = Blend(row, hue, .16);
        Line = Blend(line, hue, .43);
        Edge = Blend(line, hue, .15);
    }

    public Brush Text { get; }
    public Brush Block { get; }
    public Brush Row { get; }
    public Brush Hover { get; }
    public Brush Selected { get; }
    public Brush Line { get; }
    public Brush Edge { get; }

    internal static GroupColors For(int index) =>
        (Application.Current?.TryFindResource("EditorBackground") is SolidColorBrush { Color.R: > 127 } ? Light : Dark)[index];

    private static Brush Blend(uint background, uint color, double ratio)
    {
        byte Channel(int shift) => (byte)Math.Round(((background >> shift) & 255) * (1 - ratio) + ((color >> shift) & 255) * ratio);
        var brush = new SolidColorBrush(Color.FromRgb(Channel(16), Channel(8), Channel(0)));
        brush.Freeze();
        return brush;
    }
}

public sealed class GroupRow(ActionGroup group, bool folded) : StepRow(group.Id, 0)
{
    private ActionGroup _group = group;
    private bool _folded = folded;
    public override bool Folded => _folded;
    public override string Number => "";
    public override string Kind => _group.Name;
    public override string Summary => Folded ? _group.Count.ToString() : _group.Count == 0 ? "Drop actions here" : "";

    internal void Update(ActionGroup edited, bool collapsed)
    {
        if (_group == edited && Folded == collapsed)
            return;
        _group = edited;
        _folded = collapsed;
        Changed(nameof(Folded));
        UpdateDisplay();
    }
}

public sealed class ActionRow(ScenarioAction action, int index) : StepRow(action.Id, index)
{
    private string? _number;
    private string? _timerDuration;
    public ScenarioAction Action { get; private set; } = action;
    public override string Number => _number ?? base.Number;
    public override string Kind => Action.Enabled ? Words.Action(Action.Kind) : $"○  {Words.Action(Action.Kind)}";
    public override string Summary => _timerDuration ?? EditorViewModel.Summary(Action);

    internal void Update(ScenarioAction edited)
    {
        if (Action == edited)
            return;
        Action = edited;
        UpdateDisplay();
    }

    internal void SetMacroDisplay(string? number, string? timerDuration)
    {
        if (_number != number)
        {
            _number = number;
            Changed(nameof(Number));
        }
        if (_timerDuration != timerDuration)
        {
            _timerDuration = timerDuration;
            UpdateDisplay();
        }
    }
}


public sealed class EditorViewModel : INotifyPropertyChanged
{
    private readonly List<ScenarioProfile> _undo = [];
    private readonly Stack<ScenarioProfile> _redo = [];
    private ScenarioProfile _profile = ScenarioProfile.Create("Config 1") with { BetweenCycles = Timing.Fixed(0) };
    private ScenarioProfile? _saved;
    private ImmutableArray<ScenarioAction> _clipboard = [];
    private ActionRow? _runningRow;
    private readonly HashSet<Guid> _folded = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public ScenarioProfile Profile => Volatile.Read(ref _profile);
    public bool Dirty => !ReferenceEquals(Profile, _saved);
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool CanPaste => !_clipboard.IsEmpty;
    public IReadOnlyList<ActionRow> Rows { get; private set; } = [];
    public IReadOnlyList<StepRow> DisplayRows { get; private set; } = [];

    public void Load(ScenarioProfile profile, bool saved)
    {
        _undo.Clear();
        _redo.Clear();
        _folded.Clear();
        _saved = saved ? profile : null;
        Publish(profile);
    }

    public void MarkSaved(ScenarioProfile profile)
    {
        if (profile.Id == Profile.Id)
        {
            _saved = profile;
            Changed(nameof(Dirty));
        }
    }

    public void EditProfile(ScenarioProfile edited) => Change(edited);

    public ActionRow? Highlight(Guid? id)
    {
        if (_runningRow?.Id == id)
            return null;
        _runningRow?.SetRunning(false);
        _runningRow = id is Guid current ? Rows.FirstOrDefault(row => row.Id == current) : null;
        _runningRow?.SetRunning(true);
        return _runningRow;
    }

    public Guid Add(ActionKind kind, Guid? after, Preferences settings)
    {
        var action = new ScenarioAction
        {
            Kind = kind,
            RunWithPrevious = kind == ActionKind.StartMacroTimer,
            Hold = kind == ActionKind.KeyPress ? EditorConfig.KeyPressHold : EditorConfig.DelayTiming(Timing.Fixed(settings.DefaultHoldMs)),
            Delay = EditorConfig.DelayTiming(Timing.Fixed(settings.DefaultWaitMs))
        };
        Insert(after, [action]);
        return action.Id;
    }

    public void Update(ScenarioAction edited)
    {
        int index = IndexOf(edited.Id);
        if (index < 0)
            throw new InvalidOperationException("The action is no longer selected.");
        if (Profile.Actions[index] != edited)
            SetActions(Profile.Actions.SetItem(index, edited));
    }

    public void Delete(IReadOnlySet<Guid> ids)
    {
        if (ids.Count != 0)
            EditLayout(layout => layout.Delete(ids));
    }

    public void Copy(IReadOnlySet<Guid> ids) =>
        _clipboard = Profile.Actions.Where(action => ids.Contains(action.Id)).ToImmutableArray();

    public Guid? Paste(Guid? after) => InsertCopies(after, _clipboard);

    public Guid? Duplicate(IReadOnlySet<Guid> ids, Guid? after) =>
        InsertCopies(after, Profile.Actions.Where(action => ids.Contains(action.Id)));

    private Guid? InsertCopies(Guid? after, IEnumerable<ScenarioAction> actions)
    {
        var copies = actions.Select(action => action with { Id = Guid.NewGuid() }).ToImmutableArray();
        if (copies.IsEmpty)
            return null;
        Insert(after, copies);
        return copies[0].Id;
    }

    internal Guid AddGroup(Guid? after, IReadOnlySet<Guid>? selected)
    {
        var layout = new ActionLayout(Profile);
        var id = layout.AddGroup(after, selected);
        Change(layout.ToProfile());
        return id;
    }

    internal void RenameGroup(Guid id, string name) => EditLayout(layout => layout.Rename(id, name));
    internal void Ungroup(Guid id) => EditLayout(layout => layout.Ungroup(id));
    internal void ToggleGroup(Guid id)
    {
        if (!_folded.Add(id))
            _folded.Remove(id);
        RefreshAppearance();
    }

    internal void RefreshAppearance() => ShowRows(Profile, null);

    internal void PreviewMove(ScenarioProfile preview, IReadOnlySet<Guid> moving) => ShowRows(preview, moving);

    internal void CommitMove(ScenarioProfile preview)
    {
        if (preview == Profile)
        {
            RefreshAppearance();
            return;
        }
        foreach (var group in preview.ActionGroups)
            if (!Profile.ActionGroups.Any(previous => previous.Id == group.Id && previous.Count == group.Count))
                _folded.Remove(group.Id);
        Change(preview);
    }

    public void Undo()
    {
        if (!CanUndo)
            return;
        _redo.Push(Profile);
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Publish(previous);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next))
            return;
        _undo.Add(Profile);
        Publish(next);
    }

    private void Insert(Guid? after, ImmutableArray<ScenarioAction> actions) => EditLayout(layout => layout.Insert(after, actions));

    private void EditLayout(Action<ActionLayout> edit)
    {
        var layout = new ActionLayout(Profile);
        edit(layout);
        Change(layout.ToProfile());
    }

    private int IndexOf(Guid id)
    {
        var actions = Profile.Actions;
        for (int i = 0; i < actions.Length; i++)
            if (actions[i].Id == id)
                return i;
        return -1;
    }

    private void SetActions(ImmutableArray<ScenarioAction> actions) => Change(Profile with { Actions = actions });

    private void Change(ScenarioProfile profile)
    {
        ScenarioCompiler.ValidateProfile(profile);
        if (profile == Profile)
            return;
        _undo.Add(Profile);
        if (_undo.Count > 100)
            _undo.RemoveAt(0);
        _redo.Clear();
        Publish(profile);
    }

    private void Publish(ScenarioProfile profile)
    {
        Volatile.Write(ref _profile, profile);
        ShowRows(profile, null);
        Changed(nameof(Profile));
        Changed(nameof(Dirty));
    }

    private void ShowRows(ScenarioProfile profile, IReadOnlySet<Guid>? moving)
    {
        var existing = Rows.ToDictionary(row => row.Id);
        var headers = DisplayRows.OfType<GroupRow>().ToDictionary(row => row.Id);
        var rows = new List<ActionRow>(profile.Actions.Length);
        var visible = new List<StepRow>(profile.Actions.Length + profile.ActionGroups.Length);
        int index = 0;
        int step = 0;
        ActionKind? previous = null;
        foreach (var group in profile.ActionGroups)
        {
            while (index < group.Start)
                AddRow(null, end: false, shown: true);
            bool folded = _folded.Contains(group.Id);
            var header = headers.GetValueOrDefault(group.Id) ?? new GroupRow(group, folded);
            header.Update(group, folded);
            header.SetLayout(index, group, folded || group.Count == 0, moving: false);
            visible.Add(header);
            int end = group.Start + group.Count;
            while (index < end)
                AddRow(group, index == end - 1, !folded);
        }
        while (index < profile.Actions.Length)
            AddRow(null, end: false, shown: true);
        Rows = rows;
        if (!DisplayRows.SequenceEqual(visible))
            DisplayRows = visible;
        if (_runningRow is not null && !rows.Contains(_runningRow))
            _runningRow = null;
        Changed(nameof(Rows));

        void AddRow(ActionGroup? group, bool end, bool shown)
        {
            var action = profile.Actions[index];
            var row = existing.GetValueOrDefault(action.Id) ?? new ActionRow(action, index);
            row.Update(action);
            row.SetLayout(index++, group, end, moving?.Contains(action.Id) == true);
            string? number = null;
            if (profile.Macro?.Step == true)
            {
                if (!action.Enabled)
                    number = "—";
                else if (action.Kind == ActionKind.Wait)
                    number = "Auto";
                else
                    number = step > 0 && MacroSteps.Continues(action, previous) ? "Auto" : $"{++step:00}";
                if (action.Enabled)
                    previous = action.Kind;
            }
            string? timer = row.Action.Kind == ActionKind.StartMacroTimer && profile.Macro is { } macro
                ? $"{TimingUnits.Format(action.MacroTimerDurationMs ?? (macro.Timer ?? new()).DurationMs)} s" : null;
            row.SetMacroDisplay(number, timer);
            rows.Add(row);
            if (shown)
                visible.Add(row);
        }
    }

    internal static string Summary(ScenarioAction action) => action.Kind switch
    {
        ActionKind.KeyPress => Words.Key(action.Key),
        ActionKind.KeyHold => $"{Words.Key(action.Key)} · {TimingUnits.Format(action.Hold)}",
        ActionKind.Wait => TimingUnits.Format(action.Delay),
        ActionKind.RathuumKillFeed => RathuumSummary(action.Rathuum ?? new()),
        ActionKind.LarvaDetect => $"Timeout {TimingUnits.Format(action.LarvaTimeoutMs ?? HudGate.DefaultTimeoutMs)} s",
        ActionKind.MouseClick => $"{Words.Mouse(action.Button)} · {TimingUnits.Format(action.Hold)}",
        ActionKind.MouseMovement => action.MousePath.IsEmpty ? "No recording" :
            $"{TimingUnits.Format(MouseRoute.ForwardDurationMs(action))} s{(action.ReverseMousePath ? " · Reverse" : "")}",
        _ => ""
    };

    private static string RathuumSummary(RathuumGateOptions options) =>
        options.FirstKillOnly ? $"First kill · {options.FirstKillLeadMs} ms lead" : $"{options.ReadCorrectionMs} ms correction";

    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}
