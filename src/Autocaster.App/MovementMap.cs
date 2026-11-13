using System;
using System.Collections.Immutable;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autocaster.Core;

namespace Autocaster.App;

internal sealed class MovementMap : DockPanel
{
    private readonly MovementMapCanvas _canvas = new();
    private readonly ListBox _list = new() { MinWidth = 132, MaxWidth = 180 };
    private readonly TextBlock _zoom = Ui.Text("100%", 12);
    private readonly TextBlock _empty = Ui.Text("No recorded mouse movements");
    private readonly TextBox _pointLimit = new() { Width = 43, MaxLength = 3, Margin = new(4, 0, 5, 5),
        VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Button _applyLimit;
    private bool _updating;
    private Guid _profile;
    private ImmutableArray<ScenarioAction> _actions;
    private (Guid Id, string Detail)[] _listed = [];

    internal MovementMap(Action<ScenarioAction> apply, Action close)
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        _zoom.MinWidth = 42;
        _zoom.TextAlignment = TextAlignment.Center;
        _zoom.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(Button("−", "Zoom out", () => _canvas.ZoomAtCenter(1 / 1.25)));
        toolbar.Children.Add(_zoom);
        toolbar.Children.Add(Button("+", "Zoom in", () => _canvas.ZoomAtCenter(1.25)));
        toolbar.Children.Add(Button("Fit", "Fit all routes", _canvas.ResetView));
        toolbar.Children.Add(Button("Back", "Close movement map", close));
        var limitLabel = Ui.Text("Point limit", 12);
        limitLabel.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Insert(0, limitLabel);
        AutomationProperties.SetName(_pointLimit, "Mouse movement point limit");
        _pointLimit.SetResourceReference(StyleProperty, "DefaultTextBoxBaseStyle");
        toolbar.Children.Insert(1, _pointLimit);
        _applyLimit = Button("Apply", "Apply point limit", () =>
        {
            if (_canvas.SelectedId is not Guid id || !int.TryParse(_pointLimit.Text, out int limit) ||
                limit is < MouseRouteBuffer.MinPointLimit or > MouseRouteBuffer.MaxPointLimit)
                return;
            var action = _actions.First(item => item.Id == id);
            var path = MouseRouteBuffer.Compact(action.MousePath, limit);
            apply(action with { MousePointLimit = limit, MousePath = path });
        });
        toolbar.Children.Insert(2, _applyLimit);
        _pointLimit.TextChanged += (_, _) => UpdateLimitButton();
        var header = Ui.Header("Movement map", toolbar);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var hint = Ui.Text("Double-click a line to add a point or a point to remove it. Wheel to zoom.", 12);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
        hint.Margin = new(0, 7, 0, 0);
        SetDock(hint, Dock.Bottom);
        Children.Add(hint);

        _list.Margin = new(8, 0, 0, 0);
        _list.SetResourceReference(BackgroundProperty, "EditorChrome");
        _list.SetResourceReference(Control.BorderBrushProperty, "EditorLine");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Recorded routes");
        _list.SelectionChanged += (_, _) =>
        {
            if (!_updating && _list.SelectedItem is ListBoxItem { Tag: Guid id })
                _canvas.Select(id);
        };
        SetDock(_list, Dock.Right);
        Children.Add(_list);

        var layers = new Grid { ClipToBounds = true };
        layers.SetResourceReference(BackgroundProperty, "EditorChrome");
        AutomationProperties.SetName(_canvas, "Mouse movement map");
        layers.Children.Add(_canvas);
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.IsHitTestVisible = false;
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
        layers.Children.Add(_empty);
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = layers };
        border.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        Children.Add(border);
        _canvas.Selected += SelectListItem;
        _canvas.Changed += apply;
        _canvas.ZoomChanged += scale => _zoom.Text = $"{scale * 100:0}%";
    }

    internal void ShowProfile(ScenarioProfile profile, Guid? preferred = null)
    {
        if (_profile == profile.Id && _actions == profile.Actions)
        {
            if (preferred is Guid id)
                _canvas.Select(id);
            return;
        }
        bool reset = _profile != profile.Id;
        _profile = profile.Id;
        _actions = profile.Actions;
        var recordings = profile.Actions.Where(action => action.Kind == ActionKind.MouseMovement && !action.MousePath.IsEmpty)
            .ToImmutableArray();
        var entries = recordings.Select(action => (action.Id,
            $"{TimingUnits.Format(MouseRoute.ForwardDurationMs(action))} s" +
                (action.ReverseMousePath ? " · Reverse" : "") + (action.Enabled ? "" : " · Off"))).ToArray();
        _updating = true;
        if (!_listed.SequenceEqual(entries))
        {
            _listed = entries;
            _list.Items.Clear();
            for (int i = 0; i < entries.Length; i++)
            {
                var text = new StackPanel();
                var title = Ui.Text($"Movement {i + 1}", 12);
                title.Margin = new(0, 0, 0, 2);
                var detail = Ui.Text(entries[i].Item2, 11);
                detail.Margin = new(0);
                detail.SetResourceReference(TextBlock.ForegroundProperty, "EditorMuted");
                text.Children.Add(title);
                text.Children.Add(detail);
                _list.Items.Add(new ListBoxItem { Tag = entries[i].Id, Content = text, Padding = new(6, 4, 6, 4) });
            }
        }
        _canvas.SetRoutes(recordings, preferred, reset);
        _updating = false;
        SelectListItem(_canvas.SelectedId);
        _empty.Visibility = recordings.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectListItem(Guid? id)
    {
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, id));
        var action = _actions.FirstOrDefault(item => item.Id == id);
        _pointLimit.Text = action?.MousePointLimit.ToString() ?? "";
        _pointLimit.IsEnabled = action is not null;
        UpdateLimitButton();
    }

    private void UpdateLimitButton() => _applyLimit.IsEnabled = _canvas.SelectedId is not null &&
        int.TryParse(_pointLimit.Text, out int value) &&
        value is >= MouseRouteBuffer.MinPointLimit and <= MouseRouteBuffer.MaxPointLimit;

    private static Button Button(string label, string name, Action click)
    {
        var button = Ui.ToolbarButton(label, click);
        AutomationProperties.SetName(button, name);
        return button;
    }
}

