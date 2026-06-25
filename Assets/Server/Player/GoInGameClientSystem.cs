using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// Runs once per new connection on the client. Marks the connection as
/// "in game" (so snapshots start flowing) and sends GoInGameRequest to
/// tell the server to spawn this client's player.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial struct GoInGameClientSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        foreach (var (id, entity) in
                 SystemAPI.Query<RefRO<NetworkId>>()
                     .WithNone<NetworkStreamInGame>()
                     .WithEntityAccess())
        {
            ecb.AddComponent<NetworkStreamInGame>(entity);

            Entity req = ecb.CreateEntity();
            ecb.AddComponent(req, new GoInGameRequest());
            ecb.AddComponent(req, new SendRpcCommandRequest());
        }

        ecb.Playback(state.EntityManager);
    }
}
