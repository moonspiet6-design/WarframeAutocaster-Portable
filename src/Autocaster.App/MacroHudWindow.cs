using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autocaster.Core;
using Autocaster.Windows;
using PixelRect = System.Drawing.Rectangle;

namespace Autocaster.App;

internal enum HudPart { Timer, Step, Arrow }

internal class MacroHudWindow : Window
{
    protected MacroHudView View { get; }
    private PixelRect _bounds;

    internal MacroHudWindow() : this(new MacroHudView()) { }

    protected MacroHudWindow(MacroHudView view)
    {
        View = view;
        Title = "Macro HUD";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = view.PlacementPart.HasValue ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent;
        Topmost = true;
        ShowActivated = view.PlacementPart.HasValue;
        ShowInTaskbar = false;
        Focusable = view.PlacementPart.HasValue;
        Width = Height = 1;
        Left = Top = -32000;
        if (!view.PlacementPart.HasValue)
            Content = View;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (View.PlacementPart.HasValue)
            OverlayWindow.ExcludeFromTaskSwitcher(handle);
        else
            OverlayWindow.MakePassive(handle);
        WindowCaptureProtection.TryExclude(handle);
    }

    internal bool Present(PixelRect bounds, IReadOnlyList<MacroHudSnapshot> items, MouseMovementHint? movement, HudAnchor? arrowPosition = null)
    {
        View.Set(items, movement, arrowPosition);
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!WindowCaptureProtection.TryExclude(handle))
            return false;
        if (!IsVisible || bounds != _bounds)
        {
            if (!OverlayWindow.Place(handle, bounds))
                return false;
            _bounds = bounds;
            if (!IsVisible)
                Show();
        }
        return true;
    }
}

internal sealed class MacroHudPlacementWindow : MacroHudWindow
{
    private MacroHudSnapshot _sample;

    internal MacroHudPlacementWindow(ScenarioProfile profile, MacroStepPosition position, HudPart part,
        Action<HudAnchor, int?> save, int rathuumWaveSize = 5) : base(new MacroHudView { PlacementPart = part })
    {
        var options = profile.Macro!;
        var timer = options.Timer ?? new();
        _sample = new(profile.Id, profile.Name, part == HudPart.Step ? position : default, null, false,
            part == HudPart.Arrow ? new MacroSteps().NextMovement(profile) ?? new MouseMovementHint(Guid.Empty, 1, -1) : null,
            part == HudPart.Timer ? new MacroCountdown(profile.Id, timer.DurationMs, timer.DurationMs, timer.Position, timer.FontSize) : null,
            options.StepHudPosition, options.ArrowPosition,
            RathuumWaveSize: part == HudPart.Step && options.ShowRathuumWaveSize
                ? profile.Actions.Any(action => action.Enabled && action.Rathuum?.FirstKillOnly == true) ? 1 : rathuumWaveSize
                : null);
        var done = Ui.ToolbarButton("Done", Close, "PrimaryToolbarAction");
        done.Margin = new(0);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        if (part == HudPart.Timer)
        {
            var size = new TextBox
            {
                Text = timer.FontSize.ToString(CultureInfo.InvariantCulture), MaxLength = 2, Width = 42,
                Height = 28, Margin = new(7, 0, 7, 0), VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center, Padding = new(3, 0, 3, 0)
            };
            size.SetResourceReference(StyleProperty, "DefaultTextBoxBaseStyle");
            AutomationProperties.SetName(size, "Timer size");
            var caption = Ui.Text("Size");
            caption.Margin = new(0);
            caption.VerticalAlignment = VerticalAlignment.Center;
            tools.Children.Add(caption);
            tools.Children.Add(size);
            size.TextChanged += (_, _) =>
            {
                if (!int.TryParse(size.Text, out int fontSize) ||
                    fontSize is < MacroTimerOptions.MinFontSize or > MacroTimerOptions.MaxFontSize)
                    return;
                _sample = _sample with { Countdown = _sample.Countdown!.Value with { FontSize = fontSize } };
                View.Set([_sample], null);
            };
            Closing += (_, _) =>
            {
                var current = _sample.Countdown!.Value;
                if (current.FontSize != timer.FontSize)
                    save(current.Position, current.FontSize);
            };
        }
        tools.Children.Add(done);
        var toolbar = new Border
        {
            Child = tools, VerticalAlignment = VerticalAlignment.Top, Margin = new(10),
            CornerRadius = new(6), Padding = new(7), BorderThickness = new(1)
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, "EditorChrome");
        toolbar.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        toolbar.SetResourceReference(TextElement.ForegroundProperty, "EditorText");
        PlaceToolbar(part == HudPart.Arrow ? options.ArrowPosition : part == HudPart.Step ? options.StepHudPosition : timer.Position);
        View.PositionChanged += anchor =>
        {
            _sample = part switch
            {
                HudPart.Step => _sample with { StepPosition = anchor },
                HudPart.Arrow => _sample with { ArrowPosition = anchor },
                _ => _sample with { Countdown = _sample.Countdown!.Value with { Position = anchor } }
            };
            View.Set([_sample], _sample.NextMovement, _sample.ArrowPosition);
            PlaceToolbar(anchor);
            save(anchor, _sample.Countdown?.FontSize);
        };
        var root = new Grid { Background = Background };
        root.Children.Add(View);
        root.Children.Add(toolbar);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        void PlaceToolbar(HudAnchor? position) => toolbar.HorizontalAlignment = position is { X: > .5m, Y: < .15m }
            ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    internal bool Present(PixelRect bounds) => Present(bounds, [_sample], _sample.NextMovement, _sample.ArrowPosition);
}

internal sealed class MacroHudView : FrameworkElement
{
    private static readonly Brush Accent = FrozenBrush(0xFF, 0xAD, 0x72);
    private static readonly Brush ArrowInk = FrozenBrush(0xFF, 0xAD, 0x72, 140);
    private static readonly Brush BadgeFill = FrozenBrush(0x1A, 0x1A, 0x1D, 239);
    private static readonly Brush Shadow = FrozenBrush(0x10, 0x10, 0x13, 200);
    private static readonly Geometry Chevron = Geometry.Parse("M -6.44,-10.32 L -3.36,-12 7.84,0 -3.36,12 -6.44,10.32 3.36,0 Z");
    private static readonly Pen GuidePen = new(ArrowInk, 1) { DashStyle = DashStyles.Dash };
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface TimerFace = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private IReadOnlyList<MacroHudSnapshot> _items = [];
    private MouseMovementHint? _movement;
    private HudAnchor? _arrowPosition;
    private Rect _placementRect = Rect.Empty;
    private Vector _dragOffset;
    private HudAnchor? _dragPosition;

