using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

// ── Downsampled LOD mesh job ──────────────────────────────────────────────────

/// <summary>
/// Builds a downsampled mesh for a chunk LOD tier.
///
/// For each NxN column (in XZ), picks a representative block by majority vote
/// down the Y axis, then emits a single tall quad for visible faces.
/// This produces a coarser but recognisable version of the chunk geometry.
///
/// The factor N is passed in at runtime (2 / 4 / 8 for Medium / Far / VeryFar).
/// </summary>
[BurstCompile]
public struct BuildLODMeshJob : IJob
{
    [ReadOnly] public NativeArray<byte> Blocks;
    [ReadOnly] public NativeArray<int> BlockFaceAtlas;
    public int Factor;   // downsample factor (2, 4, or 8)

    public NativeList<float3> Vertices;
    public NativeList<float2> UVs;
    public NativeList<int> Triangles;

    public void Execute()
    {
        int S = ChunkSettings.SIZE;
        int cells = S / Factor;   // number of cells per axis at this LOD

        // Build a 2D height map and block-type map at the downsampled resolution.
        // For each XZ cell we find the topmost non-air block after downsampling.
        var cellBlock = new NativeArray<byte>(cells * cells, Allocator.Temp);
        var cellHeight = new NativeArray<int>(cells * cells, Allocator.Temp);

        for (int cx = 0; cx < cells; cx++)
            for (int cz = 0; cz < cells; cz++)
            {
                int ci = cx + cz * cells;
                cellBlock[ci] = 0;
                cellHeight[ci] = -1;

                // Sample the full-res blocks in this cell's footprint.
                // Pick the topmost non-air block as representative.
                for (int y = S - 1; y >= 0; y--)
                {
                    if (cellHeight[ci] >= 0) break;
                    for (int dx = 0; dx < Factor && cellHeight[ci] < 0; dx++)
                        for (int dz = 0; dz < Factor && cellHeight[ci] < 0; dz++)
                        {
                            int fx = cx * Factor + dx;
                            int fz = cz * Factor + dz;
                            byte b = Blocks[ChunkSettings.Index(fx, y, fz)];
                            if (b != 0)
                            {
                                cellBlock[ci] = b;
                                cellHeight[ci] = y;
                            }
                        }
                }
            }

        // Emit quads: top face for each occupied cell, side faces where height drops.
        for (int cx = 0; cx < cells; cx++)
            for (int cz = 0; cz < cells; cz++)
            {
                int ci = cx + cz * cells;
                if (cellHeight[ci] < 0) continue; // empty column

                byte b = cellBlock[ci];
                int h = cellHeight[ci] + 1; // world-space top y of this cell
                int wx = cx * Factor;
                int wz = cz * Factor;
                int w = Factor;

                // Top face (dir=0)
                int topTile = BlockFaceAtlas[b * 6 + 0];
                EmitQuad(wx, h, wz, w, w, topTile, face: 0);

                // Side faces — only where the neighbor cell is lower or empty
                // +X neighbor
                if (cx + 1 < cells)
                {
                    int ni = (cx + 1) + cz * cells;
                    int nh = cellHeight[ni] + 1;
                    if (nh < h)
                    {
                        int sideTile = BlockFaceAtlas[b * 6 + 2];
                        EmitQuad(wx + w, nh, wz, h - nh, w, sideTile, face: 2);
                    }
                }
                // -X neighbor
                if (cx - 1 >= 0)
                {
                    int ni = (cx - 1) + cz * cells;
                    int nh = cellHeight[ni] + 1;
                    if (nh < h)
                    {
                        int sideTile = BlockFaceAtlas[b * 6 + 3];
                        EmitQuad(wx, nh, wz, h - nh, w, sideTile, face: 3);
                    }
                }
                // +Z neighbor
                if (cz + 1 < cells)
                {
                    int ni = cx + (cz + 1) * cells;
                    int nh = cellHeight[ni] + 1;
                    if (nh < h)
                    {
                        int sideTile = BlockFaceAtlas[b * 6 + 4];
                        EmitQuad(wx, nh, wz + w, h - nh, w, sideTile, face: 4);
                    }
                }
                // -Z neighbor
                if (cz - 1 >= 0)
                {
                    int ni = cx + (cz - 1) * cells;
                    int nh = cellHeight[ni] + 1;
                    if (nh < h)
                    {
                        int sideTile = BlockFaceAtlas[b * 6 + 5];
                        EmitQuad(wx, nh, wz, h - nh, w, sideTile, face: 5);
                    }
                }
            }

        cellBlock.Dispose();
        cellHeight.Dispose();
    }

