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

[BurstCompile]
public struct BuildChunkMeshJob : IJob
{
    [ReadOnly] public NativeArray<byte> Blocks;
    [ReadOnly] public NativeArray<byte> NeighborPosY;
    [ReadOnly] public NativeArray<byte> NeighborNegY;
    [ReadOnly] public NativeArray<byte> NeighborPosX;
    [ReadOnly] public NativeArray<byte> NeighborNegX;
    [ReadOnly] public NativeArray<byte> NeighborPosZ;
    [ReadOnly] public NativeArray<byte> NeighborNegZ;
    [ReadOnly] public NativeArray<int> BlockFaceAtlas;

    public NativeList<float3> Vertices;
    /// <summary>UV channel 0 — local block coords (0..w, 0..h). Shader fracs
    /// these to repeat once per block unit, then scales to one atlas tile.</summary>
    public NativeList<float2> UVs;
    /// <summary>UV channel 1 — atlas tile base (col/16, row/16), same for all
    /// 4 verts of a quad since they all sample from the same tile.</summary>
    public NativeList<float2> AtlasUVs;
    public NativeList<int> Triangles;

    public void Execute()
    {
        var mask = new NativeArray<int>(ChunkSettings.FACE, Allocator.Temp);
        GreedyAxis(mask, axis: 1, forwardDir: 0, backwardDir: 1);
        GreedyAxis(mask, axis: 0, forwardDir: 2, backwardDir: 3);
        GreedyAxis(mask, axis: 2, forwardDir: 4, backwardDir: 5);
        mask.Dispose();
    }

    void GreedyAxis(NativeArray<int> mask, int axis, int forwardDir, int backwardDir)
    {
        int uAxis = (axis + 1) % 3;
        int vAxis = (axis + 2) % 3;
        int S = ChunkSettings.SIZE;
        var pos = new int3();

        for (int slice = 0; slice < S; slice++)
        {
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

    void GreedySweep(NativeArray<int> mask, int slice,
                     int axis, int uAxis, int vAxis, bool forward)
    {
        int S = ChunkSettings.SIZE;
        for (int v = 0; v < S; v++)
            for (int u = 0; u < S; u++)
            {
                int encoded = mask[ChunkSettings.SliceIndex(u, v)];
                if (encoded == 0) continue;

                int w = 1;
                while (u + w < S && mask[ChunkSettings.SliceIndex(u + w, v)] == encoded) w++;

                int h = 1;
                bool done = false;
                while (!done && v + h < S)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[ChunkSettings.SliceIndex(u + k, v + h)] != encoded) { done = true; break; }
                    if (!done) h++;
                }

                var origin = new int3();
                origin[axis] = slice; origin[uAxis] = u; origin[vAxis] = v;
                EmitQuad(origin, w, h, axis, uAxis, vAxis, encoded - 1, forward);

                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        mask[ChunkSettings.SliceIndex(u + du, v + dv)] = 0;

                u += w - 1;
            }
    }

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

        // UV0: local block coordinates across the quad.
        // (0,0)→c0, (w,0)→c1, (w,h)→c2, (0,h)→c3.
        // The shader does frac(uv0) to tile once per block unit.
        UVs.Add(new float2(0, 0));
        UVs.Add(new float2(w, 0));
        UVs.Add(new float2(w, h));
        UVs.Add(new float2(0, h));

        // UV1: atlas tile base — same for all 4 verts.
        const int ATLAS_COLS = 16;
        float tileSize = 1f / ATLAS_COLS;
        var atlasBase = new float2(
            (tile % ATLAS_COLS) * tileSize,
            (tile / ATLAS_COLS) * tileSize);
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

