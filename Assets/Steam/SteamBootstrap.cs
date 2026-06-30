using Steamworks;
using UnityEngine;

/// <summary>
/// Initializes the Steamworks client API at boot and pumps its callbacks.
/// Put one of these in your first scene. Survives scene loads.
///
/// Requires the Steam client to be running and logged in, and a steam_appid.txt
/// in the project/build root (480 = Valve's Spacewar test app for development;
/// swap in your own AppID at ship time).
///
/// This handles the client side. The dedicated-server side uses the separate
/// SteamGameServer API and gets initialized alongside the transport later.
/// </summary>
[DefaultExecutionOrder(-10000)]
public class SteamBootstrap : MonoBehaviour
{
    public static bool Initialized { get; private set; }
    public static CSteamID LocalSteamId { get; private set; }

    const uint AppId = 480; // Spacewar (dev). Replace with your own AppID later.

    static SteamBootstrap _instance;

    void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        if (!Packsize.Test())
            Debug.LogError("[Steam] Packsize.Test failed — wrong steam_api binaries for this platform.");
        if (!DllCheck.Test())
            Debug.LogError("[Steam] DllCheck.Test failed — wrong steam_api DLL versions.");

        try
        {
            // If the game was launched directly (not through Steam), relaunch via
            // Steam. Returns false in the editor and when steam_appid.txt is present.
            if (SteamAPI.RestartAppIfNecessary(new AppId_t(AppId)))
            {
                Application.Quit();
                return;
            }
        }
        catch (System.DllNotFoundException e)
        {
            Debug.LogError($"[Steam] steam_api native library not found — is Steamworks.NET installed correctly? {e}");
            return;
        }

        Initialized = SteamAPI.Init();
        if (!Initialized)
        {
            Debug.LogError("[Steam] SteamAPI.Init() failed. Is the Steam client running and logged in, " +
                           "and is steam_appid.txt present in the project root?");
            return;
        }

        LocalSteamId = SteamUser.GetSteamID();
        Debug.Log($"[Steam] Initialized as {SteamFriends.GetPersonaName()} ({LocalSteamId.m_SteamID}).");
    }

    void Update()
    {
        if (Initialized) SteamAPI.RunCallbacks();
    }

    void OnDestroy()
    {
        if (_instance != this) return;
        if (Initialized)
        {
            SteamAPI.Shutdown();
            Initialized = false;
        }
    }
}