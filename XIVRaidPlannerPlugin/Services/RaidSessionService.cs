using System;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Windows;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Snapshot of the active raid-session state exposed to other services
/// (current floor + cached priority response).
/// </summary>
public readonly record struct RaidSessionState(int? CurrentFloor, PriorityResponse? CachedPriority);

/// <summary>
/// Owns raid-session lifecycle: priority fetch, party match, auto-tier detection,
/// and the addon-timing decisions for showing the overlay on DutyComplete / NeedGreed.
/// Extracted from Plugin.cs.
/// </summary>
public sealed class RaidSessionService : IDisposable
{
    private readonly RaidPlannerClient _api;
    private readonly TerritoryService _territory;
    private readonly PartyMatchingService _partyMatching;
    private readonly BiSDataService _bisData;
    private readonly LootDetectionService _lootDetection;
    private readonly PluginThread _thread;
    private readonly IChatGui _chat;
    private readonly IPlayerState _playerState;
    private readonly Configuration _config;
    private readonly IPluginLog _log;
    private readonly PriorityOverlayWindow _overlayWindow;
    private readonly BiSViewerWindow _bisViewerWindow;
    private readonly ConfigWindow _configWindow;
    private readonly LeaveWarningWindow _leaveWarningWindow;
    private readonly IAddonLifecycle _addonLifecycle;

    private PriorityResponse? _cachedPriority;
    private bool _autoDetectedTier;

    public PriorityResponse? CachedPriority => _cachedPriority;

