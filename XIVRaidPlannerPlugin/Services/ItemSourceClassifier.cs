namespace XIVRaidPlannerPlugin.Services;

/// <summary>Pure source classification by item name + item level. Mirrors backend bis.py.</summary>
public static class ItemSourceClassifier
{
    private const int IL_SAVAGE = 795;
    private const int IL_SAVAGE_ARMOR = 790;  // Savage armor/accessories + augmented tomestone
    private const int IL_CATCHUP = 780;       // Alliance raid catch-up + unaugmented tomestone
    private const int IL_CRAFTED = 770;       // Crafted pentamelded
    private const int IL_NORMAL = 760;        // Normal raid

    public static string Classify(string name, int iLv)
    {
        var lowerName = name.ToLowerInvariant();

        // Savage raid drops (iLv 790/795)
        if (iLv >= IL_SAVAGE_ARMOR)
        {
            // Check for augmented tomestone pattern first ("Aug." prefix)
            if (lowerName.StartsWith("aug") || lowerName.Contains("augmented"))
                return "tome_up";

            // Current tier savage patterns (Dawntrail 7.2 — AAC Heavyweight Savage)
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
            // Unaugmented tome patterns (7.2 tomestone gear names)
            if (lowerName.Contains("quetzalli") || lowerName.Contains("neo kingdom") ||
                lowerName.Contains("bygone"))
                return "tome";

            return "catchup";
        }

        // Crafted gear (iLv ~770)
        if (iLv >= IL_CRAFTED)
        {
            // Crafted gear patterns (7.2 crafted names)
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

    public static bool SourceMatchesBis(string equippedSource, string bisSource) => bisSource switch
    {
        "raid" => equippedSource is "savage" or "raid",
        "tome" => equippedSource is "tome" or "tome_up",
        "base_tome" => equippedSource is "tome" or "base_tome",
        "crafted" => equippedSource == "crafted",
        _ => false,
    };
}