    bool NeighborIsAir(int3 pos, int axis, int sign)
    {
        int nx = pos.x, ny = pos.y, nz = pos.z;
        if (axis == 0) nx += sign;
        else if (axis == 1) ny += sign;
        else nz += sign;

        int S = ChunkSettings.SIZE;
        if (nx >= 0 && nx < S && ny >= 0 && ny < S && nz >= 0 && nz < S)
            return Blocks[ChunkSettings.Index(nx, ny, nz)] == 0;

        NativeArray<byte> slice;
        int su, sv;
        if (axis == 1) { slice = sign > 0 ? NeighborPosY : NeighborNegY; su = pos.x; sv = pos.z; }
        else if (axis == 0) { slice = sign > 0 ? NeighborPosX : NeighborNegX; su = pos.z; sv = pos.y; }
        else { slice = sign > 0 ? NeighborPosZ : NeighborNegZ; su = pos.x; sv = pos.y; }

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
    public NativeList<float2> AtlasUVs;
    public NativeList<int> Triangles;
    public JobHandle Handle;
    public bool HasExistingMesh;
    public Entity ExistingRenderEntity;
}

// ── System ────────────────────────────────────────────────────────────────────

[UpdateAfter(typeof(ChunkLODSystem))]
public partial class ChunkMeshSystem : SystemBase
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

    bool EnsureAtlas()
    {
        if (_blockFaceAtlas.IsCreated) return true;
        if (BlockRegistry.Faces.Length == 0) return false;

        var reg = BlockRegistry.Faces;
        _blockFaceAtlas = new NativeArray<int>(reg.Length * 6, Allocator.Persistent);
        for (int i = 0; i < reg.Length; i++)
            for (int d = 0; d < 6; d++)
                _blockFaceAtlas[i * 6 + d] = reg[i].ForDirection(d);
        return true;
    }

    void EnsurePrototype()
    {
        if (_material == null)
            _material = Resources.Load<Material>("ChunkMaterial");

        if (_prototype != Entity.Null) return;

        var blankMesh = new Mesh();
        var desc = new RenderMeshDescription(ShadowCastingMode.On, true);
        var renderMeshArray = new RenderMeshArray(new Material[] { _material }, new Mesh[] { blankMesh });

        _prototype = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(_prototype, EntityManager, desc,
            renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        EntityManager.AddComponentData(_prototype, LocalTransform.Identity);
    }

    protected override void OnUpdate()
    {
        if (!EnsureAtlas()) return;
        EnsurePrototype();

        var buildResults = new System.Collections.Generic.List<ChunkBuildResult>();

        foreach (var (pos, blocks, lod, renderRef, entity) in
            SystemAPI
                .Query<RefRO<ChunkPosition>,
                       DynamicBuffer<BlockElement>,
                       RefRO<ChunkLODState>,
                       RefRO<ChunkRenderEntity>>()
                .WithAll<ChunkDirty>()
                .WithEntityAccess())
        {
            if (lod.ValueRO.Level != ChunkLODLevel.Full) continue;

            var blocksCopy = new NativeArray<byte>(ChunkSettings.VOLUME, Allocator.TempJob);
            blocksCopy.CopyFrom(blocks.AsNativeArray().Reinterpret<byte>());

            ChunkNeighborSlices ns =
                EntityManager.HasComponent<ChunkNeighborSlices>(entity)
                    ? EntityManager.GetComponentObject<ChunkNeighborSlices>(entity)
                    : new ChunkNeighborSlices();

            var nPosY = new NativeArray<byte>(ns.PosY, Allocator.TempJob);
            var nNegY = new NativeArray<byte>(ns.NegY, Allocator.TempJob);
            var nPosX = new NativeArray<byte>(ns.PosX, Allocator.TempJob);
            var nNegX = new NativeArray<byte>(ns.NegX, Allocator.TempJob);
            var nPosZ = new NativeArray<byte>(ns.PosZ, Allocator.TempJob);
            var nNegZ = new NativeArray<byte>(ns.NegZ, Allocator.TempJob);

            var verts = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var atlasUvs = new NativeList<float2>(Allocator.TempJob);
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
                AtlasUVs = atlasUvs,
                Triangles = tris,
            }.Schedule();

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
                AtlasUVs = atlasUvs,
                Triangles = tris,
                Handle = handle,
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
                mesh = new Mesh { name = $"Chunk {r.Coord.x},{r.Coord.z}" };
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
}