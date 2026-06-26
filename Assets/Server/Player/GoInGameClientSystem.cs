using Unity.Entities;
using Unity.NetCode;

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
            ecb.AddComponent(req, new SendRpcCommandRequest
            {
                TargetConnection = entity  // ← send to the server connection specifically
            });
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}
