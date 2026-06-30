using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

/// <summary>
/// Queries Steam's game-server browser and turns results into ServerInfo.
///
/// Dev uses the LAN list — no GSLT needed, works on the Spacewar (480) appid.
/// For production, swap RequestLANServerList -> RequestInternetServerList(appId,
/// filters, response); the callbacks are identical.
///
/// Callbacks are dispatched by SteamAPI.RunCallbacks(), which SteamBootstrap
/// already pumps each frame, so this just works as long as Steam is initialized.
/// </summary>
public class SteamServerBrowser
{
    public event Action<List<ServerInfo>> OnRefreshComplete;

    readonly uint _appId;
    ISteamMatchmakingServerListResponse _response;
    HServerListRequest _request;
    bool _hasRequest;
    readonly List<ServerInfo> _results = new();

    public SteamServerBrowser(uint appId) => _appId = appId;

    public void Refresh()
    {
        if (!SteamBootstrap.Initialized) return;

        Cancel();
        _results.Clear();

        _response = new ISteamMatchmakingServerListResponse(
            OnServerResponded, OnServerFailedToRespond, OnComplete);

        _request = SteamMatchmakingServers.RequestLANServerList(new AppId_t(_appId), _response);
        _hasRequest = true;
    }

    public void Cancel()
    {
        if (!_hasRequest) return;
        _hasRequest = false;

        // Steam may already be shut down (e.g. when play mode stops and
        // SteamBootstrap was destroyed first) — nothing to release if so.
        if (!SteamBootstrap.Initialized) return;
        SteamMatchmakingServers.ReleaseRequest(_request);
    }

    void OnServerResponded(HServerListRequest request, int iServer)
    {
        var item = SteamMatchmakingServers.GetServerDetails(request, iServer);
        var addr = item.m_NetAdr;

        _results.Add(new ServerInfo
        {
            Name = item.GetServerName(),
            Address = IpToString(addr.GetIP()),
            Port = addr.GetConnectionPort(),
            Players = item.m_nPlayers,
            MaxPlayers = item.m_nMaxPlayers,
            Ping = item.m_nPing,
            Description = item.GetGameTags(), // game tags for now; rich description can come via server rules later
        });
    }

    void OnServerFailedToRespond(HServerListRequest request, int iServer) { }

    void OnComplete(HServerListRequest request, EMatchMakingServerResponse response)
    {
        Debug.Log($"[ServerBrowser] Refresh complete: {_results.Count} server(s). Response={response}");
        OnRefreshComplete?.Invoke(new List<ServerInfo>(_results));
    }

    static string IpToString(uint ip)
        => $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
}