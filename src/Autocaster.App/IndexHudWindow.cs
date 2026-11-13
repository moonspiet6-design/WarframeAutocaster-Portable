using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autocaster.Core;
using Autocaster.Windows;
using PixelRect = System.Drawing.Rectangle;

namespace Autocaster.App;

internal sealed class IndexHudWindow : Window
{
    private readonly Grid _root = new();
    private readonly Viewbox _bottom;
    private readonly IndexHudView _view = new();
    private PixelRect _bounds;
    private int _macroBadges;

    internal IndexHudWindow()
    {
        Title = "Index HUD";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = ShowInTaskbar = Focusable = false;
        Width = Height = 1;
        Left = Top = -32000;
        _bottom = new Viewbox
        {
            Child = _view, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom
        };
        _root.Children.Add(_bottom);
        _root.SizeChanged += (_, _) => UpdateInset();
        Content = _root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        OverlayWindow.MakePassive(hwnd);
        WindowCaptureProtection.TryExclude(hwnd);
    }

    internal bool Present(PixelRect bounds, IndexRunHud hud, double elapsedMs, int macroBadges)
    {
        _view.Update(hud, elapsedMs);
        if (_macroBadges != macroBadges)
        {
            _macroBadges = macroBadges;
            UpdateInset();
        }
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!WindowCaptureProtection.TryExclude(hwnd))
            return false;
        if (IsVisible && _bounds == bounds)
            return true;
        if (!OverlayWindow.Place(hwnd, bounds))
            return false;
        _bounds = bounds;
        if (!IsVisible)
            Show();
        return true;
    }

    private void UpdateInset()
    {
        // Keep the vote cards above and the Index results caption below the strip.
        double bottom = Math.Max(_root.ActualHeight * .105, 14 + _macroBadges * 32);
        _bottom.Margin = new(14, 0, 14, bottom);
    }
}