    internal HudPart? PlacementPart { get; init; }
    internal event Action<HudAnchor>? PositionChanged;

    internal static string StepLabel(MacroHudSnapshot item) => $"{item.Step.Step} / {item.Step.Total}" +
        (item.PreLarvaMs is double remaining ? $"  ·  Pre-Larva {TimingUnits.FormatCountdown(remaining)} s" :
            item.RathuumWaveSize is 1 ? "  ·  First kill" : item.RathuumWaveSize is int size ? $"  ·  Rathuum {size}" : "");

    internal void Set(IReadOnlyList<MacroHudSnapshot> items, MouseMovementHint? movement, HudAnchor? arrowPosition = null)
    {
        _items = items;
        _movement = movement;
        _arrowPosition = arrowPosition;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth < 16 || ActualHeight < 16)
            return;
        if (PlacementPart.HasValue)
        {
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            _placementRect = Rect.Empty;
            if (_dragPosition is { X: .5m })
                drawing.DrawLine(GuidePen, new(ActualWidth / 2, 0), new(ActualWidth / 2, ActualHeight));
            if (_dragPosition is { Y: .5m })
                drawing.DrawLine(GuidePen, new(0, ActualHeight / 2), new(ActualWidth, ActualHeight / 2));
        }

        if (_movement is { } arrow)
        {
            double size = ActualHeight < 140 ? .8 : 1;
            var anchor = (PlacementPart == HudPart.Arrow ? _dragPosition : null) ?? _arrowPosition ?? new(.5m, .5m);
            var location = Locate(anchor, new(32, 32));
            if (PlacementPart == HudPart.Arrow)
                _placementRect = new Rect(location, new Size(32, 32));
            drawing.PushTransform(new TranslateTransform(location.X + 16, location.Y + 16));
            drawing.PushTransform(new RotateTransform(arrow.AngleDegrees));
            drawing.PushTransform(new ScaleTransform(size, size));
            drawing.DrawGeometry(ArrowInk, null, Chevron);
            drawing.Pop();
            drawing.Pop();
            drawing.Pop();
        }

