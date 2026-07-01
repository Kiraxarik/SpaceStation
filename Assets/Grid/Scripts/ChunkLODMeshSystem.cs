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
    // ushort, not byte (§1.5) — mirrors BlockElement.Value's width.
    [ReadOnly] public NativeArray<ushort> Blocks;
    [ReadOnly] public NativeArray<int> BlockFaceAtlas;
    public int Factor;

    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<float2> AtlasUVs;
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
        var cellBlock = new NativeArray<ushort>(cells * cells * cells, Allocator.Temp);

        for (int cx = 0; cx < cells; cx++)
            for (int cy = 0; cy < cells; cy++)
                for (int cz = 0; cz < cells; cz++)
                {
                    int ci = CellIdx(cx, cy, cz, cells);
                    ushort rep = 0;

                    for (int dx = 0; dx < Factor && rep == 0; dx++)
                        for (int dy = 0; dy < Factor && rep == 0; dy++)
                            for (int dz = 0; dz < Factor && rep == 0; dz++)
                            {
                                ushort b = Blocks[ChunkSettings.Index(
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
        NativeArray<ushort> cellBlock,
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
            GreedySweep(mask, cells, slice, axis, uAxis, vAxis, forwardDir);

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
            GreedySweep(mask, cells, slice, axis, uAxis, vAxis, backwardDir);
        }
    }

    // ── Greedy sweep over one filled mask slice ───────────────────────────────

    void GreedySweep(
        NativeArray<int> mask, int cells, int slice,
        int axis, int uAxis, int vAxis, int dir)
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
                EmitQuad(origin, w, h, axis, uAxis, vAxis, encoded - 1, dir);

                // Clear consumed cells
                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        mask[u + du + (v + dv) * cells] = 0;

                u += w - 1;
            }
    }

    // ── Quad emission ─────────────────────────────────────────────────────────

    /// <summary>
    /// Per-face texture basis: which world axis (and sign) the tile's U and V
    /// map to, given a face direction (0=+Y,1=-Y,2=+X,3=-X,4=+Z,5=-Z). Must
    /// match ChunkMeshSystem.UVBasis so full-res and LOD meshes orient tiles
    /// identically.
    /// </summary>
    static void UVBasis(int dir, out int uAxis, out int uSign, out int vAxis, out int vSign)
    {
        switch (dir)
        {
            case 0: uAxis = 0; uSign = +1; vAxis = 2; vSign = +1; break; // +Y top
            case 1: uAxis = 0; uSign = -1; vAxis = 2; vSign = +1; break; // -Y bottom
            case 2: uAxis = 2; uSign = +1; vAxis = 1; vSign = +1; break; // +X
            case 3: uAxis = 2; uSign = -1; vAxis = 1; vSign = +1; break; // -X
            case 4: uAxis = 0; uSign = -1; vAxis = 1; vSign = +1; break; // +Z
            default: uAxis = 0; uSign = +1; vAxis = 1; vSign = +1; break; // -Z
        }
    }

    void EmitQuad(int3 origin, int w, int h,
                  int axis, int uAxis, int vAxis,
                  int tile, int dir)
    {
        int vertBase = Vertices.Length;
        bool forward = (dir & 1) == 0;   // even dirs (+Y/+X/+Z) are forward faces
        int axisBump = forward ? 1 : 0;
        int f = Factor;

        var c0 = new int3(origin.x, origin.y, origin.z); c0[axis] += axisBump;
        var c1 = c0; c1[uAxis] += w;
        var c2 = c0; c2[uAxis] += w; c2[vAxis] += h;
        var c3 = c0; c3[vAxis] += h;

        var wp0 = new float3(c0.x * f, c0.y * f, c0.z * f);
        var wp1 = new float3(c1.x * f, c1.y * f, c1.z * f);
        var wp2 = new float3(c2.x * f, c2.y * f, c2.z * f);
        var wp3 = new float3(c3.x * f, c3.y * f, c3.z * f);

        Vertices.Add(wp0);
        Vertices.Add(wp1);
        Vertices.Add(wp2);
        Vertices.Add(wp3);

        // UV0: same world-projected per-face basis as the full-res mesh, so a
        // tile keeps one orientation across LOD transitions (V = world-up on
        // side faces) and stays aligned to the world block grid. Vertices are
        // already in block space (cell × Factor), so the projection inherits the
        // correct per-block tiling density.
        UVBasis(dir, out int uA, out int uS, out int vA, out int vS);
        UVs.Add(new float2(uS * wp0[uA], vS * wp0[vA]));
        UVs.Add(new float2(uS * wp1[uA], vS * wp1[vA]));
        UVs.Add(new float2(uS * wp2[uA], vS * wp2[vA]));
        UVs.Add(new float2(uS * wp3[uA], vS * wp3[vA]));

        // UV1.x: Texture2DArray slice index — same for all 4 verts. The shader
        // samples float3(uv0, slice), with the array's Repeat wrap turning the
        // world-projected UV0 into per-block tiling.
        var sliceUV = new float2(tile, 0f);
        AtlasUVs.Add(sliceUV);
        AtlasUVs.Add(sliceUV);
        AtlasUVs.Add(sliceUV);
        AtlasUVs.Add(sliceUV);

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
    public NativeArray<ushort> Blocks;
    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<float2> AtlasUVs;
    public NativeList<int> Triangles;
    public JobHandle Handle;
    public int Factor;
    public bool HasExistingMesh;
    public Entity ExistingRenderEntity;
}

