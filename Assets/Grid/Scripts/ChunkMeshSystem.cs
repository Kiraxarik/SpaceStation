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
    // ushort, not byte (§1.5) — mirrors BlockElement.Value's width.
    [ReadOnly] public NativeArray<ushort> Blocks;
    [ReadOnly] public NativeArray<ushort> NeighborPosY;
    [ReadOnly] public NativeArray<ushort> NeighborNegY;
    [ReadOnly] public NativeArray<ushort> NeighborPosX;
    [ReadOnly] public NativeArray<ushort> NeighborNegX;
    [ReadOnly] public NativeArray<ushort> NeighborPosZ;
    [ReadOnly] public NativeArray<ushort> NeighborNegZ;
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
                    ushort here = Blocks[ChunkSettings.Index(pos.x, pos.y, pos.z)];
                    int encoded = 0;
                    if (here != 0 && NeighborIsAir(pos, axis, +1))
                        encoded = BlockFaceAtlas[here * 6 + forwardDir] + 1;
                    mask[ChunkSettings.SliceIndex(u, v)] = encoded;
                }
            GreedySweep(mask, slice, axis, uAxis, vAxis, forwardDir);

            for (int v = 0; v < S; v++)
                for (int u = 0; u < S; u++)
                {
                    pos[uAxis] = u; pos[vAxis] = v; pos[axis] = slice;
                    ushort here = Blocks[ChunkSettings.Index(pos.x, pos.y, pos.z)];
                    int encoded = 0;
                    if (here != 0 && NeighborIsAir(pos, axis, -1))
                        encoded = BlockFaceAtlas[here * 6 + backwardDir] + 1;
                    mask[ChunkSettings.SliceIndex(u, v)] = encoded;
                }
            GreedySweep(mask, slice, axis, uAxis, vAxis, backwardDir);
        }
    }

    void GreedySweep(NativeArray<int> mask, int slice,
                     int axis, int uAxis, int vAxis, int dir)
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
                EmitQuad(origin, w, h, axis, uAxis, vAxis, encoded - 1, dir);

                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        mask[ChunkSettings.SliceIndex(u + du, v + dv)] = 0;

                u += w - 1;
            }
    }

    /// <summary>
    /// Per-face texture basis: which world axis (and sign) the tile's U and V
    /// map to, given a face direction (0=+Y,1=-Y,2=+X,3=-X,4=+Z,5=-Z).
    /// Side faces all put V on world-up (+Y); signs are chosen so the tile reads
    /// un-mirrored when viewed from outside the face. Top/bottom use a fixed
    /// X→U, Z→V convention so floor/ceiling tiles share one orientation.
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

        var c0 = new int3(origin.x, origin.y, origin.z); c0[axis] += axisBump;
        var c1 = c0; c1[uAxis] += w;
        var c2 = c0; c2[uAxis] += w; c2[vAxis] += h;
        var c3 = c0; c3[vAxis] += h;

        var wp0 = new float3(c0.x, c0.y, c0.z);
        var wp1 = new float3(c1.x, c1.y, c1.z);
        var wp2 = new float3(c2.x, c2.y, c2.z);
        var wp3 = new float3(c3.x, c3.y, c3.z);

        Vertices.Add(wp0);
        Vertices.Add(wp1);
        Vertices.Add(wp2);
        Vertices.Add(wp3);

        // UV0: world-projected through a per-face basis so a tile shows in a
        // consistent orientation on every face — V = world-up (+Y) on all four
        // side faces, fixed convention on top/bottom — instead of inheriting the
        // greedy mesher's per-axis (uAxis,vAxis) swap that turned directional
        // tiles 90° between X and Z faces. Projecting from world position also
        // makes tiles align seamlessly across quad and chunk boundaries. The
        // shader's Repeat wrap does the per-block tiling, so absolute coords
        // (including negatives) are fine.
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

    bool NeighborIsAir(int3 pos, int axis, int sign)
    {
        int nx = pos.x, ny = pos.y, nz = pos.z;
        if (axis == 0) nx += sign;
        else if (axis == 1) ny += sign;
        else nz += sign;

        int S = ChunkSettings.SIZE;
        if (nx >= 0 && nx < S && ny >= 0 && ny < S && nz >= 0 && nz < S)
            return Blocks[ChunkSettings.Index(nx, ny, nz)] == 0;

        NativeArray<ushort> slice;
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
    public NativeArray<ushort> Blocks;
    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<float2> AtlasUVs;
    public NativeList<int> Triangles;
    public JobHandle Handle;
    public bool HasExistingMesh;
    public Entity ExistingRenderEntity;
}