internal sealed class MovementMapCanvas : MousePathPreview
{
    private sealed record Track(ScenarioAction Action, Point DefaultOrigin, Point[] Points);
    private Track[] _tracks = [];
    private Track? _selected;
    private Rect _routeBounds = Rect.Empty;
    private Rect _viewBounds = Rect.Empty;
    private Vector _pan;
    private Point? _pointer;
    private Guid? _pending;
    private bool _panning;
    private double _zoom = 1;

    internal event Action<ScenarioAction>? Changed;
    internal event Action<Guid?>? Selected;
    internal event Action<double>? ZoomChanged;
    internal Guid? SelectedId => _selected?.Action.Id;
    protected override bool CanMoveStart => true;
    protected override bool MoveHandlesIndividually => true;

    internal MovementMapCanvas() : base([])
    {
        Cursor = Cursors.Arrow;
        SetResourceReference(ForegroundProperty, "EditorMuted");
    }

    private static readonly DependencyProperty ForegroundProperty = TextBlock.ForegroundProperty.AddOwner(typeof(MovementMapCanvas),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    internal void SetRoutes(ImmutableArray<ScenarioAction> routes, Guid? preferred, bool reset)
    {
        var current = preferred ?? SelectedId;
        var previous = _tracks;
        bool sameOrder = !reset && previous.Select(track => (track.Action.Id, track.Action.Enabled, track.Action.ReverseMousePath))
            .SequenceEqual(routes.Select(action => (action.Id, action.Enabled, action.ReverseMousePath)));
        Point origin = new();
        _tracks = new Track[routes.Length];
        _routeBounds = Rect.Empty;
        for (int i = 0; i < routes.Length; i++)
        {
            var action = routes[i];
            var defaultOrigin = sameOrder ? previous[i].DefaultOrigin : origin;
            var start = action.MouseMapOrigin is { } saved ? new Point(saved.X, saved.Y) : defaultOrigin;
            var points = RoutePoints(action.MousePath, start);
            _tracks[i] = new(action, defaultOrigin, points);
            foreach (var point in points)
                _routeBounds.Union(point);
            if (action.Enabled && !action.ReverseMousePath)
                origin = points[^1];
        }
        _selected = _tracks.FirstOrDefault(track => track.Action.Id == current) ?? _tracks.FirstOrDefault();
        LoadPath(_selected?.Action.MousePath ?? []);
        if (reset || _viewBounds.IsEmpty)
            ResetView();
    }

    internal void Select(Guid id)
    {
        if (SelectedId == id || _tracks.FirstOrDefault(track => track.Action.Id == id) is not { } track)
            return;
        _selected = track;
        LoadPath(track.Action.MousePath);
        Selected?.Invoke(id);
    }

    protected override void OnPathChanged(ImmutableArray<MouseSample> path, Vector startShift)
    {
        if (_selected is { } track)
        {
            var origin = track.Action.MouseMapOrigin;
            if (startShift != default)
            {
                var start = track.Points[0] + startShift;
                origin = new((long)start.X, (long)start.Y);
            }
            Changed?.Invoke(track.Action with { MousePath = path, MouseMapOrigin = origin });
        }
    }

    internal void ResetView()
    {
        _zoom = 1;
        _pan = default;
        _viewBounds = _routeBounds;
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    internal void ZoomAtCenter(double factor) => Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2));

