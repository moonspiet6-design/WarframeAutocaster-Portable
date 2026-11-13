using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using Autocaster.Core;
using Autocaster.Windows;

namespace Autocaster.App;

internal sealed class MouseRecording : IDisposable
{
    private readonly HotkeyService _hotkeys;
    private readonly RawMouseCapture _raw;
    private readonly Action<string> _report;
    private readonly Action<Guid, ImmutableArray<MouseSample>> _completed;
    private readonly Action<bool> _recordingChanged;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private WindowTargetService? _target;
    private bool _manual;
    private Guid? _automaticAction;
    private bool _shortcutSuspended;

    internal MouseRecording(nint window, HotkeyService hotkeys, Action<string> report,
        Action<Guid, ImmutableArray<MouseSample>> completed, Action<bool> recordingChanged)
    {
        _hotkeys = hotkeys;
        _raw = new(window);
        _report = report;
        _completed = completed;
        _recordingChanged = recordingChanged;
        _watch.Tick += (_, _) =>
        {
            if (!CheckFocus())
                return;
            if (_raw.TimeLimitReached)
                Finish();
        };
    }

    internal Guid? ActionId { get; private set; }
    internal bool Recording => _raw.Recording;
    internal bool BlocksPlayback => _manual || _raw.Recording;

    internal void SelectAction(Guid? id)
    {
        _automaticAction = id;
        if (!BlocksPlayback)
            BindSelection();
    }

    internal void Arm(Guid id)
    {
        if (ActionId == id && _manual)
        {
            Reset();
            BindSelection();
            _report("Mouse recording canceled.");
            return;
        }

        Reset();
        if (Bind(id, manual: true))
            _report("Mouse recording armed. Focus Warframe and press Insert.");
    }

    private void BindSelection()
    {
        if (ActionId == _automaticAction)
            return;
        Reset();
        if (_automaticAction is Guid selected)
            Bind(selected, manual: false);
    }

    internal void SuspendShortcut(bool suspended)
    {
        _shortcutSuspended = suspended;
        if (suspended)
            _hotkeys.DisarmMouseRecording();
        else if (ActionId is not null)
            RegisterShortcut();
    }

    private bool Bind(Guid id, bool manual)
    {
        if (!_shortcutSuspended && !RegisterShortcut())
            return false;
        ActionId = id;
        _manual = manual;
        return true;
    }

    private bool RegisterShortcut()
    {
        if (_hotkeys.ArmMouseRecording(out string error))
            return true;
        _report($"Could not arm mouse recording. {error}");
        return false;
    }

    internal void Toggle()
    {
        if (ActionId is null || _shortcutSuspended)
            return;
        if (_raw.Recording)
        {
            if (CheckFocus())
                Finish();
            return;
        }

        var target = new WindowTargetService();
        if (!target.TryArm())
        {
            _report("Focus Warframe before pressing Insert.");
            return;
        }
        try
        {
            _raw.Start();
            _target = target;
            _watch.Start();
            _recordingChanged(true);
            _report("Recording mouse movement. Press Insert to finish.");
        }
        catch (Win32Exception error)
        {
            Cancel(error.Message);
        }
    }

    internal void Read(nint input)
    {
        if (_raw.Recording && CheckFocus() && !_raw.Read(input))
            Finish();
    }

    internal void Cancel(string? reason = null)
    {
        _automaticAction = null;
        Reset();
        if (reason is not null)
            _report(reason);
    }

    private void Reset()
    {
        _watch.Stop();
        _raw.Dispose();
        _hotkeys.DisarmMouseRecording();
        _target = null;
        ActionId = null;
        _manual = false;
        _recordingChanged(false);
    }

    private bool CheckFocus()
    {
        if (_target?.HasFocus() == true)
            return true;
        Reset();
        BindSelection();
        _report("Mouse recording canceled because Warframe lost focus.");
        return false;
    }

    private void Finish()
    {
        var id = ActionId;
        var path = _raw.Stop();
        Reset();
        if (id is not Guid actionId)
            return;
        if (!path.Any(sample => sample.DeltaX != 0 || sample.DeltaY != 0))
        {
            BindSelection();
            _report("No mouse movement recorded.");
            return;
        }
        _completed(actionId, path);
        BindSelection();
    }

    public void Dispose() => Cancel();
}
