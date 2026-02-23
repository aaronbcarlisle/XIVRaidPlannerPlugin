using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace XIVRaidPlannerPlugin;

/// <summary>
/// Persisted plugin configuration.
/// Saved to Dalamud's config directory automatically.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Base URL of the FFXIV Raid Planner API (e.g., "https://xivraidplanner.app").</summary>
    public string ApiBaseUrl { get; set; } = "https://xivraidplanner.app";

    /// <summary>API key (xrp_...) for authentication.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Selected static group UUID.</summary>
    public string DefaultGroupId { get; set; } = string.Empty;

    /// <summary>Display name of the selected static group (for overlay header).</summary>
    public string DefaultGroupName { get; set; } = string.Empty;

    /// <summary>Selected tier UUID.</summary>
    public string DefaultTierId { get; set; } = string.Empty;

    /// <summary>Display name of the selected tier (for overlay header).</summary>
    public string DefaultTierName { get; set; } = string.Empty;

    /// <summary>How loot logging should work.</summary>
    public AutoLogMode AutoLogMode { get; set; } = AutoLogMode.Confirm;

    /// <summary>Whether to show the priority overlay in savage instances.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Warn if leaving an instance with unclaimed priority loot.</summary>
    public bool EnableLeaveWarning { get; set; } = true;

    /// <summary>Scale factor for the overlay (0.5 - 2.0).</summary>
    public float OverlayScale { get; set; } = 1.0f;

    /// <summary>
    /// Manual overrides for matching in-game character names to planner player IDs.
    /// Key = "Firstname Lastname", Value = planner player UUID.
    /// </summary>
    public Dictionary<string, string> PlayerNameOverrides { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

public enum AutoLogMode
{
    /// <summary>Show a confirmation dialog before logging each drop.</summary>
    Confirm,

    /// <summary>Log automatically with a toast notification and undo option.</summary>
    Auto,

    /// <summary>Never auto-log; only log via manual button clicks.</summary>
    Manual,
}
