using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Autocaster.App;

internal sealed class MarqueeSelection
{
    private readonly ListBox _list;
    private readonly Border _outline = new() { BorderThickness = new(1),
        Background = new SolidColorBrush(Color.FromArgb(38, 255, 156, 84)), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _scroll = new() { Interval = TimeSpan.FromMilliseconds(65) };
    private readonly Action<int, int, bool> _select;
    private Point _origin;
    private int _anchor = -1;
    private int _clickAnchor;
    private bool _add;
    private bool _moved;
    private bool _fromGutter;
    internal bool Active => _anchor >= 0;

    internal MarqueeSelection(ListBox list, Canvas layer, Action<int, int, bool> select)
    {
        _list = list;
        _select = select;
        _outline.SetResourceReference(Border.BorderBrushProperty, "EditorAccent");
        layer.Children.Add(_outline);
        _list.PreviewMouseMove += (_, e) =>
        {
            if (!Active)
                return;
            e.Handled = true;
            var current = e.GetPosition(_list);
            if (!_moved && (current - _origin).Length < SystemParameters.MinimumVerticalDragDistance)
                return;
            _moved = true;
            var rect = _fromGutter
                ? new Rect(new Point(0, _origin.Y), new Point(Math.Max(0, _list.ActualWidth - 16), current.Y))
                : new Rect(_origin, current);
            Canvas.SetLeft(_outline, rect.Left);
            Canvas.SetTop(_outline, rect.Top);
            _outline.Width = rect.Width;
            _outline.Height = rect.Height;
            _outline.Visibility = Visibility.Visible;
        };
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!Active)
                return;
            e.Handled = true;
            int anchor = _anchor;
            int end = IndexAt(e.GetPosition(_list));
            int first = _moved ? anchor : _clickAnchor;
            Reset();
            _list.ReleaseMouseCapture();
            _select(first, end, _add);
        };
        _list.LostMouseCapture += (_, _) => Reset();
        _scroll.Tick += (_, _) =>
        {
            if (!_moved || !Active || FindScroll(_list) is not { } viewer)
                return;
            double y = Mouse.GetPosition(_list).Y;
            if (y < 12)
                viewer.LineUp();
            else if (y > _list.ActualHeight - 12)
                viewer.LineDown();
        };
    }

    internal bool Begin(MouseButtonEventArgs args, bool fromGutter)
    {
        if (_list.Items.Count == 0 || args.OriginalSource is not DependencyObject source ||
            Ui.Ancestor<ScrollBar>(source) is not null || !_list.CaptureMouse())
            return false;
        _origin = args.GetPosition(_list);
        _fromGutter = fromGutter;
        _anchor = IndexAt(_origin);
        _moved = false;
        var modifiers = Keyboard.Modifiers;
        _add = (modifiers & ModifierKeys.Control) != 0;
        _clickAnchor = (modifiers & ModifierKeys.Shift) != 0 ? _list.SelectedIndex : -1;
        if (!Active)
        {
            _list.ReleaseMouseCapture();
            return false;
        }
        _scroll.Start();
        return true;
    }

    private int IndexAt(Point point)
    {
        var hit = _list.InputHitTest(new Point(Math.Clamp(point.X, 0, Math.Max(0, _list.ActualWidth - 16)),
            Math.Clamp(point.Y, 0, Math.Max(0, _list.ActualHeight - 2)))) as DependencyObject;
        if (hit is not null && ItemsControl.ContainerFromElement(_list, hit) is ListBoxItem item)
            return _list.ItemContainerGenerator.IndexFromContainer(item);
        int nearest = -1;
        double distance = double.MaxValue;
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row ||
                !row.IsDescendantOf(_list))
                continue;
            double y = row.TranslatePoint(new Point(0, 0), _list).Y;
            double delta = Math.Max(y - point.Y, point.Y - y - row.ActualHeight);
            if (delta < distance)
            {
                nearest = i;
                distance = delta;
            }
        }
        return nearest;
    }

    private void Reset()
    {
        _anchor = -1;
        _outline.Visibility = Visibility.Collapsed;
        _scroll.Stop();
    }

    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
            return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScroll(VisualTreeHelper.GetChild(element, i)) is { } result)
                return result;
        return null;
    }
}