internal sealed class IndexHudView : Border
{
    private static readonly Brush BackgroundInk = Ink(0x18191B);
    private static readonly Brush HeaderInk = Ink(0x202124);
    private static readonly Brush Edge = Ink(0x424448);
    private static readonly Brush Track = Ink(0x3B3D41);
    private static readonly Brush TextInk = Ink(0xF3F1EE);
    private static readonly Brush Muted = Ink(0xBCBEC3);
    private static readonly Brush Accent = Ink(0xFFAD72);
    private static readonly Brush VoteInk = Ink(0xFFD088);
    private readonly TextBlock _title = Label("", 13, TextInk, bold: true);
    private readonly TextBlock _phase = Label("", 12, Muted);
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new(0, 0, 6, 0), Fill = Accent };
    private readonly TextBlock _elapsed = Value(TextInk);
    private readonly TextBlock _cash = Value(Accent);
    private readonly TextBlock _operator = Value(TextInk);
    private readonly TextBlock _operatorLabel = Label("Next Operator", 12, Muted);
    private readonly TextBlock _cycleCount = Label("", 13, TextInk);
    private readonly TextBlock _action = Label("", 11, Muted);
    private readonly Border _fill = new() { Background = Accent, CornerRadius = new(2) };
    private readonly ScaleTransform _fraction = new(0, 1);

    internal IndexHudView()
    {
        Width = 608;
        Background = BackgroundInk;
        BorderBrush = Edge;
        BorderThickness = new(1);
        CornerRadius = new(8);
        SnapsToDevicePixels = UseLayoutRounding = true;
        var root = new DockPanel();
        Child = root;

        var head = new DockPanel();
        var state = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(_dot);
        state.Children.Add(_phase);
        DockPanel.SetDock(state, Dock.Right);
        head.Children.Add(state);
        _title.Margin = new(0, 0, 12, 0);
        head.Children.Add(_title);
        var header = new Border
        {
            Background = HeaderInk, BorderBrush = Edge, BorderThickness = new(0, 0, 0, 1),
            CornerRadius = new(7, 7, 0, 0), Padding = new(11, 6, 11, 6), Child = head
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var cells = new Grid { Margin = new(0, 7, 0, 7) };
        foreach (double width in new[] { .95, .95, 1.4, .90 })
            cells.ColumnDefinitions.Add(new() { Width = new(width, GridUnitType.Star) });
        AddCell(0, Metric(Label("Run time", 12, Muted), _elapsed));
        AddCell(1, Metric(Label("Until Cash In", 12, Muted), _cash));
        var cycle = new StackPanel();
        var cycleHead = new DockPanel();
        DockPanel.SetDock(_cycleCount, Dock.Right);
        cycleHead.Children.Add(_cycleCount);
        cycleHead.Children.Add(Label("Cycle progress", 12, Muted));
        cycle.Children.Add(cycleHead);
        _fill.RenderTransform = _fraction;
        cycle.Children.Add(new Border
        {
            Background = Track, Height = 4, CornerRadius = new(2), Margin = new(0, 4, 0, 4),
            Child = _fill
        });
        cycle.Children.Add(_action);
        AddCell(2, cycle);
        AddCell(3, Metric(_operatorLabel, _operator));
        root.Children.Add(cells);

        void AddCell(int column, UIElement content)
        {
            var border = new Border
            {
                Padding = new(11, 0, 11, 0), BorderBrush = Edge,
                BorderThickness = column == 3 ? new(0) : new(0, 0, 1, 0), Child = content
            };
            Grid.SetColumn(border, column);
            cells.Children.Add(border);
        }
    }

    internal void Update(IndexRunHud hud, double elapsedMs)
    {
        bool vote = hud.Phase is IndexHudPhase.Vote or IndexHudPhase.StayInvested or IndexHudPhase.CashIn;
        bool frozen = hud.RotationFrozenAtMs.HasValue;
        _title.Text = hud.Name;
        _phase.Text = hud.Phase switch
        {
            IndexHudPhase.StayInvested => "Vote · Stay Invested",
            IndexHudPhase.CashIn => "Vote · Cash In",
            IndexHudPhase.Vote => "Round vote",
            IndexHudPhase.Paused => "Paused",
            IndexHudPhase.Preparing => "Preparing",
            _ => hud.InOperator ? "In Operator" : "Running"
        };
        _phase.Foreground = vote ? VoteInk : Muted;
        _dot.Fill = vote ? VoteInk : Accent;
        _elapsed.Text = Clock(elapsedMs, hours: true, countdown: false);
        double? cashMs = hud.CashInAfterMs - elapsedMs;
        _cash.Text = cashMs switch
        {
            null => "Off",
            <= 0 => hud.Phase == IndexHudPhase.CashIn ? "00:00:00" : "Next vote",
            _ => Clock(cashMs.Value, hours: true)
        };
        _cash.Foreground = cashMs is null ? Muted : cashMs <= 0 ? VoteInk : Accent;
        _cycleCount.Text = hud.PlannedCasts > 0 ? $"{hud.CompletedCasts} / {hud.PlannedCasts}" : "—";
        _fraction.ScaleX = hud.PlannedCasts > 0 ? Math.Clamp((double)hud.CompletedCasts / hud.PlannedCasts, 0, 1) : 0;
        _fill.Background = frozen ? Muted : Accent;
        string action = hud.Action switch
        {
            "Mushroom 2" => "Brightbonnet",
            "Mushroom 1" => "Stinkbrain",
            "Fourth ability" => "Sporespring",
            "Enter Operator" or "Return to Nokko" => "Operator & return",
            null => "—",
            _ => hud.Action
        };
        _action.Text = frozen && hud.Action is not null ? $"Paused · {action}" : action;
        _operatorLabel.Text = frozen ? hud.InOperator ? "Return · paused" : "Operator · paused"
            : hud.InOperator ? "Return in" : "Next Operator";
        double remaining = Math.Max(0, hud.OperatorDueMs.GetValueOrDefault() - (hud.RotationFrozenAtMs ?? elapsedMs));
        _operator.Text = hud.OperatorDueMs is null ? "Off" : hud.InOperator
            ? (remaining / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " s"
            : Clock(remaining, hours: false);
    }

    private static string Clock(double milliseconds, bool hours, bool countdown = true)
    {
        long seconds = (long)(countdown ? Math.Ceiling(Math.Max(0, milliseconds) / 1000) : Math.Max(0, milliseconds) / 1000);
        return hours || seconds >= 3600 ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private static StackPanel Metric(TextBlock name, TextBlock value)
    {
        var panel = new StackPanel();
        panel.Children.Add(name);
        panel.Children.Add(value);
        return panel;
    }

    private static TextBlock Value(Brush ink)
    {
        var value = Label("", 18, ink, bold: true);
        value.Margin = new(0, 2, 0, 0);
        return value;
    }

    private static TextBlock Label(string text, double size, Brush color, bool bold = false) => new()
    {
        Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = color,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static SolidColorBrush Ink(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
