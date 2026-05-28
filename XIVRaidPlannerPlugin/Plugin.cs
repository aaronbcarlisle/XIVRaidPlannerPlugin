using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Auth;
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
    private readonly BrowserAuthService _browserAuth;
    private readonly TerritoryService _territoryService;
    private readonly PartyMatchingService _partyMatching;
    private readonly LootDetectionService _lootDetection;
    private readonly LeaveWarningService _leaveWarning;
    private readonly ItemMappingService _itemMapping;
    private readonly BiSDataService _bisData;
    private readonly InventoryService _inventoryService;
    private readonly AddonHighlightService _addonHighlight;
    private readonly LootLogCoordinator _lootLog;
    private readonly RaidSessionService _raidSession;
    private readonly GearSyncService _gearSync;

    // Windows
    public readonly WindowSystem WindowSystem = new("XIVRaidPlannerPlugin");
    private readonly ConfigWindow _configWindow;
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly LootConfirmationWindow _lootConfirmWindow;
    private readonly LeaveWarningWindow _leaveWarningWindow;
    private readonly BiSViewerWindow _bisViewerWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Initialize services
        _apiClient = new RaidPlannerClient(Configuration, Log);
        _thread = new PluginThread(Framework, Log);
        _browserAuth = new BrowserAuthService(Configuration, _apiClient, Log);
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
        _configWindow = new ConfigWindow(Configuration, _apiClient, _partyMatching, PartyList, PlayerState, _thread, _browserAuth);
        _overlayWindow = new PriorityOverlayWindow(Configuration, TextureProvider);
        _lootConfirmWindow = new LootConfirmationWindow();
        _leaveWarningWindow = new LeaveWarningWindow(_leaveWarning, GameGui);
        _bisViewerWindow = new BiSViewerWindow(_bisData, _inventoryService, Configuration, DataManager, TextureProvider);

        WindowSystem.AddWindow(_configWindow);
        WindowSystem.AddWindow(_overlayWindow);
        WindowSystem.AddWindow(_lootConfirmWindow);
        WindowSystem.AddWindow(_leaveWarningWindow);
        WindowSystem.AddWindow(_bisViewerWindow);

        // Construct raid session service (owns priority cache + savage events + DutyComplete/NeedGreed)
        _raidSession = new RaidSessionService(
            _apiClient, _territoryService, _partyMatching, _bisData, _lootDetection, _thread,
            ChatGui, PlayerState, Configuration, Log,
            _overlayWindow, _bisViewerWindow, _configWindow, _leaveWarningWindow, AddonLifecycle);

        // Construct loot coordinator (depends on raid session for priority + refresh)
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
            () => _territoryService.CurrentFloorName,
            () => _raidSession.CachedPriority,
            () => _raidSession.RefreshPriority(),
            _overlayWindow,
            _lootConfirmWindow);

        // Construct gear sync service (depends on loot coordinator for new-acquisition logging)
        _gearSync = new GearSyncService(
            _apiClient, _inventoryService, _bisData, _thread, ChatGui, PlayerState, Configuration,
            _bisViewerWindow, _lootLog, () => _raidSession.GetState(), Log);

        // Initialize leave-warning addon listener now that windows + session state are available
        _leaveWarning.Initialize(
            AddonLifecycle, PlayerState, _partyMatching, _lootDetection,
            _overlayWindow, _leaveWarningWindow, () => _raidSession.GetState());

        // Wire up events
        _lootDetection.OnLootObtained += _lootLog.OnLootObtained;
        _lootDetection.OnItemPurchased += _lootLog.OnItemPurchased;
        _overlayWindow.OnManualLog += _lootLog.OnManualLog;
        _overlayWindow.OnMarkFloorCleared += _lootLog.OnMarkFloorCleared;
        _overlayWindow.OnRefresh += OnRefreshRequested;
        _lootConfirmWindow.OnConfirm += _lootLog.OnLootConfirmed;
        _bisViewerWindow.OnSyncRequested += _gearSync.Sync;

        // Register commands
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle priority overlay. '/xrp bis' for BiS viewer. '/xrp sync' to sync gear. '/xrp config' for settings.",
        });

        // Register UI drawing
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleOverlay;

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

        _lootDetection.OnLootObtained -= _lootLog.OnLootObtained;
        _lootDetection.OnItemPurchased -= _lootLog.OnItemPurchased;
        _overlayWindow.OnManualLog -= _lootLog.OnManualLog;
        _overlayWindow.OnMarkFloorCleared -= _lootLog.OnMarkFloorCleared;
        _overlayWindow.OnRefresh -= OnRefreshRequested;
        _lootConfirmWindow.OnConfirm -= _lootLog.OnLootConfirmed;
        _bisViewerWindow.OnSyncRequested -= _gearSync.Sync;

        // Dispose services that own addon listeners FIRST
        // This prevents SelectYesno/other addon events from firing handlers that touch windows
        _raidSession.Dispose();
        _leaveWarning.Dispose();

        // Dispose remaining services
        _addonHighlight.Dispose();
        _territoryService.Dispose();
        _lootDetection.Dispose();
        _apiClient.Dispose();

        // Dispose windows
        WindowSystem.RemoveAllWindows();
        _configWindow.Dispose();
        _overlayWindow.Dispose();
        _lootConfirmWindow.Dispose();
        _leaveWarningWindow.Dispose();
        _bisViewerWindow.Dispose();

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
                _gearSync.Sync();
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

    private void OnRefreshRequested()
    {
        Task.Run(async () =>
        {
            Log.Information("Manual refresh requested");
            await _raidSession.RefreshPriority();

            var cached = _raidSession.CachedPriority;
            if (cached != null)
            {
                // Re-run party matching with fresh data
                _partyMatching.MatchParty(cached.Players);
                _overlayWindow.ShowStatus("Priority data refreshed", new System.Numerics.Vector4(0.133f, 0.773f, 0.369f, 1f));
            }
        });
    }
}
