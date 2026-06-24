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
/// Reads mount ownership and totem counts from game memory, then syncs to the
/// collection participant state endpoint (POST /api/plugin/collections/sync).
///
/// This is separate from MountFarmService which updates the mount farm tracker.
/// CollectionSyncService updates per-player collection goal states used in the
/// Collections &amp; Farms board.
///
/// What is synced (V1):
///   - Mount ownership: via PlayerState.IsMountUnlocked — reliable game API
///   - Token/totem counts: via InventoryManager item scan — reliable game API
///
/// What is NOT synced:
///   - Orchestrion ownership: no reliable game API available
///   - Weapon/glamour ownership: cannot verify without BiS context
///   - Minion ownership: not included in V1 (API reliability TBD)
///
/// Threading model (matches MountFarmService pattern):
///   - ReadMountOwnership / ReadTotemCounts: MUST run on the framework/main thread
///   - API calls: run on background thread via PluginThread.RunBackground
/// </summary>
public class CollectionSyncService
{
    private readonly RaidPlannerClient _client;
    private readonly PluginThread _thread;
    private readonly IPluginLog _log;
    private readonly IPlayerState _playerState;
    private readonly IChatGui _chat;
    private readonly Configuration _config;

    /// <summary>Fired on the UI thread after a manual Sync() completes. Parameters: (isSuccess, userMessage)</summary>
    public event Action<bool, string>? SyncCompleted;

    // Cached mount catalog (shared with mount farm catalog endpoint)
    private List<MountFarmCatalogEntry>? _catalog;
    private DateTime? _lastSyncAt;
    private static readonly TimeSpan SyncCooldown = TimeSpan.FromSeconds(60);

    public CollectionSyncService(
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

        if (!_config.EnableCollectionSync)
        {
            _chat.PrintError("[XIV Raid Planner] Collection sync is disabled. Enable it in /xrp config.");
            return;
        }

        if (!_playerState.IsLoaded)
        {
            _chat.PrintError("[XIV Raid Planner] Not logged in to a character.");
            return;
        }

        if (_lastSyncAt.HasValue && DateTime.UtcNow - _lastSyncAt.Value < SyncCooldown)
        {
            var remaining = (int)(SyncCooldown - (DateTime.UtcNow - _lastSyncAt.Value)).TotalSeconds;
            _chat.Print($"[XIV Raid Planner] Collection sync on cooldown. Try again in {remaining}s.");
            return;
        }

        // Ensure catalog is available before reading game data
        if (_catalog == null || _catalog.Count == 0)
        {
            _chat.Print("[XIV Raid Planner] Fetching mount catalog for collection sync...");
            _thread.RunBackground(async () =>
            {
                await FetchCatalogAsync();
                _thread.RunOnUi(() =>
                {
                    if (_catalog != null && _catalog.Count > 0)
                        Sync();
                    else
                        _chat.PrintError("[XIV Raid Planner] Could not load mount catalog for collection sync.");
                });
            });
            return;
        }

        _lastSyncAt = DateTime.UtcNow;
        var mounts = ReadMountOwnership();
        var totems = ReadTotemCounts();
        var charName = _playerState.CharacterName?.ToString() ?? "Unknown";

        if (mounts.Count == 0 && totems.Count == 0)
        {
            var nullMounts = _catalog?.Count(e => e.MountId == null || e.MountId <= 0) ?? 0;
            _log.Warning($"[CollectionSync] No usable game IDs in catalog ({nullMounts} mount entries with null MountId). Mount ownership sync requires game IDs — this is a V2 catalog gap.");
            _chat.Print("[XIV Raid Planner] Collection sync: no game IDs in catalog yet. Mounts and tokens require catalog data updates.");
            _thread.RunOnUi(() => SyncCompleted?.Invoke(false, "No game IDs in catalog."));
            return;
        }

        _chat.Print($"[XIV Raid Planner] Syncing {mounts.Count(m => m.Owned)} owned mounts, {totems.Count} token types to Collections...");

        _thread.RunBackground(async () =>
        {
            try
            {
                var request = new PluginCollectionSyncRequest
                {
                    CharacterName = charName,
                    PluginVersion = null,
                    Mounts = mounts,
                    Currencies = totems,
                    SyncedAt = DateTime.UtcNow.ToString("o"),
                };

                var result = await _client.SyncCollectionsAsync(request);
                if (result.IsSuccess)
                {
                    var data = result.Value!;
                    var msg = $"[XIV Raid Planner] Collections synced: {data.StatesUpdated} updated, {data.TokenCountsUpdated} token counts.";
                    _thread.RunOnUi(() =>
                    {
                        _chat.Print(msg);
                        SyncCompleted?.Invoke(true, $"Collections synced ({data.StatesUpdated} updated).");
                    });
                }
                else
                {
                    _log.Warning($"[CollectionSync] API call failed: {result.Error}");
                    _thread.RunOnUi(() =>
                    {
                        _chat.PrintError("[XIV Raid Planner] Collection sync failed. Check plugin log.");
                        SyncCompleted?.Invoke(false, "Collection sync failed.");
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[CollectionSync] Sync failed: {ex.Message}");
                _thread.RunOnUi(() =>
                {
                    _chat.PrintError("[XIV Raid Planner] Collection sync failed.");
                    SyncCompleted?.Invoke(false, "Collection sync failed.");
                });
            }
        });
    }

    /// <summary>
    /// Background-safe entry point for auto-sync on login.
    /// </summary>
    public async Task AutoSyncAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_playerState.IsLoaded) return;
            if (string.IsNullOrEmpty(_config.ApiKey)) return;
            if (!_config.EnableCollectionSync || !_config.AutoSyncMountFarms) return;

            if (_catalog == null || _catalog.Count == 0)
            {
                await FetchCatalogAsync(ct);
                if (_catalog == null || _catalog.Count == 0) return;
            }

            _thread.RunOnUi(() => Sync());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error($"[CollectionSync] Auto-sync failed: {ex.Message}");
        }
    }

