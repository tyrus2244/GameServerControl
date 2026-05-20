using System.IO;
using System.Text.Json;

namespace GameServerControl.Client.Services;

public sealed class AgentConfig
{
    /// <summary>Unique key used internally (e.g. "primary", "house-server"). Auto-generated if empty.</summary>
    public string Id { get; set; } = "";
    /// <summary>Display name shown in the UI grouping.</summary>
    public string Nickname { get; set; } = "";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
}

public sealed class AppSettings
{
    /// <summary>
    /// Legacy fields — kept so single-agent setups upgrade cleanly. On load,
    /// these are folded into Agents[] if it's empty.
    /// </summary>
    public string AgentUrl { get; set; } = "http://127.0.0.1:5099";
    public string ApiToken { get; set; } = "";

    /// <summary>
    /// Multiple agent connections (federation). When non-empty, the legacy AgentUrl/ApiToken are ignored.
    /// </summary>
    public List<AgentConfig> Agents { get; set; } = new();

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameServerControl",
        "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else s = new AppSettings();
        }
        catch { s = new AppSettings(); }

        // Back-compat: bootstrap a single-entry Agents[] from the legacy fields.
        if (s.Agents.Count == 0 && !string.IsNullOrWhiteSpace(s.AgentUrl) && !string.IsNullOrWhiteSpace(s.ApiToken))
        {
            s.Agents.Add(new AgentConfig
            {
                Id = "primary",
                Nickname = "Primary",
                Url = s.AgentUrl,
                Token = s.ApiToken
            });
        }
        // Assign IDs to any agent missing one (older files; user-entered with blank Id).
        foreach (var a in s.Agents)
            if (string.IsNullOrWhiteSpace(a.Id))
                a.Id = Guid.NewGuid().ToString("N")[..8];
        return s;
    }

    public void Save()
    {
        // Keep legacy fields in sync with the first agent so older code paths still work.
        if (Agents.Count > 0)
        {
            AgentUrl = Agents[0].Url;
            ApiToken = Agents[0].Token;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
