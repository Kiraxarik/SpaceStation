using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;

/// <summary>
/// Watches for ghosts that NetCode has marked as locally-owned
/// (GhostOwnerIsLocal) and stamps the custom LocalPlayer tag on them.
/// Client-world only — server never has GhostOwnerIsLocal on anything.
/// </summary>
[UpdateInGroup(typeof(GhostSimulationSystemGroup))]
public partial struct LocalPlayerTagSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var entity in
                 SystemAPI.QueryBuilder()
                     .WithAll<GhostOwner, GhostOwnerIsLocal>()
                     .WithNone<LocalPlayer>()
                     .Build()
                     .ToEntityArray(Allocator.Temp))
        {
            ecb.AddComponent<LocalPlayer>(entity);
        }

        ecb.Playback(state.EntityManager);
    }
}