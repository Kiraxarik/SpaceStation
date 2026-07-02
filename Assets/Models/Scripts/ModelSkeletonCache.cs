using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Client-only. Builds and caches the per-bone Mesh set + hierarchy for an
/// animated model, on first request — the skeleton counterpart to
/// ModelMeshCache's single flat mesh. One shared Material per model (reused
/// across every bone, same texture-loading path as ModelMeshCache), but a
/// distinct Mesh per bone since BlockbenchGeometryParser.LoadRig authors each
/// bone's geometry relative to that bone's own pivot (RigBone.Vertices).
///
/// Source of truth for bone geometry/hierarchy is
/// BlockbenchGeometryParser.LoadRig (ParsedRig/RigBone) — this cache does no
/// transform math of its own, just builds Unity Mesh objects from the arrays
/// LoadRig already computed and converts RigBone's UnityEngine.Quaternion to
/// the Unity.Mathematics quaternion ECS consumes.
///
/// RigCollisionBox data flows through (CollisionBoxes below) but nothing spawns
/// hit-volume entities from it yet — that's the collision system's job
/// (Models doc §3), not this cache's. Keeping it here now means the geometry
/// only needs parsing once; whatever consumes it later is a pure addition, not
/// a re-parse.
///
/// A model with no usable rig (parse failure, no bones) falls back to a
/// single-bone "skeleton" wrapping ModelMeshCache's existing fallback cube, so
/// a broken animated model still shows up obviously wrong rather than crashing
/// ChunkModelInstanceSystem's spawn path.
/// </summary>
public static class ModelSkeletonCache
{
    public struct BoneEntry
    {
        public string Name;
        /// <summary>Index into Bones of the parent, or -1 for the root.</summary>
        public int ParentIndex;
        /// <summary>Rest-pose LocalTransform position, relative to the parent
        /// bone entity (or to the model root for a root bone) — straight from
        /// RigBone.RestLocalPosition, block units.</summary>
        public Vector3 LocalPosition;
        /// <summary>Rest-pose LocalTransform rotation, same relative convention
        /// as LocalPosition. Unity.Mathematics quaternion — this flows straight
        /// into ECS LocalTransform.Rotation at spawn time
        /// (ChunkModelInstanceSystem) with no further conversion needed.</summary>
        public quaternion BindRotation;
        public Mesh Mesh;
    }

    public struct SkeletonEntry
    {
        public List<BoneEntry> Bones; // root(s) not guaranteed first; ChunkModelInstanceSystem finds it via ParentIndex == -1
        public Material Material;
        public Bounds Bounds; // rest-pose authored bounds, same convention as ModelMeshCache's flat mesh bounds
        /// <summary>Parsed but not yet consumed by anything — see class remarks.</summary>
        public BlockbenchGeometryParser.RigCollisionBox[] CollisionBoxes;
    }

    static readonly Dictionary<string, SkeletonEntry> _cache = new(StringComparer.Ordinal);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetForPlaySession() => _cache.Clear();

    public static SkeletonEntry Get(string modelId)
    {
        if (_cache.TryGetValue(modelId, out var cached)) return cached;

        var built = Build(modelId);
        _cache[modelId] = built;
        return built;
    }

    static SkeletonEntry Build(string modelId)
    {
        var content = ModelRegistry.Get(modelId);
        if (content == null)
        {
            Debug.LogError($"[ModelSkeletonCache] '{modelId}': not found in ModelRegistry.");
            return Fallback(modelId);
        }

        var parsed = BlockbenchGeometryParser.LoadRig(content.GeometryPath, modelId);
        if (parsed == null || parsed.Value.Bones.Length == 0)
            return Fallback(modelId);

        var rig = parsed.Value;
        var bones = new List<BoneEntry>(rig.Bones.Length);
        foreach (var rb in rig.Bones)
        {
            var mesh = new Mesh { name = $"Model {modelId} / bone {rb.Name}" };
            mesh.indexFormat = rb.Vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(rb.Vertices);
            mesh.SetUVs(0, rb.Uvs);
            mesh.SetTriangles(rb.Triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            bones.Add(new BoneEntry
            {
                Name = rb.Name,
                ParentIndex = rb.ParentIndex,
                LocalPosition = rb.RestLocalPosition,
                // Explicit component construction rather than an implicit
                // UnityEngine.Quaternion -> Unity.Mathematics.quaternion cast —
                // avoids depending on an operator I haven't confirmed exists in
                // this project's Unity.Mathematics version.
                BindRotation = new quaternion(rb.RestLocalRotation.x, rb.RestLocalRotation.y,
                    rb.RestLocalRotation.z, rb.RestLocalRotation.w),
                Mesh = mesh,
            });
        }

        // Same shared-material path as ModelMeshCache — model textures are
        // standalone per model, not the block tile atlas, and one material
        // covers every bone of a given model.
        var material = ModelMeshCache.Get(modelId).Material;

        return new SkeletonEntry
        {
            Bones = bones,
            Material = material,
            Bounds = rig.Bounds,
            CollisionBoxes = rig.CollisionBoxes,
        };
    }

    static SkeletonEntry Fallback(string modelId)
    {
        var (mesh, material) = ModelMeshCache.Get(modelId); // shares ModelMeshCache's own fallback cube + magenta material
        var bones = new List<BoneEntry>
        {
            new BoneEntry
            {
                Name = "root",
                ParentIndex = -1,
                LocalPosition = Vector3.zero,
                BindRotation = quaternion.identity,
                Mesh = mesh,
            }
        };
        return new SkeletonEntry
        {
            Bones = bones,
            Material = material,
            Bounds = mesh.bounds,
            CollisionBoxes = Array.Empty<BlockbenchGeometryParser.RigCollisionBox>(),
        };
    }
}