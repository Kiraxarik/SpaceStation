using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

/// <summary>
/// Minimal local-testing helper. Put this on a GameObject in your main scene
/// (NOT a sub-scene). Click "Host" in one Editor instance / build, and
/// "Join" in another (e.g. via ParrelSync, a second build, or Multiplayer
/// Play Mode) to connect to localhost.
/// </summary>
public class NetworkBootstrap : MonoBehaviour
{
    public ushort Port = 7979;

    bool _started;

    void OnGUI()
    {
        var bigButton = new GUIStyle(GUI.skin.button) { fontSize = 24 };
        var bigLabel = new GUIStyle(GUI.skin.label) { fontSize = 20 };

        if (_started)
        {
            GUI.Label(new Rect(10, 10, 500, 40), "Networking already started this session.", bigLabel);
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 400, 220));

        if (GUILayout.Button("Host", bigButton, GUILayout.Height(80)))
        {
            _started = true;
            StartHost();
        }

        if (GUILayout.Button("Join localhost", bigButton, GUILayout.Height(80)))
        {
            _started = true;
            StartClient();
        }

        GUILayout.EndArea();
    }

    void StartHost()
    {
        // Creates (or reuses) a server world and a client world for the host's
        // own player, then starts listening / connecting via the
        // NetworkStreamDriver singleton (present in both worlds).
        var server = ClientServerBootstrap.CreateServerWorld("ServerWorld");
        var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");

        var ep = NetworkEndpoint.AnyIpv4.WithPort(Port);
        var serverDriverQuery = server.EntityManager.CreateEntityQuery(
            ComponentType.ReadWrite<NetworkStreamDriver>());
        serverDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ep);

        var serverEp = NetworkEndpoint.LoopbackIpv4.WithPort(Port);
        var clientDriverQuery = client.EntityManager.CreateEntityQuery(
            ComponentType.ReadWrite<NetworkStreamDriver>());
        clientDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(client.EntityManager, serverEp);
    }

    void StartClient()
    {
        var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");

        var ep = NetworkEndpoint.LoopbackIpv4.WithPort(Port);
        var clientDriverQuery = client.EntityManager.CreateEntityQuery(
            ComponentType.ReadWrite<NetworkStreamDriver>());
        clientDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(client.EntityManager, ep);
    }
}