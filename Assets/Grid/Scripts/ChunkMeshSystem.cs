using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

[BurstCompile]
public struct BuildChunkMeshJob : IJob
{
    [ReadOnly] public NativeArray<byte> Blocks;
    public NativeList<float3> Vertices;
    public NativeList<int> Triangles;

    public void Execute()
    {
        for (int x = 0; x < ChunkSettings.SIZE; x++)
            for (int y = 0; y < ChunkSettings.SIZE; y++)
                for (int z = 0; z < ChunkSettings.SIZE; z++)
                {
                    if (Blocks[ChunkSettings.Index(x, y, z)] == 0) continue;

                    if (IsAir(x, y + 1, z)) AddFace(x, y, z, 0);
                    if (IsAir(x, y - 1, z)) AddFace(x, y, z, 1);
                    if (IsAir(x + 1, y, z)) AddFace(x, y, z, 2);
                    if (IsAir(x - 1, y, z)) AddFace(x, y, z, 3);
                    if (IsAir(x, y, z + 1)) AddFace(x, y, z, 4);
                    if (IsAir(x, y, z - 1)) AddFace(x, y, z, 5);
                }
    }

    bool IsAir(int x, int y, int z)
    {
        if (x < 0 || x >= ChunkSettings.SIZE) return true;
        if (y < 0 || y >= ChunkSettings.SIZE) return true;
        if (z < 0 || z >= ChunkSettings.SIZE) return true;
        return Blocks[ChunkSettings.Index(x, y, z)] == 0;
    }

    void AddFace(int x, int y, int z, int dir)
    {
        int v = Vertices.Length;
        float3 p = new float3(x, y, z);
        switch (dir)
        {
            case 0:
                Vertices.Add(p + new float3(0, 1, 0)); Vertices.Add(p + new float3(0, 1, 1));
                Vertices.Add(p + new float3(1, 1, 1)); Vertices.Add(p + new float3(1, 1, 0)); break;
            case 1:
                Vertices.Add(p + new float3(0, 0, 0)); Vertices.Add(p + new float3(1, 0, 0));
                Vertices.Add(p + new float3(1, 0, 1)); Vertices.Add(p + new float3(0, 0, 1)); break;
            case 2:
                Vertices.Add(p + new float3(1, 0, 0)); Vertices.Add(p + new float3(1, 1, 0));
                Vertices.Add(p + new float3(1, 1, 1)); Vertices.Add(p + new float3(1, 0, 1)); break;
            case 3:
                Vertices.Add(p + new float3(0, 0, 1)); Vertices.Add(p + new float3(0, 1, 1));
                Vertices.Add(p + new float3(0, 1, 0)); Vertices.Add(p + new float3(0, 0, 0)); break;
            case 4:
                Vertices.Add(p + new float3(1, 0, 1)); Vertices.Add(p + new float3(1, 1, 1));
                Vertices.Add(p + new float3(0, 1, 1)); Vertices.Add(p + new float3(0, 0, 1)); break;
            case 5:
                Vertices.Add(p + new float3(0, 0, 0)); Vertices.Add(p + new float3(0, 1, 0));
                Vertices.Add(p + new float3(1, 1, 0)); Vertices.Add(p + new float3(1, 0, 0)); break;
        }
        Triangles.Add(v); Triangles.Add(v + 1); Triangles.Add(v + 2);
        Triangles.Add(v); Triangles.Add(v + 2); Triangles.Add(v + 3);
    }
}

public partial class ChunkMeshSystem : SystemBase
{
    // The prototype entity — all chunk render entities are cloned from this
    Entity _prototype;
    Material _material;

    protected override void OnCreate()
    {
        if (World.Name != "ClientWorld")
        {
            Enabled = false;
            return;
        }
    }

