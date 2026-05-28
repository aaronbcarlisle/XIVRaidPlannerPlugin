using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Windows;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Coordinates loot detection, confirmation, and API logging.
/// Extracted from Plugin.cs; owns all loot-event handlers and logging helpers.
/// </summary>
public sealed class LootLogCoordinator
{
    private readonly RaidPlannerClient _api;
    private readonly PluginThread _thread;
    private readonly IChatGui _chat;
    private readonly ItemMappingService _itemMapping;
    private readonly BiSDataService _bisData;
    private readonly IPlayerState _playerState;
    private readonly PartyMatchingService _partyMatching;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly Func<string?> _currentFloorName;
    private readonly Func<PriorityResponse?> _cachedPriority;
    private readonly Func<Task> _refreshPriority;

    // Kept as internal references so handlers can call MarkAsLogged / MarkLogFailed / MarkFloorCleared / ShowForLoot
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly LootConfirmationWindow _lootConfirmWindow;

    public LootLogCoordinator(
        RaidPlannerClient api,
        PluginThread thread,
        IChatGui chat,
        ItemMappingService itemMapping,
        BiSDataService bisData,
        IPlayerState playerState,
        PartyMatchingService partyMatching,
        IPluginLog log,
        Configuration config,
        Func<string?> currentFloorName,
        Func<PriorityResponse?> cachedPriority,
        Func<Task> refreshPriority,
        PriorityOverlayWindow overlayWindow,
        LootConfirmationWindow lootConfirmWindow)
    {
        _api = api;
        _thread = thread;
        _chat = chat;
        _itemMapping = itemMapping;
        _bisData = bisData;
        _playerState = playerState;
        _partyMatching = partyMatching;
        _log = log;
        _config = config;
        _currentFloorName = currentFloorName;
        _cachedPriority = cachedPriority;
        _refreshPriority = refreshPriority;
        _overlayWindow = overlayWindow;
        _lootConfirmWindow = lootConfirmWindow;
    }

    // ==================== Loot Detection ====================

    public void OnLootObtained(LootEvent loot)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            return;

        var playerId = _partyMatching.GetPlayerIdForName(loot.PlayerName);
        if (playerId == null)
        {
            _log.Warning($"Could not match loot recipient '{loot.PlayerName}' to a planner player");
            return;
        }

        var floorName = _currentFloorName() ?? "Unknown";

        // Look up player-specific augmentable slots for materials
        string[]? eligibleSlots = null;
        var priority = _cachedPriority();
        if (loot.IsMaterial && loot.MaterialType != null && priority != null)
        {
            var playerInfo = priority.Players.Find(p => p.Id == playerId);
            if (playerInfo?.AugmentableSlots != null &&
                playerInfo.AugmentableSlots.TryGetValue(loot.MaterialType, out var slots))
            {
                eligibleSlots = slots.ToArray();
            }
        }

