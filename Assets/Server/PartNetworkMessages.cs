using Unity.Collections;
using Unity.Mathematics;
using Unity.NetCode;

// ── Client → Server ───────────────────────────────────────────────────────────

/// <summary>
/// TEMPORARY test path for the sparse tile-entity + parts layer (§2). There is
/// no real interaction dispatch yet (§4 — two-hand, left=attack/right=interact,
/// tool-gated handlers) for parts to route through, so these two RPCs are a
/// direct stand-in wired to a debug input (middle-click / shift+middle-click in
/// BlockInteractionSystem) purely to prove the ECS mechanism end-to-end. Once §4
/// lands, part install/remove should route through its right-click "world
/// interaction" handler path instead, with tool-gating as a handler predicate
/// (§7 amendment) — these RPCs (or ones shaped like them) become that handler's
/// actual wire format rather than a debug shortcut.
/// </summary>
public struct InstallPartRpc : IRpcCommand
{
    /// <summary>The SOLID block the part attaches to — NOT the face-adjacent air
    /// block (unlike PlaceBlockRpc's placement target).</summary>
    public int3 WorldBlock;

    /// <summary>Namespaced part id, e.g. "base:wiring".</summary>
    public FixedString64Bytes PartId;
}

public struct RemovePartRpc : IRpcCommand
{
    public int3 WorldBlock;
    public FixedString64Bytes PartId;
}