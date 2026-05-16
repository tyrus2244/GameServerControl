using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class ConfigFieldViewModel : ObservableObject
{
    public ConfigField Field { get; }

    [ObservableProperty] private string textValue = "";
    [ObservableProperty] private bool boolValue;
    [ObservableProperty] private string? choiceValue;
    [ObservableProperty] private double numberValue;

    public string Key => Field.Key;
    public string Label => Field.Label;
    public string? Description => Field.Description;
    public ConfigFieldType Type => Field.Type;
    public string[]? Choices => Field.Choices;
    public double Min => Field.Min ?? double.MinValue;
    public double Max => Field.Max ?? double.MaxValue;

    public bool IsText      => Type is ConfigFieldType.Text or ConfigFieldType.Password;
    public bool IsMultiline => Type is ConfigFieldType.Multiline;
    public bool IsToggle    => Type is ConfigFieldType.Toggle;
    public bool IsChoice    => Type is ConfigFieldType.Choice;
    public bool IsNumber    => Type is ConfigFieldType.Integer or ConfigFieldType.Decimal;
    public bool IsPassword  => Type is ConfigFieldType.Password;

    public ConfigFieldViewModel(ConfigField field, string? initialValue)
    {
        Field = field;
        var v = initialValue ?? field.Default ?? "";
        switch (field.Type)
        {
            case ConfigFieldType.Toggle:
                BoolValue = IsTrue(v);
                break;
            case ConfigFieldType.Choice:
                ChoiceValue = string.IsNullOrEmpty(v) ? (field.Choices?.FirstOrDefault() ?? "") : v;
                break;
            case ConfigFieldType.Integer:
            case ConfigFieldType.Decimal:
                NumberValue = double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;
                break;
            default:
                TextValue = v;
                break;
        }
    }

    public string Serialize()
    {
        return Type switch
        {
            ConfigFieldType.Toggle => BoolValue ? "true" : "false",
            ConfigFieldType.Choice => ChoiceValue ?? "",
            ConfigFieldType.Integer => ((long)Math.Round(NumberValue)).ToString(CultureInfo.InvariantCulture),
            ConfigFieldType.Decimal => NumberValue.ToString("0.0######", CultureInfo.InvariantCulture),
            _ => TextValue ?? ""
        };
    }

    private static bool IsTrue(string s) =>
        !string.IsNullOrEmpty(s) &&
        (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("True", StringComparison.OrdinalIgnoreCase));
}
