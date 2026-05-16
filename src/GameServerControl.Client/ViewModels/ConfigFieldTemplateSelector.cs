using System.Windows;
using System.Windows.Controls;

namespace GameServerControl.Client.ViewModels;

public sealed class ConfigFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTpl { get; set; }
    public DataTemplate? MultilineTpl { get; set; }
    public DataTemplate? ToggleTpl { get; set; }
    public DataTemplate? ChoiceTpl { get; set; }
    public DataTemplate? NumberTpl { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not ConfigFieldViewModel vm) return base.SelectTemplate(item, container);
        if (vm.IsToggle) return ToggleTpl;
        if (vm.IsChoice) return ChoiceTpl;
        if (vm.IsMultiline) return MultilineTpl;
        if (vm.IsNumber) return NumberTpl;
        return TextTpl;
    }
}
