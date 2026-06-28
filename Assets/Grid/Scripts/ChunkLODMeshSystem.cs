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

[BurstCompile]
public struct BuildLODMeshJob : IJob
{
    [ReadOnly] public NativeArray<byte> Blocks;
    [ReadOnly] public NativeArray<int> BlockFaceAtlas;
    public int Factor;

    public NativeList<float3> Vertices;

    /// <summary>
    /// Channel 0: local block-space coordinates across the quad.
    /// Uses block units (cell units × Factor) so tiling density matches the
    /// full-res mesh — a 2-cell-wide quad at Factor=2 covers 4 blocks and tiles 4×.
    /// </summary>
    public NativeList<float2> UVs;

    /// <summary>Channel 1: atlas tile base (same for all 4 verts of a quad).</summary>
    public NativeList<float2> AtlasUVs;

    public NativeList<int> Triangles;

    public void Execute()
    {
        int cells = ChunkSettings.SIZE / Factor;

        // ── Step 1: 3D downsample ──────────────────────────────────────────────
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

        // ── Step 2: greedy mesh on downsampled grid ────────────────────────────
        var mask = new NativeArray<int>(cells * cells, Allocator.Temp);

        GreedyAxis(solid, cellBlock, mask, cells, axis: 1, forwardDir: 0, backwardDir: 1);
        GreedyAxis(solid, cellBlock, mask, cells, axis: 0, forwardDir: 2, backwardDir: 3);
        GreedyAxis(solid, cellBlock, mask, cells, axis: 2, forwardDir: 4, backwardDir: 5);

        solid.Dispose();
        cellBlock.Dispose();
        mask.Dispose();
    }

    void GreedyAxis(
        NativeArray<bool> solid, NativeArray<byte> cellBlock,
        NativeArray<int> mask, int cells,
        int axis, int forwardDir, int backwardDir)
    {
        int uAxis = (axis + 1) % 3;
        int vAxis = (axis + 2) % 3;
        var pos = new int3();

        for (int slice = 0; slice < cells; slice++)
        {
            // Forward
            for (int v = 0; v < cells; v++)
                for (int u = 0; u < cells; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    int ci = CellIdx(pos.x, pos.y, pos.z, cells);
                    int encoded = 0;
                    if (solid[ci])
                    {
                        int ni = slice + 1;
                        bool air = ni >= cells;
                        if (!air) { var n = pos; n[axis] = ni; air = !solid[CellIdx(n.x, n.y, n.z, cells)]; }
                        if (air) encoded = BlockFaceAtlas[cellBlock[ci] * 6 + forwardDir] + 1;
                    }
                    mask[u + v * cells] = encoded;
                }
            GreedySweep(mask, cells, slice, axis, uAxis, vAxis, forward: true);

            // Backward
            for (int v = 0; v < cells; v++)
                for (int u = 0; u < cells; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    int ci = CellIdx(pos.x, pos.y, pos.z, cells);
                    int encoded = 0;
                    if (solid[ci])
                    {
                        int ni = slice - 1;
                        bool air = ni < 0;
                        if (!air) { var n = pos; n[axis] = ni; air = !solid[CellIdx(n.x, n.y, n.z, cells)]; }
                        if (air) encoded = BlockFaceAtlas[cellBlock[ci] * 6 + backwardDir] + 1;
                    }
                    mask[u + v * cells] = encoded;
                }
            GreedySweep(mask, cells, slice, axis, uAxis, vAxis, forward: false);
        }
    }

    void GreedySweep(NativeArray<int> mask, int cells, int slice,
                     int axis, int uAxis, int vAxis, bool forward)
    {
        for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; u++)
            {
                int encoded = mask[u + v * cells];
                if (encoded == 0) continue;

                int w = 1;
                while (u + w < cells && mask[u + w + v * cells] == encoded) w++;

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

                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        mask[u + du + (v + dv) * cells] = 0;

                u += w - 1;
            }
    }

    void EmitQuad(int3 origin, int w, int h,
                  int axis, int uAxis, int vAxis,
                  int tile, bool forward)
    {
        int vertBase = Vertices.Length;
        int axisBump = forward ? 1 : 0;
        int f = Factor;

        var c0 = new int3(origin.x, origin.y, origin.z); c0[axis] += axisBump;
        var c1 = c0; c1[uAxis] += w;
        var c2 = c0; c2[uAxis] += w; c2[vAxis] += h;
        var c3 = c0; c3[vAxis] += h;

        Vertices.Add(new float3(c0.x * f, c0.y * f, c0.z * f));
        Vertices.Add(new float3(c1.x * f, c1.y * f, c1.z * f));
        Vertices.Add(new float3(c2.x * f, c2.y * f, c2.z * f));
        Vertices.Add(new float3(c3.x * f, c3.y * f, c3.z * f));

        // UV channel 0: block-space dimensions (cell units × Factor).
        // A 2-cell-wide quad at Factor=2 covers 4 blocks → tiles 4×,
        // matching the density of the full-res mesh.
        float uw = w * f;
        float uh = h * f;
        UVs.Add(new float2(0, 0));
        UVs.Add(new float2(uw, 0));
        UVs.Add(new float2(uw, uh));
        UVs.Add(new float2(0, uh));

        // UV channel 1: atlas tile base (same for all 4 verts).
        const int ATLAS_COLS = 16;
        float tileSize = 1f / ATLAS_COLS;
        float au = (tile % ATLAS_COLS) * tileSize;
        float av = (tile / ATLAS_COLS) * tileSize;
        var atlasBase = new float2(au, av);
        AtlasUVs.Add(atlasBase);
        AtlasUVs.Add(atlasBase);
        AtlasUVs.Add(atlasBase);
        AtlasUVs.Add(atlasBase);

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

        // ── Pass 1: collect & schedule ─────────────────────────────────────────
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

        // ── Pass 2: structural changes ─────────────────────────────────────────
        foreach (var r in buildResults)
            EntityManager.RemoveComponent<ChunkDirty>(r.Entity);

        // ── Pass 3: complete, upload, manage render entities ───────────────────
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