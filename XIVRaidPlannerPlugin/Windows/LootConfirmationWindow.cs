using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Popup window to confirm loot logging after a drop is detected.
/// Shown when AutoLogMode is Confirm.
/// </summary>
public class LootConfirmationWindow : Window, IDisposable
{
    private LootEvent? _pendingLoot;
    private string _resolvedPlayerName = "";
    private string _resolvedPlayerId = "";
    private string _resolvedSlot = "";
    private string _floorName = "";
    private int _weekNumber;

    /// <summary>Fired when user confirms the loot log. Args: playerId, slot, materialType, floorName, weekNumber.</summary>
    public event Action<string, string?, string?, string, int>? OnConfirm;

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
    public void ShowForLoot(LootEvent loot, string playerId, string playerName, string floorName, int weekNumber)
    {
        _pendingLoot = loot;
        _resolvedPlayerId = playerId;
        _resolvedPlayerName = playerName;
        _resolvedSlot = loot.GearSlot ?? "";
        _floorName = floorName;
        _weekNumber = weekNumber;
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
        ImGui.Spacing();

        // Action buttons
        if (ImGui.Button("Confirm"))
        {
            OnConfirm?.Invoke(
                _resolvedPlayerId,
                _pendingLoot.GearSlot,
                _pendingLoot.MaterialType,
                _floorName,
                _weekNumber);
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
