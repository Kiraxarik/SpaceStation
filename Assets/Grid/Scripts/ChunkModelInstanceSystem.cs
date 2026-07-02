using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
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

    /// <summary>Animated counterpart to ModelPrototype: Root is the prototype
    /// entity for the skeleton's root bone (its children reachable via the
    /// LinkedEntityGroup buffer added in BuildSkeletonPrototype), everything
    /// else mirrors ModelPrototype's anchoring data for the same reason —
    /// dictionary hit per instance, not a re-parse.</summary>
    struct SkeletonPrototype
    {
        public Entity Root;
        public UnityEngine.Vector3 RootLocalPosition;
        public quaternion RootBindRotation;
        public UnityEngine.Bounds Bounds;
    }

    struct ChunkWork
    {
        public Entity ChunkEntity;
        public List<Entity> ToDestroy;
        public List<(string ModelId, int3 WorldBlock)> ToSpawn;
    }

    readonly Dictionary<string, ModelPrototype> _prototypes = new();
    readonly Dictionary<string, SkeletonPrototype> _skeletonPrototypes = new();

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
                DestroyInstance(e);

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

    /// <summary>Destroys one tracked model instance, whether it's a flat single
    /// entity or an animated skeleton's root. Skeleton teardown is done
    /// EXPLICITLY via the LinkedEntityGroup buffer rather than assuming
    /// EntityManager.DestroyEntity(Entity) cascades through it — that cascade is
    /// documented for Instantiate; destruction isn't, so it's spelled out here
    /// instead of guessed at (same "confirm, don't assume" reasoning as this
    /// file's other structural-change-ordering notes). Buffer contents are read
    /// into a plain NativeArray before the destroy call, matching the
    /// read-before-structural-change pattern used everywhere else in this
    /// system — a GetBuffer handle would otherwise be invalidated by the very
    /// DestroyEntity call it's feeding.</summary>
    void DestroyInstance(Entity e)
    {
        if (!EntityManager.Exists(e)) return;

        if (EntityManager.HasBuffer<LinkedEntityGroup>(e))
        {
            var group = EntityManager.GetBuffer<LinkedEntityGroup>(e);
            var toKill = new NativeArray<Entity>(group.Length, Allocator.Temp);
            for (int i = 0; i < group.Length; i++) toKill[i] = group[i].Value;
            EntityManager.DestroyEntity(toKill);
            toKill.Dispose();
        }
        else
        {
            EntityManager.DestroyEntity(e);
        }
    }

    Entity SpawnInstance(string modelId, int3 worldBlock)
    {
        bool animated = ModelRegistry.Get(modelId) is { AnimationPaths.Length: > 0 };
        return animated ? SpawnSkeletonInstance(modelId, worldBlock) : SpawnFlatInstance(modelId, worldBlock);
    }

    Entity SpawnFlatInstance(string modelId, int3 worldBlock)
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

    Entity SpawnSkeletonInstance(string modelId, int3 worldBlock)
    {
        if (!_skeletonPrototypes.TryGetValue(modelId, out var proto))
        {
            proto = BuildSkeletonPrototype(modelId);
            _skeletonPrototypes[modelId] = proto;
        }

        // LinkedEntityGroup on the prototype root means this one Instantiate
        // clones the ENTIRE skeleton (root + every child bone), with Parent
        // references among the clones automatically remapped to point at each
        // other rather than at the prototype — the standard DOTS
        // prefab-hierarchy behavior.
        Entity rootInstance = EntityManager.Instantiate(proto.Root);

        // Identical anchoring math to SpawnFlatInstance (same bounds
        // convention — see its remarks), just applied to the root bone's own
        // pivot-relative position instead of LocalTransform.Identity, since a
        // skeleton root's prototype position is its pivot, not the origin.
        UnityEngine.Bounds b = proto.Bounds;
        float3 authored = new float3(b.center.x, b.min.y, b.center.z);
        float3 cellTarget = new float3(worldBlock.x + 0.5f, worldBlock.y, worldBlock.z + 0.5f);
        float3 placementOffset = cellTarget - authored;

        EntityManager.SetComponentData(rootInstance, LocalTransform.FromPositionRotation(
            placementOffset + (float3)proto.RootLocalPosition, proto.RootBindRotation));
        // (RootBindRotation is already a Unity.Mathematics quaternion — see
        // ModelSkeletonCache.BoneEntry — no UnityEngine.Quaternion conversion
        // needed at this boundary.)

        return rootInstance;
    }

    SkeletonPrototype BuildSkeletonPrototype(string modelId)
    {
        var skeleton = ModelSkeletonCache.Get(modelId);
        var desc = new RenderMeshDescription(ShadowCastingMode.On, true);

        var entityByIndex = new Entity[skeleton.Bones.Count];
        var allBones = new List<Entity>(skeleton.Bones.Count);

        Entity rootEntity = Entity.Null;
        int rootBoneIndex = -1;
        UnityEngine.Vector3 rootLocalPosition = UnityEngine.Vector3.zero;
        quaternion rootBindRotation = quaternion.identity;

        // Pass A: create every bone entity with its own per-bone mesh (see
        // ModelSkeletonCache/BlockbenchGeometryParser.LoadRig — geometry
        // authored relative to that bone's own pivot, so rotating the entity
        // pivots the mesh correctly for free) and its rest-pose LocalTransform.
        // No Parent links yet — ParentIndex can point to a bone later in this
        // same array (RigBone order isn't guaranteed parent-before-child).
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            var bone = skeleton.Bones[i];
            var renderArray = new RenderMeshArray(new[] { skeleton.Material }, new[] { bone.Mesh });

            Entity e = EntityManager.CreateEntity();
            RenderMeshUtility.AddComponents(e, EntityManager, desc, renderArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            EntityManager.AddComponentData(e, LocalTransform.FromPositionRotation(
                (float3)bone.LocalPosition, bone.BindRotation));
            EntityManager.AddComponentData(e, new AnimatedBone
            {
                ModelId = modelId,
                BoneName = bone.Name,
                BindRotation = bone.BindRotation,
            });
            EntityManager.AddComponent<Prefab>(e);

            entityByIndex[i] = e;
            allBones.Add(e);

            if (bone.ParentIndex < 0)
            {
                if (rootEntity != Entity.Null)
                    Debug.LogWarning($"[ChunkModelInstanceSystem] '{modelId}': multiple root bones found; " +
                                      $"the first one seen wins, ignoring '{bone.Name}' as an additional root.");
                else
                {
                    rootEntity = e;
                    rootBoneIndex = i;
                    rootLocalPosition = bone.LocalPosition;
                    rootBindRotation = bone.BindRotation;
                }
            }
        }

        // Pass B: now every bone entity exists, wire up Parent links. A bone
        // whose ParentIndex pointed at the SECOND root bone we ignored above
        // (rare, multi-root file) falls back to parenting under the chosen
        // root instead of an orphaned entity.
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            var bone = skeleton.Bones[i];
            if (i == rootBoneIndex) continue; // the chosen root has no Parent component at all

            int parentIndex = bone.ParentIndex;
            Entity parentEntity = (parentIndex >= 0 && parentIndex < entityByIndex.Length)
                ? entityByIndex[parentIndex]
                : rootEntity; // ParentIndex < 0 here means "an extra root bone" — fold it under the chosen one
            EntityManager.AddComponentData(entityByIndex[i], new Parent { Value = parentEntity });
        }

        // LinkedEntityGroup on the root, root first per DOTS convention — this
        // is what makes ONE Instantiate(proto.Root) clone the whole skeleton
        // (see SpawnSkeletonInstance), and what DestroyInstance reads to tear
        // the whole skeleton back down explicitly.
        allBones.Remove(rootEntity);
        allBones.Insert(0, rootEntity);
        var linkedGroup = EntityManager.AddBuffer<LinkedEntityGroup>(rootEntity);
        foreach (var e in allBones)
            linkedGroup.Add(new LinkedEntityGroup { Value = e });

        return new SkeletonPrototype
        {
            Root = rootEntity,
            RootLocalPosition = rootLocalPosition,
            RootBindRotation = rootBindRotation,
            Bounds = skeleton.Bounds,
        };
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