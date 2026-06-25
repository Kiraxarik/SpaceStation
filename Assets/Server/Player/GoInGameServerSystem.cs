using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Runs on the server. For each GoInGameRequest RPC received, marks that
/// connection as "in game", instantiates the player ghost prefab, and sets
/// GhostOwner.NetworkId so ownership (and therefore Owner Predicted
/// behavior, plus your LocalPlayer tagging on the client) resolves correctly.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct GoInGameServerSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        

        
        if (!SystemAPI.HasSingleton<PlayerSpawner>())
            return;

        Entity prefab = SystemAPI.GetSingleton<PlayerSpawner>().PlayerPrefab;
        //var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (request, rpcSource, rpcEntity) in
                 SystemAPI.Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            Entity connectionEntity = rpcSource.ValueRO.SourceConnection;

            ecb.AddComponent<NetworkStreamInGame>(connectionEntity);

            int networkId = state.EntityManager
                .GetComponentData<NetworkId>(connectionEntity).Value;

            Entity player = ecb.Instantiate(prefab);
            ecb.SetComponent(player, new GhostOwner { NetworkId = networkId });

            ecb.DestroyEntity(rpcEntity);
        }
        foreach (var (id, entity) in
                 SystemAPI.Query<RefRO<NetworkId>>()
                     .WithNone<NetworkStreamInGame>()
                     .WithEntityAccess())
        {
            Debug.Log($"[GoInGameClientSystem] New connection detected (NetworkId={id.ValueRO.Value}). Sending GoInGameRequest.");

            ecb.AddComponent<NetworkStreamInGame>(entity);

            Entity req = ecb.CreateEntity();
            ecb.AddComponent(req, new GoInGameRequest());
            ecb.AddComponent(req, new SendRpcCommandRequest());
        }

        ecb.Playback(state.EntityManager);
    }
}

/// <summary>
/// Runs once per new connection on the client. Marks the connection as
/// "in game" (so snapshots start flowing) and sends GoInGameRequest to
/// tell the server to spawn this client's player.
/// </summary>
