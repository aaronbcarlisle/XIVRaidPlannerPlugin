using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

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
    // Per-addon log flags to prevent spamming logs on every PreDraw frame
    private readonly HashSet<string> _loggedBisItems = new();
    private readonly HashSet<string> _loggedTreeList = new();

    // Track modified nodes per addon so clearing one doesn't affect others
    private readonly Dictionary<string, Dictionary<nint, SavedColor>> _modifiedNodesByAddon = new();

    // Reusable collections to avoid per-frame heap allocations in PreDraw handlers
    private readonly Dictionary<int, bool> _bisIndices = new();
    private readonly HashSet<int> _bisListItemIndices = new();

    // ==================== ShopExchangeItem Constants (from BisBuddy) ====================
    // - Item count at AtkValues[3]
    // - Item IDs starting at AtkValues[1066]
    // - Per-item visibility values starting at AtkValues[1554]; <= 1 means visible
    // - Tree list component at node ID 20
    //
    // The visibility list is critical: tree row ListItemIndex is a *compressed* index
    // across only the currently-visible items (i.e. matches whatever sub-section /
    // filter the player picked in the shop). Without it, BiS matches map to the wrong
    // tree rows in any multi-section shop.
    //
    // NOTE: These AtkValue offsets are tied to the FFXIV CLIENT version (the in-game
    // shop addon layout), not the Dalamud SDK version. They are NOT updated by SDK
    // bumps — they need re-verification after a major game patch that touches shop UI.
    // If shop highlighting stops working after a game patch, suspect these constants
    // before suspecting the rest of this file. Values cross-referenced against
    // BisBuddy (RajahOmen/BisBuddy) which uses the same reverse-engineered layout.
    private const int ShopExchItemCountIdx = 3;
    private const int ShopExchItemIdStart = 1066;
    private const int ShopExchFilterStart = 1554;
    private const uint ShopExchTreeListNodeId = 20;

    // ==================== ShopExchangeCurrency Constants (from BisBuddy) ====================
    // Same layout as ShopExchangeItem except item count lives at AtkValues[4].
    private const int ShopCurrItemCountIdx = 4;
    private const int ShopCurrItemIdStart = 1066;
    private const int ShopCurrFilterStart = 1554;
    private const uint ShopCurrTreeListNodeId = 20;

    // Items with filter value > this are filtered out and NOT rendered in the tree.
    private const uint ShopFilterVisibleMaxValue = 1;

    // ==================== NeedGreed Constants (from BisBuddy) ====================
    // Uses typed AddonNeedGreed struct, list component at node ID 6
    private const uint NeedGreedListNodeId = 6;

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
        const string addonName = "NeedGreed";

        if (!_config.EnableBisHighlighting || !_itemMapping.HasData)
        {
            ClearHighlights(addonName);
            return;
        }

        try
        {
            var addon = (AddonNeedGreed*)args.Addon.Address;
            if (addon == null || !addon->IsVisible)
            {
                ClearHighlights(addonName);
                return;
            }

            // Build mapping of item index → BiS status
            _bisIndices.Clear();
            for (var i = 0; i < addon->Items.Length; i++)
            {
                var itemId = addon->Items[i].ItemId;
                if (itemId > 0 && _itemMapping.IsBisItem(itemId))
                    _bisIndices[i] = true;
            }

            if (_bisIndices.Count == 0)
            {
                ClearHighlights(addonName);
                return;
            }

            // Get the list component by node ID
            var listComponent = (AtkComponentList*)addon->GetComponentByNodeId(NeedGreedListNodeId);
            if (listComponent == null)
            {
                ClearHighlights(addonName);
                return;
            }

            for (var i = 0; i < listComponent->ListLength; i++)
            {
                var renderer = listComponent->ItemRendererList[i].AtkComponentListItemRenderer;
                var isBis = _bisIndices.ContainsKey(renderer->ListItemIndex);
                var ownerNode = (AtkResNode*)renderer->OwnerNode;

                if (isBis)
                    ApplyBisTintRecursive(addonName, ownerNode);
                else
                    RestoreNodeRecursive(addonName, ownerNode);
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
        const string addonName = "ShopExchangeItem";

        if (!_config.EnableShopHighlighting || !_itemMapping.HasData)
        {
            ClearHighlights(addonName);
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible)
            {
                ClearHighlights(addonName);
                return;
            }

            HighlightShopItems(addon, addonName,
                ShopExchItemCountIdx, ShopExchItemIdStart, ShopExchFilterStart, ShopExchTreeListNodeId);
        }
        catch (Exception ex)
        {
            _log.Error($"[Highlight] ShopExchangeItem PreDraw: {ex.Message}");
        }
    }

    // ==================== ShopExchangeCurrency ====================

    private void OnShopExchangeCurrencyPreDraw(AddonEvent type, AddonArgs args)
    {
        const string addonName = "ShopExchangeCurrency";

        if (!_config.EnableShopHighlighting || !_itemMapping.HasData)
        {
            ClearHighlights(addonName);
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible)
            {
                ClearHighlights(addonName);
                return;
            }

            HighlightShopItems(addon, addonName,
                ShopCurrItemCountIdx, ShopCurrItemIdStart, ShopCurrFilterStart, ShopCurrTreeListNodeId);
        }
        catch (Exception ex)
        {
            _log.Error($"[Highlight] ShopExchangeCurrency PreDraw: {ex.Message}");
        }
    }

    // ==================== Shared Shop Highlighting ====================

    /// <summary>
    /// Highlight BiS items in a ShopExchange addon. Maps raw AtkValues item indices
    /// to the *compressed* (filtered/visible) index that the tree's ListItemIndex
    /// uses, so we highlight the correct row even when a sub-section is selected.
    /// </summary>
    private void HighlightShopItems(AtkUnitBase* addon, string addonName,
        int itemCountIdx, int itemIdStart, int filterStart, uint treeListNodeId)
    {
        var atkValues = addon->AtkValuesSpan;
        if (atkValues.Length <= itemCountIdx)
        {
            ClearHighlights(addonName);
            return;
        }

        var itemCount = atkValues[itemCountIdx].Int;
        if (itemCount <= 0)
        {
            ClearHighlights(addonName);
            return;
        }

        // Detect if a shield item is present (indented under a 1H weapon).
        // Shields are stored at the END of the item ID list (at itemIdStart + itemCount).
        // The visible row sits at position 1 (AddonShieldIndex), shifting subsequent
        // visible indices by +1. We also require the shield to be filter-visible.
        const int addonShieldIndex = 1;
        var shieldPresent = false;
        var shieldIdx = itemIdStart + itemCount;
        var shieldFilterIdx = filterStart + itemCount;
        if (shieldIdx < atkValues.Length
            && shieldFilterIdx < atkValues.Length
            && atkValues[shieldIdx].Type == AtkValueType.UInt
            && atkValues[shieldIdx].UInt > 0
            && atkValues[shieldFilterIdx].Type == AtkValueType.UInt
            && atkValues[shieldFilterIdx].UInt <= ShopFilterVisibleMaxValue)
        {
            shieldPresent = true;
            if (!_loggedBisItems.Contains(addonName))
                _log.Info($"[Highlight] {addonName}: shield detected at AtkValues[{shieldIdx}] (ID={atkValues[shieldIdx].UInt})");
        }

        // Build set of BiS ListItemIndex values (filter-compressed, with shield offset)
        _bisListItemIndices.Clear();
        for (var i = 0; i < itemCount; i++)
        {
            var idx = itemIdStart + i;
            if (idx >= atkValues.Length) break;

            var v = atkValues[idx];
            if (v.Type != AtkValueType.UInt) continue;
            if (!_itemMapping.IsBisItem(v.UInt)) continue;

            // Compute the compressed (filtered) tree index. If the item is currently
            // filtered out of view, GetFilteredIndex returns -1 and we skip it.
            var filteredIdx = GetFilteredIndex(i, filterStart, atkValues);
            if (filteredIdx < 0)
            {
                if (!_loggedBisItems.Contains(addonName))
                    _log.Info($"[Highlight] {addonName}: BiS ID={v.UInt} atkIdx={i} -> hidden by filter");
                continue;
            }

            var shieldOffset = (shieldPresent && i >= addonShieldIndex) ? 1 : 0;
            var listItemIdx = filteredIdx + shieldOffset;
            _bisListItemIndices.Add(listItemIdx);

            if (!_loggedBisItems.Contains(addonName))
                _log.Info($"[Highlight] {addonName}: BiS ID={v.UInt} atkIdx={i} -> listItemIdx={listItemIdx} (filtered={filteredIdx}, shieldOffset={shieldOffset})");
        }

        // Also check the shield item itself (stored at itemIdStart + itemCount)
        if (shieldPresent && _itemMapping.IsBisItem(atkValues[shieldIdx].UInt))
        {
            _bisListItemIndices.Add(addonShieldIndex);
            if (!_loggedBisItems.Contains(addonName))
                _log.Info($"[Highlight] {addonName}: shield is BiS (ID={atkValues[shieldIdx].UInt}) -> listItemIdx={addonShieldIndex}");
        }

        if (!_loggedBisItems.Contains(addonName))
        {
            _log.Info($"[Highlight] {addonName}: {_bisListItemIndices.Count} BiS item(s), itemCount={itemCount}, shield={shieldPresent}");
            _loggedBisItems.Add(addonName);
        }

        if (_bisListItemIndices.Count == 0)
        {
            ClearHighlights(addonName);
            return;
        }

        // Find the tree list component by known node ID
        AtkComponentTreeList* treeList = null;

        var treeListNode = addon->GetNodeById(treeListNodeId);
        if (treeListNode != null && (ushort)treeListNode->Type >= 1000) // Component nodes are type 1000+
        {
            treeList = (AtkComponentTreeList*)((AtkComponentNode*)treeListNode)->Component;
        }

        if (treeList == null)
        {
            if (!_loggedTreeList.Contains(addonName))
            {
                _log.Warning($"[Highlight] {addonName}: no tree list component found (GetNodeById({treeListNodeId}) returned {(treeListNode == null ? "null" : treeListNode->Type.ToString())})");
                _loggedTreeList.Add(addonName);
            }
            ClearHighlights(addonName);
            return;
        }

        if (!_loggedTreeList.Contains(addonName))
        {
            _log.Info($"[Highlight] {addonName}: tree list has {treeList->Items.Count} visible items");
            _loggedTreeList.Add(addonName);
        }

        // Iterate visible tree list items and highlight BiS matches
        for (var i = 0; i < treeList->Items.Count; i++)
        {
            var itemPtr = treeList->Items[i].Value;
            if (itemPtr == null || itemPtr->Renderer == null) continue;

            var ownerNode = (AtkResNode*)itemPtr->Renderer->OwnerNode;
            if (ownerNode == null) continue;

            var renderer = (AtkComponentListItemRenderer*)itemPtr->Renderer->OwnerNode->Component;
            if (renderer == null) continue;

            var listIndex = renderer->ListItemIndex;
            var isBis = _bisListItemIndices.Contains(listIndex);

            if (isBis)
                ApplyBisTintRecursive(addonName, ownerNode);
            else
                RestoreNodeRecursive(addonName, ownerNode);
        }
    }

    /// <summary>
    /// Convert a raw AtkValues item index into the *compressed* (visible) index that
    /// the tree's ListItemIndex uses. Items filtered out of the current view are
    /// skipped — they have a filter value above <see cref="ShopFilterVisibleMaxValue"/>.
    /// Returns -1 if the item itself is hidden by the filter.
    /// </summary>
    private static int GetFilteredIndex(int index, int filterStart, System.Span<FFXIVClientStructs.FFXIV.Component.GUI.AtkValue> atkValues)
    {
        if (atkValues.Length <= filterStart + index) return -1;

        var self = atkValues[filterStart + index];
        if (self.Type != AtkValueType.UInt || self.UInt > ShopFilterVisibleMaxValue) return -1;

        var visibleCount = 0;
        for (var i = 0; i < index; i++)
        {
            var slot = filterStart + i;
            if (slot >= atkValues.Length) break;
            var v = atkValues[slot];
            if (v.Type == AtkValueType.UInt && v.UInt <= ShopFilterVisibleMaxValue)
                visibleCount++;
        }
        return visibleCount;
    }

    // ==================== Color Tinting ====================

    /// <summary>Apply tint to a node and all its children/siblings for full-row highlighting.</summary>
    private void ApplyBisTintRecursive(string addonName, AtkResNode* node, int depth = 0)
    {
        if (node == null || depth > 10) return;
        ApplyBisTint(addonName, node);
        ApplyBisTintRecursive(addonName, node->ChildNode, depth + 1);
        if (depth > 0) // Don't walk siblings of the root node
            ApplyBisTintRecursive(addonName, node->PrevSiblingNode, depth);
    }

    /// <summary>Restore a node and all its children/siblings.</summary>
    private void RestoreNodeRecursive(string addonName, AtkResNode* node, int depth = 0)
    {
        if (node == null || depth > 10) return;
        RestoreNode(addonName, node);
        RestoreNodeRecursive(addonName, node->ChildNode, depth + 1);
        if (depth > 0)
            RestoreNodeRecursive(addonName, node->PrevSiblingNode, depth);
    }

    private Dictionary<nint, SavedColor> GetAddonNodes(string addonName)
    {
        if (!_modifiedNodesByAddon.TryGetValue(addonName, out var nodes))
        {
            nodes = new Dictionary<nint, SavedColor>();
            _modifiedNodesByAddon[addonName] = nodes;
        }
        return nodes;
    }

    /// <summary>Apply a strong teal highlight tint to a node, saving its original colors.</summary>
    private void ApplyBisTint(string addonName, AtkResNode* node)
    {
        if (node == null) return;
        var ptr = (nint)node;
        var nodes = GetAddonNodes(addonName);

        // Save original colors only on first modification
        if (!nodes.ContainsKey(ptr))
        {
            nodes[ptr] = new SavedColor
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
    private void RestoreNode(string addonName, AtkResNode* node)
    {
        if (node == null) return;
        var ptr = (nint)node;

        if (!_modifiedNodesByAddon.TryGetValue(addonName, out var nodes)) return;

        if (nodes.TryGetValue(ptr, out var saved))
        {
            node->MultiplyRed = saved.MultR;
            node->MultiplyGreen = saved.MultG;
            node->MultiplyBlue = saved.MultB;
            node->AddRed = saved.AddR;
            node->AddGreen = saved.AddG;
            node->AddBlue = saved.AddB;
            nodes.Remove(ptr);
        }
    }

    /// <summary>Restore modified nodes for a specific addon.</summary>
    private void ClearHighlights(string addonName)
    {
        if (!_modifiedNodesByAddon.TryGetValue(addonName, out var nodes)) return;

        foreach (var (ptr, saved) in nodes)
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

        nodes.Clear();
    }

    /// <summary>Restore all modified nodes across all addons.</summary>
    private void ClearAllHighlights()
    {
        // Collect keys first to avoid modifying dictionary during iteration
        var addonNames = new List<string>(_modifiedNodesByAddon.Keys);
        foreach (var addonName in addonNames)
            ClearHighlights(addonName);
    }

    // ==================== Lifecycle ====================

    private void OnAddonClose(AddonEvent type, AddonArgs args)
    {
        ClearHighlights(args.AddonName);
        _loggedBisItems.Remove(args.AddonName);
        _loggedTreeList.Remove(args.AddonName);
    }

    public void Dispose()
    {
        if (!_registered) return;

        ClearAllHighlights();

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
