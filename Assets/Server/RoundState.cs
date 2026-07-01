using Unity.Entities;
using Unity.NetCode;

public enum RoundPhase : byte { Pregame, Running, Ending }

/// <summary>Server-wide round state. Clients never hold this — they get a projection later.</summary>
public struct RoundState : IComponentData
{
    public RoundPhase Phase;
}

/// <summary>
/// Degenerate default from the Round Lifecycle doc: with no round-owner
/// registered, the server auto-starts and never ends (a persistent world).
/// Creates RoundState = Running once. A real round-owner will drive
/// Pregame/Running/Ending transitions later; this is the baseline.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct RoundStateBootstrapSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<RoundState>())
        {
            var e = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(e, new RoundState { Phase = RoundPhase.Running });
        }
        state.Enabled = false;
    }
}