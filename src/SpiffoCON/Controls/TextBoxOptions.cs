using System.Windows;
using System.Windows.Controls;

namespace SpiffoCON.Controls;

/// <summary>
/// The Fluent theme shows a clear button (about 34 px) inside a focused text box, which leaves
/// a short number field almost no room while it is being typed. <c>NoClearButton="True"</c> hides it.
/// </summary>
public static class TextBoxOptions
{
    public static readonly DependencyProperty NoClearButtonProperty = DependencyProperty.RegisterAttached(
        "NoClearButton", typeof(bool), typeof(TextBoxOptions), new PropertyMetadata(false, OnNoClearButtonChanged));

    public static bool GetNoClearButton(DependencyObject d) => (bool)d.GetValue(NoClearButtonProperty);

    public static void SetNoClearButton(DependencyObject d, bool value) => d.SetValue(NoClearButtonProperty, value);

    static void OnNoClearButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;
        box.Loaded -= Hide;
        if ((bool)e.NewValue)
        {
            box.Loaded += Hide;
            if (box.IsLoaded)
                Hide(box, null!);
        }
    }

    // a local value wins over the template's IsKeyboardFocusWithin trigger
    static void Hide(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        box.ApplyTemplate();
        if (box.Template?.FindName("DeleteButton", box) is UIElement button)
            button.Visibility = Visibility.Collapsed;
    }
}
