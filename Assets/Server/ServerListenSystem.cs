using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

/// <summary>
/// Makes the dedicated server start listening on its port the moment the
/// ServerWorld's networking is ready. Replaces the manual "Host" button — a
/// dedicated server listens on boot, nothing to click. Runs once, then disables.
/// Port must match SteamGameServerBootstrap.GamePort.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerListenSystem : ISystem
{
    public const ushort Port = 7979;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamDriver>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var endpoint = NetworkEndpoint.AnyIpv4.WithPort(Port);
        SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(endpoint);

        Debug.Log($"[ServerListen] Listening on UDP {Port}.");
        state.Enabled = false; // listen once
    }
}