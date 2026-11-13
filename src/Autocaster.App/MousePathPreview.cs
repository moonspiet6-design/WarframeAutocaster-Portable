using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Autocaster.Core;

namespace Autocaster.App;

internal class MousePathPreview : FrameworkElement
{
    private static readonly DependencyProperty AccentProperty = RegisterBrush("RouteAccent", Brushes.DarkOrange);
    private static readonly DependencyProperty SurfaceProperty = RegisterBrush("RouteSurface", Brushes.Transparent);
    private static readonly DependencyProperty GridProperty = RegisterBrush("RouteGrid", Brushes.DimGray);
    private ImmutableArray<MouseSample> _path;
    private readonly Action<ImmutableArray<MouseSample>> _changed;
    private int[] _handles = [];
    private Point[] _points = [];
    private Point[]? _original;
    private Point _startDrag;
    private bool _dragged;
    private Fit _dragFit;
    private int _selected = -1;
    private int _radius;

    protected readonly record struct Fit(double X, double Y, double Scale)
    {
        internal Point Map(Point point) => new(X + point.X * Scale, Y + point.Y * Scale);
    }

    protected virtual bool CanMoveStart => false;
    protected virtual bool MoveHandlesIndividually => false;

    internal MousePathPreview(ImmutableArray<MouseSample> path, Action<ImmutableArray<MouseSample>>? changed = null)
    {
        SetResourceReference(AccentProperty, "EditorAccent");
        SetResourceReference(SurfaceProperty, "EditorBackground");
        SetResourceReference(GridProperty, "EditorLine");
        _changed = changed ?? (_ => { });
        LoadPath(path);
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    protected void LoadPath(ImmutableArray<MouseSample> path)
    {
        _path = path;
        _points = RoutePoints(path);
        _handles = Enumerable.Range(1, path.Length).Where(i => i == path.Length ||
            path[i - 1].ControlPoint || _points[i] != _points[i - 1]).ToArray();
        InvalidateVisual();
    }

    protected static Point[] RoutePoints(ImmutableArray<MouseSample> path, Point origin = default)
    {
        var points = new Point[path.Length + 1];
        points[0] = origin;
        for (int i = 0; i < path.Length; i++)
            points[i + 1] = points[i] + new Vector(path[i].DeltaX, path[i].DeltaY);
        return points;
    }

    private static DependencyProperty RegisterBrush(string name, Brush fallback) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(MousePathPreview),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth <= 24 || ActualHeight <= 24)
            return;

        var accent = (Brush)GetValue(AccentProperty);
        var surface = (Brush)GetValue(SurfaceProperty);
        var grid = (Brush)GetValue(GridProperty);
        for (int y = 8; y < ActualHeight; y += 12)
            for (int x = 8; x < ActualWidth; x += 12)
                context.DrawEllipse(grid, null, new Point(x, y), .8, .8);
        DrawOtherRoutes(context, surface);
        if (_path.IsEmpty)
            return;

