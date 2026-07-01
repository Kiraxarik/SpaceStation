using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives the three-phase lobby UI:
///
///   Phase 1 — Loading screen (full-screen hold).
///     Shown from scene load until the server acknowledges the connection
///     (NetworkId present in ClientWorld). World streams in the background.
///
///   Phase 2 — Lobby panel.
///     Shown once connected. The thin async bar at the top tracks chunk
///     settling (dirty > 0 = still loading). Ready button is greyed until
///     dirty hits zero. Clicking Ready sends the commit RPC.
///
///   Phase 3 — In-game.
///     Once the body spawns (LocalPlayer tag appears), hide all lobby UI.
///     Camera / world take over; the lobby is gone.
///
/// Core owns this panel and the Ready button only. The mod owns everything
/// else in the lobby (player list, character creator, etc.) — those go in a
/// separate mod-owned canvas layered on top.
/// </summary>
public class LobbyUIController : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] GameObject loadingScreen;   // full-screen black hold
    [SerializeField] GameObject lobbyPanel;      // shown once connected

    [Header("Ready button")]
    [SerializeField] Button readyButton;         // Core-owned, non-moddable

    [Header("Async loading bar")]
    [SerializeField] RectTransform loadingBarFill;  // width driven by fill %
    [SerializeField] GameObject loadingBarRoot;     // hide when settled

    // ── State ─────────────────────────────────────────────────────────────────

    enum Phase { Loading, Lobby, InGame }
    Phase _phase = Phase.Loading;

    // Snapshot of initial dirty count taken the first frame we enter the lobby,
    // used as the 100% baseline for the bar. Resets if more chunks go dirty
    // (e.g. player moves and LOD kicks in).
    int _initialDirty;
    bool _initialDirtyCaptured;
    float _barWidth; // cached from the bar's parent RectTransform

    // ── Unity ─────────────────────────────────────────────────────────────────

    void Start()
    {
        SetPhase(Phase.Loading);
        readyButton.onClick.AddListener(OnReady);

        if (loadingBarFill != null && loadingBarFill.parent is RectTransform parentRect)
            _barWidth = parentRect.rect.width;
    }

    void Update()
    {
        switch (_phase)
        {
            case Phase.Loading: TickLoading(); break;
            case Phase.Lobby: TickLobby(); break;
        }
    }

    // ── Phase ticks ───────────────────────────────────────────────────────────

    void TickLoading()
    {
        // Transition to lobby only once asset sync is fully resolved (§1.B: SYNC
        // — registry handshake + asset download — happens before LOBBY). Being
        // connected (NetworkId) is no longer enough on its own: a client missing
        // mod content needs to finish downloading it first, or it'd enter the
        // lobby able to request world chunks it can't render correctly yet.
        if (AssetSyncComplete())
            SetPhase(Phase.Lobby);
    }

    void TickLobby()
    {
        // Transition to in-game once the local player body exists.
        if (LocalPlayerSpawned())
        {
            SetPhase(Phase.InGame);
            return;
        }

        int dirty = CountDirtyChunks();

        // Capture baseline the first frame we enter the lobby.
        if (!_initialDirtyCaptured)
        {
            _initialDirty = Mathf.Max(dirty, 1);
            _initialDirtyCaptured = true;
        }

        // If more dirty chunks appear than our baseline (player moved, LOD
        // kicked in), raise the baseline so the bar never goes backwards.
        if (dirty > _initialDirty) _initialDirty = dirty;

        bool settled = dirty == 0;

        // Ready button — enabled only once the world has settled.
        readyButton.interactable = settled;

        // Async bar — visible while loading, fills toward 100 % as chunks settle.
        if (loadingBarRoot != null)
            loadingBarRoot.SetActive(!settled);

        if (loadingBarFill != null && _barWidth > 0)
        {
            float pct = settled ? 1f : 1f - (float)dirty / _initialDirty;
            loadingBarFill.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _barWidth * pct);
        }
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    void OnReady()
    {
        LobbyClient.Commit();
        readyButton.interactable = false; // prevent double-click
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>True once asset sync has finished (AssetSyncReady tag on the connection).</summary>
    static bool AssetSyncComplete()
    {
        foreach (var world in World.All)
        {
            if (world.Name != "ClientWorld") continue;
            using var q = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AssetSyncReady>());
            return !q.IsEmpty;
        }
        return false;
    }

    /// <summary>True once the LocalPlayer tag exists on the client (body spawned).</summary>
    static bool LocalPlayerSpawned()
    {
        foreach (var world in World.All)
        {
            if (world.Name != "ClientWorld") continue;
            using var q = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<LocalPlayer>());
            return !q.IsEmpty;
        }
        return false;
    }

    /// <summary>Count of ChunkDirty entities in ClientWorld (chunks still building meshes).</summary>
    static int CountDirtyChunks()
    {
        foreach (var world in World.All)
        {
            if (world.Name != "ClientWorld") continue;
            using var q = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ChunkDirty>());
            return q.CalculateEntityCount();
        }
        return 0;
    }

    // ── Phase transitions ─────────────────────────────────────────────────────

    void SetPhase(Phase p)
    {
        _phase = p;
        loadingScreen.SetActive(p == Phase.Loading);
        lobbyPanel.SetActive(p == Phase.Lobby);

        if (p == Phase.Lobby)
        {
            _initialDirtyCaptured = false;
            readyButton.interactable = false; // start greyed; TickLobby enables it
        }
    }
}