using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

// ── Greedy mesh job ───────────────────────────────────────────────────────────

/// <summary>
/// Builds a greedy mesh for one chunk.
///
/// Algorithm (per axis, per direction, per slice):
///   For each 2D slice perpendicular to the axis:
///     1. Build a "face mask": for every cell, record the atlas tile index
///        if this face should be drawn, or 0 if it should be hidden.
///        Cross-chunk culling: cells on the chunk border check the
///        corresponding neighbor slice instead of going out-of-bounds.
///     2. Sweep the mask and merge adjacent cells that share the same tile
///        into the largest possible rectangle (greedy merge).
///     3. Emit one quad per merged rectangle.
///
/// UVs encode the atlas tile + local size so the shader can tile correctly.
/// </summary>
[BurstCompile]
public struct BuildChunkMeshJob : IJob
{
    // ── Inputs ────────────────────────────────────────────────────────────────

    [ReadOnly] public NativeArray<byte> Blocks;

    // Neighbor border slices, one per face direction (SIZE*SIZE each).
    // Non-zero = solid (hide face); zero = air (show face).
    [ReadOnly] public NativeArray<byte> NeighborPosY;
    [ReadOnly] public NativeArray<byte> NeighborNegY;
    [ReadOnly] public NativeArray<byte> NeighborPosX;
    [ReadOnly] public NativeArray<byte> NeighborNegX;
    [ReadOnly] public NativeArray<byte> NeighborPosZ;
    [ReadOnly] public NativeArray<byte> NeighborNegZ;

    // Per-block face atlas indices, flattened: blockID * 6 + faceDir
    [ReadOnly] public NativeArray<int> BlockFaceAtlas;

    // ── Outputs ───────────────────────────────────────────────────────────────

    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;       // atlas tile coords (integer tile column/row)
    public NativeList<int> Triangles;

    // ── Entry point ───────────────────────────────────────────────────────────

    public void Execute()
    {
        // Reusable SIZE*SIZE mask; encoded as (tileIndex + 1) so 0 = empty.
        var mask = new NativeArray<int>(ChunkSettings.FACE, Allocator.Temp);

        // Axis 1 = Y  (+Y dir=0, -Y dir=1)
        GreedyAxis(mask, axis: 1, forwardDir: 0, backwardDir: 1);
        // Axis 0 = X  (+X dir=2, -X dir=3)
        GreedyAxis(mask, axis: 0, forwardDir: 2, backwardDir: 3);
        // Axis 2 = Z  (+Z dir=4, -Z dir=5)
        GreedyAxis(mask, axis: 2, forwardDir: 4, backwardDir: 5);

        mask.Dispose();
    }

    // ── Greedy sweep for one axis ─────────────────────────────────────────────

    void GreedyAxis(NativeArray<int> mask, int axis, int forwardDir, int backwardDir)
    {
        int uAxis = (axis + 1) % 3;
        int vAxis = (axis + 2) % 3;
        int S = ChunkSettings.SIZE;
        var pos = new int3();

        for (int slice = 0; slice < S; slice++)
        {
            // ── Forward face pass ─────────────────────────────────────────────
            for (int v = 0; v < S; v++)
                for (int u = 0; u < S; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    byte here = Blocks[ChunkSettings.Index(pos.x, pos.y, pos.z)];
                    int encoded = 0;
                    if (here != 0 && NeighborIsAir(pos, axis, +1))
                        encoded = BlockFaceAtlas[here * 6 + forwardDir] + 1;
                    mask[ChunkSettings.SliceIndex(u, v)] = encoded;
                }
            GreedySweep(mask, slice, axis, uAxis, vAxis, forward: true);

            // ── Backward face pass ────────────────────────────────────────────
            for (int v = 0; v < S; v++)
                for (int u = 0; u < S; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    byte here = Blocks[ChunkSettings.Index(pos.x, pos.y, pos.z)];
                    int encoded = 0;
                    if (here != 0 && NeighborIsAir(pos, axis, -1))
                        encoded = BlockFaceAtlas[here * 6 + backwardDir] + 1;
                    mask[ChunkSettings.SliceIndex(u, v)] = encoded;
                }
            GreedySweep(mask, slice, axis, uAxis, vAxis, forward: false);
        }
    }

