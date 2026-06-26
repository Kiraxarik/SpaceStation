using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct GoInGameServerSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<PlayerSpawner>()) return;

        Entity prefab = SystemAPI.GetSingleton<PlayerSpawner>().PlayerPrefab;
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (request, rpcSource, rpcEntity) in
            SystemAPI.Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            Entity connection = rpcSource.ValueRO.SourceConnection;

            ecb.AddComponent<NetworkStreamInGame>(connection);

            int networkId = state.EntityManager
                .GetComponentData<NetworkId>(connection).Value;

            Entity player = ecb.Instantiate(prefab);
            ecb.SetComponent(player, new GhostOwner { NetworkId = networkId });

            ecb.DestroyEntity(rpcEntity);

            Debug.Log($"[GoInGameServerSystem] Spawned player for NetworkId={networkId}");
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}