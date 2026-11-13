using System;
using System.Windows;
using System.Windows.Controls;

namespace Autocaster.App;

internal partial class ProfilePicker : ComboBox
{
    internal ProfilePicker()
    {
        InitializeComponent();
        SetResourceReference(StyleProperty, typeof(ComboBox));
    }

    internal event Action<Guid>? RemoveRequested;
    internal event Action? AddRequested;

    internal ComboBoxItem Row(Guid id, string name, bool empty)
    {
        return new ComboBoxItem
        {
            Tag = id,
            Content = name,
            ContentTemplate = empty ? null : (DataTemplate)Resources["ProfileRow"],
            Visibility = empty ? Visibility.Collapsed : Visibility.Visible,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = null
        };
    }

    internal ComboBoxItem AddRow() => new()
    {
        Content = "Add new",
        ContentTemplate = (DataTemplate)Resources["AddRow"],
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        FocusVisualStyle = null
    };

    private void AddClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        IsDropDownOpen = false;
        AddRequested?.Invoke();
    }

    private void RemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && Ui.Ancestor<ComboBoxItem>(button)?.Tag is Guid id)
        {
            e.Handled = true;
            RemoveRequested?.Invoke(id);
        }
    }
}
