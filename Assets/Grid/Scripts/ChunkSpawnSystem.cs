using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;

public partial struct ChunkSpawnSystem : ISystem
{
    // How many chunks in each direction from the origin
    // 5 = a 5x5 grid of chunks (25 total)
    const int WORLD_SIZE = 100;

    public void OnCreate(ref SystemState state)
    {
        if (state.World.Name != "ClientWorld")
        {
            state.Enabled = false;
            return;
        }

        for (int x = 0; x < WORLD_SIZE; x++)
            for (int z = 0; z < WORLD_SIZE; z++)
            {
                SpawnChunk(ref state, new int3(x, 0, z));
            }
    }

    void SpawnChunk(ref SystemState state, int3 coord)
    {
        Entity chunkEntity = state.EntityManager.CreateEntity();

        state.EntityManager.AddComponentData(chunkEntity,
            new ChunkPosition { Coord = coord });

        DynamicBuffer<BlockElement> blocks =
            state.EntityManager.AddBuffer<BlockElement>(chunkEntity);
        blocks.Resize(ChunkSettings.VOLUME, NativeArrayOptions.ClearMemory);

        // Solid floor at y=0
        for (int x = 0; x < ChunkSettings.SIZE; x++)
            for (int z = 0; z < ChunkSettings.SIZE; z++)
            {
                blocks[ChunkSettings.Index(x, 0, z)] = new BlockElement { Value = 1 };
            }

        state.EntityManager.AddComponent<ChunkDirty>(chunkEntity);
    }

    public void OnUpdate(ref SystemState state) { }
}