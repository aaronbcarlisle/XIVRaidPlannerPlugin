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
    private readonly PluginThread _thread;
    private readonly TerritoryService _territoryService;
    private readonly PartyMatchingService _partyMatching;
    private readonly LootDetectionService _lootDetection;
    private readonly LeaveWarningService _leaveWarning;
    private readonly ItemMappingService _itemMapping;
    private readonly BiSDataService _bisData;
    private readonly InventoryService _inventoryService;
    private readonly AddonHighlightService _addonHighlight;
    private readonly LootLogCoordinator _lootLog;

    // Windows
    public readonly WindowSystem WindowSystem = new("XIVRaidPlannerPlugin");
    private readonly ConfigWindow _configWindow;
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly LootConfirmationWindow _lootConfirmWindow;
    private readonly LeaveWarningWindow _leaveWarningWindow;
    private readonly BiSViewerWindow _bisViewerWindow;

    // Cached priority data
    private PriorityResponse? _cachedPriority;
    private bool _autoDetectedTier;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Initialize services
        _apiClient = new RaidPlannerClient(Configuration, Log);
        _thread = new PluginThread(Framework, Log);
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

        // Construct loot coordinator (after windows so they can be injected)
        _lootLog = new LootLogCoordinator(
            _apiClient,
            _thread,
            ChatGui,
            _itemMapping,
            _bisData,
            PlayerState,
            _partyMatching,
            Log,
            Configuration,
            () => _territoryService.CurrentFloor,
            () => _territoryService.CurrentFloorName,
            () => _cachedPriority,
            async () => await RefreshPriority(), // TODO Task 12: replace with direct RaidSessionService dep when extracted
            _overlayWindow,
            _lootConfirmWindow);

        // Wire up events
        _territoryService.OnSavageEntered += OnSavageEntered;
        _territoryService.OnSavageExited += OnSavageExited;
        _lootDetection.OnLootObtained += _lootLog.OnLootObtained;
        _lootDetection.OnItemPurchased += _lootLog.OnItemPurchased;
        _overlayWindow.OnManualLog += _lootLog.OnManualLog;
        _overlayWindow.OnMarkFloorCleared += _lootLog.OnMarkFloorCleared;
        _overlayWindow.OnRefresh += OnRefreshRequested;
        _lootConfirmWindow.OnConfirm += _lootLog.OnLootConfirmed;
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
        _lootDetection.OnLootObtained -= _lootLog.OnLootObtained;
        _lootDetection.OnItemPurchased -= _lootLog.OnItemPurchased;
        _overlayWindow.OnManualLog -= _lootLog.OnManualLog;
        _overlayWindow.OnMarkFloorCleared -= _lootLog.OnMarkFloorCleared;
        _overlayWindow.OnRefresh -= OnRefreshRequested;
        _lootConfirmWindow.OnConfirm -= _lootLog.OnLootConfirmed;
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
            var charName = PlayerState.IsLoaded ? PlayerState.CharacterName?.ToString() : null;
            if (!string.IsNullOrEmpty(charName) && _bisData.CurrentPlayerGear == null)
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

        // Read equipped items — safe: called from command handler or Draw() button, both on framework thread
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
            var syncResult = await _apiClient.SyncPlayerGearAsync(
                freshGear.PlayerId,
                new SnapshotPlayerUpdateRequest { Gear = updatedGear, TomeWeapon = tomeWeaponUpdate });

            if (syncResult.IsSuccess)
            {
                // Invalidate cache before re-fetch (safe from background thread — ConcurrentDictionary)
                _bisData.InvalidatePlayer(freshGear.PlayerId);

                // Marshal UI updates to framework thread
                Framework.RunOnFrameworkThread(() =>
                {
                    ChatGui.Print($"[XRP] Gear synced: {changes} slot(s) updated.");
                    _bisViewerWindow.InvalidateEquippedGear();
                });

                // Auto-log loot entries only when in a savage instance with reliable floor data
                if (newlyAcquired.Count > 0 && _cachedPriority != null && _territoryService.CurrentFloor != null)
                {
                    try { await _lootLog.LogNewAcquisitionsAsync(freshGear.PlayerId, newlyAcquired); }
                    catch (System.Exception ex) { Log.Warning($"Loot logging failed (non-critical): {ex.Message}"); }
                }
                else if (newlyAcquired.Count > 0)
                {
                    Framework.RunOnFrameworkThread(() =>
                        ChatGui.Print($"[XRP] {newlyAcquired.Count} new BiS item(s) detected. Enter a savage instance to auto-log."));
                }

                // Re-fetch to update the BiS viewer (cache was invalidated above)
                var charName = PlayerState.IsLoaded ? PlayerState.CharacterName?.ToString() : null;
                if (!string.IsNullOrEmpty(charName))
                    await _bisData.FetchCurrentPlayerGearAsync(charName);
            }
            else
            {
                var errMsg = syncResult.Error == ApiError.Unauthorized
                    ? "[XRP] API key rejected — re-authorize via /xrp config"
                    : "[XRP] Failed to sync gear. Check connection.";
                Framework.RunOnFrameworkThread(() => ChatGui.PrintError(errMsg));
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
            // Set transiently on config (no Save) so display shows tier name; cleared on instance exit
            if (!string.IsNullOrEmpty(Configuration.DefaultGroupId) && string.IsNullOrEmpty(Configuration.DefaultTierId))
            {
                var tierResult = await _apiClient.ResolveActiveTierAsync(Configuration.DefaultGroupId);
                if (tierResult.IsSuccess)
                {
                    var activeTier = tierResult.Value!;
                    Log.Information($"Auto-detected active tier: {activeTier.TierId} ({activeTier.Id})");
                    Configuration.DefaultTierId = activeTier.Id;
                    Configuration.DefaultTierName = activeTier.TierId;
                    _autoDetectedTier = true;
                }
                else
                {
                    Log.Warning("No active tier found and no tier configured. Overlay will not show.");
                    Framework.RunOnFrameworkThread(() =>
                        ChatGui.PrintError("[XRP] No active tier found. Select a tier in /xrp config."));
                    return;
                }
            }

            if (string.IsNullOrEmpty(Configuration.DefaultTierId))
                return;

            var priorityResult = await _apiClient.GetPriorityAsync(floor);
            if (!priorityResult.IsSuccess)
            {
                var msg = priorityResult.Error == ApiError.Unauthorized
                    ? "[XRP] API key rejected — re-authorize via /xrp config."
                    : "[XRP] Couldn't fetch priority data. Check connection and try /xrp config.";
                Framework.RunOnFrameworkThread(() => ChatGui.PrintError(msg));
                return;
            }

            _cachedPriority = priorityResult.Value;
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
                var groupsResult = await _apiClient.GetStaticGroupsAsync();
                if (groupsResult.IsSuccess)
                {
                    var group = groupsResult.Value!.Find(g => g.Id == Configuration.DefaultGroupId);
                    if (group?.UserRole != null)
                        _bisData.UserRole = group.UserRole;
                }

                var charName = PlayerState.IsLoaded ? PlayerState.CharacterName?.ToString() : null;
                if (!string.IsNullOrEmpty(charName))
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
        _apiClient.InvalidateResolvedTier();

        // Clear auto-detected tier so it re-detects on next entry
        if (_autoDetectedTier)
        {
            Configuration.DefaultTierId = string.Empty;
            Configuration.DefaultTierName = string.Empty;
            _autoDetectedTier = false;
        }
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
        var charName = PlayerState.IsLoaded ? PlayerState.CharacterName?.ToString() : null;
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

    private async Task RefreshPriority()
    {
        if (_territoryService.CurrentFloor == null) return;

        var floor = _territoryService.CurrentFloor.Value;
        var priorityResult = await _apiClient.GetPriorityAsync(floor);
        if (!priorityResult.IsSuccess)
        {
            var msg = priorityResult.Error == ApiError.Unauthorized
                ? "[XRP] API key rejected — re-authorize via /xrp config."
                : "[XRP] Couldn't fetch priority data. Check connection and try /xrp config.";
            Framework.RunOnFrameworkThread(() => ChatGui.PrintError(msg));
            return;
        }

        _cachedPriority = priorityResult.Value;
        if (_cachedPriority != null)
        {
            var floorName = floor <= _cachedPriority.TierFloors.Count
                ? _cachedPriority.TierFloors[floor - 1]
                : $"Floor {floor}";
            _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, Configuration.DefaultGroupName, Configuration.DefaultTierName);
        }
    }
}