    // ── Greedy sweep over a filled mask ───────────────────────────────────────

    void GreedySweep(NativeArray<int> mask, int slice,
                     int axis, int uAxis, int vAxis, bool forward)
    {
        int S = ChunkSettings.SIZE;

        for (int v = 0; v < S; v++)
            for (int u = 0; u < S; u++)
            {
                int encoded = mask[ChunkSettings.SliceIndex(u, v)];
                if (encoded == 0) continue;

                // Grow width along u
                int w = 1;
                while (u + w < S && mask[ChunkSettings.SliceIndex(u + w, v)] == encoded)
                    w++;

                // Grow height along v while the full width row matches
                int h = 1;
                bool done = false;
                while (!done && v + h < S)
                {
                    for (int k = 0; k < w; k++)
                    {
                        if (mask[ChunkSettings.SliceIndex(u + k, v + h)] != encoded)
                        { done = true; break; }
                    }
                    if (!done) h++;
                }

                // Emit the merged quad
                var origin = new int3();
                origin[axis] = slice;
                origin[uAxis] = u;
                origin[vAxis] = v;
                EmitQuad(origin, w, h, axis, uAxis, vAxis, encoded - 1, forward);

                // Clear consumed cells
                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        mask[ChunkSettings.SliceIndex(u + du, v + dv)] = 0;

                u += w - 1; // outer loop u++ steps past the quad
            }
    }

    // ── Quad emission ──────────────────────────────────────────────────────────

    void EmitQuad(int3 origin, int w, int h,
                  int axis, int uAxis, int vAxis,
                  int tile, bool forward)
    {
        int vertBase = Vertices.Length;
        int axisBump = forward ? 1 : 0;

        var c0 = new int3(origin.x, origin.y, origin.z); c0[axis] += axisBump;
        var c1 = c0; c1[uAxis] += w;
        var c2 = c0; c2[uAxis] += w; c2[vAxis] += h;
        var c3 = c0; c3[vAxis] += h;

        Vertices.Add(new float3(c0.x, c0.y, c0.z));
        Vertices.Add(new float3(c1.x, c1.y, c1.z));
        Vertices.Add(new float3(c2.x, c2.y, c2.z));
        Vertices.Add(new float3(c3.x, c3.y, c3.z));

        // UVs: atlas tile col/row + local size for tiling.
        // Assumes 16-column atlas layout; adjust ATLAS_COLS to match your texture.
        const int ATLAS_COLS = 16;
        float tu = tile % ATLAS_COLS;
        float tv = tile / ATLAS_COLS;
        UVs.Add(new float2(tu, tv));
        UVs.Add(new float2(tu + w, tv));
        UVs.Add(new float2(tu + w, tv + h));
        UVs.Add(new float2(tu, tv + h));

        if (forward)
        {
            Triangles.Add(vertBase); Triangles.Add(vertBase + 1); Triangles.Add(vertBase + 2);
            Triangles.Add(vertBase); Triangles.Add(vertBase + 2); Triangles.Add(vertBase + 3);
        }
        else
        {
            Triangles.Add(vertBase); Triangles.Add(vertBase + 2); Triangles.Add(vertBase + 1);
            Triangles.Add(vertBase); Triangles.Add(vertBase + 3); Triangles.Add(vertBase + 2);
        }
    }

    // ── Neighbor visibility ────────────────────────────────────────────────────

