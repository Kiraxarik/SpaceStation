using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Scenes;
using UnityEngine;

/// <summary>
/// DIAGNOSTIC ONLY. Logs, once per second on the server, how many subscene
/// section entities and how many loaded-scene entities exist in the ServerWorld.
/// This tells us whether the ChunkWorld subscene (which carries the baked
/// LocalPlayer ghost prefab) is actually loading into the dedicated-server build.
///
///   sceneSections > 0 but nothing "loaded"  -> subscene present but not streamed in
///   sceneSections == 0                       -> subscene not included/registered at all
///   both > 0                                 -> subscene IS loaded (look elsewhere)
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerSubsceneDiagnosticSystem : ISystem
{
    double _next;

    public void OnUpdate(ref SystemState state)
    {
        double now = SystemAPI.Time.ElapsedTime;
        if (now < _next) return;
        _next = now + 1.0;

        int sceneSections = 0;
        foreach (var _ in SystemAPI.Query<RefRO<SceneSectionData>>()) sceneSections++;

        int resolvedScenes = 0;
        var q = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ResolvedSectionEntity>());
        resolvedScenes = q.CalculateEntityCount();

        Debug.Log($"[SubsceneDIAG] SceneSectionData entities={sceneSections}, " +
                  $"ResolvedSectionEntity entities={resolvedScenes}.");
    }
}
