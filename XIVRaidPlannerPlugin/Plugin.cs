using System.Threading.Tasks;
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
        _configWindow = new ConfigWindow(Configuration, _apiClient, _partyMatching);
        _overlayWindow = new PriorityOverlayWindow();
        _lootConfirmWindow = new LootConfirmationWindow();
        _leaveWarningWindow = new LeaveWarningWindow(_leaveWarning);

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

        Log.Information("XIV Raid Planner plugin loaded");
    }

    public void Dispose()
    {
        // Unsubscribe events
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleOverlay;

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

                _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName);
                _overlayWindow.IsOpen = true;

                // Match party members to planner players
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
        Task.Run(async () =>
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

            await LogLootAsync(playerId, gearSlot, materialType, floorName, week);
            Log.Information($"Manual log: {slot} -> {playerName}");

            // Refresh priority after logging
            await RefreshPriority();
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

    private async Task LogLootAsync(string playerId, string? gearSlot, string? materialType, string floorName, int weekNumber)
    {
        if (materialType != null)
        {
            await _apiClient.CreateMaterialLogEntryAsync(new MaterialLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                MaterialType = materialType,
                RecipientPlayerId = playerId,
                Method = "drop",
                Notes = "Logged via Dalamud plugin",
            });
        }
        else if (gearSlot != null)
        {
            await _apiClient.CreateLootLogEntryAsync(new LootLogCreateRequest
            {
                WeekNumber = weekNumber,
                Floor = floorName,
                ItemSlot = gearSlot,
                RecipientPlayerId = playerId,
                Method = "drop",
                Notes = "Logged via Dalamud plugin",
            });
        }
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

            await _apiClient.MarkFloorClearedAsync(new MarkFloorClearedRequest
            {
                WeekNumber = week,
                Floor = floorName,
                PlayerIds = playerIds,
                Notes = "Logged via Dalamud plugin",
            });

            Log.Information($"Marked {floorName} cleared for {playerIds.Count} players");
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
            _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName);
        }
    }
}