// ── System ────────────────────────────────────────────────────────────────────

[UpdateAfter(typeof(ChunkLODSystem))]
public partial class ChunkLODMeshSystem : SystemBase
{
    Entity _prototype;
    Material _material;
    NativeArray<int> _blockFaceAtlas;

    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld") { Enabled = false; return; }
    }

    protected override void OnDestroy()
    {
        if (_blockFaceAtlas.IsCreated) _blockFaceAtlas.Dispose();
    }

    /// <summary>
    /// Forces the next EnsureAtlas() call to rebuild _blockFaceAtlas (and rebind
    /// the material's Texture2DArray) from scratch. Called by ClientAssetSyncSystem
    /// after a download changes local content — mirrors ChunkMeshSystem's method
    /// of the same name; both mesh systems cache their own atlas index array and
    /// both need invalidating together.
    /// </summary>
    public void InvalidateAtlasCache()
    {
        if (_blockFaceAtlas.IsCreated) _blockFaceAtlas.Dispose();
    }

    bool EnsureAtlas()
    {
        if (_blockFaceAtlas.IsCreated) return true;
        if (BlockRegistry.Faces.Length == 0) return false;
        if (!TileAtlasBaker.EnsureBaked()) return false;

        // Rebind the material to whatever TileAtlasBaker just (re)baked — see
        // ChunkMeshSystem.EnsureAtlas for the full rationale; same fix, same
        // reason, duplicated because this is a separate SystemBase instance with
        // its own _material field.
        if (_material != null)
            _material.SetTexture("_TileArray", TileAtlasBaker.Array);

        // Resolve each block face's tile id to its Texture2DArray slice once, into
        // a flat int[] the Burst job reads. (Job is unchanged: the int it reads is
        // now a slice index instead of an old-atlas tile index.)
        var reg = BlockRegistry.Faces;
        _blockFaceAtlas = new NativeArray<int>(reg.Length * 6, Allocator.Persistent);
        for (int i = 0; i < reg.Length; i++)
            for (int d = 0; d < 6; d++)
                _blockFaceAtlas[i * 6 + d] = TileAtlasBaker.SliceOf(reg[i].ForDirection(d));
        return true;
    }

    void EnsurePrototype()
    {
        if (_material == null)
        {
            _material = Resources.Load<Material>("ChunkMaterial");
            if (_material != null)
                _material.SetTexture("_TileArray", TileAtlasBaker.Array);
        }

        if (_prototype != Entity.Null) return;

        var blankMesh = new Mesh();
        var desc = new RenderMeshDescription(ShadowCastingMode.On, true);
        var rma = new RenderMeshArray(new Material[] { _material }, new Mesh[] { blankMesh });

        _prototype = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(_prototype, EntityManager, desc, rma,
            MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        EntityManager.AddComponentData(_prototype, LocalTransform.Identity);
    }

    protected override void OnUpdate()
    {
        if (!EnsureAtlas()) return;
        EnsurePrototype();

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

            var blocksCopy = new NativeArray<ushort>(ChunkSettings.VOLUME, Allocator.TempJob);
            blocksCopy.CopyFrom(blocks.AsNativeArray().Reinterpret<ushort>());

            var verts = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var atlasUvs = new NativeList<float2>(Allocator.TempJob);
            var tris = new NativeList<int>(Allocator.TempJob);

            var handle = new BuildLODMeshJob
            {
                Blocks = blocksCopy,
                BlockFaceAtlas = _blockFaceAtlas,
                Factor = factor,
                Vertices = verts,
                UVs = uvs,
                AtlasUVs = atlasUvs,
                Triangles = tris,
            }.Schedule();

            buildResults.Add(new LODBuildResult
            {
                Entity = entity,
                Coord = pos.ValueRO.Coord,
                Blocks = blocksCopy,
                Vertices = verts,
                UVs = uvs,
                AtlasUVs = atlasUvs,
                Triangles = tris,
                Handle = handle,
                Factor = factor,
                HasExistingMesh = EntityManager.HasComponent<ChunkMeshRef>(entity),
                ExistingRenderEntity = renderRef.ValueRO.Value,
            });
        }

        foreach (var r in buildResults)
            EntityManager.RemoveComponent<ChunkDirty>(r.Entity);

        foreach (var r in buildResults)
        {
            r.Handle.Complete();

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

            mesh.SetVertices(r.Vertices.AsArray());
            mesh.SetUVs(0, r.UVs.AsArray());
            mesh.SetUVs(1, r.AtlasUVs.AsArray());

            int indexCount = r.Triangles.Length;
            mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            mesh.SetIndexBufferData(r.Triangles.AsArray(), 0, 0, indexCount,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount),
                MeshUpdateFlags.DontRecalculateBounds);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

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

                var newArray = new RenderMeshArray(new Material[] { _material }, new Mesh[] { mesh });
                EntityManager.SetComponentData(renderEntity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                EntityManager.SetSharedComponentManaged(renderEntity, newArray);
                EntityManager.SetComponentData(r.Entity, new ChunkRenderEntity { Value = renderEntity });
            }

            r.Blocks.Dispose();
            r.Vertices.Dispose();
            r.UVs.Dispose();
            r.AtlasUVs.Dispose();
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