        switch (_config.AutoLogMode)
        {
            case AutoLogMode.Confirm:
                _thread.RunBackground(async () =>
                {
                    var weekResult = await _api.GetCurrentWeekAsync();
                    var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;
                    _thread.RunOnUi(() =>
                        _lootConfirmWindow.ShowForLoot(loot, playerId, loot.PlayerName, floorName, week, eligibleSlots));
                });
                break;

            case AutoLogMode.Auto:
                _thread.RunBackground(async () =>
                {
                    var weekResult = await _api.GetCurrentWeekAsync();
                    var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;
                    // Auto-select slot if only one option; otherwise log without augmentation
                    var autoSlot = eligibleSlots is { Length: 1 } ? eligibleSlots[0] : null;
                    await LogLootAsync(playerId, loot.GearSlot, loot.MaterialType, floorName, week, autoSlot);
                    _log.Information($"Auto-logged: {loot.ItemName} -> {loot.PlayerName}");
                });
                break;

            case AutoLogMode.Manual:
                // Do nothing - user must use overlay buttons
                break;
        }
    }

    // ==================== Purchase Detection ====================

    public void OnItemPurchased(PurchaseEvent purchase)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            return;

        // Only auto-log if the purchased item is BiS
        if (!_itemMapping.HasData || !_itemMapping.IsBisItem(purchase.ItemId))
        {
            _log.Debug($"Purchased item {purchase.ItemName} is not BiS — skipping auto-log");
            return;
        }

        // Find current player's planner ID
        var charName = _playerState.IsLoaded ? _playerState.CharacterName?.ToString() : null;
        if (string.IsNullOrEmpty(charName))
            return;

        var playerId = _partyMatching.GetPlayerIdForName(charName);
        if (playerId == null)
        {
            // Try using the BiS data player ID if party matching hasn't run
            playerId = _bisData.CurrentPlayerGear?.PlayerId;
        }

        if (playerId == null)
        {
            _log.Warning($"Could not match character to planner player for purchase logging");
            return;
        }

        var capturedPlayerId = playerId;
        var capturedCharName = charName;
        var floorName = _currentFloorName() ?? "M9S"; // Default to M9S for vendor purchases

        switch (_config.AutoLogMode)
        {
            case AutoLogMode.Confirm:
                _thread.RunBackground(async () =>
                {
                    var weekResult = await _api.GetCurrentWeekAsync();
                    var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;

                    // Show confirmation for the purchase
                    var lootEvent = new LootEvent
                    {
                        PlayerName = capturedCharName,
                        ItemName = purchase.ItemName,
                        ItemId = purchase.ItemId,
                        GearSlot = purchase.GearSlot,
                        MaterialType = purchase.MaterialType,
                        Timestamp = DateTime.UtcNow,
                    };
                    _thread.RunOnUi(() =>
                        _lootConfirmWindow.ShowForLoot(lootEvent, capturedPlayerId, capturedCharName, floorName, week, null));
                });
                break;

            case AutoLogMode.Auto:
                _thread.RunBackground(async () =>
                {
                    var weekResult = await _api.GetCurrentWeekAsync();
                    var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;
                    await LogPurchaseAsync(capturedPlayerId, purchase, floorName, week);
                });
                break;

            case AutoLogMode.Manual:
                // Don't auto-log
                _chat.Print($"[XRP] BiS purchase detected: {purchase.ItemName}. Use /xrp sync to update.");
                break;
        }
    }

    private async Task<bool> LogPurchaseAsync(string playerId, PurchaseEvent purchase, string floorName, int weekNumber)
    {
        if (purchase.IsGear && purchase.GearSlot != null)
        {
            var result = await _api.CreatePurchaseLogEntryAsync(new LootLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                ItemSlot = purchase.GearSlot,
                RecipientPlayerId = playerId,
                Method = "purchase",
                Notes = "Auto-logged via Dalamud plugin",
                MarkAcquired = true,
            });

            if (result.IsSuccess)
            {
                _thread.RunOnUi(() =>
                {
                    _chat.Print($"[XRP] Purchase logged: {purchase.ItemName}");
                    _bisData.InvalidatePlayer(playerId);
                });
            }
            else
            {
                _thread.RunOnUi(() => _chat.PrintError($"[XRP] Failed to log purchase: {purchase.ItemName}"));
            }
            return result.IsSuccess;
        }

        if (purchase.IsMaterial && purchase.MaterialType != null)
        {
            var result = await _api.CreateMaterialLogEntryAsync(new MaterialLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                MaterialType = purchase.MaterialType,
                RecipientPlayerId = playerId,
                Method = "purchase",
                Notes = "Auto-logged via Dalamud plugin",
            });

            if (result.IsSuccess)
            {
                _thread.RunOnUi(() => _chat.Print($"[XRP] Material purchase logged: {purchase.ItemName}"));
            }
            else
            {
                _thread.RunOnUi(() => _chat.PrintError($"[XRP] Failed to log material purchase: {purchase.ItemName}"));
            }
            return result.IsSuccess;
        }

        return false;
    }

    // ==================== Manual Logging ====================

    public void OnManualLog(string playerId, string slot, string playerName, string? slotAugmented)
    {
        var floorName = _currentFloorName() ?? "Unknown";
        _log.Information($"Manual log requested: {slot} -> {playerName} (floor={floorName}, player={playerId}, slotAugmented={slotAugmented ?? "none"})");
        _chat.Print($"[XRP] Logging {slot} -> {playerName}...");

        _thread.RunBackground(async () =>
        {
            try
            {
                var weekResult = await _api.GetCurrentWeekAsync();
                var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;

                // Determine if this is a gear slot or material
                string? gearSlot = null;
                string? materialType = null;

                if (slot is "twine" or "glaze" or "solvent" or "universal_tomestone")
                    materialType = slot;
                else
                    gearSlot = slot;

                var success = await LogLootAsync(playerId, gearSlot, materialType, floorName, week, slotAugmented);
                if (success)
                {
                    _log.Information($"Manual log success: {slot} -> {playerName}");
                    _thread.RunOnUi(() =>
                    {
                        _chat.Print($"[XRP] Logged {slot} -> {playerName}");
                        _overlayWindow.MarkAsLogged(playerId, slot, playerName);
                    });
                    await _refreshPriority();
                }
                else
                {
                    _log.Error($"Manual log failed: {slot} -> {playerName}");
                    _thread.RunOnUi(() =>
                    {
                        _chat.PrintError($"[XRP] Failed to log {slot} -> {playerName}");
                        _overlayWindow.MarkLogFailed(slot, playerName);
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Manual log exception: {ex}");
                _thread.RunOnUi(() => _chat.PrintError($"[XRP] Error: {ex.Message}"));
            }
        });
    }

    public void OnLootConfirmed(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber, string? slotAugmented)
    {
        _thread.RunBackground(async () =>
        {
            await LogLootAsync(playerId, gearSlot, materialType, floorName, weekNumber, slotAugmented);

            // Refresh priority after logging
            await _refreshPriority();
        });
    }

    private async Task<bool> LogLootAsync(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber, string? slotAugmented = null)
    {
        if (materialType != null)
        {
            var result = await _api.CreateMaterialLogEntryAsync(new MaterialLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                MaterialType = materialType,
                RecipientPlayerId = playerId,
                Method = "drop",
                Notes = "Logged via Dalamud plugin",
                MarkAugmented = slotAugmented != null,
                SlotAugmented = slotAugmented,
            });
            return result.IsSuccess;
        }

        if (gearSlot != null)
        {
            var result = await _api.CreateLootLogEntryAsync(new LootLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                ItemSlot = gearSlot,
                RecipientPlayerId = playerId,
                Method = "drop",
                Notes = "Logged via Dalamud plugin",
                MarkAcquired = true,
            });
            return result.IsSuccess;
        }

        return false;
    }

    // ==================== Floor Cleared ====================

    public void OnMarkFloorCleared()
    {
        var priority = _cachedPriority();
        if (priority == null) return;

        var floorName = _currentFloorName() ?? "Unknown";
        var playerIds = priority.Players.ConvertAll(p => p.Id);

        _thread.RunBackground(async () =>
        {
            var weekResult = await _api.GetCurrentWeekAsync();
            var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;

            var result = await _api.MarkFloorClearedAsync(new MarkFloorClearedRequest
            {
                WeekNumber = week,
                Floor = floorName,
                PlayerIds = playerIds,
                Notes = "Logged via Dalamud plugin",
            });

            if (result.IsSuccess)
            {
                _thread.RunOnUi(() => _overlayWindow.MarkFloorCleared());
                _log.Information($"Marked {floorName} cleared for {playerIds.Count} players");
            }
        });
    }

    // ==================== Gear Sync Helper ====================

    /// <summary>Log loot entries for newly acquired BiS items detected during gear sync.</summary>
    public async Task LogNewAcquisitionsAsync(string playerId, List<string> slots)
    {
        try
        {
            var weekResult = await _api.GetCurrentWeekAsync();
            var week = weekResult.IsSuccess ? weekResult.Value!.CurrentWeek : 1;
            var priority = _cachedPriority();
            var slotToFloor = priority != null ? BuildSlotToFloorMapping(priority) : new Dictionary<string, string>();
            var logged = 0;

            foreach (var slot in slots)
            {
                var floor = slotToFloor.GetValueOrDefault(slot, _currentFloorName() ?? "M9S");
                var logResult = await _api.CreatePurchaseLogEntryAsync(new LootLogCreateRequest
                {
                    WeekNumber = week,
                    Floor = floor,
                    ItemSlot = slot,
                    RecipientPlayerId = playerId,
                    Method = "purchase",
                    Notes = "Synced via Dalamud plugin",
                    MarkAcquired = true,
                });
                if (logResult.IsSuccess) logged++;
            }

            if (logged > 0)
                _chat.Print($"[XRP] Logged {logged} gear acquisition(s).");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to log acquisitions: {ex.Message}");
        }
    }

    // ==================== Pure Slot→Floor Mapping ====================

    /// <summary>
    /// Build a mapping of gear slot → floor name from priority data.
    /// Later floors override earlier ones so that slots shared across floors
    /// resolve to the highest-floor source (BiS always drops from later floors).
    /// </summary>
    public static Dictionary<string, string> BuildSlotToFloorMapping(PriorityResponse priority)
    {
        var mapping = new Dictionary<string, string>();
        for (var f = 0; f < priority.TierFloors.Count; f++)
        {
            var floorKey = $"floor{f + 1}";
            if (priority.Priority.TryGetValue(floorKey, out var floorData))
            {
                foreach (var slotKey in floorData.Keys)
                {
                    // Later floors override (BiS drops from later floors)
                    mapping[slotKey] = priority.TierFloors[f];
                }
            }
        }
        return mapping;
    }

}
