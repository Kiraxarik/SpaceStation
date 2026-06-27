using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

// ── 3D downsampled LOD mesh job ───────────────────────────────────────────────

/// <summary>
/// Builds a downsampled greedy mesh for a chunk LOD tier.
///
/// The old implementation used a 2D heightmap (per XZ column, topmost Y block),
/// which produced two bugs:
///   1. No bottom faces — only top and side quads were ever emitted.
///   2. Tall pillars — a block at y=10 with empty neighbours generated a side
///      face stretching from y=0 all the way up to y=10.
///
/// This implementation instead:
///   1. Downsamples the full 16×16×16 block grid into a cells×cells×cells
///      boolean/type grid (cells = SIZE/Factor). A cell is solid if ANY block
///      in its Factor³ footprint is non-air; its representative type is the
///      first non-air block found.
///   2. Runs the same greedy-merge sweep as BuildChunkMeshJob over that smaller
///      grid, emitting all 6 face directions. Each quad's world-space coordinates
///      are multiplied by Factor so they align with the full-res mesh.
///
/// Factor 2 → 8×8×8 cells (Medium LOD)
/// Factor 4 → 4×4×4 cells (Far LOD)
/// Factor 8 → 2×2×2 cells (VeryFar LOD)
/// </summary>
[BurstCompile]
public struct BuildLODMeshJob : IJob
{
    [ReadOnly] public NativeArray<byte> Blocks;
    [ReadOnly] public NativeArray<int> BlockFaceAtlas;
    public int Factor;

    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<int> Triangles;

    public void Execute()
    {
        int cells = ChunkSettings.SIZE / Factor;

        // ── Step 1: 3D downsample ──────────────────────────────────────────────
        //
        // For each NxNxN cell, scan its full-res footprint. The cell is solid
        // if any block inside is non-air; the representative type is the first
        // non-air block found (break out of all three loops via the rep==0 guards).
        var solid = new NativeArray<bool>(cells * cells * cells, Allocator.Temp);
        var cellBlock = new NativeArray<byte>(cells * cells * cells, Allocator.Temp);

        for (int cx = 0; cx < cells; cx++)
            for (int cy = 0; cy < cells; cy++)
                for (int cz = 0; cz < cells; cz++)
                {
                    int ci = CellIdx(cx, cy, cz, cells);
                    byte rep = 0;

                    for (int dx = 0; dx < Factor && rep == 0; dx++)
                        for (int dy = 0; dy < Factor && rep == 0; dy++)
                            for (int dz = 0; dz < Factor && rep == 0; dz++)
                            {
                                byte b = Blocks[ChunkSettings.Index(
                                    cx * Factor + dx,
                                    cy * Factor + dy,
                                    cz * Factor + dz)];
                                if (b != 0) rep = b;
                            }

                    solid[ci] = rep != 0;
                    cellBlock[ci] = rep;
                }

        // ── Step 2: Greedy mesh over the downsampled grid ──────────────────────
        //
        // Mask is cells×cells (one 2D slice at a time). Encoded as (tileIndex+1)
        // so 0 = empty, matching the pattern in BuildChunkMeshJob.
        var mask = new NativeArray<int>(cells * cells, Allocator.Temp);

        GreedyAxis(solid, cellBlock, mask, cells, axis: 1, forwardDir: 0, backwardDir: 1); // +Y/-Y
        GreedyAxis(solid, cellBlock, mask, cells, axis: 0, forwardDir: 2, backwardDir: 3); // +X/-X
        GreedyAxis(solid, cellBlock, mask, cells, axis: 2, forwardDir: 4, backwardDir: 5); // +Z/-Z

        solid.Dispose();
        cellBlock.Dispose();
        mask.Dispose();
    }

    // ── Greedy sweep for one axis ─────────────────────────────────────────────

