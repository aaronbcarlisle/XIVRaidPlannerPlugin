using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Maps BiS item IDs from the planner API to in-game item IDs for highlighting and matching.
/// Provides O(1) lookup for whether an item is BiS and which slot it belongs to.
/// </summary>
public class ItemMappingService
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    // BiS item ID → slot name mapping (e.g., 44073 → "weapon")
    private readonly Dictionary<uint, string> _bisItemToSlot = new();

    // BiS slot → materia list mapping (e.g., "weapon" → [materia1, materia2, ...])
    private readonly Dictionary<string, List<MateriaSlotInfo>> _bisSlotToMateria = new();

    // BiS item IDs for quick membership check
    private readonly HashSet<uint> _bisItemIds = new();

    // Item name → item ID cache (for fallback resolution)
    private readonly Dictionary<string, uint> _nameToItemId = new();
    private bool _nameIndexBuilt;

    public bool HasData => _bisItemIds.Count > 0;

    public ItemMappingService(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    /// <summary>Load BiS data from API gear response into the lookup tables.</summary>
    public void LoadBisData(List<GearSlotStatusDto> gear)
    {
        _bisItemToSlot.Clear();
        _bisItemIds.Clear();
        _bisSlotToMateria.Clear();

        var resolved = 0;
        foreach (var slot in gear)
        {
            uint? itemId = slot.ItemId is > 0 ? (uint)slot.ItemId.Value : null;

            // Fallback: resolve by name if itemId is missing (legacy data imported before itemId preservation)
            if (itemId == null && !string.IsNullOrEmpty(slot.ItemName))
            {
                itemId = ResolveItemIdByName(slot.ItemName);
                if (itemId != null)
                {
                    slot.ItemId = (int)itemId.Value;
                    resolved++;
                }
            }

            if (itemId != null)
            {
                _bisItemToSlot[itemId.Value] = slot.Slot;
                _bisItemIds.Add(itemId.Value);
            }

            if (slot.Materia is { Count: > 0 })
            {
                _bisSlotToMateria[slot.Slot] = slot.Materia;
            }
        }

        if (resolved > 0)
            _log.Info($"[ItemMapping] Resolved {resolved} items by name (legacy data)");
        _log.Info($"[ItemMapping] Loaded {_bisItemIds.Count} BiS items across {gear.Count} slots");
    }

    /// <summary>Clear all loaded BiS data.</summary>
    public void Clear()
    {
        _bisItemToSlot.Clear();
        _bisItemIds.Clear();
        _bisSlotToMateria.Clear();
    }

    /// <summary>O(1) check if an item ID is in the current BiS set.</summary>
    public bool IsBisItem(uint itemId) => _bisItemIds.Contains(itemId);

    /// <summary>Get the gear slot name for a BiS item ID, or null if not BiS.</summary>
    public string? GetBisSlot(uint itemId) =>
        _bisItemToSlot.TryGetValue(itemId, out var slot) ? slot : null;

    /// <summary>Get the materia list for a BiS slot, or null if not available.</summary>
    public List<MateriaSlotInfo>? GetBisMateria(string slot) =>
        _bisSlotToMateria.TryGetValue(slot, out var materia) ? materia : null;

    /// <summary>
    /// Fallback: Resolve an item ID by name using Lumina Item sheet.
    /// Used for legacy data that doesn't have item IDs stored.
    /// </summary>
    public uint? ResolveItemIdByName(string itemName)
    {
        if (string.IsNullOrEmpty(itemName))
            return null;

        // Build name index on first use
        if (!_nameIndexBuilt)
        {
            BuildNameIndex();
        }

        return _nameToItemId.TryGetValue(itemName.ToLowerInvariant(), out var id) ? id : null;
    }

    /// <summary>
    /// Get all BiS item IDs currently loaded.
    /// </summary>
    public IReadOnlyCollection<uint> GetAllBisItemIds() => _bisItemIds;

    private void BuildNameIndex()
    {
        _nameIndexBuilt = true;
        try
        {
            var itemSheet = _dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                _log.Warning("[ItemMapping] Could not load Item sheet from Lumina");
                return;
            }

            // Only index equipment items (iLv >= 640 to limit scope to recent tiers)
            foreach (var item in itemSheet)
            {
                if (item.LevelItem.RowId >= 640 && item.EquipSlotCategory.RowId > 0)
                {
                    var name = item.Name.ToString().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(name))
                    {
                        _nameToItemId[name] = item.RowId;
                    }
                }
            }

            _log.Info($"[ItemMapping] Built name index with {_nameToItemId.Count} equipment items");
        }
        catch (System.Exception ex)
        {
            _log.Error($"[ItemMapping] Failed to build name index: {ex.Message}");
        }
    }
}
