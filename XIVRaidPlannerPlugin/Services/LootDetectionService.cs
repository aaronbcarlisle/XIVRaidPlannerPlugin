using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Monitors chat messages for loot distribution events.
/// Parses SeString payloads to extract player names and item information.
/// </summary>
public class LootDetectionService : IDisposable
{
    private readonly IChatGui _chatGui;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    /// <summary>Fired when loot is obtained by a player.</summary>
    public event Action<LootEvent>? OnLootObtained;

    /// <summary>Items that have been distributed in the current instance session.</summary>
    public List<LootEvent> DistributedLoot { get; } = new();

    /// <summary>Items seen in loot windows but not yet distributed.</summary>
    public HashSet<string> PendingDrops { get; } = new();

    public LootDetectionService(IChatGui chatGui, IDataManager dataManager, IPluginLog log)
    {
        _chatGui = chatGui;
        _dataManager = dataManager;
        _log = log;

        _chatGui.ChatMessage += OnChatMessage;
    }

    /// <summary>Clear state when entering a new instance.</summary>
    public void Reset()
    {
        DistributedLoot.Clear();
        PendingDrops.Clear();
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        // System messages for loot distribution
        // XivChatType 2105 = LootNotice (item obtained)
        // XivChatType 62 = SystemMessage
        if (type != (XivChatType)2105 && type != XivChatType.SystemMessage)
            return;

        try
        {
            var text = message.TextValue;

            // Parse "X obtains Y." pattern
            // SeString payloads contain structured data - extract player and item
            string? playerName = null;
            string? itemName = null;
            uint itemId = 0;

            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload playerPayload)
                {
                    playerName = playerPayload.PlayerName;
                }
                else if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemId = itemPayload.ItemId;
                    itemName = itemPayload.DisplayName;
                }
            }

            // If we got both a player and an item from a loot message
            if (!string.IsNullOrEmpty(playerName) && !string.IsNullOrEmpty(itemName) &&
                text.Contains("obtain", StringComparison.OrdinalIgnoreCase))
            {
                var slot = ResolveItemSlot(itemId, itemName);
                var materialType = ResolveMaterialType(itemName);

                var lootEvent = new LootEvent
                {
                    PlayerName = playerName,
                    ItemName = itemName,
                    ItemId = itemId,
                    GearSlot = slot,
                    MaterialType = materialType,
                    Timestamp = DateTime.UtcNow,
                };

                _log.Information($"Loot detected: {playerName} obtained {itemName} (slot={slot}, material={materialType})");

                DistributedLoot.Add(lootEvent);
                OnLootObtained?.Invoke(lootEvent);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error parsing loot message: {ex.Message}");
        }
    }

    /// <summary>Resolve an item to a gear slot using Lumina's EquipSlotCategory.</summary>
    private string? ResolveItemSlot(uint itemId, string itemName)
    {
        if (itemId == 0) return null;

        try
        {
            var item = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId);
            if (item == null) return null;

            var equipSlotCategory = item.Value.EquipSlotCategory.RowId;

            // Map EquipSlotCategory to our gear slot names
            // These IDs are stable across FFXIV patches
            return equipSlotCategory switch
            {
                1 => "weapon",    // MainHand
                2 => "weapon",    // OffHand (for shields, but we map to weapon)
                3 => "head",
                4 => "body",
                5 => "hands",
                7 => "legs",
                8 => "feet",
                9 => "earring",
                10 => "necklace",
                11 => "bracelet",
                12 => "ring1",    // Ring - which ring slot is determined later
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.Debug($"Failed to resolve item slot for {itemId}: {ex.Message}");
        }

        // Fallback: parse coffer names for savage loot coffers
        return ParseCofferSlot(itemName);
    }

    /// <summary>Parse savage coffer names to determine slot.</summary>
    private static string? ParseCofferSlot(string itemName)
    {
        var lower = itemName.ToLowerInvariant();

        if (lower.Contains("weapon") || lower.Contains("arm")) return "weapon";
        if (lower.Contains("head") || lower.Contains("helm") || lower.Contains("circlet")) return "head";
        if (lower.Contains("body") || lower.Contains("chest") || lower.Contains("mail")) return "body";
        if (lower.Contains("hand") || lower.Contains("glove") || lower.Contains("gauntlet")) return "hands";
        if (lower.Contains("leg") || lower.Contains("trousers") || lower.Contains("breeches")) return "legs";
        if (lower.Contains("foot") || lower.Contains("feet") || lower.Contains("boots") || lower.Contains("sabatons")) return "feet";
        if (lower.Contains("earring")) return "earring";
        if (lower.Contains("necklace") || lower.Contains("choker")) return "necklace";
        if (lower.Contains("bracelet") || lower.Contains("wristband")) return "bracelet";
        if (lower.Contains("ring")) return "ring1";

        return null;
    }

    /// <summary>Check if the item is an upgrade material.</summary>
    private static string? ResolveMaterialType(string itemName)
    {
        var lower = itemName.ToLowerInvariant();

        if (lower.Contains("twine")) return "twine";
        if (lower.Contains("glaze")) return "glaze";
        if (lower.Contains("solvent")) return "solvent";
        if (lower.Contains("universal") && lower.Contains("tomestone")) return "universal_tomestone";

        return null;
    }

    public void Dispose()
    {
        _chatGui.ChatMessage -= OnChatMessage;
    }
}

/// <summary>Represents a detected loot distribution event.</summary>
public class LootEvent
{
    public string PlayerName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public uint ItemId { get; set; }

    /// <summary>Resolved gear slot (null if not gear).</summary>
    public string? GearSlot { get; set; }

    /// <summary>Resolved material type (null if not a material).</summary>
    public string? MaterialType { get; set; }

    public DateTime Timestamp { get; set; }

    public bool IsGear => GearSlot != null;
    public bool IsMaterial => MaterialType != null;
}
