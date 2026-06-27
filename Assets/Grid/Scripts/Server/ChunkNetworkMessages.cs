using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

// ── Client → Server ───────────────────────────────────────────────────────────

/// <summary>
/// Sent once by the client after it goes in-game, asking the server to dump
/// every populated chunk it currently holds. The server replies with a stream
/// of ChunkDataRpc messages — one per non-empty chunk.
///
/// No coords, no radius: the station is finite, so the full set is bounded.
/// </summary>
public struct RequestWorldSnapshotRpc : IRpcCommand { }

/// <summary>
/// Sent by the client to place (or clear) a single block at a world-space
/// block coordinate. The server resolves which chunk that falls in, creating
/// the chunk on demand if it doesn't exist yet, applies the change, and
/// broadcasts the result to all clients.
///
/// WorldBlock is in *global block* coordinates (not chunk-local), so the
/// client never needs to know chunk boundaries to request a placement —
/// the server does the chunk resolution. This is what lets a player "place a
/// block outside the current chunk grid" and have the server spawn the chunk.
/// </summary>
public struct PlaceBlockRpc : IRpcCommand
{
    public int3 WorldBlock;   // global block coords: chunkCoord*SIZE + local
    public byte NewValue;     // 0 = clear/air, else block type
}

// ── Server → Client ───────────────────────────────────────────────────────────

/// <summary>
/// Full 4096-byte block buffer for one chunk. Sent on initial snapshot and
/// whenever a brand-new chunk is created (so the client has never seen it).
///
/// Requires "Allow Unsafe Code" in the Assembly Definition for the helpers.
/// </summary>
public struct ChunkDataRpc : IRpcCommand
{
    public int3 Coord;
    public FixedBytes4094 Blocks;
    public byte Block4094;
    public byte Block4095;

    public unsafe void FromByteArray(byte[] src)
    {
        fixed (byte* srcPtr = src)
        fixed (FixedBytes4094* dstPtr = &Blocks)
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(dstPtr, srcPtr, 4094);
        Block4094 = src[4094];
        Block4095 = src[4095];
    }

    public unsafe void ToBuffer(DynamicBuffer<BlockElement> dst)
    {
        fixed (FixedBytes4094* srcPtr = &Blocks)
        {
            byte* raw = (byte*)srcPtr;
            for (int i = 0; i < 4094; i++)
                dst[i] = new BlockElement { Value = raw[i] };
        }
        dst[4094] = new BlockElement { Value = Block4094 };
        dst[4095] = new BlockElement { Value = Block4095 };
    }
}

/// <summary>
/// A single block change inside a chunk the client already has.
/// Broadcast to all clients after a placement on an existing chunk.
/// </summary>
public struct BlockChangeRpc : IRpcCommand
{
    public int3 ChunkCoord;
    public int BlockIndex;
    public byte NewValue;
}