using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

// ── Client → Server ───────────────────────────────────────────────────────────

/// <summary>
/// Sent once by the client after it goes in-game, asking the server to dump
/// every populated chunk it currently holds. The server replies with a stream
/// of ChunkDataFragmentRpc messages — one chunk's worth of fragments per
/// non-empty chunk.
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

    /// <summary>ushort, not byte (§1.5) — mirrors BlockElement.Value's width.
    /// 0 = clear/air, else block type.</summary>
    public ushort NewValue;
}

// ── Server → Client ───────────────────────────────────────────────────────────

/// <summary>
/// Converts a chunk's block buffer between its wire byte[] form and its in-ECS
/// DynamicBuffer&lt;BlockElement&gt; form. Not an RPC struct itself — a full chunk
/// (ChunkSettings.BYTE_SIZE = 8192 bytes now that blocks are ushort, §1.5) is
/// always too big for one RPC, so chunks are only ever sent fragmented
/// (ChunkDataFragmentRpc / ChunkFragmentCodec) and reassembled client-side into
/// a plain byte[] before landing here. This used to be a fixed-blob IRpcCommand
/// struct (ChunkDataRpc) sized for exactly 4096 raw bytes; with the wire size no
/// longer fixed at "one byte per block," there's nothing left for a blittable
/// RPC shape to buy here, so it's a converter instead.
/// </summary>
public static class ChunkBlockCodec
{
    /// <summary>Unpacks BYTE_SIZE wire bytes (little-endian ushort pairs) into a
    /// chunk's block buffer.</summary>
    public static unsafe void ToBuffer(byte[] src, DynamicBuffer<BlockElement> dst)
    {
        fixed (byte* srcPtr = src)
        {
            var srcUshorts = (ushort*)srcPtr;
            for (int i = 0; i < ChunkSettings.VOLUME; i++)
                dst[i] = new BlockElement { Value = srcUshorts[i] };
        }
    }

    /// <summary>Packs a VOLUME-length ushort[] (the server's in-memory chunk data)
    /// into BYTE_SIZE wire bytes for fragmenting.</summary>
    public static unsafe void FromUshortArray(ushort[] src, byte[] dst)
    {
        fixed (ushort* srcPtr = src)
        fixed (byte* dstPtr = dst)
            UnsafeUtility.MemCpy(dstPtr, srcPtr, ChunkSettings.BYTE_SIZE);
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

    /// <summary>ushort, not byte (§1.5) — mirrors BlockElement.Value's width.</summary>
    public ushort NewValue;
}