// ── System ────────────────────────────────────────────────────────────────────

// ChunkLODSystem already declares [UpdateBefore(typeof(ChunkMeshSystem))], so an
// [UpdateAfter(typeof(ChunkLODSystem))] here was redundant even when it worked —
// and it didn't work in the server world: ChunkLODSystem carries
// [WorldSystemFilter(ClientSimulation)] and is never created on the server at
// all, while this system had no such filter and relied on a runtime
// `Enabled = false` self-disable in OnCreate instead. Unity's system-ordering
// graph builds before that runtime check ever runs, so in the server world it
// tried to order against a ChunkLODSystem instance that was never created there
// — hence "Ignoring invalid [UpdateAfterAttribute]... make sure both systems are
// in the same system group" on every single launch. The actual fix is this
// filter, not the ordering attribute: excluding the system from the server
// world entirely removes both the log spam and the pointless instantiate-then-
// disable.
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
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

    /// <summary>
    /// Forces the next EnsureAtlas() call to rebuild _blockFaceAtlas (and rebind
    /// the material's Texture2DArray) from scratch. Called by ClientAssetSyncSystem
    /// after a download changes local content — without this, a newly-downloaded
    /// tile's texture would never make it into the already-baked atlas or this
    /// system's cached slice-index array, even though the file landed on disk and
    /// BlockRegistry already knows about it.
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

        // Rebind the material to whatever TileAtlasBaker just (re)baked. Only
        // reached when _blockFaceAtlas is being (re)built — once on first bake,
        // and again whenever InvalidateAtlasCache() forced a rebuild — which are
        // exactly the moments the bound Texture2DArray can have actually changed.
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

            var blocksCopy = new NativeArray<ushort>(ChunkSettings.VOLUME, Allocator.TempJob);
            blocksCopy.CopyFrom(blocks.AsNativeArray().Reinterpret<ushort>());

            ChunkNeighborSlices ns =
                EntityManager.HasComponent<ChunkNeighborSlices>(entity)
                    ? EntityManager.GetComponentObject<ChunkNeighborSlices>(entity)
                    : new ChunkNeighborSlices();

            var nPosY = new NativeArray<ushort>(ns.PosY, Allocator.TempJob);
            var nNegY = new NativeArray<ushort>(ns.NegY, Allocator.TempJob);
            var nPosX = new NativeArray<ushort>(ns.PosX, Allocator.TempJob);
            var nNegX = new NativeArray<ushort>(ns.NegX, Allocator.TempJob);
            var nPosZ = new NativeArray<ushort>(ns.PosZ, Allocator.TempJob);
            var nNegZ = new NativeArray<ushort>(ns.NegZ, Allocator.TempJob);

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

        // NOTE: ChunkDirty is cleared per-chunk at the END of a successful build
        // below — NOT in a separate pass up front. If one chunk throws mid-loop
        // and the flags were already stripped, every queued chunk silently loses
        // its dirty flag and never renders again (this is why a single malformed
        // chunk made the whole world, seed platform included, disappear).

        foreach (var r in buildResults)
        {
            r.Handle.Complete();

            int vCount = r.Vertices.Length;
            bool uv0Ok = r.UVs.Length == vCount;
            bool uv1Ok = r.AtlasUVs.Length == vCount;

            // Hard invariant: one uv0 and one uv1 per vertex. If this trips, the
            // producer (EmitQuad / the job) Added an unequal number of verts vs
            // uvs. Log the exact chunk + counts and skip the bad channel rather
            // than letting Mesh.SetUVs throw and take the batch down with it.
            if (!uv0Ok || !uv1Ok)
            {
                Debug.LogError(
                    $"[ChunkMeshSystem] UV/vertex mismatch at chunk {r.Coord}: " +
                    $"verts={vCount}, uv0={r.UVs.Length}, uv1={r.AtlasUVs.Length}. " +
                    "EmitQuad must Add to Vertices, UVs and AtlasUVs the same number of times.");
            }

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
            if (uv0Ok) mesh.SetUVs(0, r.UVs.AsArray());
            if (uv1Ok) mesh.SetUVs(1, r.AtlasUVs.AsArray());

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

            // Build succeeded for this chunk — now it's safe to clear the flag.
            EntityManager.RemoveComponent<ChunkDirty>(r.Entity);

            r.Blocks.Dispose();
            r.Vertices.Dispose();
            r.UVs.Dispose();
            r.AtlasUVs.Dispose();
            r.Triangles.Dispose();
        }
    }
}