    bool NeighborIsAir(int3 pos, int axis, int sign)
    {
        int nx = pos.x, ny = pos.y, nz = pos.z;
        if (axis == 0) nx += sign;
        else if (axis == 1) ny += sign;
        else nz += sign;

        int S = ChunkSettings.SIZE;

        if (nx >= 0 && nx < S && ny >= 0 && ny < S && nz >= 0 && nz < S)
            return Blocks[ChunkSettings.Index(nx, ny, nz)] == 0;

        // Out-of-bounds: read neighbor slice
        NativeArray<byte> slice;
        int su, sv;

        if (axis == 1)
        {
            slice = sign > 0 ? NeighborPosY : NeighborNegY;
            su = pos.x; sv = pos.z;
        }
        else if (axis == 0)
        {
            slice = sign > 0 ? NeighborPosX : NeighborNegX;
            su = pos.z; sv = pos.y;
        }
        else
        {
            slice = sign > 0 ? NeighborPosZ : NeighborNegZ;
            su = pos.x; sv = pos.y;
        }

        return slice[ChunkSettings.SliceIndex(su, sv)] == 0;
    }
}

// ── Build result carrier ──────────────────────────────────────────────────────

struct ChunkBuildResult
{
    public Entity Entity;
    public int3 Coord;
    public NativeArray<byte> Blocks;
    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<int> Triangles;
    public JobHandle Handle;
    public bool IsFirstBuild;
}

// ── System ────────────────────────────────────────────────────────────────────

public partial class ChunkMeshSystem : SystemBase
{
    Entity _prototype;
    Material _material;

