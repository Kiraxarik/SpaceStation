using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Singleton: live coord → entity registry for all chunks the client currently
/// holds. Owned by ClientChunkReceiveSystem. Read by anything needing O(1)
/// chunk lookup by coordinate. Do NOT dispose the inner map externally.
/// </summary>
public struct ChunkCoordRegistry : IComponentData
{
    public NativeHashMap<int3, Entity> Map;
}

/// <summary>
/// Buffer element: a pending per-block change to apply to this chunk.
/// A buffer (not a single component) so multiple changes to one chunk in one
/// frame all survive. ClientChunkReceiveSystem appends; ChunkApplySystem drains.
/// </summary>
[InternalBufferCapacity(4)]
public struct ChunkBlockUpdate : IBufferElementData
{
    public int BlockIndex;
    public byte NewValue;
}

/// <summary>
/// Tag on the client connection: the one-time world snapshot request has been
/// sent. Prevents re-requesting every frame.
/// </summary>
public struct WorldSnapshotRequested : IComponentData { }

/// <summary>
/// Add to any server-side entity to trigger a server-authoritative block change
/// (explosions, timed events, AI). Resolved and broadcast by ServerChunkSystem
/// exactly like a player placement.
/// </summary>
public struct ServerBlockChange : IComponentData
{
    public int3 WorldBlock;   // global block coords
    public byte NewValue;
}