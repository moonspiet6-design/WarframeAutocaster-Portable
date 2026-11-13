using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autocaster.Windows;
using ScreenCursor = System.Windows.Forms.Cursor;
using PixelRect = System.Drawing.Rectangle;

namespace Autocaster.App;

internal sealed record MacroWheelSlot(Guid Id, string Name);

internal sealed class MacroWheelWindow : Window
{
    private readonly MacroWheelView _view = new();
    private ForegroundWindow _game;
    private System.Drawing.Point _previousPointer;
    private bool _choosing;

    internal MacroWheelWindow()
    {
        Title = "Q menu";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Width = Height = 1;
        Content = _view;
        MouseMove += (_, e) => _view.Highlight(e.GetPosition(_view));
        Deactivated += (_, _) => Finish(select: false, restoreFocus: false);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(select: false);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayWindow.ExcludeFromTaskSwitcher(new WindowInteropHelper(this).Handle);
    }

    internal bool Open(PixelRect bounds, ForegroundWindow game, IReadOnlyList<MacroWheelSlot?> slots)
    {
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        if (!WindowCaptureProtection.TryExclude(handle) || !OverlayWindow.Place(handle, bounds))
            return false;

        _game = game;
        _previousPointer = ScreenCursor.Position;
        _view.SetSlots(slots);
        Show();
        if (!OverlayWindow.TryActivate(handle))
        {
            Hide();
            return false;
        }

        _choosing = true;
        OverlayWindow.ReleasePointer();
        ScreenCursor.Position = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        return true;
    }

    internal Guid? Finish(bool select, bool restoreFocus = true)
    {
        if (!_choosing)
            return null;

        _choosing = false;
        Guid? choice = null;
        if (IsActive)
        {
            if (select)
            {
                var position = ScreenCursor.Position;
                choice = _view.ChoiceAt(_view.PointFromScreen(new Point(position.X, position.Y)));
            }
            if (restoreFocus)
            {
                ScreenCursor.Position = _previousPointer;
                _game.TryActivate();
            }
        }
        Hide();
        _game = default;
        return choice;
    }
}

internal sealed class MacroWheelView : FrameworkElement
{
    internal const int SlotCount = 8;
    private const double FullTurn = Math.PI * 2;
    private const double Slice = FullTurn / SlotCount;
    private const double Radius = 188;
    private const double Inner = 52;
    private const double Gap = .013;
    private static readonly Brush Panel = ColorBrush(0xf2, 0x22, 0x22, 0x25);
    private static readonly Brush Empty = ColorBrush(0xd9, 0x1a, 0x1a, 0x1d);
    private static readonly Brush Active = ColorBrush(0xff, 0x45, 0x2b, 0x20);
    private static readonly Brush Accent = ColorBrush(0xff, 0xff, 0xa2, 0x63);
    private static readonly Brush Text = ColorBrush(0xff, 0xf2, 0xf0, 0xee);
    private static readonly Brush Muted = ColorBrush(0xff, 0xb6, 0xb1, 0xad);
    private static readonly Pen Outline = new(ColorBrush(0xff, 0x44, 0x40, 0x40), 1);
    private static readonly Pen ActiveOutline = new(Accent, 2);
    private static readonly Typeface Font = new("Segoe UI");
    private IReadOnlyList<MacroWheelSlot?> _slots = new MacroWheelSlot?[SlotCount];
    private int _highlight = -1;

    internal void SetSlots(IReadOnlyList<MacroWheelSlot?> slots)
    {
        _slots = slots;
        _highlight = -1;
        InvalidateVisual();
    }

    internal Guid? ChoiceAt(Point position)
    {
        int index = SlotAt(position - Center, Scale);
        return index < 0 ? null : _slots[index]?.Id;
    }

    internal void Highlight(Point position)
    {
        int index = SlotAt(position - Center, Scale);
        if (index >= 0 && _slots[index] is null)
            index = -1;
        if (index == _highlight)
            return;
        _highlight = index;
        InvalidateVisual();
    }

    internal static int SlotAt(Vector offset, double scale)
    {
        double distance = offset.Length;
        if (scale <= 0 || distance <= Inner * scale || distance > Radius * scale)
            return -1;
        double angle = Math.Atan2(offset.X, -offset.Y);
        return (int)Math.Floor((angle + Slice / 2 + FullTurn) / Slice) % SlotCount;
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    private double Scale => Math.Clamp((Math.Min(ActualWidth, ActualHeight) - 12) / (Radius * 2), 0, 1);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double scale = Scale;
        if (scale <= 0)
            return;

        var center = Center;
        drawing.PushTransform(new TranslateTransform(center.X, center.Y));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        for (int i = 0; i < SlotCount; i++)
        {
            double angle = i * Slice;
            bool selected = i == _highlight;
            var slot = _slots[i];
            drawing.DrawGeometry(selected ? Active : slot is null ? Empty : Panel,
                selected ? ActiveOutline : Outline, Wedge(angle));
            if (slot is not null)
                DrawLabel(drawing, slot.Name, At(angle, 127), selected ? Accent : Text, 14, 87);
        }

        drawing.DrawEllipse(Panel, Outline, new Point(0, 0), Inner - 5, Inner - 5);
        DrawLabel(drawing, "Q", new Point(0, -10), Accent, 20, 58);
        DrawLabel(drawing, "Cancel", new Point(0, 14), Muted, 11, 64);
        drawing.Pop();
        drawing.Pop();
    }

    private void DrawLabel(DrawingContext drawing, string text, Point center, Brush color, double size, double width)
    {
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Font, size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center
        };
        drawing.DrawText(label, new Point(center.X - width / 2, center.Y - label.Height / 2));
    }

    private static Geometry Wedge(double angle)
    {
        double start = angle - Slice / 2 + Gap;
        double end = angle + Slice / 2 - Gap;
        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(At(start, Radius), isFilled: true, isClosed: true);
            context.ArcTo(At(end, Radius), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(At(end, Inner), true, false);
            context.ArcTo(At(start, Inner), new Size(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        path.Freeze();
        return path;
    }

    private static Point At(double angle, double radius) => new(Math.Sin(angle) * radius, -Math.Cos(angle) * radius);

    private static Brush ColorBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