    // Burst-readable flattened block face atlas: blockID * 6 + faceDir
    NativeArray<int> _blockFaceAtlas;

    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld") { Enabled = false; return; }
        BakeBlockFaceAtlas();
    }

    protected override void OnDestroy()
    {
        if (_blockFaceAtlas.IsCreated) _blockFaceAtlas.Dispose();
    }

    void BakeBlockFaceAtlas()
    {
        var reg = BlockRegistry.Faces;
        _blockFaceAtlas = new NativeArray<int>(reg.Length * 6, Allocator.Persistent);
        for (int i = 0; i < reg.Length; i++)
            for (int d = 0; d < 6; d++)
                _blockFaceAtlas[i * 6 + d] = reg[i].ForDirection(d);
    }

    void EnsurePrototype()
    {
        if (_material == null)
            _material = Resources.Load<Material>("ChunkMaterial");

        if (_prototype != Entity.Null) return;

        var blankMesh = new Mesh();
        var desc = new RenderMeshDescription(
            shadowCastingMode: ShadowCastingMode.On,
            receiveShadows: true);
        var renderMeshArray = new RenderMeshArray(
            new Material[] { _material },
            new Mesh[] { blankMesh });

        _prototype = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(_prototype, EntityManager, desc,
            renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        EntityManager.AddComponentData(_prototype, LocalTransform.Identity);
    }

    protected override void OnUpdate()
    {
        EnsurePrototype();

        // ── Pass 1: collect, schedule ──────────────────────────────────────────
        var buildResults = new System.Collections.Generic.List<ChunkBuildResult>();

        foreach (var (pos, blocks, entity) in
            SystemAPI
                .Query<RefRO<ChunkPosition>, DynamicBuffer<BlockElement>>()
                .WithAll<ChunkDirty>()
                .WithEntityAccess())
        {
            var blocksCopy = new NativeArray<byte>(ChunkSettings.VOLUME, Allocator.TempJob);
            blocksCopy.CopyFrom(blocks.AsNativeArray().Reinterpret<byte>());

            ChunkNeighborSlices ns =
                EntityManager.HasComponent<ChunkNeighborSlices>(entity)
                    ? EntityManager.GetComponentObject<ChunkNeighborSlices>(entity)
                    : new ChunkNeighborSlices(); // defaults all-solid

            // Copy slices into NativeArrays for the job
            var nPosY = new NativeArray<byte>(ns.PosY, Allocator.TempJob);
            var nNegY = new NativeArray<byte>(ns.NegY, Allocator.TempJob);
            var nPosX = new NativeArray<byte>(ns.PosX, Allocator.TempJob);
            var nNegX = new NativeArray<byte>(ns.NegX, Allocator.TempJob);
            var nPosZ = new NativeArray<byte>(ns.PosZ, Allocator.TempJob);
            var nNegZ = new NativeArray<byte>(ns.NegZ, Allocator.TempJob);

            var verts = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var tris = new NativeList<int>(Allocator.TempJob);

            var handle = new BuildChunkMeshJob
            {
                Blocks = blocksCopy,
                NeighborPosY = nPosY,
                NeighborNegY = nNegY,
                NeighborPosX = nPosX,
                NeighborNegX = nNegX,
                NeighborPosZ = nPosZ,
                NeighborNegZ = nNegZ,
                BlockFaceAtlas = _blockFaceAtlas,
                Vertices = verts,
                UVs = uvs,
                Triangles = tris,
            }.Schedule();

            // Slice arrays are safe to dispose after the job completes
            nPosY.Dispose(handle); nNegY.Dispose(handle);
            nPosX.Dispose(handle); nNegX.Dispose(handle);
            nPosZ.Dispose(handle); nNegZ.Dispose(handle);

            buildResults.Add(new ChunkBuildResult
            {
                Entity = entity,
                Coord = pos.ValueRO.Coord,
                Blocks = blocksCopy,
                Vertices = verts,
                UVs = uvs,
                Triangles = tris,
                Handle = handle,
                IsFirstBuild = !EntityManager.HasComponent<ChunkMeshRef>(entity),
            });
        }

        // ── Pass 2: structural changes ─────────────────────────────────────────
        foreach (var r in buildResults)
            EntityManager.RemoveComponent<ChunkDirty>(r.Entity);

        // ── Pass 3: complete, upload ───────────────────────────────────────────
        foreach (var r in buildResults)
        {
            r.Handle.Complete();

            Mesh mesh;
            if (r.IsFirstBuild)
            {
                mesh = new Mesh { name = $"Chunk {r.Coord.x},{r.Coord.z}" };
                mesh.indexFormat = IndexFormat.UInt32;

                var renderEntity = EntityManager.Instantiate(_prototype);

                EntityManager.SetComponentData(renderEntity,
                    LocalTransform.FromPosition((float3)(r.Coord * ChunkSettings.SIZE)));

                EntityManager.SetComponentData(renderEntity, new RenderBounds
                {
                    Value = new Unity.Mathematics.AABB
                    {
                        Center = new float3(ChunkSettings.SIZE * 0.5f, ChunkSettings.SIZE * 0.5f, ChunkSettings.SIZE * 0.5f),
                        Extents = new float3(ChunkSettings.SIZE * 0.5f, ChunkSettings.SIZE * 0.5f, ChunkSettings.SIZE * 0.5f)
                    }
                });

                EntityManager.AddComponentObject(r.Entity, new ChunkMeshRef { Value = mesh });

                var newArray = new RenderMeshArray(
                    new Material[] { _material },
                    new Mesh[] { mesh });
                EntityManager.SetComponentData(renderEntity,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                EntityManager.SetSharedComponentManaged(renderEntity, newArray);
            }
            else
            {
                mesh = EntityManager.GetComponentObject<ChunkMeshRef>(r.Entity).Value;
                mesh.Clear();
            }

            mesh.SetVertices(r.Vertices.AsArray());
            mesh.SetUVs(0, r.UVs.AsArray());

            int indexCount = r.Triangles.Length;
            mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            mesh.SetIndexBufferData(r.Triangles.AsArray(), 0, 0, indexCount,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount),
                MeshUpdateFlags.DontRecalculateBounds);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            r.Blocks.Dispose();
            r.Vertices.Dispose();
            r.UVs.Dispose();
            r.Triangles.Dispose();
        }
    }
}