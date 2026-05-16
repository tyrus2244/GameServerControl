using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class ConfigSectionViewModel : ObservableObject
{
    public string Title { get; }
    public ObservableCollection<ConfigFieldViewModel> Fields { get; } = new();
    public ConfigSectionViewModel(string title) { Title = title; }
}

public sealed partial class ConfigEditorViewModel : ObservableObject
{
    public ServerDef Server { get; }
    public ConfigSchema? Schema { get; }
    public ObservableCollection<ConfigSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private string headerText = "";
    [ObservableProperty] private bool hasSchema;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isError;

    public ConfigEditorViewModel(ServerDef server, ConfigSchema? schema, IDictionary<string, string> initial)
    {
        Server = server;
        Schema = schema;
        HasSchema = schema is not null && schema.AllFields.Any(f => f.Key != "_placeholder");
        HeaderText = $"Configure “{server.Name}”";
        if (schema is not null)
        {
            foreach (var sec in schema.Sections)
            {
                var sv = new ConfigSectionViewModel(sec.Title);
                foreach (var field in sec.Fields)
                {
                    initial.TryGetValue(field.Key, out var val);
                    sv.Fields.Add(new ConfigFieldViewModel(field, val));
                }
                Sections.Add(sv);
            }
        }
    }

    public Dictionary<string, string> CollectValues()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sec in Sections)
            foreach (var f in sec.Fields)
                if (f.Key != "_placeholder")
                    dict[f.Key] = f.Serialize();
        return dict;
    }
}
