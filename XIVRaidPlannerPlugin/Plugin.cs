using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
using XIVRaidPlannerPlugin.Windows;

namespace XIVRaidPlannerPlugin;

/// <summary>
/// XIV Raid Planner Dalamud Plugin.
/// In-game loot priority overlay and auto-logging for FFXIV Raid Planner.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // Dalamud services
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/xrp";

    // Configuration
    public Configuration Configuration { get; init; }

    // Services
    private readonly RaidPlannerClient _apiClient;
    private readonly TerritoryService _territoryService;
    private readonly PartyMatchingService _partyMatching;
    private readonly LootDetectionService _lootDetection;
    private readonly LeaveWarningService _leaveWarning;
    private readonly ItemMappingService _itemMapping;
    private readonly BiSDataService _bisData;
    private readonly InventoryService _inventoryService;
    private readonly AddonHighlightService _addonHighlight;

    // Windows
    public readonly WindowSystem WindowSystem = new("XIVRaidPlannerPlugin");
    private readonly ConfigWindow _configWindow;
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly LootConfirmationWindow _lootConfirmWindow;
    private readonly LeaveWarningWindow _leaveWarningWindow;
    private readonly BiSViewerWindow _bisViewerWindow;

    // Cached priority data
    private PriorityResponse? _cachedPriority;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Initialize services
        _apiClient = new RaidPlannerClient(Configuration, Log);
        _territoryService = new TerritoryService(ClientState, DataManager, Log);
        _partyMatching = new PartyMatchingService(PartyList, Configuration, Log);
        _lootDetection = new LootDetectionService(ChatGui, DataManager, Log);
        _leaveWarning = new LeaveWarningService(Configuration, Log);
        _itemMapping = new ItemMappingService(DataManager, Log);
        _bisData = new BiSDataService(_apiClient, _partyMatching, _itemMapping, Log);
        _inventoryService = new InventoryService(DataManager, Log);
        _addonHighlight = new AddonHighlightService(_itemMapping, Configuration, AddonLifecycle, Log);
        _addonHighlight.Register();

        // Initialize windows
        _configWindow = new ConfigWindow(Configuration, _apiClient, _partyMatching, PartyList, PlayerState);
        _overlayWindow = new PriorityOverlayWindow(Configuration);
        _lootConfirmWindow = new LootConfirmationWindow();
        _leaveWarningWindow = new LeaveWarningWindow(_leaveWarning, GameGui);
        _bisViewerWindow = new BiSViewerWindow(_bisData, _inventoryService, Configuration);

        WindowSystem.AddWindow(_configWindow);
        WindowSystem.AddWindow(_overlayWindow);
        WindowSystem.AddWindow(_lootConfirmWindow);
        WindowSystem.AddWindow(_leaveWarningWindow);
        WindowSystem.AddWindow(_bisViewerWindow);

        // Wire up events
        _territoryService.OnSavageEntered += OnSavageEntered;
        _territoryService.OnSavageExited += OnSavageExited;
        _lootDetection.OnLootObtained += OnLootObtained;
        _lootDetection.OnItemPurchased += OnItemPurchased;
        _overlayWindow.OnManualLog += OnManualLog;
        _overlayWindow.OnMarkFloorCleared += OnMarkFloorCleared;
        _overlayWindow.OnRefresh += OnRefreshRequested;
        _lootConfirmWindow.OnConfirm += OnLootConfirmed;
        _bisViewerWindow.OnSyncRequested += SyncGear;

        // Register commands
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle priority overlay. '/xrp bis' for BiS viewer. '/xrp sync' to sync gear. '/xrp config' for settings.",
        });

        // Register UI drawing
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleOverlay;

        // Hook into the "Abandon duty?" confirmation dialog for leave warnings
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoClose);

        // Hook for overlay timing: show on duty complete
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "DutyComplete", OnDutyCompleteSetup);

        // Hook for overlay timing: show on loot window open
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "NeedGreed", OnNeedGreedSetup);

        // Check if already in a savage instance (e.g., plugin loaded mid-instance)
        _territoryService.CheckCurrentTerritory();

        Log.Information("XIV Raid Planner plugin loaded");
    }

    public void Dispose()
    {
        // Unsubscribe events
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleOverlay;

        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoClose);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "DutyComplete", OnDutyCompleteSetup);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "NeedGreed", OnNeedGreedSetup);

        _territoryService.OnSavageEntered -= OnSavageEntered;
        _territoryService.OnSavageExited -= OnSavageExited;
        _lootDetection.OnLootObtained -= OnLootObtained;
        _lootDetection.OnItemPurchased -= OnItemPurchased;
        _overlayWindow.OnManualLog -= OnManualLog;
        _overlayWindow.OnMarkFloorCleared -= OnMarkFloorCleared;
        _overlayWindow.OnRefresh -= OnRefreshRequested;
        _lootConfirmWindow.OnConfirm -= OnLootConfirmed;
        _bisViewerWindow.OnSyncRequested -= SyncGear;

        // Dispose windows
        WindowSystem.RemoveAllWindows();
        _configWindow.Dispose();
        _overlayWindow.Dispose();
        _lootConfirmWindow.Dispose();
        _leaveWarningWindow.Dispose();
        _bisViewerWindow.Dispose();

        // Dispose services
        _addonHighlight.Dispose();
        _territoryService.Dispose();
        _lootDetection.Dispose();
        _apiClient.Dispose();

        // Remove commands
        CommandManager.RemoveHandler(CommandName);
    }

    // ==================== Command Handling ====================

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim().ToLowerInvariant();

        switch (trimmedArgs)
        {
            case "config":
            case "settings":
                ToggleConfigUi();
                break;
            case "bis":
                ToggleBisViewer();
                break;
            case "sync":
                SyncGear();
                break;
            default:
                ToggleOverlay();
                break;
        }
    }

    public void ToggleConfigUi() => _configWindow.Toggle();
    public void ToggleOverlay() => _overlayWindow.Toggle();
    public void ToggleBisViewer()
    {
        if (!_bisViewerWindow.IsOpen)
        {
            // Fetch gear data if not already loaded
            var charName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;
            if (charName != null && _bisData.CurrentPlayerGear == null)
            {
                Task.Run(async () => await _bisData.FetchCurrentPlayerGearAsync(charName));
            }
        }
        _bisViewerWindow.Toggle();
    }
    public void SyncGear()
    {
        if (string.IsNullOrEmpty(Configuration.ApiKey))
        {
            ChatGui.PrintError("[XRP] No API key configured. Use /xrp config.");
            return;
        }

        var currentGear = _bisData.CurrentPlayerGear;
        if (currentGear == null)
        {
            ChatGui.PrintError("[XRP] No BiS data loaded. Enter a savage instance or use /xrp bis first.");
            return;
        }

        ChatGui.Print("[XRP] Syncing equipped gear...");

        // Read equipped items (must happen on framework thread, which we're already on from command handler)
        var equipped = _inventoryService.ReadEquippedGear();
        if (equipped.Count == 0)
        {
            ChatGui.PrintError("[XRP] Could not read equipped gear.");
            return;
        }

        // Re-fetch fresh gear data from API before comparing (web app might have changed)
        var playerId = currentGear.PlayerId;
        _bisData.InvalidatePlayer(playerId);
        Task.Run(async () =>
        {
            // Fetch latest gear state from API (in case user reset progress on web app)
            await _bisData.FetchPlayerGearAsync(playerId, isCurrentPlayer: true);
            var freshGear = _bisData.CurrentPlayerGear;
            if (freshGear == null)
            {
                Framework.RunOnFrameworkThread(() => ChatGui.PrintError("[XRP] Failed to fetch current gear state from API."));
                return;
            }

            // Build the gear update comparing equipped items against FRESH API data
            var updatedGear = _inventoryService.BuildGearUpdate(equipped, freshGear.Gear);

            // Check tome weapon status
            TomeWeaponInfo? tomeWeaponUpdate = null;
            if (freshGear.TomeWeapon.Pursuing && equipped.TryGetValue("weapon", out var equippedWeapon))
            {
                var weaponSource = _inventoryService.ClassifySource(equippedWeapon.ItemId);
                if (weaponSource is "tome" or "tome_up")
                {
                    tomeWeaponUpdate = new TomeWeaponInfo
                    {
                        Pursuing = true,
                        HasItem = true,
                        IsAugmented = weaponSource == "tome_up",
                    };
                    Log.Info($"[Sync] Detected tome weapon: source={weaponSource}, augmented={tomeWeaponUpdate.IsAugmented}");
                }
            }

            // Count changes and track newly acquired BiS slots
            var changes = 0;
            var newlyAcquired = new List<string>();
            for (var i = 0; i < updatedGear.Count && i < freshGear.Gear.Count; i++)
            {
                if (updatedGear[i].CurrentSource != freshGear.Gear[i].CurrentSource ||
                    updatedGear[i].HasItem != freshGear.Gear[i].HasItem ||
                    updatedGear[i].IsAugmented != freshGear.Gear[i].IsAugmented)
                    changes++;

                if (updatedGear[i].HasItem && !freshGear.Gear[i].HasItem)
                    newlyAcquired.Add(updatedGear[i].Slot);
            }

            // Also count tome weapon as a change
            if (tomeWeaponUpdate != null && !freshGear.TomeWeapon.HasItem)
                changes++;

            Log.Info($"[Sync] Comparison: {changes} gear changes, {newlyAcquired.Count} newly acquired, tomeWeapon={tomeWeaponUpdate != null}");

            if (changes == 0)
            {
                Framework.RunOnFrameworkThread(() => ChatGui.Print("[XRP] Gear already up to date."));
                return;
            }

            // Send update to API
            var success = await _apiClient.SyncPlayerGearAsync(
                freshGear.PlayerId,
                new SnapshotPlayerUpdateRequest { Gear = updatedGear, TomeWeapon = tomeWeaponUpdate });

            if (success)
            {
                // Marshal UI/service updates back to the framework thread
                Framework.RunOnFrameworkThread(() =>
                {
                    ChatGui.Print($"[XRP] Gear synced: {changes} slot(s) updated.");
                    _bisData.InvalidatePlayer(freshGear.PlayerId);
                    _bisViewerWindow.InvalidateEquippedGear();
                });

                // Auto-log loot entries only when in a savage instance with reliable floor data
                if (newlyAcquired.Count > 0 && _cachedPriority != null && _territoryService.CurrentFloor != null)
                {
                    try { await LogNewAcquisitionsAsync(freshGear.PlayerId, newlyAcquired); }
                    catch (System.Exception ex) { Log.Warning($"Loot logging failed (non-critical): {ex.Message}"); }
                }
                else if (newlyAcquired.Count > 0)
                {
                    Framework.RunOnFrameworkThread(() =>
                        ChatGui.Print($"[XRP] {newlyAcquired.Count} new BiS item(s) detected. Enter a savage instance to auto-log."));
                }

                // Re-fetch to update the BiS viewer
                var charName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;
                if (charName != null)
                    await _bisData.FetchCurrentPlayerGearAsync(charName);
            }
            else
            {
                Framework.RunOnFrameworkThread(() => ChatGui.PrintError("[XRP] Failed to sync gear. Check connection."));
            }
        });
    }

    // ==================== Territory Events ====================

    private void OnSavageEntered(int floor)
    {
        if (string.IsNullOrEmpty(Configuration.ApiKey))
            return;

        _lootDetection.Reset();

        // Fetch priority data (always fetch, even if overlay is hidden, so data is ready)
        Task.Run(async () =>
        {
            // Auto-detect active tier only if none is configured
            if (!string.IsNullOrEmpty(Configuration.DefaultGroupId) && string.IsNullOrEmpty(Configuration.DefaultTierId))
            {
                var tiers = await _apiClient.GetTiersAsync(Configuration.DefaultGroupId);
                var activeTier = tiers.Find(t => t.IsActive);
                if (activeTier != null)
                {
                    Log.Information($"Auto-detected active tier: {activeTier.TierId} ({activeTier.Id})");
                    Configuration.DefaultTierId = activeTier.Id;
                    Configuration.DefaultTierName = activeTier.TierId;
                    Configuration.Save();
                }
                else
                {
                    Log.Warning("No active tier found and no tier configured. Overlay will not show.");
                    ChatGui.PrintError("[XRP] No active tier found. Select a tier in /xrp config.");
                    return;
                }
            }

            if (string.IsNullOrEmpty(Configuration.DefaultTierId))
                return;

            _cachedPriority = await _apiClient.GetPriorityAsync(floor);
            if (_cachedPriority != null)
            {
                var floorName = floor <= _cachedPriority.TierFloors.Count
                    ? _cachedPriority.TierFloors[floor - 1]
                    : $"Floor {floor}";

                _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, Configuration.DefaultGroupName, Configuration.DefaultTierName);

                // Only show overlay if configured to show on entry
                if (Configuration.ShowOverlay && Configuration.ShowOverlayOnEntry)
                    _overlayWindow.IsOpen = true;

                // Share player list with config window and run matching
                _configWindow.SetStaticPlayers(_cachedPriority.Players);
                _partyMatching.MatchParty(_cachedPriority.Players);

                // Fetch BiS data for the current player
                _bisData.AvailablePlayers = _cachedPriority.Players;

                // Set user role from the static group info
                // (already available from the API - StaticGroupInfo.UserRole)
                var charName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;
                if (charName != null)
                {
                    await _bisData.FetchCurrentPlayerGearAsync(charName);

                    // Show BiS viewer if configured
                    if (Configuration.ShowBisViewer)
                        _bisViewerWindow.IsOpen = true;
                }
            }
        });
    }

    private void OnSavageExited()
    {
        _overlayWindow.ClearData();
        _overlayWindow.IsOpen = false;
        _leaveWarningWindow.IsOpen = false;
        _bisViewerWindow.IsOpen = false;
        _bisData.ClearCache();
    }

    // ==================== Leave Warning ====================

    private void OnSelectYesnoSetup(AddonEvent type, AddonArgs args)
    {
        Log.Information($"SelectYesno triggered — floor={_territoryService.CurrentFloor}, " +
                        $"hasPriority={_cachedPriority != null}, leaveWarning={Configuration.EnableLeaveWarning}");

        // Only check when we're in a savage instance with priority data
        if (_territoryService.CurrentFloor == null || _cachedPriority == null || !Configuration.EnableLeaveWarning)
            return;

        // Find the current player's planner ID from their character name
        var charName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;
        Log.Information($"Player: loaded={PlayerState.IsLoaded}, name={charName ?? "null"}");
        if (string.IsNullOrEmpty(charName))
            return;

        var currentPlayerId = _partyMatching.GetPlayerIdForName(charName);
        Log.Information($"Matched player ID: {currentPlayerId ?? "null"}");

        // Get the floor priority data
        var floorKey = $"floor{_territoryService.CurrentFloor.Value}";
        if (!_cachedPriority.Priority.TryGetValue(floorKey, out var floorPriority))
            return;

        Log.Information($"Leave check: player={currentPlayerId ?? "null"}, " +
                        $"detected={_lootDetection.DistributedLoot.Count}, " +
                        $"manuallyLogged={_overlayWindow.LoggedEntries.Count}, " +
                        $"drops={floorPriority.Count}");

        _leaveWarning.CheckLeaveWarning(currentPlayerId, _lootDetection.DistributedLoot,
            floorPriority, _overlayWindow.LoggedEntries);

        if (_leaveWarning.ShouldShowWarning)
        {
            _leaveWarningWindow.IsOpen = true;
            Log.Information($"Leave warning shown — {_leaveWarning.WarningItems.Count} unclaimed priority items");
        }
        else
        {
            Log.Information("Leave check passed — no unclaimed priority loot");
        }
    }

    private void OnSelectYesnoClose(AddonEvent type, AddonArgs args)
    {
        // Dialog closed (Yes, No, or Escape) — dismiss our overlay
        _leaveWarning.Dismiss();
        _leaveWarningWindow.IsOpen = false;
    }

    // ==================== Overlay Timing Events ====================

    private void OnDutyCompleteSetup(AddonEvent type, AddonArgs args)
    {
        if (!Configuration.ShowOverlay || !Configuration.ShowOverlayOnDutyComplete)
            return;

        if (_cachedPriority != null && _territoryService.CurrentFloor != null)
        {
            Log.Information("Duty complete — showing overlay");
            _overlayWindow.IsOpen = true;
        }
    }

    private void OnNeedGreedSetup(AddonEvent type, AddonArgs args)
    {
        if (!Configuration.ShowOverlay || !Configuration.ShowOverlayOnLootWindow)
            return;

        if (_cachedPriority != null && _territoryService.CurrentFloor != null)
        {
            Log.Information("Loot window opened — showing overlay");
            _overlayWindow.IsOpen = true;
        }
    }

    // ==================== Loot Detection ====================

    private void OnLootObtained(LootEvent loot)
    {
        if (string.IsNullOrEmpty(Configuration.ApiKey))
            return;

        var playerId = _partyMatching.GetPlayerIdForName(loot.PlayerName);
        if (playerId == null)
        {
            Log.Warning($"Could not match loot recipient '{loot.PlayerName}' to a planner player");
            return;
        }

        var floorName = _territoryService.CurrentFloorName ?? "Unknown";

        // Look up player-specific augmentable slots for materials
        string[]? eligibleSlots = null;
        if (loot.IsMaterial && loot.MaterialType != null && _cachedPriority != null)
        {
            var playerInfo = _cachedPriority.Players.Find(p => p.Id == playerId);
            if (playerInfo?.AugmentableSlots != null &&
                playerInfo.AugmentableSlots.TryGetValue(loot.MaterialType, out var slots))
            {
                eligibleSlots = slots.ToArray();
            }
        }

        switch (Configuration.AutoLogMode)
        {
            case AutoLogMode.Confirm:
                Task.Run(async () =>
                {
                    var weekData = await _apiClient.GetCurrentWeekAsync();
                    var week = weekData?.CurrentWeek ?? 1;
                    _lootConfirmWindow.ShowForLoot(loot, playerId, loot.PlayerName, floorName, week, eligibleSlots);
                });
                break;

            case AutoLogMode.Auto:
                Task.Run(async () =>
                {
                    var weekData = await _apiClient.GetCurrentWeekAsync();
                    var week = weekData?.CurrentWeek ?? 1;
                    // Auto-select slot if only one option; otherwise log without augmentation
                    var autoSlot = eligibleSlots is { Length: 1 } ? eligibleSlots[0] : null;
                    await LogLootAsync(playerId, loot.GearSlot, loot.MaterialType, floorName, week, autoSlot);
                    Log.Information($"Auto-logged: {loot.ItemName} -> {loot.PlayerName}");
                });
                break;

            case AutoLogMode.Manual:
                // Do nothing - user must use overlay buttons
                break;
        }
    }

    // ==================== Purchase Detection (Phase 5A) ====================

    private void OnItemPurchased(PurchaseEvent purchase)
    {
        if (string.IsNullOrEmpty(Configuration.ApiKey))
            return;

        // Only auto-log if the purchased item is BiS
        if (!_itemMapping.HasData || !_itemMapping.IsBisItem(purchase.ItemId))
        {
            Log.Debug($"Purchased item {purchase.ItemName} is not BiS — skipping auto-log");
            return;
        }

        // Find current player's planner ID
        var charName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;
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
            Log.Warning($"Could not match character to planner player for purchase logging");
            return;
        }

        var capturedPlayerId = playerId;
        var floorName = _territoryService.CurrentFloorName ?? "M9S"; // Default to M9S for vendor purchases

        switch (Configuration.AutoLogMode)
        {
            case AutoLogMode.Confirm:
                Task.Run(async () =>
                {
                    var weekData = await _apiClient.GetCurrentWeekAsync();
                    var week = weekData?.CurrentWeek ?? 1;

                    // Show confirmation for the purchase
                    var lootEvent = new LootEvent
                    {
                        PlayerName = charName,
                        ItemName = purchase.ItemName,
                        ItemId = purchase.ItemId,
                        GearSlot = purchase.GearSlot,
                        MaterialType = purchase.MaterialType,
                        Timestamp = DateTime.UtcNow,
                    };
                    _lootConfirmWindow.ShowForLoot(lootEvent, capturedPlayerId, charName, floorName, week, null);
                });
                break;

            case AutoLogMode.Auto:
                Task.Run(async () =>
                {
                    var weekData = await _apiClient.GetCurrentWeekAsync();
                    var week = weekData?.CurrentWeek ?? 1;
                    await LogPurchaseAsync(capturedPlayerId, purchase, floorName, week);
                });
                break;

            case AutoLogMode.Manual:
                // Don't auto-log
                ChatGui.Print($"[XRP] BiS purchase detected: {purchase.ItemName}. Use /xrp sync to update.");
                break;
        }
    }

    private async Task<bool> LogPurchaseAsync(string playerId, PurchaseEvent purchase, string floorName, int weekNumber)
    {
        if (purchase.IsGear && purchase.GearSlot != null)
        {
            var success = await _apiClient.CreatePurchaseLogEntryAsync(new LootLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                ItemSlot = purchase.GearSlot,
                RecipientPlayerId = playerId,
                Method = "purchase",
                Notes = "Auto-logged via Dalamud plugin",
                MarkAcquired = true,
            });

            if (success)
            {
                ChatGui.Print($"[XRP] Purchase logged: {purchase.ItemName}");
                _bisData.InvalidatePlayer(playerId);
            }
            else
            {
                ChatGui.PrintError($"[XRP] Failed to log purchase: {purchase.ItemName}");
            }
            return success;
        }

        if (purchase.IsMaterial && purchase.MaterialType != null)
        {
            var success = await _apiClient.CreateMaterialLogEntryAsync(new MaterialLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                MaterialType = purchase.MaterialType,
                RecipientPlayerId = playerId,
                Method = "purchase",
                Notes = "Auto-logged via Dalamud plugin",
            });

            if (success)
            {
                ChatGui.Print($"[XRP] Material purchase logged: {purchase.ItemName}");
            }
            else
            {
                ChatGui.PrintError($"[XRP] Failed to log material purchase: {purchase.ItemName}");
            }
            return success;
        }

        return false;
    }

    // ==================== Loot Logging ====================

    private void OnManualLog(string playerId, string slot, string playerName, string? slotAugmented)
    {
        var floorName = _territoryService.CurrentFloorName ?? "Unknown";
        Log.Information($"Manual log requested: {slot} -> {playerName} (floor={floorName}, player={playerId}, slotAugmented={slotAugmented ?? "none"})");
        ChatGui.Print($"[XRP] Logging {slot} -> {playerName}...");

        Task.Run(async () =>
        {
            try
            {
                var weekData = await _apiClient.GetCurrentWeekAsync();
                var week = weekData?.CurrentWeek ?? 1;

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
                    Log.Information($"Manual log success: {slot} -> {playerName}");
                    Framework.RunOnFrameworkThread(() =>
                    {
                        ChatGui.Print($"[XRP] Logged {slot} -> {playerName}");
                        _overlayWindow.MarkAsLogged(playerId, slot, playerName);
                    });
                    await RefreshPriority();
                }
                else
                {
                    Log.Error($"Manual log failed: {slot} -> {playerName}");
                    Framework.RunOnFrameworkThread(() =>
                    {
                        ChatGui.PrintError($"[XRP] Failed to log {slot} -> {playerName}");
                        _overlayWindow.MarkLogFailed(slot, playerName);
                    });
                }
            }
            catch (System.Exception ex)
            {
                Log.Error($"Manual log exception: {ex}");
                ChatGui.PrintError($"[XRP] Error: {ex.Message}");
            }
        });
    }

    private void OnLootConfirmed(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber, string? slotAugmented)
    {
        Task.Run(async () =>
        {
            await LogLootAsync(playerId, gearSlot, materialType, floorName, weekNumber, slotAugmented);

            // Refresh priority after logging
            await RefreshPriority();
        });
    }

    private async Task<bool> LogLootAsync(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber, string? slotAugmented = null)
    {
        if (materialType != null)
        {
            return await _apiClient.CreateMaterialLogEntryAsync(new MaterialLogCreateRequest
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
        }

        if (gearSlot != null)
        {
            return await _apiClient.CreateLootLogEntryAsync(new LootLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                ItemSlot = gearSlot,
                RecipientPlayerId = playerId,
                Method = "drop",
                Notes = "Logged via Dalamud plugin",
                MarkAcquired = true,
            });
        }

        return false;
    }

    private void OnMarkFloorCleared()
    {
        if (_cachedPriority == null) return;

        var floorName = _territoryService.CurrentFloorName ?? "Unknown";
        var playerIds = _cachedPriority.Players.ConvertAll(p => p.Id);

        Task.Run(async () =>
        {
            var weekData = await _apiClient.GetCurrentWeekAsync();
            var week = weekData?.CurrentWeek ?? 1;

            var success = await _apiClient.MarkFloorClearedAsync(new MarkFloorClearedRequest
            {
                WeekNumber = week,
                Floor = floorName,
                PlayerIds = playerIds,
                Notes = "Logged via Dalamud plugin",
            });

            if (success)
            {
                Framework.RunOnFrameworkThread(() => _overlayWindow.MarkFloorCleared());
                Log.Information($"Marked {floorName} cleared for {playerIds.Count} players");
            }
        });
    }

    private void OnRefreshRequested()
    {
        Task.Run(async () =>
        {
            Log.Information("Manual refresh requested");
            await RefreshPriority();

            if (_cachedPriority != null)
            {
                // Re-run party matching with fresh data
                _partyMatching.MatchParty(_cachedPriority.Players);
                _overlayWindow.ShowStatus("Priority data refreshed", new System.Numerics.Vector4(0.133f, 0.773f, 0.369f, 1f));
            }
        });
    }

    /// <summary>Log loot entries for newly acquired BiS items detected during gear sync.</summary>
    private async Task LogNewAcquisitionsAsync(string playerId, List<string> slots)
    {
        try
        {
            var weekData = await _apiClient.GetCurrentWeekAsync();
            var week = weekData?.CurrentWeek ?? 1;
            var slotToFloor = BuildSlotToFloorMapping();
            var logged = 0;

            foreach (var slot in slots)
            {
                var floor = slotToFloor.GetValueOrDefault(slot,
                    _territoryService.CurrentFloorName ?? "M9S");
                var logSuccess = await _apiClient.CreatePurchaseLogEntryAsync(new LootLogCreateRequest
                {
                    WeekNumber = week,
                    Floor = floor,
                    ItemSlot = slot,
                    RecipientPlayerId = playerId,
                    Method = "purchase",
                    Notes = "Synced via Dalamud plugin",
                    MarkAcquired = true,
                });
                if (logSuccess) logged++;
            }

            if (logged > 0)
                ChatGui.Print($"[XRP] Logged {logged} gear acquisition(s).");
        }
        catch (System.Exception ex)
        {
            Log.Warning($"Failed to log acquisitions: {ex.Message}");
        }
    }

    /// <summary>Build a mapping of gear slot → floor name from cached priority data.</summary>
    private Dictionary<string, string> BuildSlotToFloorMapping()
    {
        var mapping = new Dictionary<string, string>();
        if (_cachedPriority == null) return mapping;

        for (var f = 0; f < _cachedPriority.TierFloors.Count; f++)
        {
            var floorKey = $"floor{f + 1}";
            if (_cachedPriority.Priority.TryGetValue(floorKey, out var floorData))
            {
                foreach (var slotKey in floorData.Keys)
                {
                    // Use highest floor (later floors override) since BiS drops from later floors
                    mapping[slotKey] = _cachedPriority.TierFloors[f];
                }
            }
        }
        return mapping;
    }

    private async Task RefreshPriority()
    {
        if (_territoryService.CurrentFloor == null) return;

        var floor = _territoryService.CurrentFloor.Value;
        _cachedPriority = await _apiClient.GetPriorityAsync(floor);

        if (_cachedPriority != null)
        {
            var floorName = floor <= _cachedPriority.TierFloors.Count
                ? _cachedPriority.TierFloors[floor - 1]
                : $"Floor {floor}";
            _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, Configuration.DefaultGroupName, Configuration.DefaultTierName);
        }
    }
}
