using System.Windows;
using System.Windows.Controls;
using SpiffoCON.Core.Commands;
using SpiffoCON.ViewModels;

namespace SpiffoCON.Controls;

/// <summary>Picks the editor for a server option by its type.</summary>
public sealed class OptionEditorSelector : DataTemplateSelector
{
    public DataTemplate? Boolean { get; set; }
    public DataTemplate? Enum { get; set; }
    public DataTemplate? Text { get; set; }
    public DataTemplate? Line { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as OptionItem)?.Info.Type switch
        {
            ServerOptionType.Boolean => Boolean,
            ServerOptionType.Enum => Enum,
            ServerOptionType.Text => Text,
            _ => Line,
        };
}
