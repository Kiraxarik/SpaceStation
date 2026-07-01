using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Marks the connection NetworkStreamInGame when it's established so the
/// client starts receiving snapshots. Commit (Ready / Join) is now driven
/// by the Ready button via LobbyClient.Commit() — not automatic.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial struct LobbyClientSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (id, entity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                     .WithNone<NetworkStreamInGame>()
                     .WithEntityAccess())
        {
            ecb.AddComponent<NetworkStreamInGame>(entity);
            Debug.Log($"[LobbyClient] Connected as NetworkId={id.ValueRO.Value}. Waiting for Ready.");
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}