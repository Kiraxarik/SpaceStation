using System;
using System.Collections.Generic;

/// <summary>
/// The dense-layer tile identity authority (architecture §0.4, §1.5).
///
/// A manifest is an ordered list of stable string IDs where the index IS the
/// session numeric ID. Air is always index 0. Everything else is assigned
/// deterministically (ordinal sort), so two processes that loaded the same
/// content independently arrive at the same numeric mapping — and a client
/// handed the server's ordering (Module 4) can reproduce it exactly.
///
/// This is shared infrastructure: it operates purely on string IDs and knows
/// nothing about blocks. Blocks are the first user; any future content that
/// lives in the dense byte array (≤256 ids) can reuse it. Rich per-instance
/// content (parts, items) lives in the sparse entity layer and keys off string
/// IDs directly — it does NOT need a byte manifest, so the 256 ceiling here is
/// the dense tile space only, not a global content limit.
///
/// Stable string ID  = canonical identity, authored, namespaced (base:wall_panel).
/// Session numeric ID = per-session wire optimization, assigned here, never authored.
/// </summary>
public sealed class ContentManifest
{
    public const byte AirId = 0;
    public const string AirName = "base:air";

    // index = numeric id, value = string id. [0] is always AirName.
    readonly string[] _byNumericId;
    readonly Dictionary<string, byte> _numericByName;

    ContentManifest(string[] byNumericId, Dictionary<string, byte> numericByName)
    {
        _byNumericId = byNumericId;
        _numericByName = numericByName;
    }

    /// <summary>Number of tile ids including air. Equals (highest id + 1).</summary>
    public int Count => _byNumericId.Length;

    /// <summary>The canonical ordering. Index = numeric id. Module 4 serializes this.</summary>
    public IReadOnlyList<string> Order => _byNumericId;

    /// <summary>String id for a numeric id, or null if out of range.</summary>
    public string NameOf(byte id) => (uint)id < (uint)_byNumericId.Length ? _byNumericId[id] : null;

    /// <summary>Numeric id for a string id. False if the manifest doesn't contain it.</summary>
    public bool TryGetId(string name, out byte id) => _numericByName.TryGetValue(name, out id);

    // ── Authoritative build (server, or any local deterministic build) ─────────

    /// <summary>
    /// Builds a manifest from an unordered set of string ids. Air is forced to 0;
    /// the rest are deduplicated and ordinal-sorted, so the result is independent
    /// of the order ids were discovered in (mod folder enumeration order, etc.).
    /// This is what makes the assignment deterministic and therefore agreeable
    /// across processes without a handshake — the handshake (Module 4) then makes
    /// the SERVER'S build authoritative even when content differs.
    /// </summary>
    public static ContentManifest Build(IEnumerable<string> stringIds)
    {
        // SortedSet with Ordinal comparer: dedup + culture-invariant deterministic order.
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string s in stringIds)
            if (!string.IsNullOrWhiteSpace(s) && !string.Equals(s, AirName, StringComparison.Ordinal))
                sorted.Add(s);

        var order = new List<string>(sorted.Count + 1) { AirName };
        order.AddRange(sorted);
        return BuildFromOrder(order);
    }

    // ── Build from an explicit ordering (client, given the server manifest) ────

    /// <summary>
    /// Builds a manifest from an explicit ordering — index = numeric id. Used by
    /// the client in Module 4 to adopt the server's authoritative assignment
    /// verbatim instead of deriving its own. order[0] must be AirName.
    /// </summary>
    public static ContentManifest BuildFromOrder(IReadOnlyList<string> order)
    {
        if (order == null || order.Count == 0 || !string.Equals(order[0], AirName, StringComparison.Ordinal))
            throw new ArgumentException($"Manifest order must begin with '{AirName}' at index 0.");
        if (order.Count > 256)
            throw new ArgumentException(
                $"Manifest has {order.Count} entries; dense tile ids are byte-bounded (max 256 including air).");

        var byId = new string[order.Count];
        var byName = new Dictionary<string, byte>(order.Count, StringComparer.Ordinal);

        for (int i = 0; i < order.Count; i++)
        {
            string name = order[i];
            byId[i] = name;
            byName[name] = (byte)i;   // last-wins on a duplicated id in the ordering
        }

        return new ContentManifest(byId, byName);
    }
}