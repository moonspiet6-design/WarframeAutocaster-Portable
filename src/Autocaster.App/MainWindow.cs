using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Autocaster.Core;
using Autocaster.Infrastructure;
using Autocaster.Windows;
using Forms = System.Windows.Forms;
using WpfMouse = System.Windows.Input.Mouse;
using static Autocaster.App.Words;

namespace Autocaster.App;

internal sealed class MainWindow : Window
{
    private const double SelectionGutter = 16;
    private readonly EditorViewModel _editor = new();
    private readonly InputRecorder _recorder;
    private readonly MacroRunner _macros;
    private int _rathuumWaveSize = 5;
    private int ReadRathuumWaveSize() => Volatile.Read(ref _rathuumWaveSize);
    private Button? _configButton;
    private Grid? _editorWorkspace;
    private readonly ColumnDefinition _macroSettingsColumn = new() { Width = new(0), MaxWidth = 358 };
    private readonly Border _macroInspector = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
    private MacroSettingsPanel? _macroSettings;
    private MovementMap? _movementMap;
    private Guid? _macroId;
    private Guid? _emptyProfileId;
    private Guid? _deletingProfile;
    private string? _macroWarning;
    private readonly WindowsDeadlineClock _clock;
    private readonly FileRunLog _log;
    private readonly ProfileService _store;
    private readonly string _settingsPath;
    private readonly string _logs;
    private readonly List<ScenarioProfile> _profiles;
    private readonly ProfilePicker _profilePicker = new();
    private readonly ListBox _actions = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBlock _progress = Ui.Text("");
    private readonly TextBlock _stepCount = Ui.Text("", 12);
    private readonly TextBlock _saveStatus = Ui.Text("", 12);
    private readonly TextBlock _macroStatus = Ui.Text("", 12);
    private readonly TextBlock _emptyHint = Ui.Text("Add a step to start");
    private readonly TextBlock _notice = Ui.Text("");
    private static readonly TimeSpan ActiveRefresh = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleRefresh = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan IdleHudRefresh = TimeSpan.FromMilliseconds(85);
    private readonly DispatcherTimer _refresh = new() { Interval = IdleRefresh };
    private readonly DispatcherTimer _hudRefresh = new() { Interval = IdleHudRefresh };
    private readonly DispatcherTimer _instanceWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _mainBuildOpen;
    private readonly WindowTargetService _hudTarget = new();
    private MacroHudWindow? _hud;
    private MacroHudPlacementWindow? _hudPlacement;
    private bool _hudUnavailable;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private Preferences _settings;
    private HotkeyService? _hotkeys;
    private ForegroundWindow _returnFocus;
    private bool _editorOpen;
    private HwndSource? _source;
    private MouseRecording? _mouseRecording;
    private Guid? _newMouseAction;
    private RecordingIndicator? _recordingIndicator;
    private readonly OverlayCursor _overlayCursor = new();
    private MarqueeSelection? _marquee;
    private Forms.NotifyIcon? _tray;
    private Form? _form;
    private StepRow? _shown;
    private Point _dragOrigin;
    private Guid? _dragSource;
    private ScenarioProfile? _dragPreview;
    private HashSet<Guid>? _dragItems;
    private double _dragY;
    private GroupRow? _dropGroup;
    private Panel? _itemsHost;
    private Guid? _collapseOnClick;
    private Guid? _insertAfter;
    private GroupRow? _contextGroup;
    private HashSet<Guid> _contextActions = [];
    private HashSet<Guid> _contextGroupMembers = [];
    private Guid? _renameGroup;
    private bool _ignoreSelection;
    private Window? _settingsPopup;
    private bool _applyingDraft;
    private bool _switchingConfig;
    private bool _capturingInput;
    private bool _resettingPlayback;
    private bool _backgroundReadersEnabled;
    private bool _closing;
    private bool _closeReady;
    private Task _save = Task.CompletedTask;
    private Task _settingsSave = Task.CompletedTask;
    private long _lastFocusSample;
    private bool _gameFocus;
    private string? _hotkeyWarning;
    private string _message;

    public MainWindow(IInputBackend input, WindowsDeadlineClock clock, FileRunLog log, ProfileService store,
        Preferences settings, string settingsPath, string logs, IReadOnlyList<ScenarioProfile> profiles, string warning)
    {
        _macros = new(input, log, Report, ReadRathuumWaveSize);
        _clock = clock;
        _log = log;
        _store = store;
        _settings = settings;
        _settingsPath = settingsPath;
        _logs = logs;
        _profiles = [.. profiles];
        _message = warning;
        Title = "Warframe Autocaster · Portable";
        Topmost = true;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "EditorBackground");
        SetResourceReference(ForegroundProperty, "EditorText");
        UseLayoutRounding = true;
        Ui.UseBorderlessFrame(this, resizable: true);
        Ui.EnableSurfaceDrag(this, () => !MapOpen);
        Ui.HideCursorWhileGameFocused(this, _overlayCursor.GameHasFocus);
        Width = WindowPlacement.DefaultWidth;
        Height = WindowPlacement.DefaultHeight;
        MinWidth = 640;
        MinHeight = 400;
        RestorePlacement(settings.Placement);
        DataContext = _editor;
        _recorder = new(this, SetInputCapture);
        Build();

        _editor.PropertyChanged += EditorChanged;
        _actions.SelectionChanged += ActionSelected;
        _profilePicker.SelectionChanged += async (_, _) => await PickProfile();
        _profilePicker.RemoveRequested += async id => await DeleteProfile(id);
        _profilePicker.AddRequested += async () => await AddProfile();
        _refresh.Tick += (_, _) => RefreshStatus();
        _hudRefresh.Tick += (_, _) => RefreshHud();
        _instanceWatch.Tick += (_, _) => CheckMainBuild();
        _autosave.Tick += async (_, _) =>
        {
            _autosave.Stop();
            if (!_closing)
                await FlushAutosave();
        };
        IsVisibleChanged += (_, _) =>
        {
            _editorOpen = IsVisible && WindowState != WindowState.Minimized;
            if (IsVisible)
                _refresh.Start();
            else
                _refresh.Stop();
        };
        StateChanged += (_, _) =>
        {
            _editorOpen = IsVisible && WindowState != WindowState.Minimized;
            if (IsVisible && WindowState == WindowState.Minimized)
                HideEditor(restoreFocus: false);
        };
        PreviewKeyDown += HandleEditorKeys;

