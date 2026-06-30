using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

/// <summary>
/// Connects the client to a server. Reuses the ClientWorld that NetCode's
/// bootstrap already created — the ECS world survives scene loads, so the
/// connection persists if you transition out of the menu scene afterward.
/// </summary>
public static class GameClient
{
    public static bool Connect(string address, ushort port)
    {
        var world = GetClientWorld();
        if (world == null)
        {
            Debug.LogError("[GameClient] ClientWorld not found. Is NetCode auto-bootstrap running?");
            return false;
        }

        if (!NetworkEndpoint.TryParse(address, port, out var ep))
        {
            Debug.LogError($"[GameClient] Could not parse address '{address}:{port}'.");
            return false;
        }

        using var query = world.EntityManager
            .CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
        query.GetSingletonRW<NetworkStreamDriver>().ValueRW
             .Connect(world.EntityManager, ep);

        Debug.Log($"[GameClient] Connecting to {address}:{port} …");
        return true;
    }

    static World GetClientWorld()
    {
        foreach (var world in World.All)
            if (world.Name == "ClientWorld") return world;
        return null;
    }
}