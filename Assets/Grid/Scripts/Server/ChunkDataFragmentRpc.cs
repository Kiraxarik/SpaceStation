using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>
/// One slice of a chunk's 4096-byte block buffer. A full chunk is too big for a
/// single NetCode RPC (single-packet limit ~1378B), so full-chunk transfers are
/// split into FragmentCount of these and reassembled client-side by coord.
///
/// Payload is FixedBytes510 (510 bytes) → 4096 bytes = 9 fragments.
/// Wire size per fragment ≈ 526B + headers, comfortably under the packet limit.
/// </summary>
public struct ChunkDataFragmentRpc : IRpcCommand
{
    public int3 Coord;
    public byte FragmentIndex;   // 0 .. FragmentCount-1
    public byte FragmentCount;   // total fragments for this chunk
    public ushort ByteCount;       // valid bytes in THIS fragment (last is partial)
    public FixedBytes510 Payload;
}

/// <summary>
/// Splits a 4096-byte chunk into ChunkDataFragmentRpc pieces and scatters
/// received pieces back into a 4096-byte staging array.
///
/// The Payload field lives on a local stack struct in both methods, so its
/// address is taken with UnsafeUtility.AddressOf (no `fixed` — that's only for
/// pinning the managed byte[], which can move under GC).
/// </summary>
public static class ChunkFragmentCodec
{
    public const int FRAG_BYTES = 510; // == sizeof(FixedBytes510)

    public static byte FragmentCount =>
        (byte)((ChunkSettings.VOLUME + FRAG_BYTES - 1) / FRAG_BYTES); // 4096 → 9

    /// <summary>Builds fragment <paramref name="fragIndex"/> from a 4096-byte source.</summary>
    public static unsafe ChunkDataFragmentRpc Build(int3 coord, byte[] src, int fragIndex)
    {
        int total = ChunkSettings.VOLUME;
        int count = (total + FRAG_BYTES - 1) / FRAG_BYTES;
        int offset = fragIndex * FRAG_BYTES;
        int bytes = math.min(FRAG_BYTES, total - offset);

        var rpc = new ChunkDataFragmentRpc
        {
            Coord = coord,
            FragmentIndex = (byte)fragIndex,
            FragmentCount = (byte)count,
            ByteCount = (ushort)bytes,
        };

        byte* dst = (byte*)UnsafeUtility.AddressOf(ref rpc.Payload);
        fixed (byte* s = src)
            UnsafeUtility.MemCpy(dst, s + offset, bytes);

        return rpc;
    }

    /// <summary>Copies this fragment's bytes into a 4096-byte staging array.</summary>
    public static unsafe void Scatter(in ChunkDataFragmentRpc rpc, byte[] dst)
    {
        int offset = rpc.FragmentIndex * FRAG_BYTES;

        ChunkDataFragmentRpc local = rpc;            // copy for AddressOf
        byte* s = (byte*)UnsafeUtility.AddressOf(ref local.Payload);

        fixed (byte* d = dst)
            UnsafeUtility.MemCpy(d + offset, s, rpc.ByteCount);
    }
}