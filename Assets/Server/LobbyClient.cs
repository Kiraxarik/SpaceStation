using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// Sends the CommitToPlayRequest RPC from UI code (the Ready button's OnClick).
/// Called by LobbyUIController; can also be called from mod lobby UI later.
/// The sole commit path — LobbyClientSystem carries no auto-commit of its own.
/// </summary>
public static class LobbyClient
{
    public static void Commit()
    {
        foreach (var world in World.All)
        {
            if (world.Name != "ClientWorld") continue;

            using var connQuery = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());

            if (connQuery.IsEmpty)
            {
                Debug.LogWarning("[LobbyClient] Not connected yet — can't commit.");
                return;
            }

            var connection = connQuery.GetSingletonEntity();
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

            var req = ecb.CreateEntity();
            ecb.AddComponent(req, new CommitToPlayRequest());
            ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connection });

            ecb.Playback(world.EntityManager);
            ecb.Dispose();

            Debug.Log("[LobbyClient] Commit sent.");
            return;
        }

        Debug.LogWarning("[LobbyClient] ClientWorld not found.");
    }
}