    void GreedyAxis(
        NativeArray<bool> solid,
        NativeArray<byte> cellBlock,
        NativeArray<int> mask,
        int cells, int axis, int forwardDir, int backwardDir)
    {
        int uAxis = (axis + 1) % 3;
        int vAxis = (axis + 2) % 3;
        var pos = new int3();

        for (int slice = 0; slice < cells; slice++)
        {
            // ── Forward face: solid cell, air on the +axis side ────────────────
            for (int v = 0; v < cells; v++)
                for (int u = 0; u < cells; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    int ci = CellIdx(pos.x, pos.y, pos.z, cells);

                    int encoded = 0;
                    if (solid[ci])
                    {
                        // Neighbour is either outside the chunk (treat as air = show face)
                        // or inside the chunk (show face only if air).
                        int ni = slice + 1;
                        bool faceVisible = ni >= cells; // outside chunk → space → air
                        if (!faceVisible)
                        {
                            var nPos = pos; nPos[axis] = ni;
                            faceVisible = !solid[CellIdx(nPos.x, nPos.y, nPos.z, cells)];
                        }
                        if (faceVisible)
                            encoded = BlockFaceAtlas[cellBlock[ci] * 6 + forwardDir] + 1;
                    }
                    mask[u + v * cells] = encoded;
                }
            GreedySweep(mask, cells, slice, axis, uAxis, vAxis, forward: true);

            // ── Backward face: solid cell, air on the -axis side ───────────────
            for (int v = 0; v < cells; v++)
                for (int u = 0; u < cells; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    int ci = CellIdx(pos.x, pos.y, pos.z, cells);

                    int encoded = 0;
                    if (solid[ci])
                    {
                        int ni = slice - 1;
                        bool faceVisible = ni < 0; // outside chunk → space → air
                        if (!faceVisible)
                        {
                            var nPos = pos; nPos[axis] = ni;
                            faceVisible = !solid[CellIdx(nPos.x, nPos.y, nPos.z, cells)];
                        }
                        if (faceVisible)
                            encoded = BlockFaceAtlas[cellBlock[ci] * 6 + backwardDir] + 1;
                    }
                    mask[u + v * cells] = encoded;
                }
            GreedySweep(mask, cells, slice, axis, uAxis, vAxis, forward: false);
        }
    }

    // ── Greedy sweep over one filled mask slice ───────────────────────────────

    void GreedySweep(
        NativeArray<int> mask, int cells, int slice,
        int axis, int uAxis, int vAxis, bool forward)
    {
        for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; u++)
            {
                int encoded = mask[u + v * cells];
                if (encoded == 0) continue;

                // Grow width along u
                int w = 1;
                while (u + w < cells && mask[u + w + v * cells] == encoded) w++;

                // Grow height along v while the full width row matches
                int h = 1;
                bool done = false;
                while (!done && v + h < cells)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[u + k + (v + h) * cells] != encoded) { done = true; break; }
                    if (!done) h++;
                }

                var origin = new int3();
                origin[axis] = slice;
                origin[uAxis] = u;
                origin[vAxis] = v;
                EmitQuad(origin, w, h, axis, uAxis, vAxis, encoded - 1, forward);

                // Clear consumed cells
                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        mask[u + du + (v + dv) * cells] = 0;

                u += w - 1;
            }
    }

    // ── Quad emission ─────────────────────────────────────────────────────────

    /// <summary>
    /// Emits a quad in the downsampled cell grid. All coordinates are multiplied
    /// by Factor to convert cell positions back to world-space voxel positions,
    /// so the LOD mesh aligns exactly with the full-res mesh's block grid.
    /// </summary>
    void EmitQuad(int3 origin, int w, int h,
                  int axis, int uAxis, int vAxis,
                  int tile, bool forward)
    {
        int vertBase = Vertices.Length;
        int axisBump = forward ? 1 : 0;
        int f = Factor; // cell → world-space voxel scale

        var c0 = new int3(origin.x, origin.y, origin.z); c0[axis] += axisBump;
        var c1 = c0; c1[uAxis] += w;
        var c2 = c0; c2[uAxis] += w; c2[vAxis] += h;
        var c3 = c0; c3[vAxis] += h;

        Vertices.Add(new float3(c0.x * f, c0.y * f, c0.z * f));
        Vertices.Add(new float3(c1.x * f, c1.y * f, c1.z * f));
        Vertices.Add(new float3(c2.x * f, c2.y * f, c2.z * f));
        Vertices.Add(new float3(c3.x * f, c3.y * f, c3.z * f));

        const int ATLAS_COLS = 16;
        float tileSize = 1f / ATLAS_COLS;
        float u0 = (tile % ATLAS_COLS) * tileSize;
        float v0 = (tile / ATLAS_COLS) * tileSize;
        UVs.Add(new float2(u0, v0));
        UVs.Add(new float2(u0 + tileSize, v0));
        UVs.Add(new float2(u0 + tileSize, v0 + tileSize));
        UVs.Add(new float2(u0, v0 + tileSize));

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    static int CellIdx(int x, int y, int z, int cells)
        => x + y * cells + z * cells * cells;
}

// ── LOD build result carrier ──────────────────────────────────────────────────

struct LODBuildResult
{
    public Entity Entity;
    public int3 Coord;
    public NativeArray<byte> Blocks;
    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<int> Triangles;
    public JobHandle Handle;
    public int Factor;
    public bool HasExistingMesh;
    public Entity ExistingRenderEntity;
}

// ── System ────────────────────────────────────────────────────────────────────

