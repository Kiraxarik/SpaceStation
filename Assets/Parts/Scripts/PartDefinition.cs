using System;

/// <summary>
/// JSON-serializable definition for one part — the sparse-layer composition
/// vocabulary (§2.2). Authored in a mod's StreamingAssets/Mods/&lt;mod&gt;/parts.json.
/// A part is what turns a plain block into something richer: install glass on a
/// frame and it becomes a window; a wall isn't a block type, it's frame + parts.
///
/// Example — parts.json entry:
/// {
///   "id":         "wiring",
///   "slot":       "subfloor-utility",
///   "primitives": [ "Conductive" ]
/// }
///
/// "primitives" names entries in PartPrimitives.KnownNames — the fixed, Coreowned
/// component vocabulary (§0.2). A part naming a primitive Core doesn't know about
/// is currently just ignored by RecomputePrimitives, not rejected.
///
/// No numeric id: like model content (§0.4), a part is sparse, string-keyed
/// content. PartRegistry still assigns an in-process ushort purely so
/// InstalledPart buffers store 2 bytes instead of a string — see its remarks for
/// why that's NOT the same kind of numeric id BlockRegistry assigns.
/// </summary>
[Serializable]
public class PartDefinitionData
{
    /// <summary>Bare id ("wiring"); namespaced by source at load → "base:wiring".</summary>
    public string id = "";

    /// <summary>Which composition slot this part occupies (§2.2) — e.g.
    /// "floor", "wall-covering", "subfloor-utility". A free-form string so mods
    /// can introduce new slots; nothing enforces slot exclusivity yet (that's
    /// construction-handler validation work for when parts get a real placement
    /// UI instead of the debug RPC path).</summary>
    public string slot = "generic";

    /// <summary>Which Core primitives this part contributes when installed.
    /// Names must match PartPrimitives.KnownNames to have any effect.</summary>
    public string[] primitives = Array.Empty<string>();
}

/// <summary>
/// A loaded, resolved part: namespaced id, source mod, slot, and declared
/// primitives. This is what PartRegistry indexes and what ServerPartSystem reads
/// when recomputing a tile's primitive flags.
/// </summary>
public sealed class PartContent
{
    public string Id;             // namespaced, e.g. "base:wiring"
    public string SourceMod;
    public string Slot;
    public string[] Primitives;
}