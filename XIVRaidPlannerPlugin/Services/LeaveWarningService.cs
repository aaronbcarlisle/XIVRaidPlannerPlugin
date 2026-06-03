using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Windows;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Warns the player if they try to leave a savage instance while unclaimed priority loot exists.
/// Checks if the current player is in the top 3 priority for any pending drop.
/// Owns the SelectYesno addon-lifecycle subscription that triggers the check.
/// Construct first (the window depends on this service), then call <see cref="Initialize"/>
/// once windows + raid-session state are available.
/// </summary>
public class LeaveWarningService : IDisposable
{
    private readonly Configuration _config;
    private readonly IPluginLog _log;

    // Set via Initialize() after dependent windows are constructed.
    private IAddonLifecycle? _addonLifecycle;
    private IPlayerState? _playerState;
    private PartyMatchingService? _partyMatching;
    private LootDetectionService? _lootDetection;
    private PriorityOverlayWindow? _overlayWindow;
    private LeaveWarningWindow? _leaveWarningWindow;
    private Func<RaidSessionState>? _sessionState;
    private bool _initialized;

    /// <summary>Whether the leave warning popup should be shown.</summary>
    public bool ShouldShowWarning { get; private set; }

    /// <summary>Items the player has high priority for but hasn't claimed.</summary>
    public List<PriorityWarningItem> WarningItems { get; private set; } = new();

    public LeaveWarningService(Configuration config, IPluginLog log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Wire SelectYesno addon listeners and references needed for live leave-warning checks.
    /// Call once after windows + RaidSessionService are constructed.
    /// </summary>
    public void Initialize(
        IAddonLifecycle addonLifecycle,
        IPlayerState playerState,
        PartyMatchingService partyMatching,
        LootDetectionService lootDetection,
        PriorityOverlayWindow overlayWindow,
        LeaveWarningWindow leaveWarningWindow,
        Func<RaidSessionState> sessionState)
    {
        if (_initialized) return;

        _addonLifecycle = addonLifecycle;
        _playerState = playerState;
        _partyMatching = partyMatching;
        _lootDetection = lootDetection;
        _overlayWindow = overlayWindow;
        _leaveWarningWindow = leaveWarningWindow;
        _sessionState = sessionState;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoClose);
        _initialized = true;
    }

