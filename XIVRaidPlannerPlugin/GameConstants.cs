using System;
using System.Collections.Generic;

namespace XIVRaidPlannerPlugin;

/// <summary>
/// Shared game data constants used across windows and services.
/// </summary>
public static class GameConstants
{
    /// <summary>Material type -> eligible augmentation slots (twine=left side, glaze=right side, solvent=weapon).</summary>
    public static readonly Dictionary<string, string[]> MaterialSlotOptions = new()
    {
        ["twine"] = new[] { "head", "body", "hands", "legs", "feet" },
        ["glaze"] = new[] { "earring", "necklace", "bracelet", "ring1", "ring2" },
        ["solvent"] = new[] { "weapon" },
    };

    /// <summary>Job abbreviation -> embedded PNG filename (lowercase).</summary>
    public static readonly Dictionary<string, string> JobIconFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "pld",
        ["WAR"] = "war",
        ["DRK"] = "drk",
        ["GNB"] = "gnb",
        ["WHM"] = "whm",
        ["SCH"] = "sch",
        ["AST"] = "ast",
        ["SGE"] = "sge",
        ["MNK"] = "mnk",
        ["DRG"] = "drg",
        ["NIN"] = "nin",
        ["SAM"] = "sam",
        ["RPR"] = "rpr",
        ["VPR"] = "vpr",
        ["BRD"] = "brd",
        ["MCH"] = "mch",
        ["DNC"] = "dnc",
        ["BLM"] = "blm",
        ["SMN"] = "smn",
        ["RDM"] = "rdm",
        ["PCT"] = "pct",
    };
}
