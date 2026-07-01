using UnityEngine;

/// <summary>
/// Auto-creates the SteamGameServerBootstrap at runtime on the SERVER ONLY, so
/// the dedicated server advertises itself without needing the component placed
/// in a scene by hand.
///
/// Context: SteamGameServerBootstrap used to live on the MainMenu scene, but the
/// server build no longer ships/loads MainMenu (it boots straight into
/// ServerInGameScene). So the advertise component went missing and the server
/// stopped appearing in the client's LAN browser. This spawns it in code
/// instead — but ONLY in a dedicated server build, guarded by UNITY_SERVER, so
/// clients never create it at all.
/// </summary>
public static class SteamGameServerAutoSpawn
{
#if UNITY_SERVER
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Spawn()
    {
        var go = new GameObject("SteamGameServerBootstrap (auto)");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<SteamGameServerBootstrap>();
    }
#endif
}