    public void Dispose()
    {
        if (_initialized && _addonLifecycle != null)
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
            _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoClose);
        }
    }

    // ==================== Addon Lifecycle Handlers ====================

    private void OnSelectYesnoSetup(AddonEvent type, AddonArgs args)
    {
        if (_sessionState == null || _playerState == null || _partyMatching == null ||
            _lootDetection == null || _overlayWindow == null || _leaveWarningWindow == null)
            return;

        var state = _sessionState();
        _log.Information($"SelectYesno triggered — floor={state.CurrentFloor}, " +
                        $"hasPriority={state.CachedPriority != null}, leaveWarning={_config.EnableLeaveWarning}");

        // Only check when we're in a savage instance with priority data
        if (state.CurrentFloor == null || state.CachedPriority == null || !_config.EnableLeaveWarning)
            return;

        // Find the current player's planner ID from their character name
        var charName = _playerState.IsLoaded ? _playerState.CharacterName?.ToString() : null;
        _log.Information($"Player: loaded={_playerState.IsLoaded}, name={charName ?? "null"}");
        if (string.IsNullOrEmpty(charName))
            return;

        var currentPlayerId = _partyMatching.GetPlayerIdForName(charName);
        _log.Information($"Matched player ID: {currentPlayerId ?? "null"}");

        // Get the floor priority data
        var floorKey = $"floor{state.CurrentFloor.Value}";
        if (!state.CachedPriority.Priority.TryGetValue(floorKey, out var floorPriority))
            return;

        _log.Information($"Leave check: player={currentPlayerId ?? "null"}, " +
                        $"detected={_lootDetection.DistributedLoot.Count}, " +
                        $"manuallyLogged={_overlayWindow.LoggedEntries.Count}, " +
                        $"drops={floorPriority.Count}");

        CheckLeaveWarning(currentPlayerId, _lootDetection.DistributedLoot,
            floorPriority, _overlayWindow.LoggedEntries);

        if (ShouldShowWarning)
        {
            _leaveWarningWindow.IsOpen = true;
            _log.Information($"Leave warning shown — {WarningItems.Count} unclaimed priority items");
        }
        else
        {
            _log.Information("Leave check passed — no unclaimed priority loot");
        }
    }

    private void OnSelectYesnoClose(AddonEvent type, AddonArgs args)
    {
        // Dialog closed (Yes, No, or Escape) — dismiss our overlay
        Dismiss();
        if (_leaveWarningWindow != null)
            _leaveWarningWindow.IsOpen = false;
    }

    /// <summary>
    /// Check if the player should be warned about leaving.
    /// Call this when the leave duty dialog appears.
    /// </summary>
    /// <param name="currentPlayerId">The planner player ID of the current user.</param>
    /// <param name="distributedLoot">Loot that has already been distributed this session (auto-detected).</param>
    /// <param name="floorPriority">Priority data for the current floor.</param>
    /// <param name="manuallyLoggedEntries">Entries logged manually via overlay (format: "playerId|slot").</param>
    public void CheckLeaveWarning(
        string? currentPlayerId,
        List<LootEvent> distributedLoot,
        Dictionary<string, List<PriorityEntry>>? floorPriority,
        IReadOnlySet<string>? manuallyLoggedEntries = null)
    {
        ShouldShowWarning = false;
        WarningItems.Clear();

        if (!_config.EnableLeaveWarning || string.IsNullOrEmpty(currentPlayerId) || floorPriority == null)
            return;

        // Check each drop type in the floor priority
        foreach (var (dropType, priorityList) in floorPriority)
        {
            // Check if this item has already been distributed (auto-detected via chat)
            var isDistributed = distributedLoot.Any(l =>
                (l.GearSlot == dropType) ||
                (l.MaterialType == dropType) ||
                (dropType == "ring" && (l.GearSlot == "ring1" || l.GearSlot == "ring2")));

            // Also check if ANY player has been manually logged for this drop type
            if (!isDistributed && manuallyLoggedEntries != null)
            {
                // Handle ring: dropType "ring" should match logged "ring1" or "ring2"
                if (dropType == "ring")
                    isDistributed = manuallyLoggedEntries.Any(e =>
                        e.EndsWith("|ring") || e.EndsWith("|ring1") || e.EndsWith("|ring2"));
                else
                    isDistributed = manuallyLoggedEntries.Any(e => e.EndsWith($"|{dropType}"));
            }

            if (isDistributed) continue;

            // Check if current player is in top 3 priority
            var playerEntry = priorityList
                .Take(3)
                .FirstOrDefault(e => e.PlayerId == currentPlayerId);

            if (playerEntry != null)
            {
                var rank = priorityList.IndexOf(playerEntry) + 1;
                WarningItems.Add(new PriorityWarningItem
                {
                    DropType = dropType,
                    Rank = rank,
                    Score = playerEntry.Score,
                });
            }
        }

        ShouldShowWarning = WarningItems.Count > 0;

        if (ShouldShowWarning)
        {
            _log.Information($"Leave warning: player has priority for {WarningItems.Count} unclaimed drops");
        }
    }

    /// <summary>Dismiss the warning (player chose "Leave Anyway").</summary>
    public void Dismiss()
    {
        ShouldShowWarning = false;
        WarningItems.Clear();
    }
}

public class PriorityWarningItem
{
    /// <summary>The drop type (gear slot or material name).</summary>
    public string DropType { get; set; } = string.Empty;

    /// <summary>Player's rank for this drop (1-3).</summary>
    public int Rank { get; set; }

    /// <summary>Player's priority score.</summary>
    public int Score { get; set; }
}
