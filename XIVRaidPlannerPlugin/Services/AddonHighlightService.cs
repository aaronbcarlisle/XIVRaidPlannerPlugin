using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Highlights BiS items in game UI addons (NeedGreed loot window, ShopExchangeItem/Currency vendors).
/// Uses PreDraw event to apply highlighting every frame while the addon is visible.
/// AtkValues layout constants sourced from BisBuddy's reverse-engineering.
/// </summary>
public unsafe class AddonHighlightService : IDisposable
{
    private readonly ItemMappingService _itemMapping;
    private readonly Configuration _config;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPluginLog _log;
    private bool _registered;
    private bool _loggedBisItems; // Prevent spamming logs on every PreDraw frame
    private bool _loggedTreeList;

    // Track modified nodes so we can restore original colors
    private readonly Dictionary<nint, SavedColor> _modifiedNodes = new();

    // ==================== ShopExchangeItem Constants (from BisBuddy) ====================
    // Item count at AtkValues[3], item IDs starting at AtkValues[1064]
    // Tree list component at node ID 20
    private const int ShopExchItemCountIdx = 3;
    private const int ShopExchItemIdStart = 1064;
    private const uint ShopExchTreeListNodeId = 20;

    // ==================== ShopExchangeCurrency Constants (from BisBuddy) ====================
    // Item count at AtkValues[4], item IDs starting at AtkValues[1064]
    private const int ShopCurrItemCountIdx = 4;
    private const int ShopCurrItemIdStart = 1064;
    private const uint ShopCurrTreeListNodeId = 20;

    // ==================== NeedGreed Constants (from BisBuddy) ====================
    // Uses typed AddonNeedGreed struct, list component at node ID 6
    private const uint NeedGreedListNodeId = 6;

    public AddonHighlightService(
        ItemMappingService itemMapping,
        Configuration config,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log)
    {
        _itemMapping = itemMapping;
        _config = config;
        _addonLifecycle = addonLifecycle;
        _log = log;
    }

