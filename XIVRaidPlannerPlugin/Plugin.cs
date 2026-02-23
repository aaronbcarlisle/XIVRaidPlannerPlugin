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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/xrp";

    // Configuration
    public Configuration Configuration { get; init; }

    // Services
    private readonly RaidPlannerClient _apiClient;
    private readonly TerritoryService _territoryService;
    private readonly PartyMatchingService _partyMatching;
    private readonly LootDetectionService _lootDetection;
    private readonly LeaveWarningService _leaveWarning;

    // Windows
    public readonly WindowSystem WindowSystem = new("XIVRaidPlannerPlugin");
    private readonly ConfigWindow _configWindow;
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly LootConfirmationWindow _lootConfirmWindow;
    private readonly LeaveWarningWindow _leaveWarningWindow;

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

        // Initialize windows
        _configWindow = new ConfigWindow(Configuration, _apiClient, _partyMatching, PartyList, PlayerState);
        _overlayWindow = new PriorityOverlayWindow();
        _lootConfirmWindow = new LootConfirmationWindow();
        _leaveWarningWindow = new LeaveWarningWindow(_leaveWarning, GameGui);

        WindowSystem.AddWindow(_configWindow);
        WindowSystem.AddWindow(_overlayWindow);
        WindowSystem.AddWindow(_lootConfirmWindow);
        WindowSystem.AddWindow(_leaveWarningWindow);

        // Wire up events
        _territoryService.OnSavageEntered += OnSavageEntered;
        _territoryService.OnSavageExited += OnSavageExited;
        _lootDetection.OnLootObtained += OnLootObtained;
        _overlayWindow.OnManualLog += OnManualLog;
        _overlayWindow.OnMarkFloorCleared += OnMarkFloorCleared;
        _lootConfirmWindow.OnConfirm += OnLootConfirmed;

        // Register commands
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle priority overlay. Use '/xrp config' to open settings.",
        });

        // Register UI drawing
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleOverlay;

        // Hook into the "Abandon duty?" confirmation dialog for leave warnings
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);

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

        _territoryService.OnSavageEntered -= OnSavageEntered;
        _territoryService.OnSavageExited -= OnSavageExited;
        _lootDetection.OnLootObtained -= OnLootObtained;
        _overlayWindow.OnManualLog -= OnManualLog;
        _overlayWindow.OnMarkFloorCleared -= OnMarkFloorCleared;
        _lootConfirmWindow.OnConfirm -= OnLootConfirmed;

        // Dispose windows
        WindowSystem.RemoveAllWindows();
        _configWindow.Dispose();
        _overlayWindow.Dispose();
        _lootConfirmWindow.Dispose();
        _leaveWarningWindow.Dispose();

        // Dispose services
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
            default:
                ToggleOverlay();
                break;
        }
    }

    public void ToggleConfigUi() => _configWindow.Toggle();
    public void ToggleOverlay() => _overlayWindow.Toggle();

    // ==================== Territory Events ====================

    private void OnSavageEntered(int floor)
    {
        if (!Configuration.ShowOverlay || string.IsNullOrEmpty(Configuration.ApiKey))
            return;

        _lootDetection.Reset();

        // Fetch priority data
        Task.Run(async () =>
        {
            _cachedPriority = await _apiClient.GetPriorityAsync(floor);
            if (_cachedPriority != null)
            {
                var floorName = floor <= _cachedPriority.TierFloors.Count
                    ? _cachedPriority.TierFloors[floor - 1]
                    : $"Floor {floor}";

                _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, Configuration.DefaultGroupName);
                _overlayWindow.IsOpen = true;

                // Share player list with config window and run matching
                _configWindow.SetStaticPlayers(_cachedPriority.Players);
                _partyMatching.MatchParty(_cachedPriority.Players);
            }
        });
    }

    private void OnSavageExited()
    {
        _overlayWindow.ClearData();
        _overlayWindow.IsOpen = false;
        _leaveWarningWindow.IsOpen = false;
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

        switch (Configuration.AutoLogMode)
        {
            case AutoLogMode.Confirm:
                Task.Run(async () =>
                {
                    var weekData = await _apiClient.GetCurrentWeekAsync();
                    var week = weekData?.CurrentWeek ?? 1;
                    _lootConfirmWindow.ShowForLoot(loot, playerId, loot.PlayerName, floorName, week);
                });
                break;

            case AutoLogMode.Auto:
                Task.Run(async () =>
                {
                    var weekData = await _apiClient.GetCurrentWeekAsync();
                    var week = weekData?.CurrentWeek ?? 1;
                    await LogLootAsync(playerId, loot.GearSlot, loot.MaterialType, floorName, week);
                    Log.Information($"Auto-logged: {loot.ItemName} -> {loot.PlayerName}");
                });
                break;

            case AutoLogMode.Manual:
                // Do nothing - user must use overlay buttons
                break;
        }
    }

    // ==================== Loot Logging ====================

    private void OnManualLog(string playerId, string slot, string playerName)
    {
        var floorName = _territoryService.CurrentFloorName ?? "Unknown";
        Log.Information($"Manual log requested: {slot} -> {playerName} (floor={floorName}, player={playerId})");
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

                var success = await LogLootAsync(playerId, gearSlot, materialType, floorName, week);
                if (success)
                {
                    Log.Information($"Manual log success: {slot} -> {playerName}");
                    ChatGui.Print($"[XRP] Logged {slot} -> {playerName}");
                    _overlayWindow.MarkAsLogged(playerId, slot, playerName);
                    await RefreshPriority();
                }
                else
                {
                    Log.Error($"Manual log failed: {slot} -> {playerName}");
                    ChatGui.PrintError($"[XRP] Failed to log {slot} -> {playerName}");
                    _overlayWindow.MarkLogFailed(slot, playerName);
                }
            }
            catch (System.Exception ex)
            {
                Log.Error($"Manual log exception: {ex}");
                ChatGui.PrintError($"[XRP] Error: {ex.Message}");
            }
        });
    }

    private void OnLootConfirmed(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber)
    {
        Task.Run(async () =>
        {
            await LogLootAsync(playerId, gearSlot, materialType, floorName, weekNumber);

            // Refresh priority after logging
            await RefreshPriority();
        });
    }

    private async Task<bool> LogLootAsync(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber)
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
                _overlayWindow.MarkFloorCleared();
                Log.Information($"Marked {floorName} cleared for {playerIds.Count} players");
            }
        });
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
            _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, Configuration.DefaultGroupName);
        }
    }
}
