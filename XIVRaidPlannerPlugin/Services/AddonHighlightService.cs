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
    // Per-addon log flags to prevent spamming logs on every PreDraw frame
    private readonly HashSet<string> _loggedBisItems = new();
    private readonly HashSet<string> _loggedTreeList = new();

    // Track modified nodes per addon so clearing one doesn't affect others
    private readonly Dictionary<string, Dictionary<nint, SavedColor>> _modifiedNodesByAddon = new();

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
                ClearHighlights(addonName);
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
            if (addon == null || !addon->IsVisible) return;

            HighlightShopItems(addon, addonName,
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
        const string addonName = "ShopExchangeCurrency";

        if (!_config.EnableShopHighlighting || !_itemMapping.HasData)
        {
            ClearHighlights(addonName);
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible) return;

            HighlightShopItems(addon, addonName,
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
            if (!_loggedBisItems.Contains(addonName))
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

                if (!_loggedBisItems.Contains(addonName))
                    _log.Info($"[Highlight] {addonName}: BiS ID={v.UInt} atkIdx={i} -> listItemIdx={listItemIdx} (shieldOffset={shieldOffset})");
            }
        }

        // Also check the shield item itself (stored at itemIdStart + itemCount)
        if (shieldPresent && _itemMapping.IsBisItem(atkValues[shieldIdx].UInt))
        {
            bisListItemIndices.Add(addonShieldIndex);
            if (!_loggedBisItems.Contains(addonName))
                _log.Info($"[Highlight] {addonName}: shield is BiS (ID={atkValues[shieldIdx].UInt}) -> listItemIdx={addonShieldIndex}");
        }

        if (!_loggedBisItems.Contains(addonName))
        {
            _log.Info($"[Highlight] {addonName}: {bisListItemIndices.Count} BiS item(s), itemCount={itemCount}, shield={shieldPresent}");
            _loggedBisItems.Add(addonName);
        }

        // Find the tree list component by known node ID
        AtkComponentTreeList* treeList = null;

        var treeListNode = addon->GetNodeById(treeListNodeId);
        if (treeListNode != null && (ushort)treeListNode->Type >= 1000) // Any component subtype (1000+)
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
            var isBis = bisListItemIndices.Contains(listIndex);

            if (isBis)
                ApplyBisTintRecursive(addonName, ownerNode);
            else
                RestoreNodeRecursive(addonName, ownerNode);
        }
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
        foreach (var (_, nodes) in _modifiedNodesByAddon)
        {
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
        }

        _modifiedNodesByAddon.Clear();
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
