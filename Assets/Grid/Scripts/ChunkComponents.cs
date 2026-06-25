using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public static class ChunkSettings
{
    public const int SIZE = 16;
    public const int VOLUME = SIZE * SIZE * SIZE;

    public static int Index(int x, int y, int z)
        => x + (y * SIZE) + (z * SIZE * SIZE);
}

public struct ChunkPosition : IComponentData
{
    public int3 Coord;
}

[InternalBufferCapacity(0)]
public struct BlockElement : IBufferElementData
{
    public byte Value;
}

public struct ChunkDirty : IComponentData { }

// Stores the generated Mesh on the entity so we can update it on rebuild
// This is a managed component — needed because Mesh is a Unity object
public class ChunkMeshRef : IComponentData
{
    public Mesh Value;
}