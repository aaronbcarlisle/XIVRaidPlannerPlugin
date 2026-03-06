using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Highlights BiS items in game UI addons (NeedGreed, shops, inventory).
/// Uses AtkNode color manipulation following BisBuddy's approach.
///
/// NOTE: All handlers are scaffolding stubs pending in-game addon inspection.
/// No listeners are registered until implementations are complete.
/// </summary>
public class AddonHighlightService : IDisposable
{
    private readonly ItemMappingService _itemMapping;
    private readonly Configuration _config;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPluginLog _log;

    public AddonHighlightService(
        ItemMappingService itemMapping,
        Configuration config,
        IAddonLifecycle addonLifecycle,
        IPluginLog log)
    {
        _itemMapping = itemMapping;
        _config = config;
        _addonLifecycle = addonLifecycle;
        _log = log;
    }

    /// <summary>Register addon listeners for BiS highlighting.</summary>
    /// <remarks>No-op until highlighting implementations are complete (requires in-game addon inspection).</remarks>
    public void Register()
    {
        // Intentionally not registering listeners yet — the handlers are scaffolding stubs.
        // Registering PreDraw listeners with no useful work wastes per-frame cycles.
        // Will register once addon node traversal is implemented after in-game testing.
        _log.Info("[Highlight] Addon highlighting not yet implemented — skipping listener registration");
    }

    public void Dispose() { }
}
