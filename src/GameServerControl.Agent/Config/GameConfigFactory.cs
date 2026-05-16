using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

public sealed class GameConfigFactory
{
    private readonly IServiceProvider _sp;
    public GameConfigFactory(IServiceProvider sp) { _sp = sp; }

    public IGameConfig? For(ServerDef def)
    {
        var schema = ConfigSchemas.For(def);
        return schema?.Key switch
        {
            "valheim"      => _sp.GetRequiredService<ValheimConfig>(),
            "palworld"     => _sp.GetRequiredService<PalworldConfig>(),
            "windrose"     => _sp.GetRequiredService<WindroseConfig>(),
            "satisfactory" => _sp.GetRequiredService<SatisfactoryConfig>(),
            _ => null
        };
    }
}
