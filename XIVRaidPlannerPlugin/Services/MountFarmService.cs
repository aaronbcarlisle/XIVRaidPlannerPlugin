using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Reads mount ownership and totem counts from game memory, then syncs to the web app API.
///
/// Mount ownership: Uses PlayerState.IsMountUnlocked(mountId) from FFXIVClientStructs.
/// Totem counts: Uses InventoryManager to count totem items across inventory containers.
///
/// Threading model (matches GearSyncService pattern):
/// - ReadMountOwnership / ReadTotemCounts: MUST run on the framework/main thread (unsafe pointers)
/// - API calls (fetch catalog, post sync): run on background thread via PluginThread.RunBackground
///
/// The public entry point is Sync(), called from /xrp mountsync command handler (framework thread).
/// It reads game data synchronously, then dispatches the API call to a background thread.
/// </summary>
public class MountFarmService
{
    private readonly RaidPlannerClient _client;
    private readonly PluginThread _thread;
    private readonly IPluginLog _log;
    private readonly IPlayerState _playerState;
    private readonly IChatGui _chat;
    private readonly Configuration _config;

    /// <summary>Fired on the UI thread after a manual Sync() completes. Parameters: (isSuccess, userMessage)</summary>
    public event Action<bool, string>? SyncCompleted;

    // Cached catalog from the server (fetched once per session)
    private List<MountFarmCatalogEntry>? _catalog;

    public MountFarmService(
        RaidPlannerClient client,
        PluginThread thread,
        IPlayerState playerState,
        IChatGui chat,
        Configuration config,
        IPluginLog log)
    {
        _client = client;
        _thread = thread;
        _playerState = playerState;
        _chat = chat;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Synchronous entry point called from command handler or UI button (framework thread).
    /// Reads game memory on the current thread, then dispatches API sync to background.
    /// </summary>
    public void Sync()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _chat.PrintError("[XIV Raid Planner] Not signed in. Use /xrp config to sign in first.");
            return;
        }

        if (!_config.EnableMountFarmSync)
        {
            _chat.PrintError("[XIV Raid Planner] Mount farm sync is disabled. Enable it in /xrp config.");
            return;
        }

        if (!_playerState.IsLoaded)
        {
            _chat.PrintError("[XIV Raid Planner] Not logged in to a character.");
            return;
        }

        // Read game data on framework thread (unsafe pointer access)
        var mounts = ReadMountOwnership();
        var totems = ReadTotemCounts();

        if (mounts.Count == 0 && totems.Count == 0)
        {
            // If catalog isn't loaded, fetch it in background and retry
            if (_catalog == null || _catalog.Count == 0)
            {
                _chat.Print("[XIV Raid Planner] Fetching mount farm catalog...");
                _thread.RunBackground(async () =>
                {
                    await FetchCatalogAsync();
                    _thread.RunOnUi(() =>
                    {
                        if (_catalog != null && _catalog.Count > 0)
                            Sync(); // catalog now loaded — retry automatically
                        else
                            _chat.PrintError("[XIV Raid Planner] Could not load mount farm catalog.");
                    });
                });
            }
            else
            {
                _chat.PrintError("[XIV Raid Planner] No mount or totem data could be read from game.");
            }
            return;
        }

        var charName = _playerState.CharacterName?.ToString() ?? "Unknown";

        _chat.Print($"[XIV Raid Planner] Syncing {mounts.Count} mounts, {totems.Count} totems...");

