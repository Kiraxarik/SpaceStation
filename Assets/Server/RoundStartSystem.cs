using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Drives Pregame → Running based on the server config's start strategy.
///
/// AllReady  — waits until every connected player has committed AND the
///             connected count >= MinPlayersToStart. Then runs a countdown.
///             If UnreadyTimeoutSeconds > 0, force-starts after that long
///             even if not everyone is ready.
///
/// Countdown — starts a fixed countdown as soon as MinPlayersToStart are
///             connected (readiness not required).
///
/// Manual    — stays in Pregame until a round-owner or admin triggers start
///             (not yet implemented; stays in Pregame indefinitely).
///
/// Once Running, this system disables itself — the round-owner raises
/// the end event when it wants Ending (Phase 4).
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct RoundStartSystem : ISystem
{
    float _countdownRemaining;
    bool _countingDown;
    float _unreadyTimer;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ServerConfig>();
        state.RequireForUpdate<RoundState>();

        // Ensure RoundState starts in Pregame (RoundStateBootstrapSystem
        // no longer auto-starts; it just creates the singleton in Pregame).
        var e = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(e, new RoundState { Phase = RoundPhase.Pregame });
    }

    public void OnUpdate(ref SystemState state)
    {
        ref var round = ref SystemAPI.GetSingletonRW<RoundState>().ValueRW;
        if (round.Phase != RoundPhase.Pregame) { state.Enabled = false; return; }

        var cfg = SystemAPI.GetSingleton<ServerConfig>();
        float dt = SystemAPI.Time.DeltaTime;

        int connected = CountConnected(ref state);
        int committed = CountCommitted(ref state);
        bool minMet = connected >= cfg.MinPlayersToStart;

        switch (cfg.Strategy)
        {
            case StartStrategy.AllReady:
                TickAllReady(ref state, ref round, cfg, dt, connected, committed, minMet);
                break;

            case StartStrategy.Countdown:
                if (minMet) TickCountdown(ref round, cfg, dt);
                else { _countingDown = false; }
                break;

            case StartStrategy.Manual:
                break; // waits for owner to signal (Phase 4)
        }
    }

    void TickAllReady(ref SystemState state, ref RoundState round,
                      ServerConfig cfg, float dt,
                      int connected, int committed, bool minMet)
    {
        bool allReady = minMet && committed >= connected;

        // Unready timeout — force-start if enabled and timer expires.
        if (cfg.UnreadyTimeoutSeconds > 0f && minMet)
        {
            _unreadyTimer += dt;
            if (_unreadyTimer >= cfg.UnreadyTimeoutSeconds)
            {
                Debug.Log("[RoundStart] Unready timeout — forcing start.");
                StartCountdown(cfg);
            }
        }

        if (allReady && !_countingDown)
        {
            Debug.Log($"[RoundStart] All {connected} player(s) ready. " +
                      $"Countdown: {cfg.StartCountdownSeconds}s.");
            StartCountdown(cfg);
        }

        // If someone disconnects mid-countdown and we drop below min, cancel.
        if (_countingDown && !minMet)
        {
            Debug.Log("[RoundStart] Player left — countdown cancelled.");
            _countingDown = false;
        }

        if (_countingDown)
            TickCountdown(ref round, cfg, dt);
    }

    void TickCountdown(ref RoundState round, ServerConfig cfg, float dt)
    {
        _countdownRemaining -= dt;
        if (_countdownRemaining <= 0f)
        {
            round.Phase = RoundPhase.Running;
            Debug.Log("[RoundStart] Round is now RUNNING.");
        }
    }

    void StartCountdown(ServerConfig cfg)
    {
        _countingDown = true;
        _countdownRemaining = cfg.StartCountdownSeconds;
        _unreadyTimer = 0f;
    }

    static int CountConnected(ref SystemState state)
    {
        using var q = state.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<NetworkId>());
        return q.CalculateEntityCount();
    }

    static int CountCommitted(ref SystemState state)
    {
        using var q = state.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<NetworkId>(),
            ComponentType.ReadOnly<Committed>());
        return q.CalculateEntityCount();
    }
}