    // Emits a single axis-aligned quad.
    // face: 0=top(+Y), 2=+X side, 3=-X side, 4=+Z side, 5=-Z side
    void EmitQuad(int x, int y, int z, int height, int width, int tile, int face)
    {
        int vb = Vertices.Length;

        switch (face)
        {
            case 0: // top face in XZ plane at y
                Vertices.Add(new float3(x, y, z));
                Vertices.Add(new float3(x + width, y, z));
                Vertices.Add(new float3(x + width, y, z + width));
                Vertices.Add(new float3(x, y, z + width));
                break;
            case 2: // +X face
                Vertices.Add(new float3(x, y, z));
                Vertices.Add(new float3(x, y + height, z));
                Vertices.Add(new float3(x, y + height, z + width));
                Vertices.Add(new float3(x, y, z + width));
                break;
            case 3: // -X face
                Vertices.Add(new float3(x, y, z + width));
                Vertices.Add(new float3(x, y + height, z + width));
                Vertices.Add(new float3(x, y + height, z));
                Vertices.Add(new float3(x, y, z));
                break;
            case 4: // +Z face
                Vertices.Add(new float3(x + width, y, z));
                Vertices.Add(new float3(x + width, y + height, z));
                Vertices.Add(new float3(x, y + height, z));
                Vertices.Add(new float3(x, y, z));
                break;
            case 5: // -Z face
                Vertices.Add(new float3(x, y, z));
                Vertices.Add(new float3(x, y + height, z));
                Vertices.Add(new float3(x + width, y + height, z));
                Vertices.Add(new float3(x + width, y, z));
                break;
        }

        const int ATLAS_COLS = 16;
        float tu = tile % ATLAS_COLS;
        float tv = tile / ATLAS_COLS;
        UVs.Add(new float2(tu, tv));
        UVs.Add(new float2(tu + width, tv));
        UVs.Add(new float2(tu + width, tv + height));
        UVs.Add(new float2(tu, tv + height));

        Triangles.Add(vb); Triangles.Add(vb + 1); Triangles.Add(vb + 2);
        Triangles.Add(vb); Triangles.Add(vb + 2); Triangles.Add(vb + 3);
    }
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
    public bool IsFirstBuild;
}

// ── System ────────────────────────────────────────────────────────────────────

/// <summary>
/// Builds LOD meshes for chunks tagged ChunkDirty whose ChunkLODState is
/// Medium, Far, or VeryFar. Full-detail chunks are handled by ChunkMeshSystem.
/// </summary>
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
        var rma = new RenderMeshArray(new Material[] { _material }, new Mesh[] { blankMesh });

        _prototype = EntityManager.CreateEntity();
        RenderMeshUtility.AddComponents(_prototype, EntityManager, desc, rma,
            MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        EntityManager.AddComponentData(_prototype, LocalTransform.Identity);
    }

    protected override void OnUpdate()
    {
        EnsurePrototype();

        // ── Pass 1: collect, schedule ──────────────────────────────────────────
        var buildResults = new System.Collections.Generic.List<LODBuildResult>();

        foreach (var (pos, blocks, lodState, entity) in
            SystemAPI
                .Query<RefRO<ChunkPosition>,
                       DynamicBuffer<BlockElement>,
                       RefRO<ChunkLODState>>()
                .WithAll<ChunkDirty>()
                .WithEntityAccess())
        {
            // This system only handles LOD tiers — full detail goes to ChunkMeshSystem
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
                mesh = new Mesh { name = $"LOD{r.Factor} {r.Coord.x},{r.Coord.z}" };
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

                // Store the render entity reference so ChunkStreamingSystem can destroy it
                EntityManager.SetComponentData(r.Entity,
                    new ChunkRenderEntity { Value = renderEntity });

                var newArray = new RenderMeshArray(
                    new Material[] { _material }, new Mesh[] { mesh });
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

    static int LODFactor(ChunkLODLevel level) => level switch
    {
        ChunkLODLevel.Medium => ChunkLODSettings.MediumFactor,
        ChunkLODLevel.Far => ChunkLODSettings.FarFactor,
        ChunkLODLevel.VeryFar => ChunkLODSettings.VeryFarFactor,
        _ => 0   // Full or Unloaded: not handled here
    };
}