    private void Zoom(double factor, Point anchor)
    {
        var fit = WorldFit();
        var world = new Point((anchor.X - fit.X) / fit.Scale, (anchor.Y - fit.Y) / fit.Scale);
        _zoom = Math.Clamp(_zoom * factor, .25, 8);
        _pan += anchor - WorldFit().Map(world);
        ZoomChanged?.Invoke(_zoom);
        InvalidateVisual();
    }

    private Fit WorldFit()
    {
        if (_viewBounds.IsEmpty)
            return new(ActualWidth / 2 + _pan.X, ActualHeight / 2 + _pan.Y, 1);
        const double padding = 30;
        double scale = Math.Max(.000001, Math.Min((ActualWidth - padding) / Math.Max(1, _viewBounds.Width),
            (ActualHeight - padding) / Math.Max(1, _viewBounds.Height))) * _zoom;
        return new(ActualWidth / 2 + _pan.X - (_viewBounds.Left + _viewBounds.Width / 2) * scale,
            ActualHeight / 2 + _pan.Y - (_viewBounds.Top + _viewBounds.Height / 2) * scale, scale);
    }

    protected override Fit GetFit()
    {
        var fit = WorldFit();
        var origin = fit.Map(_selected?.Points[0] ?? default);
        return new(origin.X, origin.Y, fit.Scale);
    }

    protected override void DrawOtherRoutes(DrawingContext context, Brush surface)
    {
        var fit = WorldFit();
        var pen = new Pen((Brush)GetValue(ForegroundProperty), 1.6);
        foreach (var track in _tracks)
        {
            if (track == _selected)
                continue;
            DrawRoute(context, track.Points, fit, pen);
            context.DrawEllipse(surface, pen, fit.Map(track.Points[0]), 4, 4);
            context.DrawEllipse(surface, pen, fit.Map(track.Points[^1]), 4, 4);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_tracks.Length == 0)
            return;
        var point = e.GetPosition(this);
        var fit = WorldFit();
        var nearest = _selected is not null && FindHandle(point, GetFit()) >= 0 ? _selected : null;
        if (nearest is null)
        {
            double distance = 49;
            foreach (var track in _tracks)
            {
                var hit = FindSegment(point, fit, track.Points);
                if (hit.Index > 0 && (hit.DistanceSquared < distance ||
                    (hit.DistanceSquared == distance && track == _selected)))
                {
                    nearest = track;
                    distance = hit.DistanceSquared;
                }
            }
        }
        if (nearest == _selected)
            base.OnMouseLeftButtonDown(e);
        if (e.Handled || !CaptureMouse())
            return;
        _pointer = point;
        _pending = nearest?.Action.Id;
        _panning = false;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_pointer is not Point previous)
        {
            base.OnMouseMove(e);
            return;
        }
        var position = e.GetPosition(this);
        var delta = position - previous;
        _panning |= delta.Length >= SystemParameters.MinimumHorizontalDragDistance;
        if (_panning)
        {
            _pan += delta;
            _pointer = position;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_pointer is null)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }
        if (!_panning && _pending is Guid id)
            Select(id);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _pointer = null;
        _pending = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsMouseCaptured)
            Zoom(Math.Pow(1.18, e.Delta / 120d), e.GetPosition(this));
        e.Handled = true;
    }
}
