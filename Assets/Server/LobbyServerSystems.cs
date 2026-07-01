using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;

/// <summary>Tags a connection Committed when its commit RPC arrives.</summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct CommitReceiveSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (req, rpc, rpcEntity) in
            SystemAPI.Query<RefRO<CommitToPlayRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            ecb.AddComponent<Committed>(rpc.ValueRO.SourceConnection);
            ecb.DestroyEntity(rpcEntity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}

/// <summary>
/// The spawn rule: a body appears when the round is Running AND the connection
/// is Committed — whichever becomes true second. Fires once per connection, and
/// is the moment the server starts streaming snapshots to that client.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct PlayerSpawnServerSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerSpawner>();
        state.RequireForUpdate<RoundState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.GetSingleton<RoundState>().Phase != RoundPhase.Running)
            return;

        Entity prefab = SystemAPI.GetSingleton<PlayerSpawner>().PlayerPrefab;
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (netId, connection) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithAll<Committed>()
                     .WithNone<PlayerSpawned>()
                     .WithEntityAccess())
        {
            ecb.AddComponent<NetworkStreamInGame>(connection); // start snapshots to this client

            Entity player = ecb.Instantiate(prefab);
            ecb.SetComponent(player, new GhostOwner { NetworkId = netId.ValueRO.Value });

            ecb.AddComponent<PlayerSpawned>(connection);
            Debug.Log($"[Spawn] Body for NetworkId={netId.ValueRO.Value} (Running ∧ Committed).");
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}