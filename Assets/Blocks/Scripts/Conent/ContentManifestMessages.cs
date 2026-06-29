using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

// ── Client → Server ───────────────────────────────────────────────────────────

/// <summary>
/// Sent once by the client after it goes in-game, asking the server for its
/// authoritative content manifest (the string id ↔ session byte id ordering,
/// architecture §1.5). The server replies with a stream of ContentManifestEntryRpc
/// followed by one ContentManifestCompleteRpc.
///
/// This must complete before the client requests world chunk data, because chunk
/// bytes are server-numbered and meaningless until the client has remapped to the
/// server's ordering.
/// </summary>
public struct ContentManifestRequestRpc : IRpcCommand { }

// ── Server → Client ───────────────────────────────────────────────────────────

/// <summary>
/// One entry of the manifest: a single tile id's session numeric id and its
/// stable string id. Sent one-per-tile-id on join. Order on the wire is
/// irrelevant — each entry carries its own NumericId, so the client places each
/// at the right index regardless of arrival order.
///
/// FixedString128Bytes caps a string id at 125 UTF-8 bytes. Namespaced ids
/// (base:wall_panel, mymod:some_block) fit comfortably; the server logs an error
/// if an id is ever too long for the wire.
/// </summary>
public struct ContentManifestEntryRpc : IRpcCommand
{
    public byte NumericId;
    public FixedString128Bytes StringId;
}

/// <summary>
/// End-of-manifest marker: tells the client how many tile ids the manifest holds
/// (= highest id + 1, including air). The client adopts the manifest once it has
/// received this marker AND all Count entries.
/// </summary>
public struct ContentManifestCompleteRpc : IRpcCommand
{
    public int Count;
}

// ── Client connection tags ────────────────────────────────────────────────────

/// <summary>
/// On the client connection: the manifest request has been sent, so it isn't
/// re-sent every frame. (Mirrors WorldSnapshotRequested.)
/// </summary>
public struct ContentManifestRequested : IComponentData { }

/// <summary>
/// On the client connection: the full server manifest has been received and
/// adopted (BlockRegistry.InitializeFromManifest). Gates ALL world/chunk
/// reception in ClientChunkReceiveSystem — nothing block-numbered is processed
/// before this exists.
/// </summary>
public struct ContentManifestReady : IComponentData { }