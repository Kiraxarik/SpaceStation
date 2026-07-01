using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime registry of part content — string-keyed, mirrors TileRegistry. Parts
/// are the sparse-layer composition vocabulary (§2.2): a tile is "rich" only
/// because it has one or more of these installed.
///
/// Unlike BlockRegistry, there is NO numeric-id wire sync here yet. Parts are
/// server-only simulation state today (§0.3) — the InstalledPart buffer never
/// replicates to clients as raw data, so there's no client/server ordering to
/// agree on the way block ids need one for the dense wire format (§1.5). A part
/// still gets an in-process ushort id (assigned deterministically from the
/// sorted id set) purely so InstalledPart buffers store 2 bytes instead of a
/// string — that id is NOT stable across a Reload() reshuffle in a way anything
/// depends on yet, because nothing persists it. Revisit this the moment parts
/// need a client-visible projection (detail mesh, §2.5) or persistence (§1.F).
///
/// Orchestrator-driven like every other content type: ContentBootstrap loads
/// each mod's parts in resolved order and hands the full set to Initialize.
/// </summary>
public static class PartRegistry
{
    public const ushort InvalidId = ushort.MaxValue;

    public static IReadOnlyDictionary<string, ushort> IdByName { get; private set; }
        = new Dictionary<string, ushort>();

    public static IReadOnlyDictionary<ushort, PartContent> ById { get; private set; }
        = new Dictionary<ushort, PartContent>();

    public static int Count => ById.Count;

    /// <summary>Numeric id for a part, or InvalidId if not found. Accepts a
    /// namespaced id ("base:wiring") or a bare name resolved against base.</summary>
    public static ushort GetId(string partName)
    {
        if (IdByName.TryGetValue(partName, out ushort id)) return id;
        if (!partName.Contains(':') && IdByName.TryGetValue($"base:{partName}", out id)) return id;
        return InvalidId;
    }

    public static PartContent GetDefinition(ushort id)
        => ById.TryGetValue(id, out var def) ? def : null;

    /// <summary>
    /// Builds the registry from the full, ordered set of part content. Ids are
    /// assigned by sorted string order rather than mod load order, so re-running
    /// this (ContentBootstrap.Reload) during a live session is deterministic —
    /// it does NOT reconcile against previously-assigned ids (see class remarks).
    /// </summary>
    public static void Initialize(List<PartContent> content)
    {
        content.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        var idByName = new Dictionary<string, ushort>(content.Count, StringComparer.Ordinal);
        var byId = new Dictionary<ushort, PartContent>(content.Count);

        for (int i = 0; i < content.Count; i++)
        {
            ushort id = (ushort)i;
            var c = content[i];

            if (idByName.ContainsKey(c.Id))
                Debug.LogWarning($"[PartRegistry] Duplicate part id '{c.Id}' — later in sorted order wins.");

            idByName[c.Id] = id;
            byId[id] = c;
        }

        IdByName = idByName;
        ById = byId;
        Debug.Log($"[PartRegistry] Ready — {byId.Count} part(s).");
    }
}