using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Reads saved gearsets from the game's RaptureGearsetModule and enriches
/// each item with Lumina data to produce a batch payload for multi-job sync.
///
/// Threading model (matches InventoryService/MountFarmService):
/// - ReadSavedGearsets: MUST run on the framework/main thread (unsafe pointers)
/// - Enrichment via Lumina is safe from any thread.
/// </summary>
public class GearsetService
{
    private readonly IDataManager _dataManager;
    private readonly InventoryService _inventory;
    private readonly IPluginLog _log;

    // FFXIV ClassJob IDs → 3-letter abbreviations used by the planner.
    // Source: Lumina ClassJob sheet RowId → Abbreviation column.
    // Only combat jobs that the planner tracks are included.
    private static readonly Dictionary<byte, string> ClassJobToAbbrev = new()
    {
        [1] = "GLA",
        [2] = "PGL",
        [3] = "MRD",
        [4] = "LNC",
        [5] = "ARC",
        [6] = "CNJ",
        [7] = "THM",
        [19] = "PLD",
        [20] = "MNK",
        [21] = "WAR",
        [22] = "DRG",
        [23] = "BRD",
        [24] = "WHM",
        [25] = "BLM",
        [26] = "ACN",
        [27] = "SMN",
        [28] = "SCH",
        [30] = "NIN",
        [31] = "MCH",
        [32] = "DRK",
        [33] = "AST",
        [34] = "SAM",
        [35] = "RDM",
        [36] = "BLU",
        [37] = "GNB",
        [38] = "DNC",
        [39] = "RPR",
        [40] = "SGE",
        [41] = "VPR",
        [42] = "PCT",
    };

    // The planner only tracks these jobs (combat jobs with raid relevance).
    private static readonly HashSet<string> TrackedJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "PLD", "WAR", "DRK", "GNB",
        "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC",
        "BLM", "SMN", "RDM", "PCT",
    };

    // Gearset equipment slot indices → planner slot names.
    // RaptureGearsetModule stores 14 item slots per gearset entry.
    // Layout matches the equipped items container but includes off-hand and soul crystal.
    private static readonly Dictionary<int, string> GearsetSlotToSlotName = new()
    {
        [0] = "weapon",      // Main Hand
        // [1] = off-hand    // Not tracked in planner
        [2] = "head",
        [3] = "body",
        [4] = "hands",
        // [5] = waist       // Legacy, always empty since 5.0
        [6] = "legs",
        [7] = "feet",
        [8] = "earring",
        [9] = "necklace",
        [10] = "bracelet",
        [11] = "ring1",
        [12] = "ring2",
        // [13] = soul crystal — Not tracked
    };

    public GearsetService(IDataManager dataManager, InventoryService inventory, IPluginLog log)
    {
        _dataManager = dataManager;
        _inventory = inventory;
        _log = log;
    }

    /// <summary>
    /// Read all valid saved gearsets from the game's RaptureGearsetModule.
    /// MUST be called on the framework/main thread.
    /// Returns a list of gearset data suitable for batch sync.
    /// </summary>
    public unsafe List<SavedGearsetData> ReadSavedGearsets()
    {
        var results = new List<SavedGearsetData>();

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
            {
                _log.Warning("[Gearset] RaptureGearsetModule not available");
                return results;
            }

            var itemSheet = _dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                _log.Warning("[Gearset] Item sheet not available");
                return results;
            }

            // FFXIV supports up to 100 gearset slots (indices 0-99)
            for (var i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var entry = module->GetGearset(i);
                if (entry == null)
                    continue;

                var classJobId = entry->ClassJob;
                if (!ClassJobToAbbrev.TryGetValue(classJobId, out var jobAbbrev))
                    continue;

                if (!TrackedJobs.Contains(jobAbbrev))
                    continue;

                var gearsetName = entry->NameString;
                if (string.IsNullOrWhiteSpace(gearsetName))
                    gearsetName = $"Gearset {i + 1}";

                var items = new List<PluginGearSlotDto>();
                var hasAnyItem = false;

                for (var slotIdx = 0; slotIdx < 14; slotIdx++)
                {
                    if (!GearsetSlotToSlotName.TryGetValue(slotIdx, out var slotName))
                        continue;

                    var itemId = entry->Items[slotIdx].ItemId;
                    // Strip HQ flag (item IDs above 1_000_000 indicate HQ in game memory)
                    var baseItemId = itemId > 1_000_000 ? itemId - 1_000_000 : itemId;

                    if (baseItemId == 0)
                    {
                        items.Add(new PluginGearSlotDto
                        {
                            Slot = slotName,
                            HasItem = false,
                            CurrentSource = "unknown",
                        });
                        continue;
                    }

                    hasAnyItem = true;
                    var itemRow = itemSheet.GetRowOrDefault(baseItemId);
                    var itemName = itemRow?.Name.ToString() ?? $"Item {baseItemId}";
                    var itemLevel = itemRow != null ? (int)itemRow.Value.LevelItem.RowId : 0;
                    var iconId = itemRow != null ? (int)itemRow.Value.Icon : 0;
                    var source = _inventory.ClassifySource(baseItemId);

                    items.Add(new PluginGearSlotDto
                    {
                        Slot = slotName,
                        HasItem = true,
                        CurrentSource = source,
                        ItemId = (int)baseItemId,
                        ItemName = itemName,
                        ItemLevel = itemLevel,
                        ItemIcon = iconId > 0 ? iconId.ToString() : null,
                        IsAugmented = false,
                    });
                }

                if (!hasAnyItem)
                    continue;

                var avgIlvl = items
                    .Where(it => it.ItemLevel is > 0)
                    .Select(it => it.ItemLevel!.Value)
                    .DefaultIfEmpty(0)
                    .Average();

                results.Add(new SavedGearsetData
                {
                    GearsetIndex = i,
                    GearsetName = gearsetName,
                    ClassJobId = classJobId,
                    Job = jobAbbrev,
                    Items = items,
                    AvgItemLevel = (int)Math.Round(avgIlvl),
                });
            }

            _log.Info($"[Gearset] Read {results.Count} saved gearsets");
        }
        catch (Exception ex)
        {
            _log.Error($"[Gearset] Failed to read saved gearsets: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Deduplicate gearsets by job — when multiple gearsets exist for the same job,
    /// keep the one with the highest average item level.
    /// </summary>
    public static List<SavedGearsetData> DeduplicateByJob(List<SavedGearsetData> gearsets)
    {
        return gearsets
            .GroupBy(g => g.Job, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(g => g.AvgItemLevel).ThenBy(g => g.GearsetIndex).First())
            .ToList();
    }
}

/// <summary>A saved gearset read from game memory, ready for sync.</summary>
public class SavedGearsetData
{
    public int GearsetIndex { get; set; }
    public string GearsetName { get; set; } = string.Empty;
    public byte ClassJobId { get; set; }
    public string Job { get; set; } = string.Empty;
    public List<PluginGearSlotDto> Items { get; set; } = new();
    public int AvgItemLevel { get; set; }
}

/// <summary>Item slot in a saved gearset, matching the existing plugin gear sync payload shape.</summary>
public class PluginGearSlotDto
{
    public string Slot { get; set; } = string.Empty;
    public bool HasItem { get; set; }
    public string CurrentSource { get; set; } = "unknown";
    public bool IsAugmented { get; set; }
    public int? ItemId { get; set; }
    public string? ItemName { get; set; }
    public int? ItemLevel { get; set; }
    public string? ItemIcon { get; set; }
}