    public RaidSessionService(
        RaidPlannerClient api,
        TerritoryService territory,
        PartyMatchingService partyMatching,
        BiSDataService bisData,
        LootDetectionService lootDetection,
        PluginThread thread,
        IChatGui chat,
        IPlayerState playerState,
        Configuration config,
        IPluginLog log,
        PriorityOverlayWindow overlayWindow,
        BiSViewerWindow bisViewerWindow,
        ConfigWindow configWindow,
        LeaveWarningWindow leaveWarningWindow,
        IAddonLifecycle addonLifecycle)
    {
        _api = api;
        _territory = territory;
        _partyMatching = partyMatching;
        _bisData = bisData;
        _lootDetection = lootDetection;
        _thread = thread;
        _chat = chat;
        _playerState = playerState;
        _config = config;
        _log = log;
        _overlayWindow = overlayWindow;
        _bisViewerWindow = bisViewerWindow;
        _configWindow = configWindow;
        _leaveWarningWindow = leaveWarningWindow;
        _addonLifecycle = addonLifecycle;

        _territory.OnSavageEntered += OnSavageEntered;
        _territory.OnSavageExited += OnSavageExited;
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "DutyComplete", OnDutyCompleteSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "NeedGreed", OnNeedGreedSetup);
    }

    /// <summary>Snapshot of the current floor + cached priority for consumers.</summary>
    public RaidSessionState GetState() => new(_territory.CurrentFloor, _cachedPriority);

    public void Dispose()
    {
        _territory.OnSavageEntered -= OnSavageEntered;
        _territory.OnSavageExited -= OnSavageExited;
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "DutyComplete", OnDutyCompleteSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "NeedGreed", OnNeedGreedSetup);
    }

    // ==================== Territory Events ====================

    private void OnSavageEntered(int floor)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            return;

        _lootDetection.Reset();

        // Fetch priority data (always fetch, even if overlay is hidden, so data is ready)
        _thread.RunBackground(async () =>
        {
            // Auto-detect active tier only if none is configured
            // Set transiently on config (no Save) so display shows tier name; cleared on instance exit
            if (!string.IsNullOrEmpty(_config.DefaultGroupId) && string.IsNullOrEmpty(_config.DefaultTierId))
            {
                var tierResult = await _api.ResolveActiveTierAsync(_config.DefaultGroupId);
                if (tierResult.IsSuccess)
                {
                    var activeTier = tierResult.Value!;
                    _log.Information($"Auto-detected active tier: {activeTier.TierId} ({activeTier.Id})");
                    _config.DefaultTierId = activeTier.Id;
                    _config.DefaultTierName = activeTier.TierId;
                    _autoDetectedTier = true;
                }
                else
                {
                    _log.Warning("No active tier found and no tier configured. Overlay will not show.");
                    _thread.RunOnUi(() =>
                        _chat.PrintError("[XRP] No active tier found. Select a tier in /xrp config."));
                    return;
                }
            }

            if (string.IsNullOrEmpty(_config.DefaultTierId))
                return;

            var priorityResult = await _api.GetPriorityAsync(floor);
            if (!priorityResult.IsSuccess)
            {
                var msg = priorityResult.Error == ApiError.Unauthorized
                    ? "[XRP] API key rejected — re-authorize via /xrp config."
                    : "[XRP] Couldn't fetch priority data. Check connection and try /xrp config.";
                _thread.RunOnUi(() => _chat.PrintError(msg));
                return;
            }

            _cachedPriority = priorityResult.Value;
            if (_cachedPriority != null)
            {
                var floorName = floor <= _cachedPriority.TierFloors.Count
                    ? _cachedPriority.TierFloors[floor - 1]
                    : $"Floor {floor}";

                _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, _config.DefaultGroupName, _config.DefaultTierName);

                // Only show overlay if configured to show on entry
                if (_config.ShowOverlay && _config.ShowOverlayOnEntry)
                    _overlayWindow.IsOpen = true;

                // Share player list with config window and run matching
                _configWindow.SetStaticPlayers(_cachedPriority.Players);
                _partyMatching.MatchParty(_cachedPriority.Players);

                // Fetch BiS data for the current player
                _bisData.AvailablePlayers = _cachedPriority.Players;

                // Set user role from the static group info
                var groupsResult = await _api.GetStaticGroupsAsync();
                if (groupsResult.IsSuccess)
                {
                    var group = groupsResult.Value!.Find(g => g.Id == _config.DefaultGroupId);
                    if (group?.UserRole != null)
                        _bisData.UserRole = group.UserRole;
                }

                var charName = _playerState.IsLoaded ? _playerState.CharacterName?.ToString() : null;
                if (!string.IsNullOrEmpty(charName))
                {
                    await _bisData.FetchCurrentPlayerGearAsync(charName);

                    // Show BiS viewer if configured
                    if (_config.ShowBisViewer)
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
        _api.InvalidateResolvedTier();

        // Clear auto-detected tier so it re-detects on next entry
        if (_autoDetectedTier)
        {
            _config.DefaultTierId = string.Empty;
            _config.DefaultTierName = string.Empty;
            _autoDetectedTier = false;
        }
    }

    // ==================== Refresh ====================

    /// <summary>Re-fetch priority for the current floor and update the overlay.</summary>
    public async Task RefreshPriority()
    {
        if (_territory.CurrentFloor == null) return;

        var floor = _territory.CurrentFloor.Value;
        var priorityResult = await _api.GetPriorityAsync(floor);
        if (!priorityResult.IsSuccess)
        {
            var msg = priorityResult.Error == ApiError.Unauthorized
                ? "[XRP] API key rejected — re-authorize via /xrp config."
                : "[XRP] Couldn't fetch priority data. Check connection and try /xrp config.";
            _thread.RunOnUi(() => _chat.PrintError(msg));
            return;
        }

        _cachedPriority = priorityResult.Value;
        if (_cachedPriority != null)
        {
            var floorName = floor <= _cachedPriority.TierFloors.Count
                ? _cachedPriority.TierFloors[floor - 1]
                : $"Floor {floor}";
            _overlayWindow.SetPriorityData(_cachedPriority, floor, floorName, _config.DefaultGroupName, _config.DefaultTierName);
        }
    }

    // ==================== Overlay Timing Events ====================

    private void OnDutyCompleteSetup(AddonEvent type, AddonArgs args)
    {
        if (!_config.ShowOverlay || !_config.ShowOverlayOnDutyComplete)
            return;

        if (_cachedPriority != null && _territory.CurrentFloor != null)
        {
            _log.Information("Duty complete — showing overlay");
            _overlayWindow.IsOpen = true;
        }
    }

    private void OnNeedGreedSetup(AddonEvent type, AddonArgs args)
    {
        if (!_config.ShowOverlay || !_config.ShowOverlayOnLootWindow)
            return;

        if (_cachedPriority != null && _territory.CurrentFloor != null)
        {
            _log.Information("Loot window opened — showing overlay");
            _overlayWindow.IsOpen = true;
        }
    }
}
