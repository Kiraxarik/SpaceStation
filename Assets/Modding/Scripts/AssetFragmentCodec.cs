using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

/// <summary>
/// Splits an arbitrary-length file's bytes into AssetFileFragmentRpc pieces and
/// scatters received pieces back into a staging array. The variable-length
/// sibling of ChunkFragmentCodec, which only ever handles a fixed 4096-byte
/// chunk — here the source length differs per file, so FragmentCount is
/// computed from it rather than being a compile-time constant.
///
/// Requires "Allow Unsafe Code" (already enabled project-wide — see
/// ChunkDataFragmentRpc.cs, which uses the same pattern).
/// </summary>
public static class AssetFragmentCodec
{
    public const int FRAG_BYTES = 510; // == sizeof(FixedBytes510)

    public static int FragmentCountFor(int totalBytes)
        => Math.Max(1, (totalBytes + FRAG_BYTES - 1) / FRAG_BYTES);

    /// <summary>Builds fragment <paramref name="fragIndex"/> from a file's full byte array.</summary>
    public static unsafe AssetFileFragmentRpc Build(string modId, string relativePath, byte[] src, int fragIndex)
    {
        int total = src.Length;
        int count = FragmentCountFor(total);
        int offset = fragIndex * FRAG_BYTES;
        int bytes = Math.Min(FRAG_BYTES, total - offset);

        var rpc = new AssetFileFragmentRpc
        {
            ModId = ToFixed64(modId),
            RelativePath = ToFixed128(relativePath),
            FragmentIndex = (ushort)fragIndex,
            FragmentCount = (ushort)count,
            TotalBytes = total,
            ByteCount = (ushort)bytes,
        };

        byte* dst = (byte*)UnsafeUtility.AddressOf(ref rpc.Payload);
        fixed (byte* s = src)
            UnsafeUtility.MemCpy(dst, s + offset, bytes);

        return rpc;
    }

    /// <summary>Copies this fragment's bytes into a destination buffer sized to TotalBytes.</summary>
    public static unsafe void Scatter(in AssetFileFragmentRpc rpc, byte[] dst)
    {
        int offset = rpc.FragmentIndex * FRAG_BYTES;

        AssetFileFragmentRpc local = rpc;            // copy for AddressOf
        byte* s = (byte*)UnsafeUtility.AddressOf(ref local.Payload);

        fixed (byte* d = dst)
            UnsafeUtility.MemCpy(d + offset, s, rpc.ByteCount);
    }

    // ── Wire-safe string helpers (mirrors ServerContentManifestSystem.ToFixed) ──

    public static FixedString64Bytes ToFixed64(string s)
    {
        FixedString64Bytes fs = default;
        fs.CopyFromTruncated(s);
        return fs;
    }

    public static FixedString128Bytes ToFixed128(string s)
    {
        FixedString128Bytes fs = default;
        fs.CopyFromTruncated(s);
        return fs;
    }
}