using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering;

/// <summary>
/// Spawns a real per-instance mesh (via ModelMeshCache/BlockbenchGeometryParser)
/// for every block in a chunk whose BlockDefinitionData.model is set — the
/// non-voxel visual path (architecture, Models §1.E) landing on the dense grid
/// for the first time.
///
/// Runs on ChunkDirty, BEFORE ChunkMeshSystem consumes/clears that tag (hence
/// UpdateBefore) — reads the same dense buffer the greedy mesher reads, once
/// per dirty event, at Full LOD only. At distance, a model-backed block falls
/// back to its ordinary tile faces via the unmodified LOD downsample path
/// (ChunkLODMeshSystem) — see ChunkMeshSystem's IsModelBacked remarks for why
/// that split is deliberate.
///
/// One render entity per block instance, tracked in each chunk's
/// ChunkModelInstance buffer so a re-dirty (block placed/removed) can cleanly
/// tear down and rebuild rather than accumulating orphans. This rebuilds ALL of
/// a chunk's model instances on every dirty event rather than diffing — model
/// placement is cold-path/player-rate (§0.5), so simplicity is worth more than
/// the (currently negligible — few model blocks per chunk) savings of
/// incremental diffing. Revisit if a chunk ever holds hundreds of these.
///
/// STRUCTURAL-CHANGE ORDERING (two bugs fixed here, same root cause each time):
/// 1. Pass 1 reads the dirty-chunk query into plain managed lists and does NOT
///    touch EntityManager structurally — any CreateEntity/Instantiate/
///    DestroyEntity, even against an unrelated entity, invalidates a live query
///    enumerator. Pass 2, after the query is done, does the actual spawning.
/// 2. Within Pass 2, a DynamicBuffer handle fetched via GetBuffer() is ALSO
///    invalidated by the very next structural change — including Instantiate
///    calls for the SAME entity's own new instances. So spawning (which calls
///    Instantiate, and CreateEntity the first time a given model id is seen)
///    happens FIRST, collecting plain Entity references into a list; only once
///    every structural change for that chunk is finished is the buffer fetched
///    and written to, in one uninterrupted Clear+Add sequence.
/// Same shape as ChunkMeshSystem's buildResults list and ClientChunkReceiveSystem's
/// completedChunks list for point 1; point 2 is the same idea applied one level
/// deeper, to the per-chunk output buffer instead of the input query.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateBefore(typeof(ChunkMeshSystem))]
public partial class ChunkModelInstanceSystem : SystemBase
{
    struct ModelPrototype
    {
        public Entity Prototype;
        /// <summary>Authored mesh bounds (Unity block units), cached alongside the
        /// prototype so per-instance anchoring is a dictionary hit, not a re-parse.</summary>
        public UnityEngine.Bounds Bounds;
    }

    struct ChunkWork
    {
        public Entity ChunkEntity;
        public List<Entity> ToDestroy;
        public List<(string ModelId, int3 WorldBlock)> ToSpawn;
    }

