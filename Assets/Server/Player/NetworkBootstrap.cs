using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

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
            GUI.Label(new Rect(10, 10, 500, 40), "Networking already started.", bigLabel);
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 400, 220));
        if (GUILayout.Button("Host", bigButton, GUILayout.Height(80))) { _started = true; StartHost(); }
        if (GUILayout.Button("Join localhost", bigButton, GUILayout.Height(80))) { _started = true; StartClient(); }
        GUILayout.EndArea();
    }

    void StartHost()
    {
        // Use the worlds NetCode already created — don't make new ones
        var server = GetExistingWorld("ServerWorld");
        var client = GetExistingWorld("ClientWorld");

        if (server == null || client == null)
        {
            Debug.LogError("ServerWorld or ClientWorld not found. Make sure NetCode auto-bootstrap is running.");
            return;
        }

        var listenEp = NetworkEndpoint.AnyIpv4.WithPort(Port);
        server.EntityManager
              .CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>())
              .GetSingletonRW<NetworkStreamDriver>().ValueRW
              .Listen(listenEp);

        var connectEp = NetworkEndpoint.LoopbackIpv4.WithPort(Port);
        client.EntityManager
              .CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>())
              .GetSingletonRW<NetworkStreamDriver>().ValueRW
              .Connect(client.EntityManager, connectEp);
    }

    void StartClient()
    {
        var client = GetExistingWorld("ClientWorld");

        if (client == null)
        {
            Debug.LogError("ClientWorld not found.");
            return;
        }

        var connectEp = NetworkEndpoint.LoopbackIpv4.WithPort(Port);
        client.EntityManager
              .CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>())
              .GetSingletonRW<NetworkStreamDriver>().ValueRW
              .Connect(client.EntityManager, connectEp);
    }

    static World GetExistingWorld(string name)
    {
        foreach (var world in World.All)
            if (world.Name == name) return world;
        return null;
    }
}