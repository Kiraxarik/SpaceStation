using Unity.NetCode;
using UnityEngine.Scripting;

/// <summary>
/// Enforces the dedicated-server-only model:
///   - a dedicated server build creates ONLY a ServerWorld,
///   - a client build creates ONLY a ClientWorld (never hosts),
///   - never an in-process "client server".
///
/// Connections are never automatic (AutoConnectPort = 0) — the menu / Steam
/// browser is the only thing that triggers a connect.
///
/// In the editor it defers to the default behavior so MultiplayerPlayMode (MPPM)
/// and the PlayMode Tools window still let you run as Client, Server, or both
/// for quick testing.
/// </summary>
[Preserve]
public class GameBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        AutoConnectPort = 0; // connection is menu/Steam-driven, never automatic

#if UNITY_EDITOR
        // Honor PlayMode Tools / MPPM role selection while developing.
        return base.Initialize(defaultWorldName);
#else
        CreateLocalWorld(defaultWorldName);
#if UNITY_SERVER
        CreateServerWorld("ServerWorld"); // dedicated server: server only
#else
        CreateClientWorld("ClientWorld");  // client build: never hosts
#endif
        return true;
#endif
    }
}