        var stepRows = new Dictionary<HudAnchor, int>();
        int defaultStepRows = 0;
        foreach (var item in _items.Where(item => item.Step.Total > 0))
        {
            var step = Text(StepLabel(item), 13, Accent, Math.Max(1, ActualWidth - 40));
            var text = Text(item.Name, 13, Brushes.WhiteSmoke, Math.Max(1, ActualWidth - step.Width - 34));
            double width = text.Width + step.Width + 26;
            var anchor = PlacementPart == HudPart.Step ? _dragPosition ?? item.StepPosition : item.StepPosition;
            int row = anchor is { } custom ? stepRows.GetValueOrDefault(custom) : defaultStepRows++;
            if (anchor is { } placed)
                stepRows[placed] = row + 1;
            var location = Locate(anchor ?? new(.5m, 1m), new(width, 28));
            if (anchor is null)
                location.Y -= ActualHeight < 140 ? 6 : 13;
            location.Y = StackY(location.Y, 28, row, gap: 4, preferUp: anchor is null);
            var rect = new Rect(location, new Size(width, 28));
            drawing.DrawRoundedRectangle(BadgeFill, null, rect, 5, 5);
            drawing.DrawText(text, new Point(location.X + 9, location.Y + 5));
            drawing.DrawText(step, new Point(location.X + 17 + text.Width, location.Y + 5));
            if (PlacementPart == HudPart.Step)
                _placementRect = rect;
        }

        var used = new Dictionary<HudAnchor, int>();
        foreach (var item in _items)
        {
            if (item.Countdown is not { } timer)
                continue;
            var label = FormatTimer(timer);
            var text = Text(label, timer.FontSize, Accent, ActualWidth, TimerFace);
            var shadow = Text(label, timer.FontSize, Shadow, ActualWidth, TimerFace);
            var full = Text(FormatTimer(timer with { RemainingMs = timer.DurationMs }), timer.FontSize, Accent, ActualWidth, TimerFace);
            var size = new Size(full.Width, full.Height);
            var anchor = PlacementPart == HudPart.Timer ? _dragPosition ?? timer.Position : timer.Position;
            int row = used.GetValueOrDefault(anchor);
            used[anchor] = row + 1;
            var location = Locate(anchor, size);
            location.Y = StackY(location.Y, size.Height, row);
            var inkOrigin = location + new Vector(size.Width - text.Width, 0);
            drawing.DrawText(shadow, inkOrigin + new Vector(1, 1));
            drawing.DrawText(text, inkOrigin);
            if (PlacementPart == HudPart.Timer)
                _placementRect = new Rect(location, size);
        }
    }

    internal static string FormatTimer(MacroCountdown timer)
    {
        if (timer.ShowsMilliseconds)
            return (timer.RemainingMs / 1000m).ToString("0.000", CultureInfo.InvariantCulture);
        var time = TimeSpan.FromSeconds((timer.RemainingMs + 999) / 1000);
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (!PlacementPart.HasValue || !_placementRect.Contains(point) || !CaptureMouse())
            return;
        _dragOffset = point - _placementRect.TopLeft;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!PlacementPart.HasValue)
            return;
        var point = e.GetPosition(this);
        Cursor = IsMouseCaptured || _placementRect.Contains(point) ? Cursors.SizeAll : Cursors.Arrow;
        if (!IsMouseCaptured)
            return;
        _dragPosition = SnapPosition(point - _dragOffset, RenderSize, _placementRect.Size);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        var position = _dragPosition;
        ReleaseMouseCapture();
        if (position is { } anchor)
            PositionChanged?.Invoke(anchor);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragPosition = null;
        InvalidateVisual();
        base.OnLostMouseCapture(e);
    }

    internal static HudAnchor SnapPosition(Point topLeft, Size viewport, Size item)
    {
        static decimal Axis(double value, double travel)
        {
            const double snap = 12;
            if (travel <= 0 || Math.Abs(value - travel / 2) <= snap)
                return .5m;
            return (decimal)Math.Clamp(value / travel, 0, 1);
        }
        return new(Axis(topLeft.X, viewport.Width - item.Width), Axis(topLeft.Y, viewport.Height - item.Height));
    }

    private Point Locate(HudAnchor anchor, Size item) => new(Math.Max(0, ActualWidth - item.Width) * (double)anchor.X,
        Math.Max(0, ActualHeight - item.Height) * (double)anchor.Y);

    private double StackY(double y, double height, int row, int gap = 0, bool preferUp = false)
    {
        double shift = row * (height + gap);
        return preferUp || y + shift + height > ActualHeight ? Math.Max(0, y - shift) : y + shift;
    }

    private FormattedText Text(string value, double size, Brush ink, double maxWidth, Typeface? face = null) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Face, size, ink,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    private static Brush FrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
