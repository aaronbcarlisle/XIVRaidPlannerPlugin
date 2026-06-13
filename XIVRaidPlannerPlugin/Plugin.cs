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
    private readonly GearsetService _gearsetService;
    private readonly GearSyncService _gearSync;
    private readonly MountFarmService _mountFarm;

    // Windows
    public readonly WindowSystem WindowSystem = new("XIVRaidPlannerPlugin");
    private readonly ConfigWindow _configWindow;
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly LootConfirmationWindow _lootConfirmWindow;
    private readonly LeaveWarningWindow _leaveWarningWindow;
    private readonly BiSViewerWindow _bisViewerWindow;
    private CharacterSyncOverlay? _characterSyncOverlay;

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
        _configWindow = new ConfigWindow(Configuration, _apiClient, _partyMatching, PartyList, PlayerState, _thread, _browserAuth, _bisData);
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

        // Construct gearset service (reads saved gearsets from game memory)
        _gearsetService = new GearsetService(DataManager, _inventoryService, Log);

        // Construct gear sync service (depends on loot coordinator for new-acquisition logging)
        _gearSync = new GearSyncService(
            _apiClient, _inventoryService, _bisData, _thread, ChatGui, PlayerState, Configuration,
            _bisViewerWindow, _lootLog, () => _raidSession.GetState(), Log, _gearsetService);
        _configWindow.SetGearSync(_gearSync);

        // Mount farm sync service
        _mountFarm = new MountFarmService(_apiClient, _thread, PlayerState, ChatGui, Configuration, Log);

        _characterSyncOverlay = new CharacterSyncOverlay(_gearSync, _mountFarm, Configuration, GameGui, ToggleConfigUi);

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
        _bisViewerWindow.OnRefreshRequested += OnBisRefreshRequested;
        // When the local character's player-link changes, drop the cached BiS gear and re-fetch
        // so the BiS window doesn't keep showing the previous player's data.
        _partyMatching.OnOverrideChanged += OnLocalPlayerLinkChanged;

        // Auto-sync mount farms on login if enabled
        ClientState.Login += OnLogin;

        // Register commands
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle BiS viewer. '/xrp sync' to sync all. '/xrp syncgear' current job. '/xrp syncgear all' all gearsets. '/xrp syncgear BRD' specific job. '/xrp mountsync' mounts. '/xrp priority' overlay. '/xrp config' settings.",
        });

        // Register UI drawing
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += _characterSyncOverlay.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        // Match the bare `/xrp` command — BiS viewer is the more useful default in town.
        PluginInterface.UiBuilder.OpenMainUi += ToggleBisViewer;

        // Check if already in a savage instance (e.g., plugin loaded mid-instance)
        _territoryService.CheckCurrentTerritory();

        Log.Information("XIV Raid Planner plugin loaded");
    }

    public void Dispose()
    {
        // Unsubscribe events
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= _characterSyncOverlay!.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleBisViewer;

        ClientState.Login -= OnLogin;
        _lootDetection.OnLootObtained -= _lootLog.OnLootObtained;
        _lootDetection.OnItemPurchased -= _lootLog.OnItemPurchased;
        _overlayWindow.OnManualLog -= _lootLog.OnManualLog;
        _overlayWindow.OnMarkFloorCleared -= _lootLog.OnMarkFloorCleared;
        _overlayWindow.OnRefresh -= OnRefreshRequested;
        _lootConfirmWindow.OnConfirm -= _lootLog.OnLootConfirmed;
        _bisViewerWindow.OnSyncRequested -= _gearSync.Sync;
        _bisViewerWindow.OnRefreshRequested -= OnBisRefreshRequested;
        _partyMatching.OnOverrideChanged -= OnLocalPlayerLinkChanged;

        // Dispose services that own addon listeners FIRST
        // This prevents SelectYesno/other addon events from firing handlers that touch windows
        _raidSession.Dispose();
        _leaveWarning.Dispose();

        // Dispose remaining services
        _addonHighlight.Dispose();
        _territoryService.Dispose();
        _lootDetection.Dispose();
        _apiClient.Dispose();

        // Dispose overlays and windows
        _characterSyncOverlay?.Dispose();
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
            case "priority":
            case "overlay":
                ToggleOverlay();
                break;
            case "bis":
                ToggleBisViewer();
                break;
            case "sync":
                // Run all enabled sync modules
                _gearSync.SyncSavedGearsets();
                if (Configuration.EnableMountFarmSync)
                    _mountFarm.Sync();
                break;
            case "gearsync":
            case "gear":
                _gearSync.SyncSavedGearsets();
                break;
            case "syncgear":
            case "syncgear current":
                // /xrp syncgear  or  /xrp syncgear current  — sync current equipped job
                _gearSync.SyncProfileGear();
                break;
            case "syncgear all":
                // /xrp syncgear all — sync all saved gearsets
                _gearSync.SyncSavedGearsets();
                break;
            case "mountsync":
            case "mount":
            case "mounts":
                _mountFarm.Sync();
                break;
            default:
                // /xrp syncgear BRD — sync specific job's saved gearset
                if (trimmedArgs.StartsWith("syncgear "))
                {
                    var jobArg = trimmedArgs["syncgear ".Length..].Trim().ToUpperInvariant();
                    _gearSync.SyncJobGearset(jobArg);
                    break;
                }

                // Bare /xrp opens BiS — useful in town between pulls.
                // Priority overlay still auto-opens via ShowOverlayOnEntry/OnDutyComplete/OnLootWindow.
                ToggleBisViewer();
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
                _thread.RunBackground(async () => await _bisData.FetchCurrentPlayerGearAsync(charName));
            }
        }
        _bisViewerWindow.Toggle();
    }

    private void OnLocalPlayerLinkChanged(string characterName, string? newPlayerId)
    {
        var localName = PlayerState.IsLoaded ? PlayerState.CharacterName?.ToString() : null;
        if (string.IsNullOrEmpty(localName) || localName != characterName) return;

        var oldPlayerId = _bisData.CurrentPlayerGear?.PlayerId;
        if (oldPlayerId != null)
            _bisData.InvalidatePlayer(oldPlayerId);

        // If the override was cleared (None), don't auto-fetch — let the user open BiS to retry.
        if (newPlayerId == null) return;

        _thread.RunBackground(async () =>
        {
            await _bisData.FetchCurrentPlayerGearAsync(localName);
            // Surface fetch failures on the Players tab — without this, a 401/network
            // error from a Players-tab dropdown change is only visible the next time
            // the user opens the BiS window, with no connection back to what they did.
            if (!string.IsNullOrEmpty(_bisData.LastError))
                _configWindow.ShowPlayerLinkStatus($"Linked, but couldn't load BiS gear: {_bisData.LastError}", Theme.Warning);
        });
    }

    /// <summary>
    /// Refresh BiS gear data on viewer open and on the Refresh button. Fetches the
    /// player ID we were last viewing (so dropdown selections persist across reopens),
    /// or the current player if none was selected. Decides isCurrent based on the
    /// local character's planner-side link rather than the cached CurrentPlayerGear,
    /// because the latter is null between refreshes when the cache is invalidated.
    /// </summary>
    private void OnBisRefreshRequested(string? playerId)
    {
        _thread.RunBackground(async () =>
        {
            var localName = PlayerState.IsLoaded ? PlayerState.CharacterName?.ToString() : null;
            var localLinkedId = !string.IsNullOrEmpty(localName)
                ? _partyMatching.GetPlayerIdForName(localName)
                : null;

            if (!string.IsNullOrEmpty(playerId))
            {
                var isCurrent = !string.IsNullOrEmpty(localLinkedId) && playerId == localLinkedId;
                await _bisData.FetchPlayerGearAsync(playerId, isCurrentPlayer: isCurrent, forceRefresh: true);
            }
            else if (!string.IsNullOrEmpty(localLinkedId))
            {
                // Bare refresh with no remembered viewed player — refresh the local user.
                await _bisData.FetchPlayerGearAsync(localLinkedId, isCurrentPlayer: true, forceRefresh: true);
            }
            else if (!string.IsNullOrEmpty(localName))
            {
                await _bisData.FetchCurrentPlayerGearAsync(localName);
            }
        });
    }

    private void OnLogin()
    {
        if (!Configuration.AutoSyncMountFarms || !Configuration.EnableMountFarmSync)
            return;
        if (string.IsNullOrEmpty(Configuration.ApiKey))
            return;
        // Delay to allow game state to fully initialize before reading inventory
        _thread.RunBackground(async () =>
        {
            await System.Threading.Tasks.Task.Delay(5000);
            Log.Information("[MountFarm] Auto-sync triggered on login");
            await _mountFarm.AutoSyncAsync();
        });
    }

    private void OnRefreshRequested()
    {
        _thread.RunBackground(async () =>
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
