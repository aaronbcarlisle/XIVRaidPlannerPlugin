using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Fetches and caches player gear data from the planner API.
/// Links the current in-game character to a planner player ID via PartyMatchingService.
/// Provides gear data for the BiS viewer window.
/// </summary>
public class BiSDataService
{
    private readonly RaidPlannerClient _apiClient;
    private readonly PartyMatchingService _partyMatching;
    private readonly ItemMappingService _itemMapping;
    private readonly IPluginLog _log;

    // Cached gear data per player ID (concurrent for safe access from framework + background threads)
    private readonly ConcurrentDictionary<string, PlayerGearResponse> _gearCache = new();

    // Serializes fetch operations to prevent concurrent mutation of cache/state
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    // Current player's gear (the logged-in user's character)
    public PlayerGearResponse? CurrentPlayerGear { get; private set; }

    // Currently viewed player (for Lead/Owner dropdown)
    public PlayerGearResponse? ViewedPlayerGear { get; private set; }

    // The user's role in the static (for dropdown visibility)
    public string? UserRole { get; set; }

    // Whether data is currently being fetched
    public bool IsFetching { get; private set; }

    // Event fired when gear data is updated
    public event Action? OnGearDataUpdated;

    // Last fetch error message
    public string? LastError { get; private set; }

    // Available players for the dropdown (populated from priority data)
    public List<PlayerInfo>? AvailablePlayers { get; set; }

    public BiSDataService(
        RaidPlannerClient apiClient,
        PartyMatchingService partyMatching,
        ItemMappingService itemMapping,
        IPluginLog log)
    {
        _apiClient = apiClient;
        _partyMatching = partyMatching;
        _itemMapping = itemMapping;
        _log = log;
    }

    /// <summary>
    /// Fetch gear for the current player (matched via character name).
    /// Called on savage entry or /xrp bis command.
    /// </summary>
    public async Task FetchCurrentPlayerGearAsync(string? characterName = null)
    {
        // Resolve player ID, then delegate to FetchPlayerGearAsync which manages IsFetching
        string? playerId = null;
        if (characterName != null)
            playerId = _partyMatching.GetPlayerIdForName(characterName);

        if (playerId == null)
        {
            _log.Warning("[BiSData] Could not match character to planner player");
            LastError = "Character not matched to a planner player";
            return;
        }

        await FetchPlayerGearAsync(playerId, isCurrentPlayer: true);
    }

    /// <summary>
    /// Fetch gear for a specific player ID (used by Lead/Owner dropdown).
    /// </summary>
    public async Task FetchPlayerGearAsync(string playerId, bool isCurrentPlayer = false)
    {
        // Current-player fetches wait; non-current-player fetches bail if busy
        if (isCurrentPlayer)
            await _fetchLock.WaitAsync();
        else if (!await _fetchLock.WaitAsync(0))
            return;

        IsFetching = true;
        LastError = null;

        try
        {
            // Check cache first
            if (_gearCache.TryGetValue(playerId, out var cached))
            {
                if (isCurrentPlayer)
                {
                    CurrentPlayerGear = cached;
                    _itemMapping.LoadBisData(cached.Gear);
                }
                ViewedPlayerGear = cached;
                OnGearDataUpdated?.Invoke();
                return;
            }

            var gear = await _apiClient.GetPlayerGearAsync(playerId);
            if (gear == null)
            {
                _log.Warning($"[BiSData] API returned null for player {playerId}");
                LastError = "Failed to fetch gear data";
                return;
            }

            // Cache the result
            _gearCache[playerId] = gear;

            if (isCurrentPlayer)
            {
                CurrentPlayerGear = gear;
                _itemMapping.LoadBisData(gear.Gear);
            }
            ViewedPlayerGear = gear;

            _log.Info($"[BiSData] Fetched gear for {gear.PlayerName} ({gear.Job}): {gear.Gear.Count} slots");
            OnGearDataUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"[BiSData] Failed to fetch gear for player {playerId}: {ex.Message}");
            LastError = ex.Message;
        }
        finally
        {
            IsFetching = false;
            _fetchLock.Release();
        }
    }

    /// <summary>Clear the cache (e.g., when switching statics or tiers).</summary>
    public void ClearCache()
    {
        _gearCache.Clear();
        CurrentPlayerGear = null;
        ViewedPlayerGear = null;
        _itemMapping.Clear();
    }

    /// <summary>Invalidate a specific player's cache entry (e.g., after gear sync).</summary>
    public void InvalidatePlayer(string playerId)
    {
        _gearCache.TryRemove(playerId, out _);
        if (CurrentPlayerGear?.PlayerId == playerId)
            CurrentPlayerGear = null;
        if (ViewedPlayerGear?.PlayerId == playerId)
            ViewedPlayerGear = null;
    }

    /// <summary>Check if the user has Lead or Owner role (can view other players' gear).</summary>
    public bool CanViewOtherPlayers =>
        UserRole is "owner" or "lead";
}
