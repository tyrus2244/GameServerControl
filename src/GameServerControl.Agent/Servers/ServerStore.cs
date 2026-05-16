using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed class ServerStore
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly ServerRegistry _registry;
    private readonly object _lock = new();

    public ServerStore(IConfiguration cfg, IHostEnvironment env, ServerRegistry registry)
    {
        _registry = registry;
        var configured = cfg["Agent:ServersJsonPath"] ?? "servers.json";
        _path = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    public ServerDef Add(ServerDef def)
    {
        Validate(def);
        lock (_lock)
        {
            var list = LoadAll();
            if (list.Any(s => string.Equals(s.Id, def.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Server with id '{def.Id}' already exists.");
            list.Add(def);
            SaveAll(list);
            _registry.Reload();
        }
        return def;
    }

    public ServerDef Update(string id, ServerDef def)
    {
        // Force the URL id onto the def so the caller can't accidentally rename via PUT body.
        var normalized = def with { Id = id };
        Validate(normalized);
        lock (_lock)
        {
            var list = LoadAll();
            var idx = list.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException(id);
            list[idx] = normalized;
            SaveAll(list);
            _registry.Reload();
        }
        return normalized;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = LoadAll();
            var removed = list.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveAll(list);
            _registry.Reload();
        }
        return true;
    }

    private static void Validate(ServerDef def)
    {
        if (string.IsNullOrWhiteSpace(def.Id) || !IdPattern.IsMatch(def.Id))
            throw new ArgumentException("Id must be lowercase letters, digits, and hyphens (e.g. 'valheim-2').");
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new ArgumentException("Name is required.");
        if (def.HostingMode == HostingMode.Vm && string.IsNullOrWhiteSpace(def.VmName))
            throw new ArgumentException("VmName is required when HostingMode is Vm.");
        if (string.IsNullOrWhiteSpace(def.GuestExePath))
            throw new ArgumentException("GuestExePath is required.");
    }

    private List<ServerDef> LoadAll()
    {
        if (!File.Exists(_path)) return new List<ServerDef>();
        var json = File.ReadAllText(_path);
        var doc = JsonSerializer.Deserialize<ServersFile>(json, JsonOpts);
        return doc?.Servers?.ToList() ?? new List<ServerDef>();
    }

    private void SaveAll(List<ServerDef> list)
    {
        var doc = new ServersFile { Servers = list.ToArray() };
        var json = JsonSerializer.Serialize(doc, JsonOpts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        // Atomic swap so a crash mid-write never leaves a corrupt servers.json.
        if (File.Exists(_path))
            File.Replace(tmp, _path, _path + ".bak");
        else
            File.Move(tmp, _path);
    }

    private sealed class ServersFile
    {
        public ServerDef[]? Servers { get; set; }
    }
}