    /// <summary>Register addon lifecycle listeners for BiS highlighting.</summary>
    public void Register()
    {
        if (_registered) return;

        // PreDraw fires every frame — ensures addon data is fully populated before we process
        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "NeedGreed", OnNeedGreedPreDraw);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "NeedGreed", OnAddonClose);

        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ShopExchangeItem", OnShopExchangeItemPreDraw);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ShopExchangeItem", OnAddonClose);

        _addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ShopExchangeCurrency", OnShopExchangeCurrencyPreDraw);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ShopExchangeCurrency", OnAddonClose);

        _registered = true;
        _log.Info("[Highlight] Addon highlighting registered (NeedGreed, ShopExchangeItem, ShopExchangeCurrency)");
    }

    // ==================== NeedGreed ====================

    private void OnNeedGreedPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!_config.EnableBisHighlighting || !_itemMapping.HasData) return;

        try
        {
            var addon = (AddonNeedGreed*)args.Addon.Address;
            if (addon == null || !addon->IsVisible) return;

            // Build mapping of item index → BiS status
            var bisIndices = new Dictionary<int, bool>();
            for (var i = 0; i < addon->Items.Length; i++)
            {
                var itemId = addon->Items[i].ItemId;
                if (itemId > 0 && _itemMapping.IsBisItem(itemId))
                    bisIndices[i] = true;
            }

            if (bisIndices.Count == 0)
            {
                ClearHighlights();
                return;
            }

            // Get the list component by node ID
            var listComponent = (AtkComponentList*)addon->GetComponentByNodeId(NeedGreedListNodeId);
            if (listComponent == null) return;

            for (var i = 0; i < listComponent->ListLength; i++)
            {
                var renderer = listComponent->ItemRendererList[i].AtkComponentListItemRenderer;
                var isBis = bisIndices.ContainsKey(renderer->ListItemIndex);
                var ownerNode = (AtkResNode*)renderer->OwnerNode;

                if (isBis)
                    ApplyBisTintRecursive(ownerNode);
                else
                    RestoreNodeRecursive(ownerNode);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Highlight] NeedGreed PreDraw: {ex.Message}");
        }
    }

    // ==================== ShopExchangeItem ====================

    private void OnShopExchangeItemPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!_config.EnableShopHighlighting || !_itemMapping.HasData) return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible) return;

            HighlightShopItems(addon, "ShopExchangeItem",
                ShopExchItemCountIdx, ShopExchItemIdStart, ShopExchTreeListNodeId);
        }
        catch (Exception ex)
        {
            _log.Error($"[Highlight] ShopExchangeItem PreDraw: {ex.Message}");
        }
    }

    // ==================== ShopExchangeCurrency ====================

    private void OnShopExchangeCurrencyPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!_config.EnableShopHighlighting || !_itemMapping.HasData) return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible) return;

            HighlightShopItems(addon, "ShopExchangeCurrency",
                ShopCurrItemCountIdx, ShopCurrItemIdStart, ShopCurrTreeListNodeId);
        }
        catch (Exception ex)
        {
            _log.Error($"[Highlight] ShopExchangeCurrency PreDraw: {ex.Message}");
        }
    }

    // ==================== Shared Shop Highlighting ====================

    /// <summary>
    /// Highlight BiS items in a ShopExchange addon using AtkValues for item IDs
    /// and AtkComponentTreeList for accessing visible item renderers.
    /// </summary>
    private void HighlightShopItems(AtkUnitBase* addon, string addonName,
        int itemCountIdx, int itemIdStart, uint treeListNodeId)
    {
        var atkValues = addon->AtkValuesSpan;
        if (atkValues.Length <= itemCountIdx) return;

        var itemCount = atkValues[itemCountIdx].Int;
        if (itemCount <= 0) return;

        // Detect if a shield item is present (indented under a 1H weapon).
        // Shields are stored at the END of the item ID list (at itemIdStart + itemCount)
        // but visually appear at position 1 (AddonShieldIndex), shifting all other indices by +1.
        const int addonShieldIndex = 1;
        var shieldPresent = false;
        var shieldIdx = itemIdStart + itemCount;
        if (shieldIdx < atkValues.Length
            && atkValues[shieldIdx].Type == AtkValueType.UInt
            && atkValues[shieldIdx].UInt > 0)
        {
            shieldPresent = true;
            if (!_loggedBisItems)
                _log.Info($"[Highlight] {addonName}: shield detected at AtkValues[{shieldIdx}] (ID={atkValues[shieldIdx].UInt})");
        }

        // Build set of BiS ListItemIndex values (accounting for shield offset)
        var bisListItemIndices = new HashSet<int>();
        for (var i = 0; i < itemCount; i++)
        {
            var idx = itemIdStart + i;
            if (idx >= atkValues.Length) break;

            var v = atkValues[idx];
            if (v.Type != AtkValueType.UInt) continue;

            if (_itemMapping.IsBisItem(v.UInt))
            {
                // When a shield is present and this item is at or after the shield position,
                // the visual ListItemIndex is shifted by +1
                var shieldOffset = (shieldPresent && i >= addonShieldIndex) ? 1 : 0;
                var listItemIdx = i + shieldOffset;
                bisListItemIndices.Add(listItemIdx);

                if (!_loggedBisItems)
                    _log.Info($"[Highlight] {addonName}: BiS ID={v.UInt} atkIdx={i} -> listItemIdx={listItemIdx} (shieldOffset={shieldOffset})");
            }
        }

        if (!_loggedBisItems && bisListItemIndices.Count > 0)
        {
            _log.Info($"[Highlight] {addonName}: {bisListItemIndices.Count} BiS item(s), itemCount={itemCount}, shield={shieldPresent}");
            _loggedBisItems = true;
        }

        // Find the tree list component — try GetNodeById first, fall back to scanning
        AtkComponentTreeList* treeList = null;

        var treeListNode = addon->GetNodeById(treeListNodeId);
        if (treeListNode != null && (ushort)treeListNode->Type >= 1000) // Any component subtype (1000+)
        {
            treeList = (AtkComponentTreeList*)((AtkComponentNode*)treeListNode)->Component;
        }

        // Fallback: scan all nodes for a component that looks like a tree list
        if (treeList == null)
        {
            for (var n = 0; n < addon->UldManager.NodeListCount; n++)
            {
                var node = addon->UldManager.NodeList[n];
                if (node == null || (ushort)node->Type < 1000) continue; // Skip non-component nodes
                var comp = (AtkComponentNode*)node;
                if (comp->Component == null) continue;
                // Try casting to tree list and check if it has items
                try
                {
                    var candidate = (AtkComponentTreeList*)comp->Component;
                    if (candidate->Items.Count > 0)
                    {
                        treeList = candidate;
                        if (!_loggedTreeList)
                            _log.Info($"[Highlight] {addonName}: found tree list via scan (nodeId={node->NodeId}, items={candidate->Items.Count})");
                        break;
                    }
                }
                catch { /* not a tree list */ }
            }
        }

        if (treeList == null)
        {
            if (!_loggedTreeList)
            {
                _log.Warning($"[Highlight] {addonName}: no tree list component found (GetNodeById({treeListNodeId}) returned {(treeListNode == null ? "null" : treeListNode->Type.ToString())})");
                _loggedTreeList = true;
            }
            return;
        }

        if (!_loggedTreeList)
        {
            _log.Info($"[Highlight] {addonName}: tree list has {treeList->Items.Count} visible items");
            _loggedTreeList = true;
        }

        // Iterate visible tree list items and highlight BiS matches
        var highlighted = 0;
        for (var i = 0; i < treeList->Items.Count; i++)
        {
            var itemPtr = treeList->Items[i].Value;
            if (itemPtr == null || itemPtr->Renderer == null) continue;

            var ownerNode = (AtkResNode*)itemPtr->Renderer->OwnerNode;
            if (ownerNode == null) continue;

            var renderer = (AtkComponentListItemRenderer*)itemPtr->Renderer->OwnerNode->Component;
            if (renderer == null) continue;

            var listIndex = renderer->ListItemIndex;
            var isBis = bisListItemIndices.Contains(listIndex);

            if (isBis)
            {
                ApplyBisTintRecursive(ownerNode);
                highlighted++;
            }
            else
            {
                RestoreNodeRecursive(ownerNode);
            }
        }

        if (!_loggedBisItems && highlighted > 0)
            _log.Info($"[Highlight] {addonName}: highlighted {highlighted} item(s)");
    }

    // ==================== Color Tinting ====================

    /// <summary>Apply tint to a node and all its children/siblings for full-row highlighting.</summary>
    private void ApplyBisTintRecursive(AtkResNode* node, int depth = 0)
    {
        if (node == null || depth > 10) return;
        ApplyBisTint(node);
        ApplyBisTintRecursive(node->ChildNode, depth + 1);
        if (depth > 0) // Don't walk siblings of the root node
            ApplyBisTintRecursive(node->PrevSiblingNode, depth);
    }

    /// <summary>Restore a node and all its children/siblings.</summary>
    private void RestoreNodeRecursive(AtkResNode* node, int depth = 0)
    {
        if (node == null || depth > 10) return;
        RestoreNode(node);
        RestoreNodeRecursive(node->ChildNode, depth + 1);
        if (depth > 0)
            RestoreNodeRecursive(node->PrevSiblingNode, depth);
    }

    /// <summary>Apply a strong teal highlight tint to a node, saving its original colors.</summary>
    private void ApplyBisTint(AtkResNode* node)
    {
        if (node == null) return;
        var ptr = (nint)node;

        // Save original colors only on first modification
        if (!_modifiedNodes.ContainsKey(ptr))
        {
            _modifiedNodes[ptr] = new SavedColor
            {
                MultR = node->MultiplyRed,
                MultG = node->MultiplyGreen,
                MultB = node->MultiplyBlue,
                AddR = node->AddRed,
                AddG = node->AddGreen,
                AddB = node->AddBlue,
            };
        }

        // Strong teal tint: suppress red, boost green/blue via both multiply and additive
        node->MultiplyRed = 120;
        node->MultiplyGreen = 255;
        node->MultiplyBlue = 230;
        node->AddRed = -20;
        node->AddGreen = 40;
        node->AddBlue = 30;
    }

    /// <summary>Restore a single node to its original colors if it was modified.</summary>
    private void RestoreNode(AtkResNode* node)
    {
        if (node == null) return;
        var ptr = (nint)node;

        if (_modifiedNodes.TryGetValue(ptr, out var saved))
        {
            node->MultiplyRed = saved.MultR;
            node->MultiplyGreen = saved.MultG;
            node->MultiplyBlue = saved.MultB;
            node->AddRed = saved.AddR;
            node->AddGreen = saved.AddG;
            node->AddBlue = saved.AddB;
            _modifiedNodes.Remove(ptr);
        }
    }

    /// <summary>Restore all modified nodes and clear the tracking dictionary.</summary>
    private void ClearHighlights()
    {
        foreach (var (ptr, saved) in _modifiedNodes)
        {
            try
            {
                var node = (AtkResNode*)ptr;
                node->MultiplyRed = saved.MultR;
                node->MultiplyGreen = saved.MultG;
                node->MultiplyBlue = saved.MultB;
                node->AddRed = saved.AddR;
                node->AddGreen = saved.AddG;
                node->AddBlue = saved.AddB;
            }
            catch { /* Node may have been freed */ }
        }

        _modifiedNodes.Clear();
    }

    // ==================== Lifecycle ====================

    private void OnAddonClose(AddonEvent type, AddonArgs args)
    {
        ClearHighlights();
        _loggedBisItems = false;
        _loggedTreeList = false;
    }

    public void Dispose()
    {
        if (!_registered) return;

        ClearHighlights();

        _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "NeedGreed", OnNeedGreedPreDraw);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "NeedGreed", OnAddonClose);

        _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ShopExchangeItem", OnShopExchangeItemPreDraw);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ShopExchangeItem", OnAddonClose);

        _addonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ShopExchangeCurrency", OnShopExchangeCurrencyPreDraw);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ShopExchangeCurrency", OnAddonClose);

        _registered = false;
    }

    /// <summary>Stores a node's original color values for restoration.</summary>
    private struct SavedColor
    {
        public byte MultR, MultG, MultB;
        public short AddR, AddG, AddB;
    }
}
