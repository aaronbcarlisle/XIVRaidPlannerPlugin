using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Highlights BiS items in game UI addons (NeedGreed, shops, inventory).
/// Uses AtkNode color manipulation following BisBuddy's approach.
///
/// NOTE: The actual AtkNode traversal for each addon requires in-game inspection
/// of node structures. The framework is ready; specific node indices need testing.
/// </summary>
public class AddonHighlightService : IDisposable
{
    private readonly ItemMappingService _itemMapping;
    private readonly Configuration _config;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPluginLog _log;

    // Track consecutive failures for auto-disable
    private int _failureCount;
    private const int MaxFailures = 5;
    private bool _disabled;

    // Track registration state
    private bool _shopsRegistered;
    private bool _inventoryRegistered;

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

    /// <summary>Register core addon listeners (NeedGreed + MateriaAttach).</summary>
    /// <remarks>No-op until highlighting implementations are complete (requires in-game addon inspection).</remarks>
    public void Register()
    {
        // Intentionally not registering listeners yet — the handlers are scaffolding stubs.
        // Registering PreDraw listeners with no useful work wastes per-frame cycles.
        // Will register once OnNeedGreedPreDraw/OnMateriaAttachPreDraw are implemented.
        _log.Info("[Highlight] Addon highlighting not yet implemented — skipping listener registration");
    }

    /// <summary>Register for shop addons (Phase 4).</summary>
    public void RegisterShopAddons()
    {
        if (_shopsRegistered) return;
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "Shop", OnShopPreDraw);
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ShopExchangeItem", OnShopExchangePreDraw);
        _shopsRegistered = true;
        _log.Info("[Highlight] Registered shop addon listeners");
    }

    /// <summary>Register for inventory addons (Phase 4).</summary>
    public void RegisterInventoryAddons()
    {
        if (_inventoryRegistered) return;
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "InventoryLarge", OnInventoryPreDraw);
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "InventoryExpansion", OnInventoryPreDraw);
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ArmouryChest", OnArmouryPreDraw);
        _inventoryRegistered = true;
        _log.Info("[Highlight] Registered inventory addon listeners");
    }

    // ==================== NeedGreed Addon ====================

    private void OnNeedGreedPreDraw(AddonEvent type, AddonArgs args)
    {
        if (_disabled || !_config.EnableBisHighlighting || !_itemMapping.HasData)
            return;

        try
        {
            // The NeedGreed addon stores loot items in its AtkValues.
            // Item IDs are at specific AtkValue indices (version-dependent).
            //
            // Implementation requires in-game addon inspection to determine:
            // 1. Which AtkValue indices contain item IDs
            // 2. Which child nodes control item icon/background
            // 3. How to apply color tint via AtkResNode.Color
            //
            // Reference: BisBuddy source code for NeedGreed handling
            // This will be completed after in-game testing with an addon inspector.

            _failureCount = 0;
        }
        catch (Exception ex)
        {
            _failureCount++;
            _log.Error($"[Highlight] NeedGreed error ({_failureCount}/{MaxFailures}): {ex.Message}");
            if (_failureCount >= MaxFailures)
            {
                _disabled = true;
                _log.Error("[Highlight] Too many failures — auto-disabled highlighting");
            }
        }
    }

    // ==================== Shop Addons (Phase 4) ====================

    private void OnShopPreDraw(AddonEvent type, AddonArgs args)
    {
        if (_disabled || !_config.EnableShopHighlighting || !_itemMapping.HasData)
            return;

        // Phase 4: Implement shop item highlighting
    }

    private void OnShopExchangePreDraw(AddonEvent type, AddonArgs args)
    {
        if (_disabled || !_config.EnableShopHighlighting || !_itemMapping.HasData)
            return;

        // Phase 4: Implement book exchange shop highlighting
    }

    // ==================== Inventory Addons (Phase 4) ====================

    private void OnInventoryPreDraw(AddonEvent type, AddonArgs args)
    {
        if (_disabled || !_config.EnableInventoryHighlighting || !_itemMapping.HasData)
            return;

        // Phase 4: Implement inventory item highlighting
    }

    private void OnArmouryPreDraw(AddonEvent type, AddonArgs args)
    {
        if (_disabled || !_config.EnableInventoryHighlighting || !_itemMapping.HasData)
            return;

        // Phase 4: Implement armoury chest highlighting
    }

    // ==================== Materia Attach (Phase 5B) ====================

    private void OnMateriaAttachPreDraw(AddonEvent type, AddonArgs args)
    {
        if (_disabled || !_config.EnableBisHighlighting || !_itemMapping.HasData)
            return;

        try
        {
            // The MateriaAttach addon shows:
            // 1. Target gear piece being melded
            // 2. List of available materia
            //
            // Implementation requires in-game inspection to determine:
            // 1. AtkValue index for target item ID
            // 2. AtkValue layout for materia list item IDs
            // 3. Node structure for highlighting matches
            //
            // Reference: BisBuddy materia handling

            _failureCount = 0;
        }
        catch (Exception ex)
        {
            _failureCount++;
            _log.Error($"[Highlight] MateriaAttach error ({_failureCount}/{MaxFailures}): {ex.Message}");
            if (_failureCount >= MaxFailures)
            {
                _disabled = true;
                _log.Error("[Highlight] Too many failures — auto-disabled highlighting");
            }
        }
    }

    public void Dispose()
    {
        // NeedGreed and MateriaAttach not yet registered (scaffolding only)
        if (_shopsRegistered)
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "Shop", OnShopPreDraw);
            _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ShopExchangeItem", OnShopExchangePreDraw);
        }
        if (_inventoryRegistered)
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "InventoryLarge", OnInventoryPreDraw);
            _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "InventoryExpansion", OnInventoryPreDraw);
            _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ArmouryChest", OnArmouryPreDraw);
        }
    }
}
