using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

// ── Client → Server ───────────────────────────────────────────────────────────

/// <summary>
/// Sent once by the client after it has adopted the block manifest
/// (ContentManifestReady), asking the server for its authoritative asset hash
/// manifest (architecture §7.4). The server replies with a stream of
/// AssetManifestFileEntryRpc — one per shippable file, across every loaded mod
/// — followed by one AssetManifestCompleteRpc.
///
/// This runs strictly after the block-id handshake (§1.5): asset identity
/// (which files exist, their hashes) is a different question from numeric tile
/// ordering, but both must resolve before the client is allowed to request
/// world chunk data or enter the lobby (AssetSyncReady, ClientAssetSyncSystem).
/// </summary>
public struct AssetManifestRequestRpc : IRpcCommand { }

/// <summary>
/// One shippable file: which mod it belongs to, its path within that mod
/// folder, its SHA-256 hash, and its byte size. Sent one-per-file; order on
/// the wire is irrelevant, the client just counts until AssetManifestCompleteRpc.
/// </summary>
public struct AssetManifestFileEntryRpc : IRpcCommand
{
    public FixedString64Bytes ModId;
    public FixedString128Bytes RelativePath;

    /// <summary>SHA-256 hex, 64 chars — fits comfortably in the 125-byte capacity.</summary>
    public FixedString128Bytes Hash;

    public int Size;
}

/// <summary>End-of-manifest marker: total file count across every mod.</summary>
public struct AssetManifestCompleteRpc : IRpcCommand
{
    public int Count;
}

/// <summary>
/// Client → server: "send me this specific file." ModId + RelativePath must
/// match an entry already present in the server's own AssetManifest — the
/// server validates by exact lookup before ever touching the filesystem
/// (ServerAssetSyncSystem), so a client can only ever request a path that was
/// already enumerated from real files on disk.
/// </summary>
public struct RequestAssetFileRpc : IRpcCommand
{
    public FixedString64Bytes ModId;
    public FixedString128Bytes RelativePath;
}

/// <summary>
/// One fragment of a requested file's bytes. Mirrors ChunkDataFragmentRpc's
/// shape, but for a variable-length payload instead of a fixed 4096-byte chunk:
/// FragmentIndex/Count are ushort (not byte) so a single file can span up to
/// 65535 fragments — about 33MB at 510 bytes/fragment. That's generous for
/// current content (small textures, short geometry/animation JSON, short
/// sound clips); widen to int if a shipped asset ever needs more.
/// </summary>
public struct AssetFileFragmentRpc : IRpcCommand
{
    public FixedString64Bytes ModId;
    public FixedString128Bytes RelativePath;

    public ushort FragmentIndex;   // 0 .. FragmentCount-1
    public ushort FragmentCount;
    public int TotalBytes;         // full file size, valid on every fragment
    public ushort ByteCount;       // valid bytes in THIS fragment (last is partial)
    public FixedBytes510 Payload;
}

// ── Client connection tags ────────────────────────────────────────────────────

/// <summary>On the client connection: the asset manifest request has been sent.</summary>
public struct AssetManifestRequested : IComponentData { }

/// <summary>
/// On the client connection: asset sync is fully resolved — either the client
/// already matched the server's manifest, or every needed file finished
/// downloading and content was reloaded. Gates world/chunk reception
/// (ClientChunkReceiveSystem) and lobby entry (LobbyUIController) — nothing
/// that depends on complete local content runs before this exists.
/// </summary>
public struct AssetSyncReady : IComponentData { }