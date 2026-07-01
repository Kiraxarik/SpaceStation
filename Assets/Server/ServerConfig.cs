using System;
using System.IO;
using Unity.Entities;
using UnityEngine;

// ── Data shape (matches server_config.json) ───────────────────────────────────

[Serializable]
public class ServerConfigData
{
    /// <summary>Which start strategy to use. "allready" | "countdown" | "manual"</summary>
    public string startStrategy = "allready";

    /// <summary>Minimum connected+committed players before the round can start.</summary>
    public int minPlayersToStart = 1;

    /// <summary>
    /// Countdown seconds after start condition is met before the round actually
    /// begins. 0 = instant. Used by both "allready" and "countdown" strategies.
    /// </summary>
    public float startCountdownSeconds = 10f;

    /// <summary>
    /// Seconds a player can sit unready before being force-started anyway.
    /// 0 = disabled (wait forever). Only applies to "allready" strategy.
    /// </summary>
    public float unreadyTimeoutSeconds = 0f;

    /// <summary>Max players. Shown in the Steam server browser.</summary>
    public int maxPlayers = 50;

    /// <summary>Server name shown in the browser.</summary>
    public string serverName = "My Station";

    /// <summary>Short description shown in the server list.</summary>
    public string serverDescription = "";
}

// ── ECS singleton ─────────────────────────────────────────────────────────────

/// <summary>Loaded once at boot; all server systems read this.</summary>
public struct ServerConfig : IComponentData
{
    public int MinPlayersToStart;
    public float StartCountdownSeconds;
    public float UnreadyTimeoutSeconds;
    public int MaxPlayers;
    public StartStrategy Strategy;
}

public enum StartStrategy : byte { AllReady, Countdown, Manual }

// ── Loader ────────────────────────────────────────────────────────────────────

/// <summary>
/// Reads StreamingAssets/server_config.json and creates the ServerConfig
/// singleton. Falls back to defaults if the file is missing (dev baseline).
/// Runs once on the server, then disables.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerConfigLoaderSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "server_config.json");
        ServerConfigData data;

        if (File.Exists(path))
        {
            data = JsonUtility.FromJson<ServerConfigData>(File.ReadAllText(path));
            Debug.Log($"[ServerConfig] Loaded from {path}.");
        }
        else
        {
            data = new ServerConfigData();
            Debug.Log("[ServerConfig] server_config.json not found — using defaults.");
        }

        StartStrategy strategy = data.startStrategy switch
        {
            "countdown" => StartStrategy.Countdown,
            "manual" => StartStrategy.Manual,
            _ => StartStrategy.AllReady,
        };

        var e = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(e, new ServerConfig
        {
            MinPlayersToStart = Mathf.Max(1, data.minPlayersToStart),
            StartCountdownSeconds = Mathf.Max(0f, data.startCountdownSeconds),
            UnreadyTimeoutSeconds = data.unreadyTimeoutSeconds,
            MaxPlayers = Mathf.Max(1, data.maxPlayers),
            Strategy = strategy,
        });

        state.Enabled = false;
    }
}