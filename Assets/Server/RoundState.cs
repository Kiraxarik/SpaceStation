using Unity.Entities;

public enum RoundPhase : byte { Pregame, Running, Ending }

/// <summary>Server-wide round state. Clients never hold this — they get a projection later.</summary>
public struct RoundState : IComponentData
{
    public RoundPhase Phase;
}