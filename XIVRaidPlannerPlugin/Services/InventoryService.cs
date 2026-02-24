using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Reads equipped gear from the game, classifies sources, and syncs to the web app API.
/// </summary>
public class InventoryService
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    // Equipment slot indices → gear slot names
    // See: https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InventoryItem.cs
    private static readonly Dictionary<int, string> EquipSlotToGearSlot = new()
    {
        [0] = "weapon",    // Main Hand
        // [1] = "offhand", // Off Hand (not tracked in planner)
        [2] = "head",
        [3] = "body",
        [4] = "hands",
        [5] = "legs",      // Waist was removed, legs is slot 5 now in equipped
        [6] = "feet",
        // Slot indices 7-8 are waist/legs in some older references
        // In EquippedItems: 0=weapon, 1=offhand, 2=head, 3=body, 4=hands, 5=legs, 6=feet,
        //                   7=earring, 8=necklace, 9=bracelet, 10=ring1, 11=ring2
        [7] = "earring",
        [8] = "necklace",
        [9] = "bracelet",
        [10] = "ring1",
        [11] = "ring2",
    };

    // Item level ranges for source classification (Dawntrail Savage tier)
    // These will need updating each major patch cycle
    private const int IL_SAVAGE = 795;
    private const int IL_SAVAGE_ARMOR = 790;  // Savage armor/accessories
    private const int IL_TOME_AUG = 790;      // Augmented tomestone
    private const int IL_CATCHUP = 780;       // Alliance raid catch-up
    private const int IL_TOME = 780;          // Unaugmented tomestone
    private const int IL_CRAFTED = 770;       // Crafted pentamelded
    private const int IL_NORMAL = 760;        // Normal raid

    public InventoryService(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    /// <summary>
    /// Read currently equipped gear and return a list of slot → item ID mappings.
    /// MUST be called on the framework/main thread.
    /// </summary>
    public unsafe Dictionary<string, EquippedItem> ReadEquippedGear()
    {
        var result = new Dictionary<string, EquippedItem>();

        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
            {
                _log.Warning("[Inventory] InventoryManager not available");
                return result;
            }

            var container = inventory->GetInventoryContainer(InventoryType.EquippedItems);
            if (container == null)
            {
                _log.Warning("[Inventory] EquippedItems container not available");
                return result;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                if (!EquipSlotToGearSlot.TryGetValue(i, out var slotName))
                    continue;

                result[slotName] = new EquippedItem
                {
                    ItemId = item->ItemId,
                    IsHq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                };
            }

            _log.Info($"[Inventory] Read {result.Count} equipped items");
        }
        catch (Exception ex)
        {
            _log.Error($"[Inventory] Failed to read equipped gear: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Classify an item's source category based on Lumina data.
    /// Mirrors the backend logic in bis.py determine_source().
    /// </summary>
    public string ClassifySource(uint itemId)
    {
        try
        {
            var itemSheet = _dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
                return "unknown";

            var item = itemSheet.GetRowOrDefault(itemId);
            if (item == null)
                return "unknown";

            var iLv = (int)item.Value.LevelItem.RowId;
            var name = item.Value.Name.ToString();

            return ClassifyByNameAndLevel(name, iLv);
        }
        catch (Exception ex)
        {
            _log.Error($"[Inventory] Failed to classify item {itemId}: {ex.Message}");
            return "unknown";
        }
    }

    /// <summary>
    /// Classify gear source by item name patterns and item level.
    /// </summary>
    private string ClassifyByNameAndLevel(string name, int iLv)
    {
        var lowerName = name.ToLowerInvariant();

        // Savage raid drops (iLv 790/795)
        if (iLv >= IL_SAVAGE_ARMOR)
        {
            // Check for augmented tomestone pattern first ("Aug." prefix)
            if (lowerName.StartsWith("aug") || lowerName.Contains("augmented"))
                return "tome_up";

            // Current tier savage patterns
            if (lowerName.Contains("ascension") || lowerName.Contains("cruiserweight") ||
                lowerName.Contains("grand champion") || lowerName.Contains("heavyweight"))
                return "savage";

            // If it's the right iLv and not a known tome pattern, default to savage
            if (iLv >= IL_SAVAGE)
                return "savage";

            return "tome_up"; // 790 non-savage = augmented tome
        }

        // Catch-up gear (alliance raid, iLv 780+)
        if (iLv >= IL_CATCHUP)
        {
            // Unaugmented tome patterns
            if (lowerName.Contains("quetzalli") || lowerName.Contains("neo kingdom") ||
                lowerName.Contains("bygone"))
                return "tome";

            return "catchup";
        }

        // Crafted gear (iLv ~770)
        if (iLv >= IL_CRAFTED)
        {
            if (lowerName.Contains("claro") || lowerName.Contains("agonist") ||
                lowerName.Contains("archeo kingdom"))
                return "crafted";

            // Relic weapons
            if (lowerName.Contains("relic") || lowerName.Contains("manderville"))
                return "relic";

            return "prep"; // Previous tier BiS
        }

        // Normal raid
        if (iLv >= IL_NORMAL)
            return "normal";

        return "unknown";
    }

    /// <summary>
    /// Build gear update from equipped items, comparing with current BiS data.
    /// Returns the list of gear slots to update.
    /// </summary>
    public List<GearSlotStatusDto> BuildGearUpdate(
        Dictionary<string, EquippedItem> equipped,
        List<GearSlotStatusDto> currentGear)
    {
        var updated = new List<GearSlotStatusDto>();

        foreach (var gear in currentGear)
        {
            var slot = new GearSlotStatusDto
            {
                Slot = gear.Slot,
                BisSource = gear.BisSource,
                HasItem = gear.HasItem,
                IsAugmented = gear.IsAugmented,
                ItemId = gear.ItemId,
                ItemName = gear.ItemName,
                ItemLevel = gear.ItemLevel,
                ItemIcon = gear.ItemIcon,
                Materia = gear.Materia,
            };

            if (equipped.TryGetValue(gear.Slot, out var equippedItem))
            {
                var source = ClassifySource(equippedItem.ItemId);
                slot.CurrentSource = source;

                // Check if equipped item matches BiS
                if (gear.ItemId.HasValue && equippedItem.ItemId == (uint)gear.ItemId.Value)
                {
                    slot.HasItem = true;
                    // If tome BiS, check if it's the augmented version
                    if (gear.BisSource == "tome" && source == "tome_up")
                        slot.IsAugmented = true;
                }
            }
            else
            {
                slot.CurrentSource = gear.CurrentSource;
            }

            updated.Add(slot);
        }

        return updated;
    }
}

/// <summary>Represents an equipped item read from game memory.</summary>
public class EquippedItem
{
    public uint ItemId { get; set; }
    public bool IsHq { get; set; }
}
