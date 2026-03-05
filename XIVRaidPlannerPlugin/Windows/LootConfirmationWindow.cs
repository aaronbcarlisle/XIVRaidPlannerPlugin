using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Popup window to confirm loot logging after a drop is detected.
/// Shown when AutoLogMode is Confirm.
/// </summary>
public class LootConfirmationWindow : Window, IDisposable
{
    private static Dictionary<string, string[]> MaterialSlotOptions => GameConstants.MaterialSlotOptions;

    private LootEvent? _pendingLoot;
    private string _resolvedPlayerName = "";
    private string _resolvedPlayerId = "";
    private string _resolvedSlot = "";
    private string _floorName = "";
    private int _weekNumber;

    // Material slot augmentation selection
    private int _selectedSlotIndex;
    private string? _selectedSlotAugmented;
    private string[]? _eligibleSlots;

    /// <summary>Fired when user confirms the loot log. Args: playerId, slot, materialType, floorName, weekNumber, slotAugmented.</summary>
    public event Action<string, string?, string?, string, int, string?>? OnConfirm;

    /// <summary>Fired when user skips (dismisses without logging).</summary>
    public event Action? OnSkip;

    public LootConfirmationWindow()
        : base("Log Loot?",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 120),
            MaximumSize = new Vector2(400, 250),
        };
    }

    /// <summary>Show the confirmation for a detected loot event.</summary>
    public void ShowForLoot(LootEvent loot, string playerId, string playerName, string floorName, int weekNumber, string[]? playerEligibleSlots = null)
    {
        _pendingLoot = loot;
        _resolvedPlayerId = playerId;
        _resolvedPlayerName = playerName;
        _resolvedSlot = loot.GearSlot ?? "";
        _floorName = floorName;
        _weekNumber = weekNumber;

        // Initialize material slot selection using player-specific eligible slots
        if (loot.IsMaterial && loot.MaterialType != null)
        {
            // Use player-specific slots if available, fall back to static mapping
            _eligibleSlots = playerEligibleSlots;
            if (_eligibleSlots == null && MaterialSlotOptions.TryGetValue(loot.MaterialType, out var fallbackSlots))
                _eligibleSlots = fallbackSlots;

            if (_eligibleSlots is { Length: > 0 })
            {
                _selectedSlotIndex = 0;
                _selectedSlotAugmented = _eligibleSlots[0];
            }
            else
            {
                _selectedSlotIndex = 0;
                _selectedSlotAugmented = null;
            }
        }
        else
        {
            _eligibleSlots = null;
            _selectedSlotIndex = 0;
            _selectedSlotAugmented = null;
        }

        IsOpen = true;
    }

    public override void Draw()
    {
        if (_pendingLoot == null)
        {
            IsOpen = false;
            return;
        }

        // Display what was detected
        if (_pendingLoot.IsGear)
        {
            ImGui.Text($"{FormatSlotName(_resolvedSlot)} -> {_resolvedPlayerName}");
        }
        else if (_pendingLoot.IsMaterial)
        {
            ImGui.Text($"{FormatMaterialName(_pendingLoot.MaterialType!)} -> {_resolvedPlayerName}");
        }
        else
        {
            ImGui.Text($"{_pendingLoot.ItemName} -> {_resolvedPlayerName}");
        }

        ImGui.TextDisabled($"Floor: {_floorName}  Week: {_weekNumber}");

        // Slot selection dropdown for materials with augmentable slots
        if (_pendingLoot.IsMaterial && _eligibleSlots is { Length: > 0 })
        {
            ImGui.Spacing();
            ImGui.Text("Augment slot:");
            ImGui.SameLine();

            // Build display names
            var displayNames = new string[_eligibleSlots.Length];
            for (var i = 0; i < _eligibleSlots.Length; i++)
                displayNames[i] = FormatSlotName(_eligibleSlots[i]);

            if (ImGui.Combo("##augment_slot", ref _selectedSlotIndex, displayNames, displayNames.Length))
            {
                _selectedSlotAugmented = _eligibleSlots[_selectedSlotIndex];
            }
        }

        ImGui.Spacing();

        // Action buttons
        if (ImGui.Button("Confirm"))
        {
            OnConfirm?.Invoke(
                _resolvedPlayerId,
                _pendingLoot.GearSlot,
                _pendingLoot.MaterialType,
                _floorName,
                _weekNumber,
                _selectedSlotAugmented);
            Close();
        }

        ImGui.SameLine();

        if (ImGui.Button("Skip"))
        {
            OnSkip?.Invoke();
            Close();
        }
    }

    private void Close()
    {
        _pendingLoot = null;
        IsOpen = false;
    }

    private static string FormatSlotName(string slot)
    {
        return slot switch
        {
            "ring1" or "ring2" => "Ring",
            "tome_weapon" => "Tome Weapon",
            _ => char.ToUpper(slot[0]) + slot[1..],
        };
    }

    private static string FormatMaterialName(string material)
    {
        return material switch
        {
            "universal_tomestone" => "Universal Tomestone",
            _ => char.ToUpper(material[0]) + material[1..],
        };
    }

    public void Dispose() { }
}