    private async Task FetchCatalogAsync(CancellationToken ct = default)
    {
        var catalogResult = await _client.GetMountFarmCatalogAsync(ct);
        if (!catalogResult.IsSuccess)
        {
            _log.Warning($"[CollectionSync] Failed to fetch mount catalog: {catalogResult.Error}");
            return;
        }
        _catalog = catalogResult.Value?.Entries ?? new List<MountFarmCatalogEntry>();
        _log.Info($"[CollectionSync] Fetched catalog with {_catalog.Count} entries");
    }

    /// <summary>
    /// Read which mounts the player has unlocked using PlayerState.IsMountUnlocked.
    /// MUST be called on the framework/main thread.
    /// </summary>
    private unsafe List<CollectionMountItem> ReadMountOwnership()
    {
        var items = new List<CollectionMountItem>();
        if (_catalog == null || _catalog.Count == 0) return items;

        try
        {
            var ps = PlayerState.Instance();
            if (ps == null)
            {
                _log.Warning("[CollectionSync] PlayerState not available");
                return items;
            }

            foreach (var entry in _catalog)
            {
                if (entry.MountId == null || entry.MountId <= 0)
                    continue;

                var owned = ps->IsMountUnlocked((uint)entry.MountId.Value);
                items.Add(new CollectionMountItem
                {
                    MountId = entry.MountId.Value,
                    TrialId = entry.TrialId,
                    Owned = owned,
                });
            }

            _log.Info($"[CollectionSync] Read {items.Count} mounts, {items.Count(m => m.Owned)} owned");
        }
        catch (Exception ex)
        {
            _log.Error($"[CollectionSync] Failed to read mount ownership: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// Count totem items across inventory containers.
    /// MUST be called on the framework/main thread.
    /// </summary>
    private unsafe List<CollectionTokenItem> ReadTotemCounts()
    {
        var items = new List<CollectionTokenItem>();
        if (_catalog == null || _catalog.Count == 0) return items;

        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
            {
                _log.Warning("[CollectionSync] InventoryManager not available");
                return items;
            }

            var totemEntries = _catalog.Where(e => e.TotemItemId is > 0).ToList();
            if (totemEntries.Count == 0) return items;

            var totemLookup = new HashSet<int>(totemEntries.Select(e => e.TotemItemId!.Value));

            var containerTypes = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
                InventoryType.KeyItems,
            };

            var counts = new Dictionary<int, int>();

            foreach (var containerType in containerTypes)
            {
                var container = inventory->GetInventoryContainer(containerType);
                if (container == null) continue;
                if (container->Size > 500) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;

                    var itemId = (int)slot->ItemId;
                    if (totemLookup.Contains(itemId))
                    {
                        counts.TryGetValue(itemId, out var existing);
                        counts[itemId] = existing + (int)slot->Quantity;
                    }
                }
            }

            foreach (var entry in totemEntries)
            {
                counts.TryGetValue(entry.TotemItemId!.Value, out var count);
                items.Add(new CollectionTokenItem
                {
                    ItemId = entry.TotemItemId.Value,
                    TokenName = entry.TotemName,
                    Count = count,
                });
            }

            _log.Info($"[CollectionSync] Read {items.Count} token types, {items.Count(t => t.Count > 0)} with non-zero counts");
        }
        catch (Exception ex)
        {
            _log.Error($"[CollectionSync] Failed to read token counts: {ex.Message}");
        }

        return items;
    }
}