        // API call on background thread
        _thread.RunBackground(async () =>
        {
            try
            {
                var request = new PluginMountFarmSyncRequest
                {
                    CharacterName = charName,
                    Mounts = mounts,
                    Totems = totems,
                    Source = "plugin",
                    SyncedAt = DateTime.UtcNow.ToString("o"),
                };

                var result = await _client.SyncMountFarmsAsync(request);
                if (result.IsSuccess)
                {
                    _thread.RunOnUi(() =>
                    {
                        _chat.Print("[XIV Raid Planner] Mount farm sync complete.");
                        SyncCompleted?.Invoke(true, "Mounts synced.");
                    });
                }
                else
                {
                    _log.Warning($"[MountFarm] Sync API call failed: {result.Error}");
                    _thread.RunOnUi(() =>
                    {
                        _chat.PrintError("[XIV Raid Planner] Mount farm sync failed. Check plugin log.");
                        SyncCompleted?.Invoke(false, "Mount sync failed.");
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[MountFarm] Sync failed: {ex.Message}");
                _thread.RunOnUi(() =>
                {
                    _chat.PrintError("[XIV Raid Planner] Mount farm sync failed.");
                    SyncCompleted?.Invoke(false, "Mount sync failed.");
                });
            }
        });
    }

    /// <summary>
    /// Background-safe entry point for auto-sync on login.
    /// Fetches catalog, then schedules a framework-thread read + background API call.
    /// </summary>
    public async Task AutoSyncAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_playerState.IsLoaded) return;
            if (string.IsNullOrEmpty(_config.ApiKey)) return;
            if (!_config.EnableMountFarmSync || !_config.AutoSyncMountFarms) return;

            // Ensure catalog is loaded (API call, safe on background thread)
            if (_catalog == null || _catalog.Count == 0)
            {
                await FetchCatalogAsync(ct);
                if (_catalog == null || _catalog.Count == 0) return;
            }

            // Schedule the actual sync on the framework thread
            _thread.RunOnUi(() => Sync());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error($"[MountFarm] Auto-sync failed: {ex.Message}");
        }
    }

    /// <summary>Fetch catalog from server. Safe to call from background thread.</summary>
    private async Task FetchCatalogAsync(CancellationToken ct = default)
    {
        var catalogResult = await _client.GetMountFarmCatalogAsync(ct);
        if (!catalogResult.IsSuccess)
        {
            _log.Warning($"[MountFarm] Failed to fetch catalog: {catalogResult.Error}");
            return;
        }
        _catalog = catalogResult.Value?.Entries ?? new List<MountFarmCatalogEntry>();
        _log.Info($"[MountFarm] Fetched catalog with {_catalog.Count} entries");
    }

    /// <summary>
    /// Read which mounts the player has unlocked using PlayerState.IsMountUnlocked.
    /// MUST be called on the framework/main thread.
    /// </summary>
    private unsafe List<MountSyncItem> ReadMountOwnership()
    {
        var items = new List<MountSyncItem>();

        if (_catalog == null || _catalog.Count == 0)
            return items;

        try
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps == null)
            {
                _log.Warning("[MountFarm] PlayerState not available");
                return items;
            }

            foreach (var entry in _catalog)
            {
                if (entry.MountId == null || entry.MountId <= 0)
                    continue;

                var owned = ps->IsMountUnlocked((uint)entry.MountId.Value);
                items.Add(new MountSyncItem
                {
                    MountId = entry.MountId.Value,
                    TrialId = entry.TrialId,
                    Owned = owned,
                });
            }

            var ownedCount = items.Count(m => m.Owned);
            _log.Info($"[MountFarm] Read {items.Count} mounts, {ownedCount} owned");
        }
        catch (Exception ex)
        {
            _log.Error($"[MountFarm] Failed to read mount ownership: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Count totem items across all inventory containers.
    /// MUST be called on the framework/main thread.
    /// </summary>
    private unsafe List<TotemSyncItem> ReadTotemCounts()
    {
        var items = new List<TotemSyncItem>();

        if (_catalog == null || _catalog.Count == 0)
            return items;

        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
            {
                _log.Warning("[MountFarm] InventoryManager not available");
                return items;
            }

            var totemEntries = _catalog
                .Where(e => e.TotemItemId is > 0)
                .ToList();

            if (totemEntries.Count == 0)
                return items;

            var totemLookup = new HashSet<int>(
                totemEntries.Select(e => e.TotemItemId!.Value));

            var containerTypes = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
                InventoryType.KeyItems,
            };

            var counts = new Dictionary<int, int>();
            var foundIn = new Dictionary<int, List<string>>();

            var containerNames = new Dictionary<InventoryType, string>
            {
                [InventoryType.Inventory1] = "Bag 1",
                [InventoryType.Inventory2] = "Bag 2",
                [InventoryType.Inventory3] = "Bag 3",
                [InventoryType.Inventory4] = "Bag 4",
                [InventoryType.KeyItems] = "Key Items",
            };

            foreach (var containerType in containerTypes)
            {
                var container = inventory->GetInventoryContainer(containerType);
                if (container == null)
                    continue;

                if (container->Size > 500)
                {
                    _log.Warning($"[MountFarm] Suspicious container size {container->Size} for {containerType}, skipping");
                    continue;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var itemId = (int)slot->ItemId;
                    if (totemLookup.Contains(itemId))
                    {
                        counts.TryGetValue(itemId, out var existing);
                        counts[itemId] = existing + (int)slot->Quantity;

                        if (!foundIn.ContainsKey(itemId))
                            foundIn[itemId] = new List<string>();
                        var name = containerNames.GetValueOrDefault(containerType, containerType.ToString());
                        if (!foundIn[itemId].Contains(name))
                            foundIn[itemId].Add(name);
                    }
                }
            }

            foreach (var entry in totemEntries)
            {
                counts.TryGetValue(entry.TotemItemId!.Value, out var count);
                foundIn.TryGetValue(entry.TotemItemId.Value, out var locations);
                items.Add(new TotemSyncItem
                {
                    ItemId = entry.TotemItemId.Value,
                    TrialId = entry.TrialId,
                    TotemName = entry.TotemName,
                    Count = count,
                    FoundIn = locations,
                });
            }

            var nonZero = items.Count(t => t.Count > 0);
            _log.Info($"[MountFarm] Read {items.Count} totem types, {nonZero} with non-zero counts");
        }
        catch (Exception ex)
        {
            _log.Error($"[MountFarm] Failed to read totem counts: {ex.Message}");
        }

        return items;
    }
}
