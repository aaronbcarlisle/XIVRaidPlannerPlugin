using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Windows;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Owns the equipped-gear → fresh-API-gear diff and the resulting sync POST.
/// Extracted from Plugin.cs.
/// </summary>
public sealed class GearSyncService
{
    private readonly RaidPlannerClient _api;
    private readonly InventoryService _inventory;
    private readonly BiSDataService _bisData;
    private readonly PluginThread _thread;
    private readonly IChatGui _chat;
    private readonly IPlayerState _playerState;
    private readonly Configuration _config;
    private readonly BiSViewerWindow _bisViewerWindow;
    private readonly LootLogCoordinator _lootLog;
    private readonly Func<RaidSessionState> _sessionState;
    private readonly GearsetService? _gearsetService;
    private readonly IPluginLog _log;

    /// <summary>
    /// Fired on the UI thread after any sync completes (success or failure).
    /// Parameters: (isSuccess, userMessage)
    /// </summary>
    public event Action<bool, string>? SyncCompleted;

    public GearSyncService(
        RaidPlannerClient api,
        InventoryService inventory,
        BiSDataService bisData,
        PluginThread thread,
        IChatGui chat,
        IPlayerState playerState,
        Configuration config,
        BiSViewerWindow bisViewerWindow,
        LootLogCoordinator lootLog,
        Func<RaidSessionState> sessionState,
        IPluginLog log,
        GearsetService? gearsetService = null)
    {
        _api = api;
        _inventory = inventory;
        _bisData = bisData;
        _thread = thread;
        _chat = chat;
        _playerState = playerState;
        _config = config;
        _bisViewerWindow = bisViewerWindow;
        _lootLog = lootLog;
        _sessionState = sessionState;
        _gearsetService = gearsetService;
        _log = log;
    }

    private void RecordSyncResult(int jobCount, string? error)
    {
        _config.LastGearSyncAt = DateTime.UtcNow.ToString("o");
        _config.LastGearSyncJobCount = jobCount;
        _config.LastGearSyncError = error ?? string.Empty;
        _config.Save();
    }

    // ==================== Pure diff (TDD) ====================

    /// <summary>
    /// Diff updated equipped gear against fresh API gear, counting changes
    /// and detecting newly-acquired slots (now-has-item, previously-didn't).
    /// </summary>
    public static GearDiff Diff(List<GearSlotStatusDto> updated, List<GearSlotStatusDto> fresh)
    {
        var changes = 0;
        var acquired = new List<string>();
        for (var i = 0; i < updated.Count && i < fresh.Count; i++)
        {
            if (updated[i].CurrentSource != fresh[i].CurrentSource ||
                updated[i].HasItem != fresh[i].HasItem ||
                updated[i].IsAugmented != fresh[i].IsAugmented)
                changes++;
            if (updated[i].HasItem && !fresh[i].HasItem)
                acquired.Add(updated[i].Slot);
        }
        return new GearDiff { ChangeCount = changes, NewlyAcquired = acquired };
    }

    // ==================== Sync entry point ====================

