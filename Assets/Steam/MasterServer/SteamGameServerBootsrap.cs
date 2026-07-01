using Steamworks;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Advertises this dedicated server on Steam's game-server browser. Put it on
/// the dedicated server build only — NOT alongside SteamBootstrap (that's the
/// client API; this is the separate Game Server API).
///
/// Role-gated: this component lives in MainMenu.unity, which every process
/// loads (server and client alike, and every MPPM role in-editor). Without a
/// gate, a client would also call GameServer.Init and try to log on as a
/// Steam game server — colliding on GamePort/QueryPort with the real server
/// and double-advertising on the browser. Start() checks for an actual
/// ServerWorld in this process and no-ops otherwise.
///
/// Anonymous logon is fine for LAN/dev. For the INTERNET browser the server
/// needs your real AppID plus a Game Server Login Token (GSLT) — at that point
/// replace LogOnAnonymous() with LogOn(token).
/// </summary>
public class SteamGameServerBootstrap : MonoBehaviour
{
    [Header("Identity")]
    public string ServerName = "My Station";
    public string GameDescription = "SpaceStation";
    public string ModDir = "spacestation";
    public string GameTags = "";

    [Header("Ports / capacity")]
    public ushort GamePort = 7979;   // your NetCode UDP port
    public ushort QueryPort = 27016; // Steam query port
    public int MaxPlayers = 50;

    const uint AppId = 480; // Spacewar (dev). Replace with your own AppID later.

    bool _init;
    Callback<SteamServersConnected_t> _onConnected;
    Callback<SteamServerConnectFailure_t> _onConnectFailure;
    Callback<SteamServersDisconnected_t> _onDisconnected;

    void Start()
    {
        // Client processes (including every non-server MPPM role) load this
        // same MainMenu scene. Only a process that actually created a
        // ServerWorld should stand up the Steam game-server API.
        if (!HasServerWorld())
        {
            Debug.Log("[SteamGS] No ServerWorld in this process — skipping game-server advertisement.");
            enabled = false;
            return;
        }

        // unIP = 0 -> bind all interfaces. eServerModeAuthentication = VAC off,
        // auth on (use eServerModeAuthenticationAndSecure once you have VAC).
        // NOTE: GameServer.Init's signature is the one version-sensitive line —
        // this is the SDK 1.6x form (no separate Steam port). If it doesn't
        // resolve, check the overload for a usSteamPort parameter.
        if (!GameServer.Init(0, GamePort, QueryPort,
                EServerMode.eServerModeAuthentication, "1.0.0.0"))
        {
            Debug.LogError("[SteamGS] GameServer.Init failed.");
            return;
        }
        _init = true;

        // Game-server callbacks must be registered with CreateGameServer (not Create).
        _onConnected = Callback<SteamServersConnected_t>.CreateGameServer(OnConnected);
        _onConnectFailure = Callback<SteamServerConnectFailure_t>.CreateGameServer(OnConnectFailure);
        _onDisconnected = Callback<SteamServersDisconnected_t>.CreateGameServer(OnDisconnected);

        SteamGameServer.SetProduct("SpaceStation");
        SteamGameServer.SetGameDescription(GameDescription);
        SteamGameServer.SetModDir(ModDir);
        SteamGameServer.SetDedicatedServer(true);
        SteamGameServer.SetServerName(ServerName);
        SteamGameServer.SetMaxPlayerCount(MaxPlayers);
        SteamGameServer.SetPasswordProtected(false);
        SteamGameServer.SetGameTags(GameTags);

        SteamGameServer.LogOnAnonymous();
        SteamGameServer.SetAdvertiseServerActive(true);

        Debug.Log($"[SteamGS] Init OK. Logging on anonymously — '{ServerName}' " +
                  $"port {GamePort} (query {QueryPort}).");
    }

    static bool HasServerWorld()
    {
        foreach (var world in World.All)
            if (world.IsServer()) return true;
        return false;
    }

    void OnConnected(SteamServersConnected_t _)
        => Debug.Log($"[SteamGS] Logged on to Steam (SteamID {SteamGameServer.GetSteamID().m_SteamID}). " +
                     "Now visible to the browser — refresh the client list.");

    void OnConnectFailure(SteamServerConnectFailure_t cb)
        => Debug.LogError($"[SteamGS] Logon FAILED: {cb.m_eResult} (still retrying: {cb.m_bStillRetrying}).");

    void OnDisconnected(SteamServersDisconnected_t cb)
        => Debug.LogWarning($"[SteamGS] Disconnected from Steam: {cb.m_eResult}.");

    void Update()
    {
        if (_init) GameServer.RunCallbacks();
    }

    void OnDestroy()
    {
        if (!_init) return;
        SteamGameServer.SetAdvertiseServerActive(false);
        SteamGameServer.LogOff();
        GameServer.Shutdown();
        _init = false;
    }
}