        var profile = settings.RestoreProfile ? _profiles.Find(p => p.Id == settings.LastProfile) : null;
        profile ??= _profiles.FirstOrDefault(p => p.Macro?.Enabled == true) ?? _profiles.FirstOrDefault();
        Open(profile ?? CreateProfile(), profile is not null, empty: profile is null);
    }

    private void Build()
    {
        var root = new DockPanel();
        var frame = new Border { BorderThickness = new Thickness(1), Child = root };
        frame.SetResourceReference(Border.BackgroundProperty, "EditorBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        Content = frame;

        var settings = Ui.ToolbarButton("Settings", OpenSettings);
        settings.Margin = new(0);
        var heading = new DockPanel();
        DockPanel.SetDock(settings, Dock.Right);
        heading.Children.Add(settings);
        var mark = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1,11 L 5,11 L 8,3 L 11,15 L 14,7 L 18,7"),
            StrokeThickness = 1.7, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mark.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "EditorAccentText");
        var logo = new Border { Child = mark, Width = 27, Height = 27, CornerRadius = new CornerRadius(5), Margin = new(0, 0, 8, 0) };
        logo.SetResourceReference(Border.BackgroundProperty, "EditorSelected");
        DockPanel.SetDock(logo, Dock.Left);
        heading.Children.Add(logo);
        var appTitle = Ui.Text("Warframe Autocaster", 16);
        appTitle.FontWeight = FontWeights.SemiBold;
        appTitle.VerticalAlignment = VerticalAlignment.Center;
        appTitle.Margin = new(0);
        heading.Children.Add(appTitle);
        heading.Margin = new(12, 7, 12, 6);
        var chrome = new Border { Child = heading };
        chrome.SetResourceReference(Border.BackgroundProperty, "EditorChrome");
        DockPanel.SetDock(chrome, Dock.Top);
        root.Children.Add(chrome);

        var commands = new WrapPanel { Margin = new(12, 2, 12, 6) };
        var navigation = new Border { Child = commands, BorderThickness = new Thickness(0, 0, 0, 1) };
        navigation.SetResourceReference(Border.BackgroundProperty, "EditorChrome");
        navigation.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        DockPanel.SetDock(navigation, Dock.Top);
        root.Children.Add(navigation);
        commands.Children.Add(_profilePicker);
        _configButton = Ui.ToolbarButton("Macro settings", ToggleProfileDetails);
        commands.Children.Add(_configButton);

        var status = new DockPanel();
        foreach (var label in new[] { _saveStatus, _progress, _macroStatus })
        {
            label.Margin = new(0);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
        }
        _saveStatus.Margin = new(0, 0, 14, 0);
        DockPanel.SetDock(_saveStatus, Dock.Left);
        DockPanel.SetDock(_macroStatus, Dock.Right);
        status.Children.Add(_saveStatus);
        status.Children.Add(_macroStatus);
        status.Children.Add(_progress);
        var footer = new StackPanel();
        footer.Children.Add(status);
        _notice.Visibility = Visibility.Collapsed;
        _notice.Margin = new(0, 4, 0, 0);
        footer.Children.Add(_notice);
        var footerSurface = new Border { Child = footer, Padding = new(12, 6, 12, 6), BorderThickness = new Thickness(0, 1, 0, 0) };
        footerSurface.SetResourceReference(Border.BackgroundProperty, "EditorChrome");
        footerSurface.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        DockPanel.SetDock(footerSurface, Dock.Bottom);
        root.Children.Add(footerSurface);

        var content = new Grid { Margin = new(12, 7, 12, 7) };
        root.Children.Add(content);
        var workspace = _editorWorkspace = new Grid();
        workspace.ColumnDefinitions.Add(new());
        workspace.ColumnDefinitions.Add(_macroSettingsColumn);
        content.Children.Add(workspace);
        var list = new DockPanel();
        workspace.Children.Add(list);
        _macroInspector.Margin = new(10, 0, 0, 0);
        _macroInspector.BorderThickness = new(1);
        _macroInspector.CornerRadius = new(6);
        _macroInspector.SetResourceReference(Border.BackgroundProperty, "EditorChrome");
        _macroInspector.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        Grid.SetColumn(_macroInspector, 1);
        workspace.Children.Add(_macroInspector);
        _movementMap = new(action => Modify(() => _editor.Update(action)), CloseMovementMap) { Visibility = Visibility.Collapsed };
        content.Children.Add(_movementMap);
        var add = Ui.ToolbarButton("+  Add step", style: "PrimaryToolbarAction");
        var addMenu = new ContextMenu { PlacementTarget = add, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        PopulateActionTypes(addMenu, addMenu.Items, includeDelay: true);
        add.ContextMenu = addMenu;
        add.Click += (_, _) =>
        {
            if (_capturingInput || _switchingConfig)
                return;
            _insertAfter = Selected?.Id;
            addMenu.IsOpen = true;
        };
        var steps = Ui.Header("Steps", add);
        var title = (TextBlock)steps.Children[^1];
        steps.Children.RemoveAt(steps.Children.Count - 1);
        DockPanel.SetDock(title, Dock.Left);
        title.Margin = new(0, 0, 8, 5);
        title.FontSize = 15;
        steps.Children.Add(title);
        _stepCount.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
        _stepCount.Margin = new(0);
        _stepCount.VerticalAlignment = VerticalAlignment.Center;
        var count = new Border { Child = _stepCount, Padding = new(5, 1, 5, 1), Margin = new(0, 0, 0, 5),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        count.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        steps.Children.Add(count);
        DockPanel.SetDock(steps, Dock.Top);
        list.Children.Add(steps);
        _actions.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "StepRow");
        _actions.Background = Brushes.Transparent;
        _actions.BorderThickness = new Thickness(0);
        _actions.Padding = new Thickness(0);
        KeyboardNavigation.SetTabNavigation(_actions, KeyboardNavigationMode.Continue);
        ScrollViewer.SetHorizontalScrollBarVisibility(_actions, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetScrollUnit(_actions, ScrollUnit.Pixel);
        _actions.ContextMenu = BuildActionMenu();
        _actions.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (RowAt(args.OriginalSource) is not null && args.OriginalSource is DependencyObject source &&
                Ui.Ancestor<Form>(source) is null)
                args.Handled = true;
        };
        _actions.ContextMenuOpening += (_, args) =>
        {
            if (_capturingInput || _dragPreview is not null)
            {
                args.Handled = true;
                return;
            }
            var row = args.CursorLeft < 0 ? Selected : WpfMouse.GetPosition(_actions).X < SelectionGutter
                ? null : RowAt(args.OriginalSource);
            SetContextTarget(row);
        };
        _actions.PreviewKeyDown += (_, args) =>
        {
            if (args.OriginalSource is not ListBoxItem { DataContext: StepRow row } ||
                args.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) ||
                Keyboard.Modifiers != ModifierKeys.None)
                return;
            args.Handled = true;
            if (row is GroupRow group)
            {
                Modify(() => _editor.ToggleGroup(group.Id));
                return;
            }
            if (_shown?.Id == row.Id)
            {
                if (ApplyDraft())
                    _actions.UnselectAll();
            }
            else if (ApplyDraft())
                Select(row.Id);
        };
        _actions.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _dragSource = _collapseOnClick = null;
            if (args.OriginalSource is DependencyObject source &&
                (Ui.Ancestor<Form>(source) is not null || Ui.Ancestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null))
                return;
            _dragOrigin = args.GetPosition(_actions);
            bool gutter = _dragOrigin.X < SelectionGutter;
            var row = gutter ? null : RowAt(args.OriginalSource);
            if (row is GroupRow group && Keyboard.Modifiers == ModifierKeys.None)
            {
                _collapseOnClick = group.Id;
                args.Handled = true;
                return;
            }
            _dragSource = (row as ActionRow)?.Id;
            if (_dragSource is Guid id && _shown?.Id == id && Keyboard.Modifiers == ModifierKeys.None)
                _collapseOnClick = id;
            if ((_dragSource is null || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) && _marquee?.Begin(args, gutter) == true)
            {
                _dragSource = null;
                args.Handled = true;
            }
            else if (row is ActionRow && Keyboard.Modifiers == ModifierKeys.None)
                args.Handled = true;
        };
        _actions.PreviewMouseMove += (_, args) =>
        {
            if (_dragSource is not Guid id || args.LeftButton != MouseButtonState.Pressed)
                return;
            var point = args.GetPosition(_actions);
            if (_dragPreview is null)
            {
                var delta = point - _dragOrigin;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                _collapseOnClick = null;
                if (!ApplyDraft() || !WpfMouse.Capture(_actions, CaptureMode.Element))
                {
                    _dragSource = null;
                    return;
                }
                BeginRowDrag(id);
            }
            args.Handled = true;
            UpdateDrag(point, id);
        };
        _actions.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (_dragPreview is not null)
            {
                args.Handled = true;
                if (_dragSource is Guid source)
                    UpdateDrag(args.GetPosition(_actions), source);
                FinishDrag(commit: true);
                return;
            }
            if (RowAt(args.OriginalSource) is GroupRow group && _collapseOnClick == group.Id)
            {
                _collapseOnClick = null;
                args.Handled = true;
                Modify(() => _editor.ToggleGroup(group.Id));
                return;
            }
            if (_collapseOnClick is Guid id && RowAt(args.OriginalSource)?.Id == id && ApplyDraft())
            {
                args.Handled = true;
                ShowDetails(null);
                _actions.UnselectAll();
            }
            else if (_dragSource is Guid clicked && RowAt(args.OriginalSource)?.Id == clicked &&
                Keyboard.Modifiers == ModifierKeys.None && ApplyDraft())
            {
                Select(clicked);
                if (_actions.ItemContainerGenerator.ContainerFromItem(Selected) is ListBoxItem item)
                    item.Focus();
                args.Handled = true;
            }
            _dragSource = _collapseOnClick = null;
        };
        _actions.LostMouseCapture += (_, _) =>
        {
            if (_dragPreview is not null && WpfMouse.Captured != _actions)
                FinishDrag(commit: false);
        };
        VirtualizingPanel.SetVirtualizationMode(_actions, VirtualizationMode.Recycling);
        var rows = new Grid();
        rows.Children.Add(_actions);
        var marqueeLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        rows.Children.Add(marqueeLayer);
        _marquee = new(_actions, marqueeLayer, SelectRange);
        _emptyHint.IsHitTestVisible = false;
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.VerticalAlignment = VerticalAlignment.Center;
        rows.Children.Add(_emptyHint);
        list.Children.Add(rows);
    }

    private void BeginRowDrag(Guid id)
    {
        _dragSource = id;
        _dragPreview = _editor.Profile;
        var selected = SelectedIds();
        _dragItems = selected.Contains(id) ? selected : [id];
        _dragY = _dragOrigin.Y;
        if (_shown is not null && _actions.Items.IndexOf(_shown) >=
            _actions.Items.IndexOf(_editor.Rows.First(row => row.Id == id)))
        {
            ShowDetails(null);
            _actions.UpdateLayout();
        }
        _editor.PreviewMove(_dragPreview, _dragItems);
    }

    private void UpdateDrag(Point point, Guid source)
    {
        int direction = Math.Sign(point.Y - _dragY);
        _dragY = point.Y;
        _dropGroup = null;
        if (point.X < 0 || point.X >= _actions.ActualWidth || point.Y < 0 || point.Y >= _actions.ActualHeight)
            return;
        var hit = _actions.InputHitTest(point);
        if (hit is DependencyObject element && Ui.Ancestor<System.Windows.Controls.Primitives.ScrollBar>(element) is not null)
            return;
        var realized = RealizedItems().ToArray();
        var item = realized.FirstOrDefault(candidate =>
        {
            double top = candidate.TranslatePoint(new Point(), _actions).Y;
            return point.Y >= top && point.Y < top + candidate.ActualHeight;
        });
        var sourceRow = _editor.Rows.First(action => action.Id == source);
        if (item?.DataContext is GroupRow group)
        {
            if (sourceRow.GroupId != group.Id)
                _dropGroup = group;
            return;
        }
        if (direction == 0)
            return;
        if (item?.DataContext is ActionRow)
        {
            int from = _actions.Items.IndexOf(sourceRow);
            var target = realized
                .Where(candidate => candidate.DataContext is ActionRow action && !_dragItems!.Contains(action.Id) &&
                    direction * (_actions.ItemContainerGenerator.IndexFromContainer(candidate) - from) > 0)
                .Select(candidate => (Row: (ActionRow)candidate.DataContext, Middle: HeaderMiddle(candidate)))
                .Where(candidate => direction * (point.Y - candidate.Middle) >= 0)
                .OrderByDescending(candidate => direction * candidate.Middle)
                .FirstOrDefault();
            if (target.Row is not null)
                MovePreview(target.Row.Id, after: direction > 0);
            return;
        }
        if (sourceRow.GroupId is not Guid groupId)
            return;
        int index = _actions.Items.IndexOf(sourceRow);
        int boundary = index;
        while (boundary + direction >= 0 && boundary + direction < _actions.Items.Count &&
            _actions.Items[boundary + direction] is StepRow next && next.GroupId == groupId)
            boundary += direction;
        if (_actions.ItemContainerGenerator.ContainerFromIndex(boundary) is ListBoxItem edge)
        {
            double y = edge.TranslatePoint(new Point(0, direction > 0 ? edge.ActualHeight : 0), _actions).Y;
            if (direction * (point.Y - y) >= 0)
                MovePreview(groupId, after: direction > 0, outside: true);
        }

        double HeaderMiddle(ListBoxItem candidate)
        {
            var header = candidate.Template.FindName("RowHeader", candidate) as FrameworkElement ?? candidate;
            var frame = candidate.Template.FindName("RowFrame", candidate) as Border;
            return header.TranslatePoint(new Point(0, header.ActualHeight / 2), _actions).Y -
                ((frame?.RenderTransform as TranslateTransform)?.Y ?? 0);
        }
    }

    private void MovePreview(Guid target, bool after, bool outside = false)
    {
        var layout = new ActionLayout(_dragPreview!);
        layout.Move(_dragItems!, target, after, outside);
        var next = layout.ToProfile();
        if (next == _dragPreview)
            return;
        var previous = SystemParameters.ClientAreaAnimation ? RealizedFrames().ToDictionary(entry => entry.Id, entry => entry.Top) : null;
        _dragPreview = next;
        _editor.PreviewMove(next, _dragItems!);
        _actions.UpdateLayout();
        if (previous is null)
            return;
        foreach (var entry in RealizedFrames())
        {
            var frame = entry.Frame;
            if (!previous.TryGetValue(entry.Id, out double oldTop))
                continue;
            double shift = oldTop - (entry.Top - ((frame.RenderTransform as TranslateTransform)?.Y ?? 0));
            if (Math.Abs(shift) < 1)
                continue;
            var translation = new TranslateTransform();
            frame.RenderTransform = translation;
            var animation = new DoubleAnimation(shift, 0, TimeSpan.FromMilliseconds(145))
            { DecelerationRatio = .8, FillBehavior = FillBehavior.Stop };
            translation.BeginAnimation(TranslateTransform.YProperty, animation);
        }
    }

    private IEnumerable<(Guid Id, Border Frame, double Top)> RealizedFrames()
    {
        foreach (var item in RealizedItems())
            if (item is { DataContext: StepRow row, Template: { } template } &&
                template.FindName("RowFrame", item) is Border frame)
                yield return (row.Id, frame, frame.TranslatePoint(new Point(), _actions).Y);
    }

    private IEnumerable<ListBoxItem> RealizedItems()
    {
        if (_itemsHost?.IsDescendantOf(_actions) != true)
            _itemsHost = FindItemsHost(_actions);
        return _itemsHost?.Children.OfType<ListBoxItem>() ?? Enumerable.Empty<ListBoxItem>();

        static Panel? FindItemsHost(DependencyObject node)
        {
            if (node is Panel { IsItemsHost: true } panel)
                return panel;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (FindItemsHost(VisualTreeHelper.GetChild(node, i)) is { } child)
                    return child;
            return null;
        }
    }

    private void FinishDrag(bool commit)
    {
        if (commit && _dropGroup is { } group)
            MovePreview(group.Id, after: true);
        var preview = _dragPreview!;
        _dragPreview = null;
        _dragItems = null;
        _dragSource = null;
        _dropGroup = null;
        if (WpfMouse.Captured == _actions)
            _actions.ReleaseMouseCapture();
        if (commit)
        {
            if (_shown is not null)
                ShowDetails(null);
            Modify(() => _editor.CommitMove(preview));
        }
        else
            _editor.RefreshAppearance();
    }

    private void PopulateActionTypes(ContextMenu menu, ItemCollection items, bool includeDelay)
    {
        ActionKind[] kinds = [ActionKind.KeyPress, ActionKind.KeyHold, ActionKind.MouseClick, ActionKind.MouseMovement,
            ActionKind.Wait, ActionKind.StartMacroTimer, ActionKind.LarvaDetect, ActionKind.RathuumKillFeed];
        foreach (var kind in kinds)
        {
            if (!includeDelay && kind == ActionKind.Wait)
                continue;
            var entry = new MenuItem { Header = Action(kind) };
            if (kind == ActionKind.StartMacroTimer)
                menu.Opened += (_, _) => entry.Visibility = _editor.Profile.Macro is null ? Visibility.Collapsed : Visibility.Visible;
            entry.Click += (_, _) => Add(kind);
            items.Add(entry);
        }
    }

    private ContextMenu BuildActionMenu()
    {
        static MenuItem Item(string text, Action run, string shortcut = "")
        {
            var item = new MenuItem { Header = text, InputGestureText = shortcut };
            item.Click += (_, _) => run();
            return item;
        }

        var menu = new ContextMenu();
        var add = new MenuItem { Header = "Add action" };
        PopulateActionTypes(menu, add.Items, includeDelay: false);
        menu.Items.Add(add);
        menu.Items.Add(Item("Add delay", () => Add(ActionKind.Wait)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add group", () => Modify(() => _editor.AddGroup(_insertAfter, _contextGroupMembers))));
        menu.Items.Add(new Separator());
        bool OnRow() => _contextActions.Count != 0;
        AddCommand("Copy", () => _editor.Copy(_contextActions), OnRow, "Ctrl+C");
        AddCommand("Paste", () => Modify(() => Paste(_insertAfter)), () => _editor.CanPaste, "Ctrl+V");
        AddCommand("Duplicate", () => Modify(() => Select(_editor.Duplicate(_contextActions, _insertAfter))), OnRow, "Ctrl+D");
        AddCommand("Delete", () => Modify(() => _editor.Delete(_contextActions)), OnRow, "Del");
        var listCommands = menu.Items.OfType<UIElement>().ToArray();
        var rename = Item("Rename", () => Modify(() => BeginRename(_contextGroup!.Id)));
        var ungroup = Item("Ungroup", () => Modify(() => _editor.Ungroup(_contextGroup!.Id)));
        menu.Items.Add(rename);
        menu.Items.Add(ungroup);
        menu.Opened += (_, _) =>
        {
            bool onGroup = _contextGroup is not null;
            foreach (var item in listCommands)
                item.Visibility = onGroup ? Visibility.Collapsed : Visibility.Visible;
            rename.Visibility = ungroup.Visibility = onGroup ? Visibility.Visible : Visibility.Collapsed;
        };

        void AddCommand(string text, Action run, Func<bool> enabled, string shortcut = "")
        {
            var item = Item(text, run, shortcut);
            menu.Items.Add(item);
            menu.Opened += (_, _) => item.IsEnabled = enabled();
        }
        return menu;
    }

    private void SetContextTarget(StepRow? row)
    {
        _contextGroup = row as GroupRow;
        _insertAfter = row?.Id;
        var selected = SelectedIds();
        _contextGroupMembers = selected;
        _contextActions = row is ActionRow action
            ? selected.Contains(action.Id) ? selected : [action.Id]
            : [];
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorViewModel.Rows))
        {
            if (_mouseRecording is { BlocksPlayback: true, ActionId: Guid id } && !_editor.Rows.Any(row => row.Id == id))
                _mouseRecording.Cancel("Mouse recording canceled because its action was removed.");
            RefreshRows();
        }
        if (args.PropertyName == nameof(EditorViewModel.Profile))
        {
            _macroSettings?.Refresh(_editor.Profile, updateFields: !_applyingDraft);
            RefreshRecordingTarget();
            RefreshPicker();
            ConfigureMacroHotkeys();
            var macro = _editor.Profile.Macro!;
            if (MapOpen)
            {
                if (EditorForms.HasMovementMap(_editor.Profile))
                    _movementMap?.ShowProfile(_editor.Profile);
                else
                    CloseMovementMap();
            }
            _macroStatus.Text = $"{new Hotkey((HotkeyModifiers)macro.Modifiers, macro.Key)}  ·  {(macro.Enabled ? (macro.Step ? "Step mode" : "Enabled") : "Off")}";
            if (_editor.Dirty)
                QueueAutosave();
        }
        if (args.PropertyName == nameof(EditorViewModel.Dirty))
        {
            RefreshPicker();
            RefreshSaveStatus();
        }
    }

    private IReadOnlyList<StepRow> VisibleRows => _editor.DisplayRows;

    private void RefreshRows(bool refreshEditor = true)
    {
        var rows = VisibleRows;
        var shown = _actions.ItemsSource as ObservableCollection<StepRow>;
        if (shown is null || !shown.SequenceEqual(rows))
        {
            var ids = _actions.SelectedItems.OfType<StepRow>().Select(row => row.Id).ToHashSet();
            _ignoreSelection = true;
            if (_dragPreview is not null && shown is not null)
            {
                var source = _dragSource;
                if (_dragItems?.Count == 1 && shown.Count == rows.Count &&
                    shown.Where(row => row.Id != source).SequenceEqual(rows.Where(row => row.Id != source)))
                {
                    var moving = shown.First(row => row.Id == source);
                    shown.Move(shown.IndexOf(moving), rows.TakeWhile(row => row != moving).Count());
                }
                else
                {
                    var expected = rows.ToHashSet();
                    for (int i = shown.Count - 1; i >= 0; i--)
                        if (!expected.Contains(shown[i]))
                            shown.RemoveAt(i);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (i < shown.Count && shown[i] == rows[i])
                            continue;
                        int at = shown.IndexOf(rows[i]);
                        if (at >= 0)
                            shown.Move(at, i);
                        else
                            shown.Insert(i, rows[i]);
                    }
                }
            }
            else
                _actions.ItemsSource = new ObservableCollection<StepRow>(rows);
            foreach (var row in rows.Where(row => ids.Contains(row.Id) && !_actions.SelectedItems.Contains(row)))
                _actions.SelectedItems.Add(row);
            _ignoreSelection = false;
        }
        _stepCount.Text = _editor.Rows.Count.ToString();
        _emptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (refreshEditor && !_applyingDraft && _dragPreview is null)
            ShowDetails(_shown?.Id == Editable?.Id ? Editable : null);
    }

    private StepRow? Selected => _actions.SelectedItem as StepRow;
    private StepRow? Editable => _actions.SelectedItems.Count == 1 ? Selected : null;
    private HashSet<Guid> SelectedIds() => _actions.SelectedItems.OfType<ActionRow>().Select(row => row.Id).ToHashSet();
    private StepRow? RowAt(object source) => source is DependencyObject element
        ? (ItemsControl.ContainerFromElement(_actions, element) as ListBoxItem)?.DataContext as StepRow
        : null;


    private void SelectRange(int first, int last, bool add)
    {
        if (!ApplyDraft())
            return;
        _ignoreSelection = true;
        if (!add)
            _actions.UnselectAll();
        if (first >= 0 && last >= 0)
            for (int index = Math.Min(first, last); index <= Math.Max(first, last) && index < _actions.Items.Count; index++)
            {
                var row = _actions.Items[index];
                if (row is ActionRow && !_actions.SelectedItems.Contains(row))
                    _actions.SelectedItems.Add(row);
            }
        _ignoreSelection = false;
        ShowDetails(null);
    }

    private void ActionSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_ignoreSelection || _dragPreview is not null ||
            (_dragSource is not null && Keyboard.Modifiers == ModifierKeys.None) ||
            WpfMouse.RightButton == MouseButtonState.Pressed ||
            args.OriginalSource != _actions ||
            Editable?.Id == _shown?.Id)
            return;
        if (!ApplyDraft())
        {
            _ignoreSelection = true;
            _actions.SelectedItem = _shown is null ? null : VisibleRows.FirstOrDefault(row => row.Id == _shown.Id);
            _ignoreSelection = false;
            return;
        }
        ShowDetails(Editable);
        RevealInlineEditor();
    }

    private void ToggleProfileDetails()
    {
        if (!ApplyDraft())
            return;
        if (MapOpen)
        {
            CloseMovementMap();
            return;
        }
        SetMacroSettingsOpen(_macroSettings is null);
    }

    private bool MapOpen => _movementMap?.Visibility == Visibility.Visible;

    private void SetMacroSettingsOpen(bool open)
    {
        if (open == (_macroSettings is not null))
            return;
        if (open)
        {
            _macroSettings = new(() => _editor.Profile, _editor.EditProfile, _recorder,
                _settings.MacroResetHotkey, SetMacroResetShortcut, PlaceHud, OpenMovementMap);
            _macroSettings.Edited += QueueAutosave;
            _macroInspector.Child = Ui.Scroll(_macroSettings, bottomPadding: 0);
            _macroSettingsColumn.MinWidth = 296;
            _macroSettingsColumn.Width = new(.82, GridUnitType.Star);
            _macroInspector.Visibility = Visibility.Visible;
        }
        else
        {
            _macroSettings!.Edited -= QueueAutosave;
            _macroSettings = null;
            _macroInspector.Child = null;
            _macroInspector.Visibility = Visibility.Collapsed;
            _macroSettingsColumn.MinWidth = 0;
            _macroSettingsColumn.Width = new(0);
        }
        RefreshSettingsButton();
        RefreshSaveStatus();
    }

    private void OpenMovementMap()
    {
        if (MapOpen || _movementMap is null || _editorWorkspace is null ||
            !EditorForms.HasMovementMap(_editor.Profile) || !ApplyDraft())
            return;
        _movementMap.ShowProfile(_editor.Profile, Selected?.Id);
        _movementMap.Visibility = Visibility.Visible;
        _editorWorkspace.Visibility = Visibility.Collapsed;
    }

    private void CloseMovementMap()
    {
        if (!MapOpen || _movementMap is null || _editorWorkspace is null)
            return;
        _movementMap.Visibility = Visibility.Collapsed;
        _editorWorkspace.Visibility = Visibility.Visible;
    }

    private void ShowDetails(StepRow? row)
    {
        if (_form is not null)
            _form.Edited -= QueueAutosave;
        _shown?.SetEditor(null);
        var profile = _editor.Profile;
        _shown = row;
        _form = row switch
        {
            ActionRow action => EditorForms.ActionForm(action.Action, _recorder, _editor.Update, ArmMouseRecording,
                inline: true, macro: true, timer: profile.Macro?.Timer,
                followsHudGate: profile.Macro?.Step == true && profile.Actions.TakeWhile(item => item.Id != action.Id)
                    .LastOrDefault(item => item.Enabled) is { } previous && ScenarioAction.IsHudGate(previous.Kind)),
            GroupRow group when _renameGroup == group.Id => GroupNameForm(group),
            _ => null
        };
        row?.SetEditor(_form);
        if (_form is not null)
        {
            if (row?.GroupColors is { } colors)
            {
                _form.Resources["EditorAccent"] = colors.Text;
                _form.Resources["EditorAccentInk"] = colors.Selected;
            }
            _form.Edited += QueueAutosave;
        }
        RefreshSettingsButton();
        RefreshSaveStatus();
        RefreshRecordingTarget();
    }

    private void RefreshSettingsButton() =>
        _configButton?.SetResourceReference(StyleProperty, _macroSettings is not null
            ? "ActiveToolbarAction" : "ToolbarAction");

    private void BeginRename(Guid id)
    {
        _renameGroup = id;
        Select(id);
        var editor = _form;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(editor, _form) && editor?.Children.OfType<TextBox>().FirstOrDefault() is { } input)
            {
                input.Focus();
                input.SelectAll();
            }
        });
    }

    private Form GroupNameForm(GroupRow group)
    {
        var form = new Form { MaxWidth = 390, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = form.TextField("Group name", group.Kind, 30);
        form.SetApply(() => _editor.RenameGroup(group.Id, name()));
        form.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter)
                return;
            args.Handled = true;
            if (!ApplyDraft())
                return;
            _renameGroup = null;
            _actions.UnselectAll();
        };
        return form;
    }

    private bool HasDraft => _form?.Dirty == true || _macroSettings?.Dirty == true;

    private void RefreshSaveStatus() =>
        _saveStatus.Text = _editor.Dirty || HasDraft ? "Unsaved changes" : EmptyProfile ? "" : "Saved";

    private void RevealInlineEditor()
    {
        if (_shown is not { } row || _form is not { } editor)
            return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_actions.IsVisible && ReferenceEquals(_form, editor))
            {
                _actions.ScrollIntoView(row);
                editor.BringIntoView();
            }
        });
    }

    private void QueueAutosave()
    {
        if (_closing)
            return;
        RefreshSaveStatus();
        _autosave.Stop();
        _autosave.Start();
    }

    private bool ApplyDraft()
    {
        _applyingDraft = true;
        try { return _form?.TryApply() != false && _macroSettings?.TryApply() != false; }
        finally { _applyingDraft = false; }
    }

    private void Select(Guid? id)
    {
        if (id is null)
            return;
        _ignoreSelection = true;
        _actions.SelectedItem = VisibleRows.FirstOrDefault(row => row.Id == id);
        if (Selected is { } row)
            _actions.ScrollIntoView(row);
        _ignoreSelection = false;
        ShowDetails(Editable);
        RevealInlineEditor();
    }

    private void Modify(Action operation)
    {
        if (!ApplyDraft())
            return;
        try { operation(); }
        catch (Exception error) when (error is ScenarioException or InvalidOperationException) { Report(error.Message); }
    }

    private void Add(ActionKind kind) => Modify(() =>
    {
        var id = _editor.Add(kind, _insertAfter, _settings);
        if (kind == ActionKind.MouseMovement)
            _newMouseAction = id;
        Select(id);
    });
    private void Copy() => _editor.Copy(SelectedIds());
    private void Paste(Guid? after) => Select(_editor.Paste(after));
    private void DuplicateActions() => Select(_editor.Duplicate(SelectedIds(), Selected?.Id));
    private void DeleteActions() => _editor.Delete(SelectedIds());

    private void HandleEditorKeys(object sender, KeyEventArgs args)
    {
        if (_dragPreview is not null)
        {
            if (args.Key == System.Windows.Input.Key.Escape)
                FinishDrag(commit: false);
            args.Handled = true;
            return;
        }
        if (_capturingInput)
            return;
        if (Keyboard.FocusedElement is TextBox or ComboBoxItem)
            return;
        if (MapOpen && args.Key == System.Windows.Input.Key.Escape)
        {
            CloseMovementMap();
            args.Handled = true;
            return;
        }
        if (MapOpen && args.Key is not (System.Windows.Input.Key.Z or System.Windows.Input.Key.Y))
            return;
        Action? action = null;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            action = args.Key switch
            {
                System.Windows.Input.Key.C => Copy,
                System.Windows.Input.Key.V => () => Modify(() => Paste(Selected?.Id)),
                System.Windows.Input.Key.D => () => Modify(DuplicateActions),
                System.Windows.Input.Key.Z => () => Modify(_editor.Undo),
                System.Windows.Input.Key.Y => () => Modify(_editor.Redo),
                _ => null
            };
        }
        if (args.Key == System.Windows.Input.Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            action = () => Modify(DeleteActions);
        if (action is null)
            return;
        args.Handled = true;
        action();
    }

    private void Open(ScenarioProfile profile, bool saved, bool empty = false)
    {
        _renameGroup = null;
        CloseMovementMap();
        SetMacroSettingsOpen(false);
        _newMouseAction = null;
        _mouseRecording?.Cancel();
        _emptyProfileId = empty ? profile.Id : null;
        _macroId = profile.Id;
        _autosave.Stop();
        ShowDetails(null);
        _ignoreSelection = true;
        _actions.UnselectAll();
        _ignoreSelection = false;
        _editor.Load(profile, saved || empty);
        _log.Write($"Opened macro “{profile.Name}”.");
        RefreshPicker();
    }

    private void RefreshPicker()
    {
        var active = _editor.Profile;
        _ignoreSelection = true;
        var rows = _profiles.Where(p => p.Id != active.Id).Append(active)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p =>
            {
                bool empty = p.Id == active.Id && EmptyProfile;
                string name = empty ? "No macros"
                    : p.Name + (p.Id == active.Id && _editor.Dirty ? " *" : "");
                return _profilePicker.Row(p.Id, name, empty);
            })
            .ToList();
        rows.Add(_profilePicker.AddRow());
        // A focused item may disappear on refresh. Keep focus inside the picker so its popup stays open.
        if (_profilePicker.IsDropDownOpen && (_profilePicker.IsKeyboardFocusWithin ||
            Keyboard.FocusedElement is ComboBoxItem focused && ItemsControl.ItemsControlFromItemContainer(focused) == _profilePicker))
            _profilePicker.Focus();
        _profilePicker.ItemsSource = rows;
        _profilePicker.SelectedItem = rows.First(item => Equals(item.Tag, active.Id));
        _ignoreSelection = false;
    }

    private bool EmptyProfile => _emptyProfileId == _editor.Profile.Id && !_editor.Dirty;

    private ScenarioProfile CreateProfile()
    {
        int number = 1;
        while (_profiles.Any(item => item.Name == $"Macro {number}") ||
            (!EmptyProfile && _editor.Profile.Name == $"Macro {number}"))
            number++;
        return ScenarioProfile.Create($"Macro {number}") with { Macro = new(), BetweenCycles = Timing.Fixed(0) };
    }

    private async Task PickProfile()
    {
        if (_ignoreSelection || _switchingConfig || _profilePicker.SelectedItem is not ComboBoxItem item)
            return;
        if (item.Tag is null)
        {
            if (_profilePicker.IsDropDownOpen)
                await AddProfile();
            else
                RefreshPicker();
        }
        else if (item.Tag is Guid id && id != _editor.Profile.Id && _profiles.Find(p => p.Id == id) is { } selected)
            await SwitchConfig(selected, true);
    }

    private Task AddProfile() => SwitchConfig(CreateProfile(), saved: false);

    private async Task SwitchConfig(ScenarioProfile selected, bool saved, bool empty = false)
    {
        if (_switchingConfig || selected.Id == _editor.Profile.Id)
            return;
        _switchingConfig = true;
        try
        {
            if (await FlushAutosave() && !_editor.Dirty && !HasDraft)
                Open(selected, saved, empty);
        }
        finally
        {
            _switchingConfig = false;
            RefreshPicker();
        }
    }

    private async Task DeleteProfile(Guid id)
    {
        if (_closing || _switchingConfig)
            return;
        var active = _editor.Profile;
        bool deletingActive = active.Id == id;

        _switchingConfig = true;
        _deletingProfile = id;
        try
        {
            if (deletingActive)
                _autosave.Stop();
            await _save;
            var removed = _profiles.Find(item => item.Id == id) ?? (deletingActive ? active : null);
            if (removed is null)
                return;
            if (_profiles.Any(item => item.Id == id))
                await _store.Delete(id);
            _profiles.RemoveAll(item => item.Id == id);
            if (deletingActive)
            {
                var next = _profiles.FirstOrDefault();
                Open(next ?? CreateProfile(), next is not null, empty: next is null);
            }
            ConfigureMacroHotkeys();
            if (removed.Macro is not null)
                await _macros.Remove(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report(error.Message);
        }
        finally
        {
            _deletingProfile = null;
            _switchingConfig = false;
            RefreshPicker();
            if (_editor.Dirty || HasDraft)
                QueueAutosave();
        }
    }

    private async Task<bool> FlushAutosave()
    {
        _autosave.Stop();
        if (_deletingProfile == _editor.Profile.Id || !ApplyDraft())
            return false;
        _autosave.Stop();
        bool saved = (!_editor.Dirty && _save.IsCompleted) || await SaveProfile();
        RefreshSaveStatus();
        return saved;
    }

    private async Task<bool> SaveProfile()
    {
        var snapshot = _editor.Profile;
        var operation = SaveProfileSnapshot(_save, snapshot);
        _save = operation;
        return await operation;
    }

    private async Task<bool> SaveProfileSnapshot(Task previous, ScenarioProfile snapshot)
    {
        try
        {
            await previous;
            await Task.Run(() => _store.Save(snapshot));
            int index = _profiles.FindIndex(p => p.Id == snapshot.Id);
            if (index < 0)
                _profiles.Add(snapshot);
            else
                _profiles[index] = snapshot;
            if (_emptyProfileId == snapshot.Id)
                _emptyProfileId = null;
            _editor.MarkSaved(snapshot);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ScenarioException)
        {
            Report(error.Message);
            return false;
        }
    }

    private void RefreshRecordingTarget() => _mouseRecording?.SelectAction(RecordingTarget());

    private Guid? RecordingTarget()
    {
        var profile = _editor.Profile;
        var actions = profile.Actions;
        var selected = Editable?.Id;
        return actions.FirstOrDefault(action => action.Id == selected && action.Kind == ActionKind.MouseMovement)?.Id ??
            actions.FirstOrDefault(action => action.Id == _newMouseAction && EmptyRoute(action))?.Id ??
            actions.FirstOrDefault(action => action.Id == profile.LastRecordedMouseAction &&
                action.Kind == ActionKind.MouseMovement && !action.MousePath.IsEmpty)?.Id ??
            actions.LastOrDefault(EmptyRoute)?.Id ??
            OnlyRoute();

        static bool EmptyRoute(ScenarioAction action) => action.Kind == ActionKind.MouseMovement && action.MousePath.IsEmpty;

        Guid? OnlyRoute()
        {
            // Legacy profiles with multiple recorded routes need an explicit selection the first time.
            var routes = actions.Where(action => action.Kind == ActionKind.MouseMovement).Take(2).ToArray();
            return routes.Length == 1 ? routes[0].Id : null;
        }
    }

    private void ArmMouseRecording(Guid id)
    {
        if (_mainBuildOpen)
        {
            Report("Close the main build before recording in Portable.");
            return;
        }
        if (_macros.Active)
        {
            Report("Stop macro playback before recording mouse movement.");
            return;
        }
        if (ApplyDraft())
            _mouseRecording?.Arm(id);
    }

    private void SaveMouseRecording(Guid id, ImmutableArray<MouseSample> path)
    {
        if (!ApplyDraft())
        {
            Report("Could not apply the recording. Correct the highlighted field before recording again.");
            return;
        }
        var profile = _editor.Profile;
        if (profile.Actions.FirstOrDefault(action => action.Id == id) is { Kind: ActionKind.MouseMovement } current)
        {
            path = MouseRouteBuffer.Compact(path, current.MousePointLimit);
            path = path.Select((sample, index) => sample with
            {
                Interpolate = sample.Interpolate || sample.OffsetMs - (index == 0 ? 0 : path[index - 1].OffsetMs) <= 16
            }).ToImmutableArray();
            var recorded = EditorConfig.UpgradeMouseTiming(current with { MousePath = path });
            _editor.EditProfile(profile with
            {
                Actions = profile.Actions.SetItem(profile.Actions.IndexOf(current), recorded),
                LastRecordedMouseAction = id
            });
            _newMouseAction = null;
            RefreshRecordingTarget();
            Report($"Mouse route recorded · {TimingUnits.Format(path[^1].OffsetMs)} s.");
            _ = FlushAutosave();
        }
    }

    private void OpenSettings()
    {
        if (_settingsPopup is { } opened)
        {
            opened.Close();
            return;
        }

        var popup = SettingsWindow.Create(this, _settings, _logs, SetInputCapture, edited =>
        {
            if (!_mainBuildOpen && _hotkeys?.ConfigureGlobals(edited.EditorHotkey, edited.MacroResetHotkey, edited.RathuumWaveHotkey,
                edited.PlaybackResetHotkey) is { Count: > 0 } errors)
            {
                _hotkeyWarning = string.Join(" | ", errors);
                return _hotkeyWarning;
            }
            _hotkeyWarning = null;
            if (_settings.Dark != edited.Dark)
            {
                App.ApplyTheme(edited.Dark);
                _editor.RefreshAppearance();
            }
            if (_settings.CaptureProtection != edited.CaptureProtection)
                WindowCaptureProtection.SetEnabled(edited.CaptureProtection);
            bool resetChanged = _settings.MacroResetHotkey != edited.MacroResetHotkey;
            _settings = edited;
            ConfigureMacroHotkeys();
            if (resetChanged)
                _macroSettings?.SyncReset(_settings.MacroResetHotkey);
            _clock.PrecisionMicroseconds = edited.SpinMicroseconds;
            _log.Detailed = edited.DetailedLog;
            _settingsSave = SaveSettings(_settingsSave);
            return null;
        }, Report);
        _settingsPopup = popup;
        popup.Closed += (_, _) => _settingsPopup = null;
        popup.Show();
    }

    private void SetInputCapture(bool active)
    {
        _capturingInput = active;
        _mouseRecording?.SuspendShortcut(active || _mainBuildOpen);
        if (_hotkeys is null)
            return;
        if (active)
        {
            _hotkeys.Clear();
        }
        else
        {
            RegisterGlobalHotkeys();
        }
    }

    private void ConfigureMacroHotkeys()
    {
        if (_hotkeys is null || _capturingInput || _mainBuildOpen)
            return;
        var active = _editor.Profile;
        var shortcuts = MacroProfiles()
            .Where(profile => profile.Macro is { Enabled: true })
            .ToDictionary(profile => profile.Id, profile => new Hotkey((HotkeyModifiers)profile.Macro!.Modifiers, profile.Macro.Key));
        var errors = _hotkeys.ConfigureMacros(shortcuts).ToList();
        if (_hotkeys.ConfigureMacroReset(_settings.MacroResetHotkey) is { } resetError)
            errors.Add(resetError);
        if (_hotkeys.ConfigureRathuumWaveSize(_settings.RathuumWaveHotkey) is { } waveError)
            errors.Add(waveError);
        if (_hotkeys.ConfigurePlaybackReset(_settings.PlaybackResetHotkey) is { } playbackError)
            errors.Add(playbackError);
        _macroWarning = errors.Count == 0 ? null : string.Join(" | ", errors);
        if (_backgroundReadersEnabled)
        {
            var gate = _profiles.Where(profile => profile.Id != active.Id).Prepend(active)
                .Where(profile => profile.Macro?.Enabled == true)
                .SelectMany(profile => profile.Actions)
                .FirstOrDefault(action => action.Enabled && action.Kind == ActionKind.RathuumKillFeed);
            _macros.ConfigureRathuumWarmup(gate is null ? null : gate.Rathuum ?? new());
        }
    }

    private string? SetMacroResetShortcut(Hotkey shortcut)
    {
        if (_closing || !ApplyDraft())
            return "Correct the highlighted field before changing the shortcut.";
        if (_settings.MacroResetHotkey == shortcut)
            return null;
        var active = _editor.Profile;
        if (shortcut == new Hotkey(0, InputKey.Insert) || shortcut == _settings.RathuumWaveHotkey ||
            shortcut == _settings.PlaybackResetHotkey ||
            _settings.EditorHotkey == shortcut ||
            _profiles.Where(profile => profile.Id != active.Id).Append(active).Any(profile =>
                profile.Macro is { Enabled: true } macro && new Hotkey((HotkeyModifiers)macro.Modifiers, macro.Key) == shortcut))
            return $"{shortcut} is already assigned or reserved.";
        if (!_mainBuildOpen && _hotkeys?.ConfigureMacroReset(shortcut) is { } error)
            return error;
        _settings = _settings with { MacroResetHotkey = shortcut };
        ConfigureMacroHotkeys();
        _settingsSave = SaveSettings(_settingsSave);
        return null;
    }

    private IEnumerable<ScenarioProfile> MacroProfiles()
    {
        var active = _editor.Profile;
        return _profiles.Where(profile => profile.Id != active.Id && profile.Macro is not null)
            .Concat(active.Macro is null || EmptyProfile ? [] : [active]);
    }

    private void TriggerMacro(Guid id)
    {
        if (_closing || _resettingPlayback || _hudPlacement is not null || _deletingProfile == id ||
            _mouseRecording?.BlocksPlayback == true || App.MainBuildIsOpen())
            return;
        bool selected = _editor.Profile.Id == id;
        if (selected && !ApplyDraft())
            return;
        var macro = selected ? _editor.Profile : _profiles.Find(profile => profile.Id == id);
        if (macro is not null)
        {
            _macros.Trigger(macro, _settings);
            RefreshHud();
        }
    }

    private void RefreshHud()
    {
        if (_mainBuildOpen || _closing || _hudPlacement is not null)
            return;
        var active = _editor.Profile;
        var items = _hudUnavailable ? [] : MacroProfiles()
            .Select(_macros.ReadHud).OfType<MacroHudSnapshot>()
            .Where(item => item.Step.Total > 0 || item.NextMovement is not null || item.Countdown is not null).ToArray();
        if (items.Length == 0 || !_hudTarget.TryGetFocusedClientBounds(out var bounds))
        {
            if (_hudRefresh.Interval != IdleHudRefresh)
                _hudRefresh.Interval = IdleHudRefresh;
            _hud?.Hide();
            return;
        }
        var refresh = items.Any(item => item.Countdown is { ShowsMilliseconds: true } || item.PreLarvaMs.HasValue) ? ActiveRefresh : IdleHudRefresh;
        if (_hudRefresh.Interval != refresh)
            _hudRefresh.Interval = refresh;
        {
            var direction = items.FirstOrDefault(item => item.MacroId == _macros.LastTriggered && item.NextMovement is not null)
                ?? items.FirstOrDefault(item => item.MacroId == active.Id && item.NextMovement is not null)
                ?? items.FirstOrDefault(item => item.NextMovement is not null);
            _hud ??= new();
            if (!_hud.Present(bounds, items, direction?.NextMovement, direction?.ArrowPosition))
            {
                _hud.Hide();
                _hudUnavailable = true;
                Report("Could not show the capture-protected macro HUD.");
            }
        }
    }

    private void PlaceHud(HudPart part)
    {
        if (_closing || _hudPlacement is not null || !ApplyDraft() || _editor.Profile.Macro is null)
            return;
        var profile = _editor.Profile;
        var position = _macros.ReadHud(profile)?.Step ?? default;
        if (position.Total == 0)
            position = new MacroSteps().Position(profile);
        if (position.Total == 0)
            position = new(1, 1);
        if (!_hudTarget.TryGetGameClientBounds(out var bounds))
            bounds = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;

        var placement = new MacroHudPlacementWindow(profile, position, part, (anchor, fontSize) =>
        {
            var current = _editor.Profile;
            if (current.Id != profile.Id || current.Macro is not { } options)
                return;
            var updated = part switch
            {
                HudPart.Step => options with { StepHudPosition = anchor },
                HudPart.Arrow => options with { ArrowPosition = anchor },
                _ => options with { Timer = (options.Timer ?? new()) with
                    { Position = anchor, FontSize = fontSize ?? MacroTimerOptions.DefaultFontSize } }
            };
            _editor.EditProfile(current with { Macro = updated });
        }, ReadRathuumWaveSize());
        _hud?.Hide();
        _hudRefresh.Interval = IdleHudRefresh;
        _hudPlacement = placement;
        if (!placement.Present(bounds))
        {
            _hudPlacement = null;
            placement.Close();
            Report("Could not open capture-protected HUD placement.");
            return;
        }
        placement.Closed += (_, _) =>
        {
            _hudPlacement = null;
            if (!_closing)
            {
                ShowActivated = true;
                Show();
                Activate();
                RefreshStatus();
            }
        };
        Hide();
    }

    private Task StopRuns() => _macros.StopAll();

    private async Task ResetPlayback()
    {
        if (_closing || _capturingInput || _resettingPlayback)
            return;
        _resettingPlayback = true;
        try
        {
            var stopped = _macros.ResetPlayback();
            RefreshHud();
            await stopped;
            Report(_macros.HasUnreleasedInput
                ? "Playback reset. Some held inputs could not be released. Press reset again to retry."
                : "Playback and timers reset.");
            RefreshHud();
        }
        finally { _resettingPlayback = false; }
    }

    private async Task SaveSettings(Task previous)
    {
        try
        {
            await previous;
            await (_settings with { LastProfile = _macroId, Placement = CapturePlacement() }).Save(_settingsPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report(error.Message);
        }
    }

    private void RefreshStatus()
    {
        var refresh = _macros.Active ? ActiveRefresh : IdleRefresh;
        if (_refresh.Interval != refresh)
            _refresh.Interval = refresh;
        var macroHud = _macros.ReadHud(_editor.Profile);
        if (_editor.Highlight(macroHud?.ActiveActionId) is { } highlighted && !MapOpen)
            _actions.ScrollIntoView(highlighted);
        if (Stopwatch.GetElapsedTime(_lastFocusSample).TotalMilliseconds >= 500)
        {
            _gameFocus = WindowTargetService.GameHasFocus();
            _lastFocusSample = Stopwatch.GetTimestamp();
        }
        _progress.Text = _gameFocus ? "Warframe focused" : "Warframe not focused";
        var waiting = macroHud?.WaitingForHud;
        var wave = macroHud?.RathuumWave;
        var preLarvaMs = macroHud?.PreLarvaMs;
        if (waiting is { } hudGate)
        {
            _progress.Text = preLarvaMs is double remaining ? $"Pre-Larva in {TimingUnits.FormatCountdown(remaining)} s" :
                hudGate.Rathuum is null ? "Waiting for Larva digits…" : wave switch
            {
                { Status: RathuumWaveStatus.AwaitingBaseline } baseline => hudGate.Rathuum.StartAtCurrentScore
                    ? "Reading current team score…" : $"Waiting for score 0{(baseline.Score is int seen ? $" (read {seen})" : "")}…",
                { Status: RathuumWaveStatus.Ready } => hudGate.Rathuum.FirstKillOnly
                    ? "Rathuum: first kill confirmed, waiting for launch" : "Rathuum: wave confirmed, waiting for launch",
                { Status: RathuumWaveStatus.Tentative } => "Rathuum: checking kill times…",
                { } observed => hudGate.Rathuum.FirstKillOnly
                    ? $"Rathuum: waiting for first confirmed kill · score {observed.Score?.ToString() ?? "—"}"
                    : $"Rathuum: {observed.NamedKills} / {ReadRathuumWaveSize()} kills · score {observed.Score?.ToString() ?? "—"}",
                _ => "Waiting for Rathuum kill feed…"
            };
        }
        if (waiting?.Rathuum is null && _editor.Profile.Actions.FirstOrDefault(action => action.Enabled && action.Kind == ActionKind.RathuumKillFeed) is { } rathuum)
            _progress.Text += rathuum.Rathuum?.FirstKillOnly == true ? " · Rathuum: first kill" : $" · Rathuum: {ReadRathuumWaveSize()} kills";
        _notice.Text = string.Join("  ·  ", new[] { _mainBuildOpen ? "Main build open · Portable hotkeys paused" : null,
            _message, _hotkeyWarning, _macroWarning, _log.Error }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        _notice.Visibility = _notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Report(string message)
    {
        _message = message;
        _log.Write(message);
        RefreshStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowCaptureProtection.TryExclude(hwnd);
        OverlayWindow.ExcludeFromTaskSwitcher(hwnd);
        _source = HwndSource.FromHwnd(hwnd);
        _hotkeys = new HotkeyService(hwnd);
        _mouseRecording = new(hwnd, _hotkeys, Report, SaveMouseRecording, ShowRecordingIndicator);
        _mainBuildOpen = App.MainBuildIsOpen();
        _mouseRecording.SuspendShortcut(_mainBuildOpen);
        RefreshRecordingTarget();
        _source?.AddHook(HotkeyMessage);
        RegisterGlobalHotkeys();
        _hudRefresh.Start();
        _instanceWatch.Start();
    }

    private void RegisterGlobalHotkeys()
    {
        if (_hotkeys is null || _capturingInput || _mainBuildOpen)
            return;
        var errors = _hotkeys.ConfigureGlobals(_settings.EditorHotkey, _settings.MacroResetHotkey,
            _settings.RathuumWaveHotkey, _settings.PlaybackResetHotkey);
        _hotkeyWarning = errors.Count == 0 ? null : string.Join(" | ", errors);
        ConfigureMacroHotkeys();
    }

    private void CheckMainBuild()
    {
        bool open = App.MainBuildIsOpen();
        if (_closing || open == _mainBuildOpen)
            return;
        _mainBuildOpen = open;
        if (open)
        {
            _mouseRecording?.Cancel();
            _mouseRecording?.SuspendShortcut(true);
            _hotkeys?.Clear();
            _hud?.Hide();
            _macros.ConfigureRathuumWarmup(null);
            _ = StopRuns();
        }
        else
        {
            _mouseRecording?.SuspendShortcut(_capturingInput);
            RefreshRecordingTarget();
            RegisterGlobalHotkeys();
        }
        RefreshStatus();
    }

    private void ShowRecordingIndicator(bool recording)
    {
        if (recording)
            (_recordingIndicator ??= new()).ShowOverGame();
        else if (_recordingIndicator is { IsVisible: true } indicator)
            indicator.Hide();
    }

    private nint HotkeyMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == OverlayCursor.SetCursorMessage && _overlayCursor.HideWhileGameFocused())
        {
            handled = true;
            return 1;
        }
        if (message == RawMouseCapture.InputMessage)
        {
            _mouseRecording?.Read(lParam);
            return 0;
        }
        if (message != HotkeyService.Message || _capturingInput || _mainBuildOpen || App.MainBuildIsOpen())
            return 0;
        if (_hotkeys?.TryGetMacro((int)wParam, lParam, out var macro) == true)
        {
            _ = Dispatcher.BeginInvoke(() => TriggerMacro(macro));
            handled = true;
            return 0;
        }
        switch ((HotkeyAction)(int)wParam)
        {
            case HotkeyAction.ResetMacroSteps when _hotkeys?.IsMessageFor(HotkeyAction.ResetMacroSteps, lParam) == true:
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    if (!_closing)
                        await _macros.ResetSteps();
                });
                break;
            case HotkeyAction.ToggleRathuumWaveSize when _hotkeys?.IsMessageFor(HotkeyAction.ToggleRathuumWaveSize, lParam) == true:
                _ = Dispatcher.BeginInvoke(ToggleRathuumWaveSize);
                break;
            case HotkeyAction.ResetPlayback when _hotkeys?.IsMessageFor(HotkeyAction.ResetPlayback, lParam) == true:
                _ = Dispatcher.BeginInvoke(async () => await ResetPlayback());
                break;
            case HotkeyAction.RecordMouse:
                if (_mouseRecording is { } recording)
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_macros.Active)
                            Report("Stop macro playback before recording mouse movement.");
                        else if (recording.Recording || ApplyDraft())
                            recording.Toggle();
                    });
                break;
            case HotkeyAction.ToggleEditor: Dispatcher.BeginInvoke(ToggleEditor, DispatcherPriority.Input); break;
            default: return 0;
        }
        handled = true;
        return 0;
    }

    private void ToggleRathuumWaveSize()
    {
        if (_closing || _capturingInput)
            return;
        int size = ReadRathuumWaveSize() == 5 ? 8 : 5;
        Volatile.Write(ref _rathuumWaveSize, size);
        _log.Write($"Rathuum wave size: {size} kills.");
        RefreshStatus();
        RefreshHud();
    }

    internal void ShowOnStartup(bool showWithoutActivation)
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        _backgroundReadersEnabled = true;
        ConfigureMacroHotkeys();
        if (!_mainBuildOpen && !showWithoutActivation && _settings.StartInTray && _hotkeys?.IsRegistered(HotkeyAction.ToggleEditor) == true)
            HideEditor(restoreFocus: false);
        else
            ShowEditor(activate: !showWithoutActivation);
    }

    private void ToggleEditor()
    {
        if (_closing || !IsEnabled)
            return;
        if (_hudPlacement is { } placement)
        {
            placement.Close();
            return;
        }
        _settingsPopup?.Close();
        if (_editorOpen)
            HideEditor(restoreFocus: true);
        else
            ShowEditor();
    }

    private void HideEditor(bool restoreFocus)
    {
        if (_tray is null)
        {
            _tray = new Forms.NotifyIcon { Text = Title, Icon = System.Drawing.SystemIcons.Application };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show editor", null, (_, _) => Dispatcher.BeginInvoke(ShowEditor));
            menu.Items.Add("Stop", null, (_, _) => _ = StopRuns());
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(Close));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowEditor);
        }
        _settingsPopup?.Close();
        if (HasDraft)
            _ = FlushAutosave();
        _tray.Visible = true;
        if (restoreFocus && ForegroundWindow.IsOwnWindowActive)
            _returnFocus.TryActivate();
        _returnFocus = default;
        if (IsVisible)
            Hide();
    }

    private void ShowEditor() => ShowEditor(activate: true);

    private void ShowEditor(bool activate)
    {
        if (_closing)
            return;
        if (!_editorOpen)
        {
            _returnFocus = ForegroundWindow.CaptureExternal();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (WindowState == WindowState.Normal)
                _returnFocus.MoveToSameMonitor(new WindowInteropHelper(this).Handle);
            _editorOpen = true;
            ShowActivated = activate;
            Show();
        }
        if (_tray is not null)
            _tray.Visible = false;
        if (activate)
            Activate();
        RefreshStatus();
    }

    private WindowPlacement? CapturePlacement()
    {
        var rect = RestoreBounds;
        if (!rect.IsEmpty && double.IsFinite(rect.Left) && double.IsFinite(rect.Top))
            return new(rect.Left, rect.Top, rect.Width, rect.Height);
        return double.IsFinite(Left) && double.IsFinite(Top)
            ? new(Left, Top, Width, Height) : _settings.Placement;
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is not { } p || !double.IsFinite(p.Left) || !double.IsFinite(p.Top) ||
            !double.IsFinite(p.Width) || !double.IsFinite(p.Height) || p.Width < MinWidth || p.Height < MinHeight)
            return;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!desktop.Contains(new Point(p.Left + 40, p.Top + 25)))
            return;
        Left = p.Left;
        Top = p.Top;
        Width = Math.Min(p.Width, desktop.Width);
        Height = Math.Min(p.Height, desktop.Height);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeReady)
            return;
        e.Cancel = true;
        if (_closing || _deletingProfile is not null)
            return;
        _closing = true;
        _autosave.Stop();
        try
        {
            _settingsPopup?.Close();
            _hudPlacement?.Close();
            if (!await FlushAutosave())
            {
                if (HasDraft)
                    Report("Correct the highlighted field before exiting.");
                return;
            }
            await _save;
            _mouseRecording?.Dispose();
            _hud?.Hide();
            _recordingIndicator?.Close();
            _recordingIndicator = null;
            await StopRuns();
            _backgroundReadersEnabled = false;
            _macros.ConfigureRathuumWarmup(null);
            await SaveSettings(_settingsSave);
            _refresh.Stop();
            _hudRefresh.Stop();
            _instanceWatch.Stop();
            _hud?.Close();
            _source?.RemoveHook(HotkeyMessage);
            _hotkeys?.Dispose();
            _tray?.ContextMenuStrip?.Dispose();
            _tray?.Dispose();
            _clock.Dispose();
            await _log.DisposeAsync();
            _closeReady = true;
            _ = Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        }
        finally { _closing = false; }
    }
}