    readonly Dictionary<string, ModelPrototype> _prototypes = new();

    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld") { Enabled = false; return; }
    }

    protected override void OnUpdate()
    {
        // ── Pass 1: read-only over the query ────────────────────────────────────
        var work = new List<ChunkWork>();

        foreach (var (pos, blocks, lod, instances, entity) in
            SystemAPI.Query<RefRO<ChunkPosition>, DynamicBuffer<BlockElement>,
                             RefRO<ChunkLODState>, DynamicBuffer<ChunkModelInstance>>()
                     .WithAll<ChunkDirty>()
                     .WithEntityAccess())
        {
            if (lod.ValueRO.Level != ChunkLODLevel.Full) continue;

            var toDestroy = new List<Entity>(instances.Length);
            for (int i = 0; i < instances.Length; i++)
                toDestroy.Add(instances[i].Value);

            int3 chunkOrigin = pos.ValueRO.Coord * ChunkSettings.SIZE;
            var toSpawn = new List<(string ModelId, int3 WorldBlock)>();

            for (int i = 0; i < blocks.Length; i++)
            {
                ushort blockId = blocks[i].Value;
                if (blockId == 0) continue;

                var def = BlockRegistry.GetDefinition(blockId);
                if (def == null || string.IsNullOrEmpty(def.model)) continue;

                toSpawn.Add((def.model, chunkOrigin + LocalCoordOf(i)));
            }

            work.Add(new ChunkWork { ChunkEntity = entity, ToDestroy = toDestroy, ToSpawn = toSpawn });
        }

        // ── Pass 2: structural changes, query enumerator is done ───────────────
        foreach (var w in work)
        {
            foreach (var e in w.ToDestroy)
                if (EntityManager.Exists(e))
                    EntityManager.DestroyEntity(e);

            // Spawn everything FIRST, collecting plain Entity refs — do not touch
            // the ChunkModelInstance buffer yet. SpawnInstance's Instantiate (and
            // CreateEntity, the first time a model id is built) are structural
            // changes that would invalidate a buffer handle fetched before them,
            // including one fetched for this SAME chunk entity.
            var spawned = new List<Entity>(w.ToSpawn.Count);
            foreach (var (modelId, worldBlock) in w.ToSpawn)
                spawned.Add(SpawnInstance(modelId, worldBlock));

            // Now that every structural change for this chunk is done, fetch the
            // buffer fresh and write it in one uninterrupted sequence.
            var newInstances = EntityManager.GetBuffer<ChunkModelInstance>(w.ChunkEntity);
            newInstances.Clear();
            foreach (var e in spawned)
                newInstances.Add(new ChunkModelInstance { Value = e });
        }

        // Deliberately does NOT touch ChunkDirty itself — it only reads it.
        // ChunkMeshSystem (UpdateAfter this system) owns clearing it at Full LOD.
    }

    Entity SpawnInstance(string modelId, int3 worldBlock)
    {
        if (!_prototypes.TryGetValue(modelId, out var proto))
        {
            proto = BuildPrototype(modelId);
            _prototypes[modelId] = proto;
        }

        Entity instance = EntityManager.Instantiate(proto.Prototype);

        // Anchor the model into its voxel cell from its ACTUAL authored bounds,
        // not from an assumed authoring convention. A block model should fill the
        // 1×1×1 cell [worldBlock, worldBlock+1]: horizontally centered, resting on
        // the cell floor. So we translate by (cell target − authored position):
        //   • X/Z: put the model's horizontal CENTER at the cell's horizontal
        //     center (worldBlock + 0.5).
        //   • Y:   put the model's BOTTOM at the cell floor (worldBlock.y).
        //
        // This is correct regardless of how the artist placed the model in
        // Blockbench — centered ([-8..8]), corner ([0..16]), floating, offset,
        // whatever. An earlier version hardcoded +0.5 X/Z, which only worked for
        // the centered default export and silently broke any other authoring;
        // deriving from bounds removes that assumption entirely. (Bounds come
        // from the baked Mesh, which BlockbenchGeometryParser sized via
        // RecalculateBounds.)
        //
        // Uses the prototype's own mesh bounds via the cached ModelMeshCache
        // entry — same mesh every instance shares, so this is a dictionary hit,
        // not a re-parse.
        UnityEngine.Bounds b = proto.Bounds;
        float3 authored = new float3(b.center.x, b.min.y, b.center.z);
        float3 cellTarget = new float3(worldBlock.x + 0.5f, worldBlock.y, worldBlock.z + 0.5f);
        float3 pos = cellTarget - authored;

        EntityManager.SetComponentData(instance, LocalTransform.FromPosition(pos));
        return instance;
    }

    ModelPrototype BuildPrototype(string modelId)
    {
        var (mesh, material) = ModelMeshCache.Get(modelId);

        var desc = new RenderMeshDescription(ShadowCastingMode.On, true);
        // A RenderMeshArray built once per model id and shared (via SharedComponent
        // semantics) across every instance of that model — this is what lets DOTS
        // batch them, same reasoning as ChunkMeshSystem's per-chunk RenderMeshArray.
        var renderArray = new RenderMeshArray(new[] { material }, new[] { mesh });

        Entity prototype = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(prototype, EntityManager, desc, renderArray,
            MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        EntityManager.AddComponentData(prototype, LocalTransform.Identity);

        // CRITICAL: tag the prototype as a Prefab. Without this the prototype is a
        // live, rendered entity sitting at LocalTransform.Identity (world origin) —
        // a phantom model at (0,0,0) that no block corresponds to and nothing can
        // remove. (This was the "extra model that keeps coming back": it appears
        // the moment the first model block spawns and BuildPrototype runs.) The
        // Prefab tag excludes it from rendering and from every system query, while
        // EntityManager.Instantiate still copies it — the standard DOTS prototype
        // pattern. Note ChunkMeshSystem's render-mesh prototype has the same shape
        // but never showed a phantom because its blankMesh is empty; this one has
        // real geometry, so the omission was visible.
        EntityManager.AddComponent<Prefab>(prototype);

        return new ModelPrototype { Prototype = prototype, Bounds = mesh.bounds };
    }

    static int3 LocalCoordOf(int flatIndex)
    {
        int S = ChunkSettings.SIZE;
        int x = flatIndex % S;
        int y = (flatIndex / S) % S;
        int z = flatIndex / (S * S);
        return new int3(x, y, z);
    }
}