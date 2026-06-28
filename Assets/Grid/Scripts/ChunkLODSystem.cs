using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

// ── Burst job: LOD transition pass ───────────────────────────────────────────

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

        if (target != ChunkLODLevel.Unloaded)
            ToMarkDirty.Add(entity);
    }

    static ChunkLODLevel ComputeTarget(int3 coord, int3 player, ChunkViewDistanceSettings s)
    {
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
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<GhostOwnerIsLocal>());

        _lastPlayerChunk = new int3(int.MaxValue, int.MaxValue, int.MaxValue);

        RequireForUpdate<ChunkViewDistanceSettings>();
    }

    protected override void OnUpdate()
    {
        if (_playerQuery.IsEmpty) return;

        var viewDist = SystemAPI.GetSingleton<ChunkViewDistanceSettings>();

        // GhostOwnerIsLocal is an enableable component — GetSingletonEntity()
        // throws on queries that contain enableable types. Use ToEntityArray
        // and take the first result instead.
        var playerEntities = _playerQuery.ToEntityArray(Allocator.Temp);
        if (playerEntities.Length == 0) { playerEntities.Dispose(); return; }

        var playerXform = EntityManager.GetComponentData<LocalTransform>(playerEntities[0]);
        playerEntities.Dispose();

        int3 playerChunk = WorldToChunk(playerXform.Position);

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