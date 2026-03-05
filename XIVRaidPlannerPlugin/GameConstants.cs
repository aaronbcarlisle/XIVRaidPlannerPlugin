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
}
