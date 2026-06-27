using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ── Burst job: LOD transition pass ───────────────────────────────────────────

/// <summary>
/// For every chunk entity, computes its target LOD tier from Chebyshev distance
/// to the player. When the tier changes, updates ChunkLODState and clears the
/// render-entity reference in place, recording which render entities to destroy
/// and which chunks to mark dirty for remeshing.
///
/// Pure blittable math — fully Burst compiled. Writes two output NativeLists, so
/// it runs single-threaded (.Run); convert to ParallelWriter if profiling shows
/// the per-chunk scan is a bottleneck at very high chunk counts.
/// </summary>
[BurstCompile]
partial struct LODRetierJob : IJobEntity
{
    [ReadOnly] public int3 PlayerChunk;
    [ReadOnly] public ChunkViewDistanceSettings ViewDist;

    public NativeList<Entity> ToDestroyRender;
    public NativeList<Entity> ToMarkDirty;

    public void Execute(
        Entity entity,
        ref ChunkLODState lodState,
        ref ChunkRenderEntity renderRef,
        in ChunkPosition pos)
    {
        ChunkLODLevel target = ComputeTarget(pos.Coord, PlayerChunk, ViewDist);
        if (target == lodState.Level) return;

        if (renderRef.Value != Entity.Null)
            ToDestroyRender.Add(renderRef.Value);

        lodState.Level = target;
        renderRef.Value = Entity.Null;

        // Unloaded chunks keep their cached block buffer but build no mesh.
        if (target != ChunkLODLevel.Unloaded)
            ToMarkDirty.Add(entity);
    }

    static ChunkLODLevel ComputeTarget(int3 coord, int3 player, ChunkViewDistanceSettings s)
    {
        // Full 3D Chebyshev distance — Y matters now that the station extends
        // on all axes (unlike the old flat-grid X/Z-only version).
        int d = math.max(
                    math.abs(coord.x - player.x),
                    math.max(math.abs(coord.y - player.y),
                             math.abs(coord.z - player.z)));

        if (d <= s.FullDetailRadius) return ChunkLODLevel.Full;
        if (d <= s.MediumLODRadius) return ChunkLODLevel.Medium;
        if (d <= s.FarLODRadius) return ChunkLODLevel.Far;
        if (d <= s.VeryFarRadius) return ChunkLODLevel.VeryFar;
        return ChunkLODLevel.Unloaded;
    }
}

// ── System ────────────────────────────────────────────────────────────────────

/// <summary>
/// Distance-based LOD retiering for chunks that already exist.
///
/// This system does NOT create, destroy, or stream chunks — the server is the
/// sole authority on which chunks exist (see ServerChunkSystem /
/// ClientChunkReceiveSystem). All this does is adjust the LOD tier of resident
/// chunks as the player moves, and destroy render entities whose tier changed
/// so the mesh systems rebuild them at the new detail level.
///
/// Runs before the mesh systems so LOD state is settled before meshing. Skips
/// the whole per-chunk scan on frames where the player hasn't crossed a chunk
/// boundary, since tiers can only change when the player's chunk coord changes.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateBefore(typeof(ChunkMeshSystem))]
[UpdateBefore(typeof(ChunkLODMeshSystem))]
public partial class ChunkLODSystem : SystemBase
{
    EntityQuery _playerQuery;
    int3 _lastPlayerChunk;
    bool _hasRunOnce;

    protected override void OnCreate()
    {
        _playerQuery = GetEntityQuery(
            ComponentType.ReadOnly<LocalPlayer>(),
            ComponentType.ReadOnly<LocalTransform>());

        _lastPlayerChunk = new int3(int.MaxValue, int.MaxValue, int.MaxValue);

        RequireForUpdate(_playerQuery);
        RequireForUpdate<ChunkViewDistanceSettings>();
    }

    protected override void OnUpdate()
    {
        var viewDist = SystemAPI.GetSingleton<ChunkViewDistanceSettings>();
        var playerXform = EntityManager.GetComponentData<LocalTransform>(
                              _playerQuery.GetSingletonEntity());
        int3 playerChunk = WorldToChunk(playerXform.Position);

        // Tiers only change when the player's chunk coord changes (or first run).
        bool playerMoved = !playerChunk.Equals(_lastPlayerChunk);
        if (!playerMoved && _hasRunOnce) return;

        _lastPlayerChunk = playerChunk;
        _hasRunOnce = true;

        var toDestroyRender = new NativeList<Entity>(64, Allocator.TempJob);
        var toMarkDirty = new NativeList<Entity>(64, Allocator.TempJob);

        new LODRetierJob
        {
            PlayerChunk = playerChunk,
            ViewDist = viewDist,
            ToDestroyRender = toDestroyRender,
            ToMarkDirty = toMarkDirty,
        }.Run();

        foreach (var re in toDestroyRender)
            if (EntityManager.Exists(re))
                EntityManager.DestroyEntity(re);

        foreach (var e in toMarkDirty)
            if (!EntityManager.HasComponent<ChunkDirty>(e))
                EntityManager.AddComponent<ChunkDirty>(e);

        toDestroyRender.Dispose();
        toMarkDirty.Dispose();
    }

    static int3 WorldToChunk(float3 pos)
        => new int3(
            (int)math.floor(pos.x / ChunkSettings.SIZE),
            (int)math.floor(pos.y / ChunkSettings.SIZE),
            (int)math.floor(pos.z / ChunkSettings.SIZE));
}