    void EnsurePrototype()
    {
        if (_material == null)
            _material = Resources.Load<Material>("ChunkMaterial");

        if (_prototype != Entity.Null) return;

        // A blank mesh for the prototype — each chunk replaces this with its own
        var blankMesh = new Mesh();

        var desc = new RenderMeshDescription(
            shadowCastingMode: ShadowCastingMode.On,
            receiveShadows: true);

        var renderMeshArray = new RenderMeshArray(
            new Material[] { _material },
            new Mesh[] { blankMesh });

        _prototype = EntityManager.CreateEntity();

        // Adds all the rendering components Entities Graphics needs
        RenderMeshUtility.AddComponents(
            _prototype,
            EntityManager,
            desc,
            renderMeshArray,
            MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

        // Give it a default transform
        EntityManager.AddComponentData(_prototype, LocalTransform.Identity);
    }

    protected override void OnUpdate()
    {
        EnsurePrototype();

        // --- Pass 1: collect block data before any structural changes ---
        var buildData = new System.Collections.Generic.List<(
            Entity entity,
            int3 coord,
            NativeArray<byte> blocks,
            bool isFirstBuild)>();

        foreach (var (pos, blocks, entity) in
            SystemAPI
                .Query<RefRO<ChunkPosition>, DynamicBuffer<BlockElement>>()
                .WithAll<ChunkDirty>()
                .WithEntityAccess())
        {
            var copy = new NativeArray<byte>(ChunkSettings.VOLUME, Allocator.TempJob);
            copy.CopyFrom(blocks.AsNativeArray().Reinterpret<byte>());

            buildData.Add((
                entity,
                pos.ValueRO.Coord,
                copy,
                isFirstBuild: !EntityManager.HasComponent<ChunkMeshRef>(entity)));
        }

        // --- Pass 2: structural changes ---
        foreach (var (entity, _, _, _) in buildData)
            EntityManager.RemoveComponent<ChunkDirty>(entity);

        // --- Pass 3: build and upload meshes ---
        foreach (var (entity, coord, blocksCopy, isFirstBuild) in buildData)
        {
            var verts = new NativeList<float3>(Allocator.TempJob);
            var tris = new NativeList<int>(Allocator.TempJob);

            new BuildChunkMeshJob
            {
                Blocks = blocksCopy,
                Vertices = verts,
                Triangles = tris,
            }.Run();

            // Build or reuse the Mesh object
            Mesh mesh;
            if (isFirstBuild)
            {
                mesh = new Mesh { name = $"Chunk {coord.x},{coord.z}" };
                mesh.indexFormat = IndexFormat.UInt32;

                // Clone the prototype to get a fully set-up render entity
                var renderEntity = EntityManager.Instantiate(_prototype);

                // Position it in world space
                EntityManager.SetComponentData(renderEntity,
                    LocalTransform.FromPosition((float3)(coord * ChunkSettings.SIZE)));

                // Tell Entities Graphics the bounding box of this chunk
                // so it doesn't get incorrectly culled at screen edges
                EntityManager.SetComponentData(renderEntity, new RenderBounds
                {
                    Value = new Unity.Mathematics.AABB
                    {
                        // Center is the middle of the chunk in local space
                        Center = new float3(ChunkSettings.SIZE / 2f, ChunkSettings.SIZE / 2f, ChunkSettings.SIZE / 2f),
                        // Extents is half-size in each direction
                        Extents = new float3(ChunkSettings.SIZE / 2f, ChunkSettings.SIZE / 2f, ChunkSettings.SIZE / 2f)
                    }
                });

                // Store mesh reference on the chunk entity so we can update it later
                EntityManager.AddComponentObject(entity,
                    new ChunkMeshRef { Value = mesh });

                // Point the render entity's RenderMeshArray at our new mesh
                var newArray = new RenderMeshArray(
                    new Material[] { _material },
                    new Mesh[] { mesh });
                EntityManager.SetComponentData(renderEntity,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                EntityManager.SetSharedComponentManaged(renderEntity, newArray);
            }
            else
            {
                // Rebuild — just update the existing mesh in place
                mesh = EntityManager.GetComponentObject<ChunkMeshRef>(entity).Value;
                mesh.Clear();
            }

            mesh.SetVertices(verts.AsArray());
            mesh.SetTriangles(tris.AsArray().ToArray(), 0);
            mesh.RecalculateNormals();

            blocksCopy.Dispose();
            verts.Dispose();
            tris.Dispose();
        }
    }
}