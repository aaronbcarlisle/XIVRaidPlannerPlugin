using System;
using System.Collections.Generic;
using System.Numerics;

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

    /// <summary>Role colors matching the web app design system.</summary>
    public static readonly Dictionary<string, Vector4> RoleColors = new()
    {
        ["tank"] = new Vector4(0.353f, 0.624f, 0.831f, 1f),     // #5a9fd4
        ["healer"] = new Vector4(0.353f, 0.831f, 0.565f, 1f),   // #5ad490
        ["melee"] = new Vector4(0.831f, 0.353f, 0.353f, 1f),    // #d45a5a
        ["ranged"] = new Vector4(0.831f, 0.627f, 0.353f, 1f),   // #d4a05a
        ["caster"] = new Vector4(0.706f, 0.353f, 0.831f, 1f),   // #b45ad4
    };

    /// <summary>Job abbreviation -> embedded PNG filename (lowercase).</summary>
    public static readonly Dictionary<string, string> JobIconFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "pld", ["WAR"] = "war", ["DRK"] = "drk", ["GNB"] = "gnb",
        ["WHM"] = "whm", ["SCH"] = "sch", ["AST"] = "ast", ["SGE"] = "sge",
        ["MNK"] = "mnk", ["DRG"] = "drg", ["NIN"] = "nin", ["SAM"] = "sam", ["RPR"] = "rpr", ["VPR"] = "vpr",
        ["BRD"] = "brd", ["MCH"] = "mch", ["DNC"] = "dnc",
        ["BLM"] = "blm", ["SMN"] = "smn", ["RDM"] = "rdm", ["PCT"] = "pct",
    };
}