        var fit = _original is null ? GetFit() : _dragFit;
        var routePen = new Pen(accent, 1.8);
        var handlePen = new Pen(accent, 1.5);
        var fineHandlePen = new Pen(accent, .8);
        DrawRoute(context, _points, fit, routePen);
        foreach (int index in _handles)
        {
            bool endpoint = index == _points.Length - 1;
            double radius = index == _selected ? 5 : endpoint ? 4 : HandleRadius(index, fit.Scale);
            context.DrawEllipse(endpoint ? accent : surface,
                radius < 2 ? fineHandlePen : handlePen, fit.Map(_points[index]), radius, radius);
        }
        double startRadius = _selected == 0 ? 5 : 4;
        context.DrawEllipse(surface, handlePen, fit.Map(_points[0]), startRadius, startRadius);
    }

    protected virtual void DrawOtherRoutes(DrawingContext context, Brush surface) { }

    protected static void DrawRoute(DrawingContext context, Point[] points, Fit fit, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var line = geometry.Open())
        {
            line.BeginFigure(fit.Map(points[0]), isFilled: false, isClosed: false);
            for (int i = 1; i < points.Length; i++)
                line.LineTo(fit.Map(points[i]), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_path.IsEmpty || ActualWidth <= 24 || ActualHeight <= 24)
            return;
        var position = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            EditPointAt(position);
            return;
        }
        var fit = GetFit();
        int hit = FindHandle(position, fit);
        bool singlePoint = MoveHandlesIndividually && hit >= 0;
        var segment = hit < 0 ? FindSegment(position, fit, _points) : default;
        if (hit < 0 && segment.Index > 0)
            hit = Math.Max(1, segment.Fraction < .5 ? segment.Index - 1 : segment.Index);
        if (hit < 0 || !CaptureMouse())
            return;
        e.Handled = true;
        _selected = hit;
        _radius = singlePoint ? 1 : Math.Max(2, (_points.Length + 19) / 20 * 2);
        _startDrag = position;
        _dragged = false;
        _dragFit = fit;
        _original = (Point[])_points.Clone();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_original is null)
            return;
        e.Handled = true;
        var delta = e.GetPosition(this) - _startDrag;
        if (!_dragged && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragged = true;
        MovePoint(_original, _points, _selected, _radius, delta / _dragFit.Scale);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_original is null)
            return;
        e.Handled = true;
        bool moved = _dragged;
        var path = moved ? Rebuild(_path, _points) : _path;
        var startShift = moved ? new Vector(Math.Round(_points[0].X), Math.Round(_points[0].Y)) : default;
        _original = null;
        _selected = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (moved)
            PublishPath(path, startShift);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_original is not { } original)
            return;
        _points = original;
        _original = null;
        _selected = -1;
        InvalidateVisual();
    }

    protected virtual Fit GetFit()
    {
        double left = _points.Min(point => point.X), right = _points.Max(point => point.X);
        double top = _points.Min(point => point.Y), bottom = _points.Max(point => point.Y);
        const double padding = 24;
        double scale = Math.Min((ActualWidth - padding) / Math.Max(1, right - left),
            (ActualHeight - padding) / Math.Max(1, bottom - top));
        return new((ActualWidth - (left + right) * scale) / 2, (ActualHeight - (top + bottom) * scale) / 2, scale);
    }

    protected int FindHandle(Point position, Fit fit)
    {
        double best = 49;
        int hit = -1;
        foreach (int index in _handles)
        {
            double distance = (position - fit.Map(_points[index])).LengthSquared;
            double radius = index == _points.Length - 1 ? 7 : HandleRadius(index, fit.Scale) + 1.5;
            if (distance <= best && distance <= radius * radius)
            {
                best = distance;
                hit = index;
            }
        }
        if (CanMoveStart && (position - fit.Map(_points[0])).LengthSquared < best)
            hit = 0;
        return hit;
    }

    private double HandleRadius(int index, double scale)
    {
        var points = _original ?? _points;
        double nearby = Math.Min((points[index] - points[index - 1]).Length, (points[index + 1] - points[index]).Length);
        return Math.Clamp(nearby * scale / 3, 1, 3);
    }

    protected static (int Index, double Fraction, double DistanceSquared) FindSegment(Point position, Fit fit, Point[] points)
    {
        (int Index, double Fraction, double DistanceSquared) hit = (0, 0, 49);
        for (int index = 1; index < points.Length; index++)
        {
            var start = fit.Map(points[index - 1]);
            var segment = fit.Map(points[index]) - start;
            double along = segment.LengthSquared == 0 ? 0 :
                Math.Clamp(Vector.Multiply(position - start, segment) / segment.LengthSquared, 0, 1);
            double distance = (position - (start + along * segment)).LengthSquared;
            if (distance < hit.DistanceSquared)
                hit = (index, along, distance);
        }
        return hit;
    }

    internal void EditPointAt(Point position)
    {
        var fit = GetFit();
        int handle = FindHandle(position, fit);
        var (index, fraction, _) = handle < 0 ? FindSegment(position, fit, _points) : default;
        if ((handle < 0 && index == 0) || handle == 0 || handle == _path.Length)
            return;
        var edited = handle > 0
            ? MouseRoute.RemovePoint(_path, handle - 1)
            : MouseRoute.InsertPoint(_path, index - 1, fraction);
        if (!edited.SequenceEqual(_path))
            PublishPath(edited);
    }

    private void PublishPath(ImmutableArray<MouseSample> path, Vector startShift = default)
    {
        if (path.SequenceEqual(_path) && startShift == default)
        {
            LoadPath(_path);
            return;
        }
        LoadPath(path);
        OnPathChanged(path, startShift);
    }

    protected virtual void OnPathChanged(ImmutableArray<MouseSample> path, Vector startShift) => _changed(path);

    internal static void MovePoint(Point[] original, Point[] points, int selected, int radius, Vector delta)
    {
        for (int i = 0; i < original.Length; i++)
        {
            double weight = (i == 0 || i == original.Length - 1) && i != selected ? 0 :
                Math.Max(0, 1d - (double)Math.Abs(i - selected) / radius);
            points[i] = original[i] + weight * delta;
        }
    }

    internal static ImmutableArray<MouseSample> Rebuild(ImmutableArray<MouseSample> path, IReadOnlyList<Point> points)
    {
        var result = ImmutableArray.CreateBuilder<MouseSample>(path.Length);
        Point previous = new(Math.Round(points[0].X), Math.Round(points[0].Y));
        for (int i = 0; i < path.Length; i++)
        {
            var point = points[i + 1];
            int x = (int)Math.Clamp(Math.Round(point.X - previous.X), -MouseRoute.MaxDelta, MouseRoute.MaxDelta);
            int y = (int)Math.Clamp(Math.Round(point.Y - previous.Y), -MouseRoute.MaxDelta, MouseRoute.MaxDelta);
            result.Add(path[i] with { DeltaX = x, DeltaY = y });
            previous += new Vector(x, y);
        }
        return result.MoveToImmutable();
    }
}