/// <summary>
/// Builds LOD meshes for chunks tagged ChunkDirty whose ChunkLODState is
/// Medium, Far, or VeryFar. Full-detail chunks are handled by ChunkMeshSystem.
/// </summary>
[UpdateAfter(typeof(ChunkLODSystem))]
public partial class ChunkLODMeshSystem : SystemBase
{
    Entity _prototype;
    Material _material;
    NativeArray<int> _blockFaceAtlas;

    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld") { Enabled = false; return; }
        BakeAtlas();
    }

    protected override void OnDestroy()
    {
        if (_blockFaceAtlas.IsCreated) _blockFaceAtlas.Dispose();
    }

    void BakeAtlas()
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
        var rma = new RenderMeshArray(
                            new Material[] { _material },
                            new Mesh[] { blankMesh });

        _prototype = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(_prototype, EntityManager, desc, rma,
            MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        EntityManager.AddComponentData(_prototype, LocalTransform.Identity);
    }

    protected override void OnUpdate()
    {
        EnsurePrototype();

        // ── Pass 1: collect dirty LOD chunks, schedule jobs ────────────────────
        var buildResults = new System.Collections.Generic.List<LODBuildResult>();

        foreach (var (pos, blocks, lodState, renderRef, entity) in
            SystemAPI
                .Query<RefRO<ChunkPosition>,
                       DynamicBuffer<BlockElement>,
                       RefRO<ChunkLODState>,
                       RefRO<ChunkRenderEntity>>()
                .WithAll<ChunkDirty>()
                .WithEntityAccess())
        {
            int factor = LODFactor(lodState.ValueRO.Level);
            if (factor <= 0) continue;

            var blocksCopy = new NativeArray<byte>(ChunkSettings.VOLUME, Allocator.TempJob);
            blocksCopy.CopyFrom(blocks.AsNativeArray().Reinterpret<byte>());

            var verts = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var tris = new NativeList<int>(Allocator.TempJob);

            var handle = new BuildLODMeshJob
            {
                Blocks = blocksCopy,
                BlockFaceAtlas = _blockFaceAtlas,
                Factor = factor,
                Vertices = verts,
                UVs = uvs,
                Triangles = tris,
            }.Schedule();

            buildResults.Add(new LODBuildResult
            {
                Entity = entity,
                Coord = pos.ValueRO.Coord,
                Blocks = blocksCopy,
                Vertices = verts,
                UVs = uvs,
                Triangles = tris,
                Handle = handle,
                Factor = factor,
                HasExistingMesh = EntityManager.HasComponent<ChunkMeshRef>(entity),
                ExistingRenderEntity = renderRef.ValueRO.Value,
            });
        }

        // ── Pass 2: structural changes ─────────────────────────────────────────
        foreach (var r in buildResults)
            EntityManager.RemoveComponent<ChunkDirty>(r.Entity);

        // ── Pass 3: complete, upload, manage render entities ───────────────────
        foreach (var r in buildResults)
        {
            r.Handle.Complete();

            // Resolve or allocate the Mesh object
            Mesh mesh;
            if (r.HasExistingMesh)
            {
                mesh = EntityManager.GetComponentObject<ChunkMeshRef>(r.Entity).Value;
                mesh.Clear();
            }
            else
            {
                mesh = new Mesh { name = $"LOD{r.Factor} {r.Coord}" };
                mesh.indexFormat = IndexFormat.UInt32;
                EntityManager.AddComponentObject(r.Entity, new ChunkMeshRef { Value = mesh });
            }

            // Upload geometry
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

            // Spawn a render entity if none is currently live
            if (r.ExistingRenderEntity == Entity.Null)
            {
                var renderEntity = EntityManager.Instantiate(_prototype);

                EntityManager.SetComponentData(renderEntity,
                    LocalTransform.FromPosition((float3)(r.Coord * ChunkSettings.SIZE)));

                EntityManager.SetComponentData(renderEntity, new RenderBounds
                {
                    Value = new Unity.Mathematics.AABB
                    {
                        Center = new float3(ChunkSettings.SIZE * 0.5f,
                                             ChunkSettings.SIZE * 0.5f,
                                             ChunkSettings.SIZE * 0.5f),
                        Extents = new float3(ChunkSettings.SIZE * 0.5f,
                                             ChunkSettings.SIZE * 0.5f,
                                             ChunkSettings.SIZE * 0.5f)
                    }
                });

                var newArray = new RenderMeshArray(
                    new Material[] { _material },
                    new Mesh[] { mesh });
                EntityManager.SetComponentData(renderEntity,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                EntityManager.SetSharedComponentManaged(renderEntity, newArray);

                EntityManager.SetComponentData(r.Entity,
                    new ChunkRenderEntity { Value = renderEntity });
            }

            r.Blocks.Dispose();
            r.Vertices.Dispose();
            r.UVs.Dispose();
            r.Triangles.Dispose();
        }
    }

    static int LODFactor(ChunkLODLevel level) => level switch
    {
        ChunkLODLevel.Medium => ChunkLODSettings.MediumFactor,
        ChunkLODLevel.Far => ChunkLODSettings.FarFactor,
        ChunkLODLevel.VeryFar => ChunkLODSettings.VeryFarFactor,
        _ => 0
    };
}