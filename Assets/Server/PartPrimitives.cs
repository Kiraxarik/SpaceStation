using Unity.Entities;

/// <summary>
/// The fixed string-name → ComponentType vocabulary for part primitives (§0.2,
/// the queryable-flag rule). This switch statement IS "Core owns the component
/// vocabulary" made concrete — a part's "primitives" array in parts.json names
/// entries here by string; adding a new primitive means adding both a component
/// (PartComponents.cs) and a case here, deliberately, rather than any dynamic or
/// reflective binding. A part definition naming an unknown primitive is silently
/// ignored (logged nowhere yet — fine while the vocabulary is this small; worth
/// a load-time validation pass once mods can add parts).
/// </summary>
public static class PartPrimitives
{
    public static readonly string[] KnownNames = { "GasBarrier", "Conductive" };

    public static bool TryGetComponentType(string primitiveName, out ComponentType type)
    {
        switch (primitiveName)
        {
            case "GasBarrier": type = ComponentType.ReadWrite<GasBarrier>(); return true;
            case "Conductive": type = ComponentType.ReadWrite<Conductive>(); return true;
            default: type = default; return false;
        }
    }
}