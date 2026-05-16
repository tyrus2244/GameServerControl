using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed class ServerRegistry
{
    private readonly ILogger<ServerRegistry> _logger;
    private readonly string _path;
    private readonly object _lock = new();
    private List<ServerDef> _servers = new();

    public ServerRegistry(IConfiguration cfg, IHostEnvironment env, ILogger<ServerRegistry> logger)
    {
        _logger = logger;
        var configured = cfg["Agent:ServersJsonPath"] ?? "servers.json";
        _path = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        Reload();
    }

    public IReadOnlyList<ServerDef> All
    {
        get { lock (_lock) return _servers.ToArray(); }
    }

    public ServerDef? Get(string id)
    {
        lock (_lock) return _servers.FirstOrDefault(s => s.Id == id);
    }

    public void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogWarning("servers.json not found at {Path}", _path);
                lock (_lock) _servers = new();
                return;
            }
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<ServersFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
            lock (_lock) _servers = doc?.Servers?.ToList() ?? new();
            _logger.LogInformation("Loaded {Count} servers from {Path}", _servers.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {Path}", _path);
        }
    }

    private sealed class ServersFile
    {
        public ServerDef[]? Servers { get; set; }
    }
}
