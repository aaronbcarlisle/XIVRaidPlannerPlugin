using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XIVRaidPlannerPlugin;

/// <summary>
/// Persisted plugin configuration.
/// Saved to Dalamud's config directory automatically.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public const string DefaultApiBaseUrl = "https://api.xivraidplanner.app";
    public const string DefaultFrontendBaseUrl = "https://xivraidplanner.app";

    public int Version { get; set; } = 0;

    /// <summary>Whether to use custom API/Frontend URLs instead of the defaults.</summary>
    public bool UseCustomUrls { get; set; } = false;

    /// <summary>Custom API base URL (only used when UseCustomUrls is true).</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>Custom frontend base URL (only used when UseCustomUrls is true).</summary>
    public string FrontendBaseUrl { get; set; } = string.Empty;

    /// <summary>Effective API URL — returns custom URL if enabled, otherwise the default.</summary>
    [JsonIgnore]
    public string EffectiveApiBaseUrl =>
        UseCustomUrls && !string.IsNullOrEmpty(ApiBaseUrl) ? ApiBaseUrl : DefaultApiBaseUrl;

    /// <summary>
    /// Effective frontend URL — returns custom URL if enabled, otherwise the default.
    /// When using custom URLs but no custom frontend URL is set, falls back to the effective API URL
    /// to keep API and web links pointed at the same environment.
    /// </summary>
    [JsonIgnore]
    public string EffectiveFrontendBaseUrl =>
        UseCustomUrls
            ? (!string.IsNullOrEmpty(FrontendBaseUrl) ? FrontendBaseUrl : EffectiveApiBaseUrl)
            : DefaultFrontendBaseUrl;

    /// <summary>API key (xrp_...) for authentication.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Selected static group UUID.</summary>
    public string DefaultGroupId { get; set; } = string.Empty;

    /// <summary>Display name of the selected static group (for overlay header).</summary>
    public string DefaultGroupName { get; set; } = string.Empty;

    /// <summary>Share code of the selected static group (for web app links).</summary>
    public string DefaultGroupShareCode { get; set; } = string.Empty;

    /// <summary>Selected tier UUID.</summary>
    public string DefaultTierId { get; set; } = string.Empty;

    /// <summary>Display name of the selected tier (for overlay header).</summary>
    public string DefaultTierName { get; set; } = string.Empty;

    /// <summary>How loot logging should work.</summary>
    public AutoLogMode AutoLogMode { get; set; } = AutoLogMode.Confirm;

    /// <summary>Whether to show the priority overlay in savage instances (master toggle).</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Show overlay when entering a raid instance.</summary>
    public bool ShowOverlayOnEntry { get; set; } = true;

    /// <summary>Show overlay when a duty completes (boss killed).</summary>
    public bool ShowOverlayOnDutyComplete { get; set; } = false;

    /// <summary>Show overlay when the loot window (Need/Greed) opens.</summary>
    public bool ShowOverlayOnLootWindow { get; set; } = false;

    /// <summary>Warn if leaving an instance with unclaimed priority loot.</summary>
    public bool EnableLeaveWarning { get; set; } = true;

    /// <summary>Show the BiS viewer in savage instances.</summary>
    public bool ShowBisViewer { get; set; } = false;

    /// <summary>Auto-sync equipped gear to web app on savage entry.</summary>
    public bool AutoSyncGear { get; set; } = false;

    /// <summary>Highlight BiS items in the Need/Greed loot window.</summary>
    public bool EnableBisHighlighting { get; set; } = true;

    /// <summary>Highlight BiS items in tome/book vendor shops.</summary>
    public bool EnableShopHighlighting { get; set; } = true;

    /// <summary>Highlight BiS items in inventory/armoury chest.</summary>
    public bool EnableInventoryHighlighting { get; set; } = true;

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
