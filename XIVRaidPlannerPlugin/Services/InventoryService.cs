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
    // The EquippedItems container retains the legacy waist slot at index 5 (always empty).
    // See: https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InventoryItem.cs
    private static readonly Dictionary<int, string> EquipSlotToGearSlot = new()
    {
        [0] = "weapon",      // Main Hand
        // [1] = "offhand",  // Off Hand (not tracked in planner)
        [2] = "head",
        [3] = "body",
        [4] = "hands",
        // [5] = waist       // Legacy slot, always empty since 5.0 — do NOT map
        [6] = "legs",
        [7] = "feet",
        [8] = "earring",
        [9] = "necklace",
        [10] = "bracelet",
        [11] = "ring1",
        [12] = "ring2",
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

                // Read materia from the inventory item (uses accessor methods)
                var matCount = 0;
                var matTypes = new ushort[5];
                var matGrades = new byte[5];
                try
                {
                    matCount = item->GetMateriaCount();
                    for (var m = 0; m < matCount && m < 5; m++)
                    {
                        matTypes[m] = item->GetMateriaId((byte)m);
                        matGrades[m] = item->GetMateriaGrade((byte)m);
                    }
                }
                catch { /* materia reading optional */ }

                result[slotName] = new EquippedItem
                {
                    ItemId = item->ItemId,
                    IsHq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    MateriaTypes = matTypes,
                    MateriaGrades = matGrades,
                    MateriaCount = matCount,
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
    /// Read equipped gear and enrich with Lumina data (name, level, icon) for display.
    /// MUST be called on the framework/main thread.
    /// </summary>
    public Dictionary<string, EquippedItemDetails> ReadEquippedGearEnriched()
    {
        var raw = ReadEquippedGear();
        var result = new Dictionary<string, EquippedItemDetails>();

        var itemSheet = _dataManager.GetExcelSheet<Item>();
        if (itemSheet == null) return result;

        foreach (var (slot, equipped) in raw)
        {
            var item = itemSheet.GetRowOrDefault(equipped.ItemId);
            if (item == null) continue;

            var details = new EquippedItemDetails
            {
                ItemId = equipped.ItemId,
                ItemName = item.Value.Name.ToString(),
                ItemLevel = (int)item.Value.LevelItem.RowId,
                IconId = (uint)item.Value.Icon,
                Source = ClassifySource(equipped.ItemId),
            };

            // Resolve equipped materia
            if (equipped.MateriaCount > 0)
                details.Materia = ResolveMateriaDetails(equipped, itemSheet);

            result[slot] = details;
        }

        _log.Info($"[Inventory] Enriched {result.Count} equipped items with Lumina data");
        return result;
    }

    /// <summary>Check if an equipped source classification matches the BiS source target.</summary>
    private static bool SourceMatchesBis(string equippedSource, string bisSource)
    {
        return bisSource switch
        {
            "raid" => equippedSource is "savage" or "raid",
            "tome" => equippedSource is "tome" or "tome_up",
            "base_tome" => equippedSource is "tome" or "base_tome",
            "crafted" => equippedSource == "crafted",
            _ => false,
        };
    }

    /// <summary>Resolve materia type+grade to item details via Lumina.</summary>
    private List<MateriaDetail> ResolveMateriaDetails(EquippedItem equipped, Lumina.Excel.ExcelSheet<Item> itemSheet)
    {
        var list = new List<MateriaDetail>();

        try
        {
            var materiaSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Materia>();
            if (materiaSheet == null) return list;

            for (var m = 0; m < equipped.MateriaCount; m++)
            {
                try
                {
                    var matRow = materiaSheet.GetRowOrDefault(equipped.MateriaTypes[m]);
                    if (matRow == null) continue;

                    var grade = equipped.MateriaGrades[m];

                    // Get the materia item for this grade via the Item collection
                    var materiaItemRef = matRow.Value.Item[grade];
                    var materiaItemId = materiaItemRef.RowId;
                    if (materiaItemId == 0) continue;

                    var materiaItem = itemSheet.GetRowOrDefault(materiaItemId);
                    if (materiaItem == null) continue;

                    var name = materiaItem.Value.Name.ToString();

                    // Get full stat name and value from Materia sheet
                    var fullStatName = "";
                    var statValue = 0;
                    try
                    {
                        fullStatName = matRow.Value.BaseParam.Value.Name.ToString();
                        statValue = matRow.Value.Value[grade];
                    }
                    catch { /* optional fields */ }

                    list.Add(new MateriaDetail
                    {
                        Name = name,
                        IconId = (uint)materiaItem.Value.Icon,
                        Stat = AbbreviateStat(name),
                        FullStatName = fullStatName,
                        StatValue = statValue,
                    });
                }
                catch { /* skip individual materia on error */ }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"[Inventory] Materia resolution failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>Extract stat abbreviation from materia item name.</summary>
    private static string AbbreviateStat(string materiaName)
    {
        var lower = materiaName.ToLowerInvariant();
        if (lower.Contains("savage might") || lower.Contains("critical")) return "CRT";
        if (lower.Contains("savage aim") || lower.Contains("direct hit")) return "DH";
        if (lower.Contains("heavens' eye") || lower.Contains("determination")) return "DET";
        if (lower.Contains("quickarm") || lower.Contains("skill speed")) return "SKS";
        if (lower.Contains("quicktongue") || lower.Contains("spell speed")) return "SPS";
        if (lower.Contains("battledance") || lower.Contains("tenacity")) return "TEN";
        if (lower.Contains("piety")) return "PIE";
        return materiaName.Length > 5 ? materiaName[..3].ToUpper() : materiaName.ToUpper();
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

                // Check if equipped item matches BiS — try item ID first, then source match
                if (gear.ItemId.HasValue && equippedItem.ItemId == (uint)gear.ItemId.Value)
                {
                    // Exact item ID match
                    slot.HasItem = true;
                    if (gear.BisSource == "tome" && source == "tome_up")
                        slot.IsAugmented = true;
                }
                else if (gear.BisSource != null && SourceMatchesBis(source, gear.BisSource))
                {
                    // Fallback: equipped source matches or exceeds BiS source
                    slot.HasItem = true;
                    if (gear.BisSource == "tome" && source == "tome_up")
                        slot.IsAugmented = true;
                    _log.Debug($"[Inventory] Source match for {gear.Slot}: equipped={source}, bis={gear.BisSource}");
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
    public ushort[] MateriaTypes { get; set; } = Array.Empty<ushort>();
    public byte[] MateriaGrades { get; set; } = Array.Empty<byte>();
    public int MateriaCount { get; set; }
}

/// <summary>Equipped item enriched with Lumina data for display.</summary>
public class EquippedItemDetails
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int ItemLevel { get; set; }
    public uint IconId { get; set; }
    public string Source { get; set; } = "unknown";
    public List<MateriaDetail> Materia { get; set; } = new();
}

/// <summary>Resolved materia info for tooltip display.</summary>
public class MateriaDetail
{
    public string Name { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public string Stat { get; set; } = string.Empty;
    public string FullStatName { get; set; } = string.Empty;
    public int StatValue { get; set; }
}