    /// <summary>Sync equipped gear with the web app (called from `/xrp sync` and BiS viewer button).</summary>
    public void Sync()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _chat.PrintError("[XRP] No API key configured. Use /xrp config.");
            return;
        }

        var currentGear = _bisData.CurrentPlayerGear;
        if (currentGear == null)
        {
            _chat.PrintError("[XRP] No BiS data loaded. Enter a savage instance or use /xrp bis first.");
            return;
        }

        _chat.Print("[XRP] Syncing equipped gear...");

        // Read equipped items — safe: called from command handler or Draw() button, both on framework thread
        var equipped = _inventory.ReadEquippedGear();
        if (equipped.Count == 0)
        {
            _chat.PrintError("[XRP] Could not read equipped gear.");
            return;
        }

        // Re-fetch fresh gear data from API before comparing (web app might have changed)
        var playerId = currentGear.PlayerId;
        _bisData.InvalidatePlayer(playerId);
        _thread.RunBackground(async () =>
        {
            // Fetch latest gear state from API (in case user reset progress on web app)
            await _bisData.FetchPlayerGearAsync(playerId, isCurrentPlayer: true);
            var freshGear = _bisData.CurrentPlayerGear;
            if (freshGear == null)
            {
                _thread.RunOnUi(() => _chat.PrintError("[XRP] Failed to fetch current gear state from API."));
                return;
            }

            // Build the gear update comparing equipped items against FRESH API data
            var updatedGear = _inventory.BuildGearUpdate(equipped, freshGear.Gear);

            // Check tome weapon status
            TomeWeaponInfo? tomeWeaponUpdate = null;
            if (freshGear.TomeWeapon.Pursuing && equipped.TryGetValue("weapon", out var equippedWeapon))
            {
                var weaponSource = _inventory.ClassifySource(equippedWeapon.ItemId);
                if (weaponSource is "tome" or "tome_up")
                {
                    tomeWeaponUpdate = new TomeWeaponInfo
                    {
                        Pursuing = true,
                        HasItem = true,
                        IsAugmented = weaponSource == "tome_up",
                    };
                    _log.Info($"[Sync] Detected tome weapon: source={weaponSource}, augmented={tomeWeaponUpdate.IsAugmented}");
                }
            }

            // Diff updated vs. fresh
            var diff = Diff(updatedGear, freshGear.Gear);
            var changes = diff.ChangeCount;
            var newlyAcquired = diff.NewlyAcquired;

            // Also count tome weapon as a change
            if (tomeWeaponUpdate != null && !freshGear.TomeWeapon.HasItem)
                changes++;

            _log.Info($"[Sync] Comparison: {changes} gear changes, {newlyAcquired.Count} newly acquired, tomeWeapon={tomeWeaponUpdate != null}");

            if (changes == 0)
            {
                _thread.RunOnUi(() => _chat.Print("[XRP] Gear already up to date."));
                return;
            }

            // Send update to API
            var syncResult = await _api.SyncPlayerGearAsync(
                freshGear.PlayerId,
                new SnapshotPlayerUpdateRequest { Gear = updatedGear, TomeWeapon = tomeWeaponUpdate });

            if (syncResult.IsSuccess)
            {
                // Invalidate cache before re-fetch (safe from background thread — ConcurrentDictionary)
                _bisData.InvalidatePlayer(freshGear.PlayerId);

                // Marshal UI updates to framework thread
                _thread.RunOnUi(() =>
                {
                    _chat.Print($"[XRP] Gear synced: {changes} slot(s) updated.");
                    _bisViewerWindow.InvalidateEquippedGear();
                });

                // Auto-log loot entries only when in a savage instance with reliable floor data
                var state = _sessionState();
                if (newlyAcquired.Count > 0 && state.CachedPriority != null && state.CurrentFloor != null)
                {
                    try { await _lootLog.LogNewAcquisitionsAsync(freshGear.PlayerId, newlyAcquired); }
                    catch (Exception ex) { _log.Warning($"Loot logging failed (non-critical): {ex.Message}"); }
                }
                else if (newlyAcquired.Count > 0)
                {
                    _thread.RunOnUi(() =>
                        _chat.Print($"[XRP] {newlyAcquired.Count} new BiS item(s) detected. Enter a savage instance to auto-log."));
                }

                // Re-fetch to update the BiS viewer (cache was invalidated above)
                var charName = _playerState.IsLoaded ? _playerState.CharacterName?.ToString() : null;
                if (!string.IsNullOrEmpty(charName))
                    await _bisData.FetchCurrentPlayerGearAsync(charName);
            }
            else
            {
                var errMsg = syncResult.Error == ApiError.Unauthorized
                    ? "[XRP] API key rejected — re-authorize via /xrp config"
                    : "[XRP] Failed to sync gear. Check connection.";
                _thread.RunOnUi(() => _chat.PrintError(errMsg));
            }
        });
    }

    /// <summary>
    /// Sync all saved gearsets to the profile backend (batch multi-job sync).
    /// Falls back to current-job sync if no saved gearsets are found.
    /// </summary>
    public void SyncSavedGearsets()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _chat.PrintError("[XRP] No API key configured. Use /xrp config.");
            return;
        }

        if (_gearsetService == null)
        {
            _chat.Print("[XRP] Gearset sync not available — falling back to current gear sync.");
            SyncProfileGear();
            return;
        }

        _chat.Print("[XRP] Reading saved gearsets...");

        // Read saved gearsets on framework thread
        var gearsets = _gearsetService.ReadSavedGearsets();
        if (gearsets.Count == 0)
        {
            _chat.Print("[XRP] No saved gearsets found. Syncing currently equipped gear instead.");
            SyncProfileGear();
            return;
        }

        // Deduplicate by job (highest iLvl wins)
        var deduplicated = GearsetService.DeduplicateByJob(gearsets);

        if (!_playerState.IsLoaded)
        {
            _chat.PrintError("[XRP] Character not fully loaded yet.");
            return;
        }

        var charName = _playerState.CharacterName?.ToString();
        var charWorld = _playerState.HomeWorld.Value.Name.ToString();

        if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(charWorld))
        {
            _chat.PrintError("[XRP] Could not determine character name/world.");
            return;
        }

        _log.Info($"[GearSync] Syncing {deduplicated.Count} gearsets | char='{charName}' world='{charWorld}'");
        if (deduplicated.Count > 0)
            _log.Info($"[GearSync] First gearset: index={deduplicated[0].GearsetIndex} name='{deduplicated[0].GearsetName}' job={deduplicated[0].Job}");

        _chat.Print($"[XRP] Syncing {deduplicated.Count} saved gearset(s)...");

        _thread.RunBackground(async () =>
        {
            var request = new PluginBatchGearsetSyncRequest
            {
                CharacterName = charName,
                CharacterWorld = charWorld,
                Source = "plugin",
                PluginVersion = null,
            };

            foreach (var gs in deduplicated)
            {
                var gearSlots = new List<PluginGearsetSyncGearSlot>();
                foreach (var item in gs.Items)
                {
                    gearSlots.Add(new PluginGearsetSyncGearSlot
                    {
                        Slot = item.Slot,
                        HasItem = item.HasItem,
                        CurrentSource = item.CurrentSource,
                        IsAugmented = item.IsAugmented,
                        ItemId = item.ItemId,
                        ItemName = item.ItemName,
                        ItemLevel = item.ItemLevel,
                        ItemIcon = item.ItemIcon,
                    });
                }

                request.Gearsets.Add(new PluginGearsetEntry
                {
                    GearsetIndex = gs.GearsetIndex,
                    GearsetName = gs.GearsetName,
                    Job = gs.Job,
                    ClassJobId = gs.ClassJobId,
                    Gear = gearSlots,
                });
            }

            var result = await _api.SyncBatchGearsetsAsync(request);
            if (result.IsSuccess)
            {
                var data = result.Value!;
                var changedJobs = data.SyncedJobs.Where(j => j.GearChanged).Select(j => j.Job).ToList();
                var msg = changedJobs.Count > 0
                    ? $"[XRP] Synced {data.TotalSynced} gearset(s): {string.Join(", ", changedJobs)} updated."
                    : $"[XRP] {data.TotalSynced} gearset(s) checked — all up to date.";
                RecordSyncResult(data.TotalSynced, null);
                _thread.RunOnUi(() =>
                {
                    _chat.Print(msg);
                    SyncCompleted?.Invoke(true, msg.Replace("[XRP] ", string.Empty));
                });
            }
            else
            {
                var errMsg = result.Error switch
                {
                    ApiError.Unauthorized => "[XRP] API key rejected — re-authorize via /xrp config.",
                    ApiError.NotFound => $"[XRP] Gearset sync failed (404): character '{charName}' on '{charWorld}' not found, or API URL is wrong. Link your character on the profile page. Check the Dalamud log for details.",
                    ApiError.Server => "[XRP] Backend error during gearset sync (500). Check the server logs.",
                    ApiError.Unknown => $"[XRP] Gearset sync rejected by server (422). Your character may not be linked, or a payload field is invalid. Check the Dalamud log for details.",
                    _ => "[XRP] Gearset sync failed — network error. Check your API URL and connection.",
                };
                RecordSyncResult(0, errMsg.Replace("[XRP] ", string.Empty));
                _thread.RunOnUi(() =>
                {
                    _chat.PrintError(errMsg);
                    SyncCompleted?.Invoke(false, errMsg.Replace("[XRP] ", string.Empty));
                });
            }
        });
    }

    /// <summary>
    /// Sync currently equipped gear to the profile backend (single-job, plugin → profile).
    /// Used as fallback when saved gearsets are not available.
    /// </summary>
    public void SyncProfileGear()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _chat.PrintError("[XRP] No API key configured. Use /xrp config.");
            return;
        }

        if (!_playerState.IsLoaded)
        {
            _chat.PrintError("[XRP] Character not fully loaded yet.");
            return;
        }

        var charName = _playerState.CharacterName?.ToString();
        var charWorld = _playerState.HomeWorld.Value.Name.ToString();
        if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(charWorld))
        {
            _chat.PrintError("[XRP] Could not determine character name/world.");
            return;
        }

        var equipped = _inventory.ReadEquippedGearEnriched();
        if (equipped.Count == 0)
        {
            _chat.PrintError("[XRP] Could not read equipped gear.");
            return;
        }

        // Detect current job from player state
        var classJobId = _playerState.ClassJob.RowId;
        if (classJobId == 0)
        {
            _chat.PrintError("[XRP] Could not detect current job.");
            return;
        }

        var jobAbbrev = _playerState.ClassJob.Value.Abbreviation.ToString()?.ToUpperInvariant();
        if (string.IsNullOrEmpty(jobAbbrev))
        {
            _chat.PrintError("[XRP] Could not resolve current job abbreviation.");
            return;
        }

        _chat.Print($"[XRP] Syncing {jobAbbrev} equipped gear to profile...");

        _thread.RunBackground(async () =>
        {
            var gearSlots = new List<PluginGearsetSyncGearSlot>();
            foreach (var (slot, details) in equipped)
            {
                gearSlots.Add(new PluginGearsetSyncGearSlot
                {
                    Slot = slot,
                    HasItem = true,
                    CurrentSource = details.Source,
                    ItemId = (int)details.ItemId,
                    ItemName = details.ItemName,
                    ItemLevel = details.ItemLevel,
                    ItemIcon = details.IconId > 0 ? details.IconId.ToString() : null,
                });
            }

            var request = new PluginBatchGearsetSyncRequest
            {
                CharacterName = charName,
                CharacterWorld = charWorld,
                Source = "plugin",
                PluginVersion = null,
                Gearsets = new List<PluginGearsetEntry>
                {
                    new()
                    {
                        GearsetIndex = -1,
                        GearsetName = $"{jobAbbrev} (equipped)",
                        Job = jobAbbrev,
                        ClassJobId = (int)_playerState.ClassJob.RowId,
                        Gear = gearSlots,
                    },
                },
            };

            var result = await _api.SyncBatchGearsetsAsync(request);
            if (result.IsSuccess)
            {
                var data = result.Value!;
                var changed = data.SyncedJobs.Any(j => j.GearChanged);
                var msg = changed
                    ? $"[XRP] {jobAbbrev} gear synced to profile."
                    : $"[XRP] {jobAbbrev} gear already up to date.";
                RecordSyncResult(1, null);
                _thread.RunOnUi(() =>
                {
                    _chat.Print(msg);
                    SyncCompleted?.Invoke(true, msg.Replace("[XRP] ", string.Empty));
                });
            }
            else
            {
                var errMsg = result.Error == ApiError.Unauthorized
                    ? "[XRP] API key rejected — re-authorize via /xrp config"
                    : "[XRP] Failed to sync gear. Check connection.";
                RecordSyncResult(0, errMsg.Replace("[XRP] ", string.Empty));
                _thread.RunOnUi(() =>
                {
                    _chat.PrintError(errMsg);
                    SyncCompleted?.Invoke(false, errMsg.Replace("[XRP] ", string.Empty));
                });
            }
        });
    }

    /// <summary>
    /// Sync the saved gearset for a specific job abbreviation (e.g. "BRD").
    /// Used by /xrp syncgear BRD. Falls back to current equipped gear if no saved gearset is found.
    /// </summary>
    public void SyncJobGearset(string jobAbbrev)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _chat.PrintError("[XRP] No API key configured. Use /xrp config.");
            return;
        }

        if (_gearsetService == null)
        {
            _chat.Print($"[XRP] Gearset sync not available — falling back to current gear sync.");
            SyncProfileGear();
            return;
        }

        // ReadSavedGearsets must run on the framework/main thread — called here from OnCommand.
        var gearsets = _gearsetService.ReadSavedGearsets();
        var match = GearsetService.DeduplicateByJob(gearsets)
            .FirstOrDefault(g => string.Equals(g.Job, jobAbbrev, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            _chat.PrintError($"[XRP] No saved gearset found for {jobAbbrev.ToUpperInvariant()}. Ensure you have a gearset saved in-game for that job.");
            return;
        }

        if (!_playerState.IsLoaded)
        {
            _chat.PrintError("[XRP] Character not fully loaded yet.");
            return;
        }

        var charName = _playerState.CharacterName?.ToString();
        var charWorld = _playerState.HomeWorld.Value.Name.ToString();
        if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(charWorld))
        {
            _chat.PrintError("[XRP] Could not determine character name/world.");
            return;
        }

        _chat.Print($"[XRP] Syncing {match.Job} saved gearset...");

        _thread.RunBackground(async () =>
        {
            var gearSlots = match.Items.Select(item => new PluginGearsetSyncGearSlot
            {
                Slot = item.Slot,
                HasItem = item.HasItem,
                CurrentSource = item.CurrentSource,
                IsAugmented = item.IsAugmented,
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                ItemLevel = item.ItemLevel,
                ItemIcon = item.ItemIcon,
            }).ToList();

            var request = new PluginBatchGearsetSyncRequest
            {
                CharacterName = charName,
                CharacterWorld = charWorld,
                Source = "plugin",
                Gearsets =
                [
                    new PluginGearsetEntry
                    {
                        GearsetIndex = match.GearsetIndex,
                        GearsetName = match.GearsetName,
                        Job = match.Job,
                        ClassJobId = match.ClassJobId,
                        Gear = gearSlots,
                    },
                ],
            };

            var result = await _api.SyncBatchGearsetsAsync(request);
            if (result.IsSuccess)
            {
                var changed = result.Value!.SyncedJobs.Any(j => j.GearChanged);
                var msg = changed
                    ? $"[XRP] {match.Job} gear synced to profile."
                    : $"[XRP] {match.Job} gear already up to date.";
                RecordSyncResult(1, null);
                _thread.RunOnUi(() =>
                {
                    _chat.Print(msg);
                    SyncCompleted?.Invoke(true, msg.Replace("[XRP] ", string.Empty));
                });
            }
            else
            {
                var errMsg = result.Error == ApiError.Unauthorized
                    ? "[XRP] API key rejected — re-authorize via /xrp config"
                    : $"[XRP] Failed to sync {match.Job} gear. Check connection.";
                RecordSyncResult(0, errMsg.Replace("[XRP] ", string.Empty));
                _thread.RunOnUi(() =>
                {
                    _chat.PrintError(errMsg);
                    SyncCompleted?.Invoke(false, errMsg.Replace("[XRP] ", string.Empty));
                });
            }
        });
    }
}

/// <summary>Result of diffing equipped gear against fresh API gear.</summary>
public sealed class GearDiff
{
    public int ChangeCount { get; init; }
    public List<string> NewlyAcquired { get; init; } = new();
}
