using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autocaster.Windows;

namespace Autocaster.App;

internal sealed class RecordingIndicator : Window
{
    internal RecordingIndicator()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Width = 225;
        Height = 34;
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8, Fill = Brushes.IndianRed,
            Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center
        });
        var text = new TextBlock { Text = "Recording mouse  ·  Insert to stop", FontSize = 12 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "EditorText");
        content.Children.Add(text);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = content
        };
        badge.SetResourceReference(Border.BackgroundProperty, "EditorChrome");
        badge.SetResourceReference(Border.BorderBrushProperty, "EditorLine");
        Content = badge;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        OverlayWindow.MakePassive(handle);
        WindowCaptureProtection.TryExclude(handle);
    }

    internal void ShowOverGame()
    {
        var game = ForegroundWindow.CaptureExternal();
        game.MoveToSameMonitor(new WindowInteropHelper(this).EnsureHandle(), nearTop: true);
        Show();
    }
}
